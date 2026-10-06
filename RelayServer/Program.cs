using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemoteControl.Common;

namespace RemoteControl.RelayServer
{
    /// <summary>
    /// 中继会话 - 一个被控端 + 任意多个控制端
    /// </summary>
    public class RelaySession
    {
        public string DeviceId { get; set; } = "";
        public NetworkConnection? ServerConnection { get; set; }
        public ConcurrentDictionary<int, NetworkConnection> Clients { get; } = new();
        public DateTime LastHeartbeat { get; set; } = DateTime.Now;
        public bool IsActive => ServerConnection != null;
    }

    /// <summary>
    /// 中继服务器 - 用于 NAT 穿透和远程连接。
    /// 控制端发来的包加上客户端ID后转给被控端；被控端发来的包带着收件人列表，中继按列表分发。
    /// </summary>
    public class RelayServer
    {
        private const int MaxClientsPerDevice = 10;

        private TcpListener? _listener;
        private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private int _nextClientId;

        public int Port { get; private set; } = 16888;

        public async Task StartAsync(int port = 16888)
        {
            if (_isRunning) return;

            Port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _isRunning = true;

                Log($"中继服务器已启动，监听端口 {Port}");
                Log($"等待连接...");

                // 启动清理任务
                _ = CleanupTask(_cts.Token);

                await AcceptClientsAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                Log($"启动失败: {ex.Message}");
                _isRunning = false;
            }
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                    Log($"新连接: {endpoint}");

                    var connection = new NetworkConnection(client);
                    _ = HandleConnectionAsync(connection);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Log($"接受连接失败: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandleConnectionAsync(NetworkConnection connection)
        {
            // 在首包之前就订阅断开事件，之后的处理都挂在 closed 上，保证只清理一次
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstPacket = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<Packet>? handler = null;

            connection.PacketReceived += packet =>
            {
                if (handler != null) handler(packet);
                else firstPacket.TrySetResult(packet);
            };
            connection.Disconnected += () =>
            {
                firstPacket.TrySetCanceled();
                closed.TrySetResult();
            };
            connection.Start();

            string? deviceId = null;
            bool isServer = false;
            int clientId = 0;

            try
            {
                // 首包到达或 10 秒超时，先到先得
                var finished = await Task.WhenAny(firstPacket.Task, Task.Delay(10000));
                if (finished != firstPacket.Task || !firstPacket.Task.IsCompletedSuccessfully)
                {
                    Log($"连接超时或断开，未收到识别包: {connection.RemoteEndPoint}");
                    return;
                }

                var packet = firstPacket.Task.Result;
                var msg = packet.Type == MessageType.Connect ? ConnectMessage.Deserialize(packet.Data) : null;
                if (msg == null || string.IsNullOrWhiteSpace(msg.DeviceId))
                {
                    Log($"无效的识别包: {connection.RemoteEndPoint}");
                    return;
                }

                deviceId = msg.DeviceId;

                // 服务端：UserName 以 "SERVER:" 开头；客户端：以 "CLIENT:" 开头
                if (msg.UserName.StartsWith("SERVER:"))
                {
                    isServer = true;
                    var session = RegisterServer(deviceId, connection, msg);
                    handler = p => OnServerPacket(session, connection, p);
                }
                else if (msg.UserName.StartsWith("CLIENT:"))
                {
                    var session = AttachClient(deviceId, connection, packet, out clientId);
                    if (session == null) return;
                    int id = clientId;
                    handler = p => OnClientPacket(session, id, p);
                }
                else
                {
                    Log($"未知的连接类型: {connection.RemoteEndPoint}");
                    return;
                }

                // 保持连接直到断开
                await closed.Task;
            }
            catch (Exception ex)
            {
                Log($"处理连接异常: {ex.Message}");
            }
            finally
            {
                if (deviceId != null && _sessions.TryGetValue(deviceId, out var session))
                {
                    if (isServer)
                    {
                        // 仅当会话仍属于这条连接才移除，避免误删重新注册的新会话
                        if (session.ServerConnection == connection &&
                            ((ICollection<KeyValuePair<string, RelaySession>>)_sessions).Remove(new(deviceId, session)))
                        {
                            foreach (var c in session.Clients.Values) c.Dispose();
                            session.Clients.Clear();
                            Log($"服务端离线: {deviceId}");
                        }
                    }
                    else if (clientId != 0 && session.Clients.TryRemove(clientId, out _))
                    {
                        Log($"客户端断开: {deviceId} #{clientId}，剩余 {session.Clients.Count} 人");

                        // 通知被控端这个访问者走了，让它停止为他编码
                        _ = SafeSendAsync(session.ServerConnection, RelayEnvelope.FromClient(clientId,
                            new Packet { Type = MessageType.Disconnect, Data = Array.Empty<byte>() }));
                    }
                }

                connection.Dispose();
            }
        }

        private RelaySession RegisterServer(string deviceId, NetworkConnection connection, ConnectMessage msg)
        {
            var session = _sessions.GetOrAdd(deviceId, _ => new RelaySession { DeviceId = deviceId });

            var old = session.ServerConnection;
            session.ServerConnection = connection;
            session.LastHeartbeat = DateTime.Now;

            if (old != null && old != connection)
            {
                // 被控端重新注册（例如重启），旧连接上的访问者都要重新连
                foreach (var c in session.Clients.Values) c.Dispose();
                session.Clients.Clear();
                old.Dispose();
            }

            Log($"服务端注册成功: {deviceId} ({msg.DeviceName})");

            // 发送注册确认
            var response = new ConnectMessage
            {
                DeviceId = deviceId,
                DeviceName = "RelayServer",
                UserName = "OK",
                Password = "OK"
            };

            _ = SafeSendAsync(connection, new Packet
            {
                Type = MessageType.ConnectResponse,
                Data = response.Serialize()
            });

            return session;
        }

        private RelaySession? AttachClient(string deviceId, NetworkConnection connection, Packet connectPacket, out int clientId)
        {
            clientId = 0;

            string? error = null;
            if (!_sessions.TryGetValue(deviceId, out var session) || session.ServerConnection == null)
            {
                error = "设备不在线";
            }
            else if (session.Clients.Count >= MaxClientsPerDevice)
            {
                error = $"该设备已有 {MaxClientsPerDevice} 人在访问";
            }

            if (error != null)
            {
                Log($"客户端连接失败: 设备 {deviceId} {error}");

                var response = new ConnectMessage
                {
                    DeviceId = deviceId,
                    DeviceName = "RelayServer",
                    UserName = "ERROR",
                    Password = error
                };

                // 先把失败原因发完再断开
                SafeSendAsync(connection, new Packet
                {
                    Type = MessageType.ConnectResponse,
                    Data = response.Serialize()
                }).Wait(2000);

                return null;
            }

            clientId = Interlocked.Increment(ref _nextClientId);
            session!.Clients[clientId] = connection;

            Log($"客户端已接入: {deviceId} #{clientId} <- {connection.RemoteEndPoint}，当前 {session.Clients.Count} 人");

            // 把客户端的连接请求（含密码）转发给服务端，由服务端校验密码并回复，中继不做认证判断
            _ = SafeSendAsync(session.ServerConnection, RelayEnvelope.FromClient(clientId, connectPacket));
            return session;
        }

        private void OnServerPacket(RelaySession session, NetworkConnection server, Packet packet)
        {
            session.LastHeartbeat = DateTime.Now;

            if (packet.Type == MessageType.Heartbeat)
            {
                return;
            }

            if (!RelayEnvelope.TryReadToClients(packet, out var targets, out var inner))
            {
                return; // 旧版被控端不带收件人，无法路由
            }

            foreach (int id in targets)
            {
                if (!session.Clients.TryGetValue(id, out var client)) continue;

                if (inner.Type == MessageType.Disconnect)
                {
                    // 被控端要求断开这个客户端（密码错误、人数已满）
                    session.Clients.TryRemove(id, out _);
                    client.Dispose();
                    continue;
                }

                _ = SafeSendAsync(client, inner);
            }
        }

        private void OnClientPacket(RelaySession session, int clientId, Packet packet)
        {
            _ = SafeSendAsync(session.ServerConnection, RelayEnvelope.FromClient(clientId, packet));
        }

        /// <summary>
        /// 转发时对端可能刚好断开，异常直接吞掉，断开由 Disconnected 事件处理
        /// </summary>
        private static async Task SafeSendAsync(NetworkConnection? target, Packet packet)
        {
            if (target == null) return;
            try
            {
                await target.SendPacketAsync(packet).ConfigureAwait(false);
            }
            catch
            {
                // 忽略
            }
        }

        private async Task CleanupTask(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(30000, ct); // 每30秒清理一次

                    var timeout = TimeSpan.FromMinutes(5);
                    var now = DateTime.Now;

                    foreach (var kvp in _sessions)
                    {
                        var session = kvp.Value;
                        if (now - session.LastHeartbeat > timeout)
                        {
                            Log($"会话超时，清理: {kvp.Key}");
                            if (_sessions.TryRemove(kvp.Key, out var removed))
                            {
                                removed.ServerConnection?.Dispose();
                                foreach (var c in removed.Clients.Values) c.Dispose();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Log($"清理任务异常: {ex.Message}");
                    }
                }
            }
        }

        public void ShowStatus()
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine($"在线设备数: {_sessions.Count}");
            Console.WriteLine("========================================");

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                var serverStatus = session.ServerConnection?.IsConnected == true ? "在线" : "离线";
                var lastSeen = (DateTime.Now - session.LastHeartbeat).TotalSeconds;

                Console.WriteLine($"设备ID: {kvp.Key}");
                Console.WriteLine($"  服务端: {serverStatus}");
                Console.WriteLine($"  访问者: {session.Clients.Count} 人" +
                                  (session.Clients.IsEmpty ? "" : $" ({string.Join(", ", session.Clients.Values.Select(c => c.RemoteEndPoint))})"));
                Console.WriteLine($"  最后活动: {lastSeen:F0}秒前");
                Console.WriteLine();
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _cts?.Cancel();
            _listener?.Stop();

            foreach (var session in _sessions.Values)
            {
                session.ServerConnection?.Dispose();
                foreach (var c in session.Clients.Values) c.Dispose();
            }
            _sessions.Clear();

            _isRunning = false;
            Log("中继服务器已停止");
        }

        private void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "远程控制 - 中继服务器";

            Console.WriteLine("╔════════════════════════════════════════════════╗");
            Console.WriteLine("║                                                ║");
            Console.WriteLine("║          远程控制软件 - 中继服务器              ║");
            Console.WriteLine("║                                                ║");
            Console.WriteLine("╚════════════════════════════════════════════════╝");
            Console.WriteLine();

            int port = 16888;
            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
            {
                port = customPort;
            }

            var server = new RelayServer();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n正在关闭服务器...");
                server.Stop();
                Environment.Exit(0);
            };

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(10000); // 每10秒显示一次状态
                    server.ShowStatus();
                }
            });

            await server.StartAsync(port);
        }
    }
}
