using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    /// <summary>
    /// 远程服务器 - 支持直连和中继模式，支持多个控制端同时访问。
    /// 所有人看到同一个屏幕、都可以操作，每个人的按键和点击会实时通知给其他人。
    /// </summary>
    public class RemoteServer : IDisposable
    {
        private const int MaxViewers = 10;

        private TcpListener? _listener;
        private NetworkConnection? _relayConnection;
        private readonly ScreenCapture _screenCapture = new();
        private readonly InputSimulator _inputSimulator = new();
        private readonly FileTransferManager _fileTransferManager = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private bool _disposed;
        private bool _useRelay;

        private readonly ConcurrentDictionary<int, ViewerSession> _viewers = new();
        private int _nextDirectViewerId;

        // 多人同时操作时，同一时刻只注入一个人的输入，避免键盘事件交错
        private readonly object _inputLock = new();
        private int _controllerId;

        // 截屏线程（所有访问者共享）和每个访问者自己的编码线程
        private readonly object _streamLock = new();
        private CancellationTokenSource? _captureCts;
        private volatile int _captureIntervalMs = 1000 / 30;

        // 画面静止后再多编几帧：CBR 下大改动后的第一帧比较糊，后续 P 帧会逐步补清楚
        private const int RefineFrames = 20;

        public event Action<string>? StatusChanged;
        public event Action? ClientConnected;
        public event Action? ClientDisconnected;

        /// <summary>在线访问者变化（加入、离开、改名、换人操作）</summary>
        public event Action<IReadOnlyList<ViewerInfo>, int>? ViewersChanged;

        /// <summary>协作动态（谁按了什么键、点了哪里）</summary>
        public event Action<InputActivityMessage>? Activity;

        public int Port { get; private set; } = 5900;
        public bool IsRunning => _isRunning;

        /// <summary>中继模式下，中继已确认注册（此后客户端才能按设备ID连接）</summary>
        public bool IsRegistered { get; private set; }
        public string DeviceId { get; set; } = "";
        public string Password { get; set; } = "123456";
        public bool UseRelay => _useRelay;
        public int ViewerCount => _viewers.Values.Count(v => v.Authenticated);

        /// <summary>
        /// 发送 Ctrl+Alt+Del。返回空串表示成功，否则是失败原因。
        /// 只有无人值守服务（SYSTEM）能调用 SendSAS，普通模式下为 null
        /// </summary>
        public Func<string>? SasHandler { get; set; }

        /// <summary>
        /// 接入确认：密码正确后询问本机是否允许（名字、地址 → 允许）。为 null 表示不用确认
        /// </summary>
        public Func<string, string, Task<bool>>? ApprovalHandler { get; set; }

        /// <summary>白板标注（在被控端屏幕上显示，界面线程外触发）</summary>
        public event Action<AnnotationMessage>? AnnotationReceived;

        private const int ApprovalTimeoutMs = 30_000;

        public RemoteServer()
        {
            DeviceId = GenerateDeviceId();
            // 显示器变化时通知所有人，并让每个人的编码器按新尺寸重建
            _screenCapture.MonitorsChanged += () =>
            {
                foreach (var v in _viewers.Values) v.KeyFrameRequested = true;
                BroadcastMonitorList();
                // 坐标是相对显示器的，换屏后旧标注就对不上了
                AnnotationReceived?.Invoke(new AnnotationMessage { Kind = AnnotationKind.Clear });
            };
        }

        public MonitorInfo[] Monitors => _screenCapture.Monitors;
        public int SelectedMonitor => _screenCapture.SelectedIndex;

        private MonitorListMessage CurrentMonitorList() =>
            new() { Monitors = _screenCapture.Monitors, Current = _screenCapture.SelectedIndex };

        private void BroadcastMonitorList()
        {
            var packet = new Packet { Type = MessageType.MonitorList, Data = CurrentMonitorList().Serialize() };
            foreach (var v in AuthenticatedViewers())
            {
                _ = SafeSendAsync(v, packet);
            }
        }

        public static string GenerateDeviceId()
        {
            // 9 位纯数字，和 ToDesk 一样方便口头报号
            return Random.Shared.Next(100_000_000, 1_000_000_000).ToString();
        }

        /// <summary>
        /// 启动直连模式
        /// </summary>
        public async Task StartAsync(int port = 5900)
        {
            if (_isRunning) return;

            _useRelay = false;
            Port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(System.Net.IPAddress.Any, Port);
                _listener.Start();
                _isRunning = true;

                OnStatusChanged($"服务已启动（直连模式），监听端口 {Port}");
                OnStatusChanged($"设备ID: {DeviceId}");

                await AcceptClientsAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                OnStatusChanged($"启动失败: {ex.Message}");
                _isRunning = false;
            }
        }

        /// <summary>
        /// 启动中继模式
        /// </summary>
        public async Task StartRelayModeAsync(string relayHost, int relayPort = 16888)
        {
            if (_isRunning) return;

            _useRelay = true;
            _cts = new CancellationTokenSource();

            try
            {
                OnStatusChanged($"正在连接中继服务器 {relayHost}:{relayPort}...");

                _relayConnection = await NetworkConnection.ConnectAsync(relayHost, relayPort, 10000);
                if (_relayConnection == null)
                {
                    OnStatusChanged("连接中继服务器失败");
                    return;
                }

                _isRunning = true;
                OnStatusChanged($"已连接到中继服务器");
                OnStatusChanged($"设备ID: {DeviceId}");
                // 不打印密码：无人值守时日志会写进文件

                // 先订阅再开始接收，避免丢包
                var relay = _relayConnection;
                relay.PacketReceived += OnRelayPacket;
                relay.Disconnected += OnRelayDisconnected;
                relay.Start();

                // 注册到中继服务器（不发送密码，密码只在服务端本地校验）
                var registerMsg = new ConnectMessage
                {
                    DeviceId = DeviceId,
                    DeviceName = Environment.MachineName,
                    UserName = "SERVER:" + Environment.UserName,
                    Password = ""
                };

                await relay.SendPacketAsync(new Packet
                {
                    Type = MessageType.Connect,
                    Data = registerMsg.Serialize()
                });

                OnStatusChanged("正在注册到中继服务器...");

                // 保持连接。中继 5 分钟收不到服务端数据会清理会话，
                // 所以没有客户端时也要定期发心跳
                int tick = 0;
                while (_isRunning && relay.IsConnected)
                {
                    await Task.Delay(1000);
                    if (++tick % 30 == 0)
                    {
                        try
                        {
                            await relay.SendPacketAsync(new Packet { Type = MessageType.Heartbeat, Data = Array.Empty<byte>() });
                        }
                        catch
                        {
                            break; // 发送失败说明连接已断，由 Disconnected 事件处理
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged($"中继模式启动失败: {ex.Message}");
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
                    string address = client.Client.RemoteEndPoint?.ToString() ?? "未知";
                    OnStatusChanged($"客户端已连接: {address}");

                    var connection = new NetworkConnection(client);
                    int id = Interlocked.Increment(ref _nextDirectViewerId);
                    var viewer = new ViewerSession(id, address, connection.SendPacketAsync, connection);

                    if (!_viewers.TryAdd(id, viewer))
                    {
                        connection.Dispose();
                        continue;
                    }

                    connection.PacketReceived += packet => OnViewerPacket(viewer, packet);
                    connection.Disconnected += () => RemoveViewer(id, "连接断开");
                    connection.Start();
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        OnStatusChanged($"接受连接失败: {ex.Message}");
                    }
                }
            }
        }

        #region 中继路由

        private void OnRelayPacket(Packet packet)
        {
            try
            {
                switch (packet.Type)
                {
                    case MessageType.RelayFromClient:
                        if (RelayEnvelope.TryReadFromClient(packet, out int clientId, out var inner))
                        {
                            HandleRelayedClientPacket(clientId, inner);
                        }
                        break;

                    case MessageType.ConnectResponse:
                        // 中继确认注册后，客户端才能按设备ID找到这台机器
                        if (!IsRegistered)
                        {
                            IsRegistered = true;
                            OnStatusChanged("已注册到中继服务器，等待客户端连接...");
                        }
                        break;

                    case MessageType.Heartbeat:
                        break;
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged($"处理中继消息失败: {ex.Message}");
            }
        }

        private void HandleRelayedClientPacket(int clientId, Packet packet)
        {
            // 新客户端第一个包是 Connect；中继分配的 ID 全局唯一，直接用作访问者 ID
            if (packet.Type == MessageType.Connect && !_viewers.ContainsKey(clientId))
            {
                var msg = ConnectMessage.Deserialize(packet.Data);
                var relay = _relayConnection;
                if (msg == null || relay == null) return;

                var viewer = new ViewerSession(clientId, msg.DeviceName,
                    p => relay.SendPacketAsync(RelayEnvelope.ToClients(new[] { clientId }, p)), null);
                _viewers.TryAdd(clientId, viewer);
            }

            if (packet.Type == MessageType.Disconnect)
            {
                // 客户端主动断开，或中继通知客户端已掉线
                RemoveViewer(clientId, "已断开");
                return;
            }

            if (_viewers.TryGetValue(clientId, out var v))
            {
                OnViewerPacket(v, packet);
            }
        }

        #endregion

        #region 访问者消息

        private void OnViewerPacket(ViewerSession viewer, Packet packet)
        {
            try
            {
                // 未通过密码认证前，只接受 Connect 和心跳，其余指令一律丢弃
                if (!viewer.Authenticated && packet.Type != MessageType.Connect && packet.Type != MessageType.Heartbeat)
                {
                    return;
                }

                switch (packet.Type)
                {
                    case MessageType.Connect:
                        HandleConnect(viewer, packet.Data);
                        break;

                    case MessageType.FrameAck:
                        viewer.OnAck();
                        break;

                    case MessageType.RequestKeyFrame:
                        viewer.KeyFrameRequested = true;
                        break;

                    case MessageType.ChangeFrameRate:
                        if (packet.Data.Length >= 4)
                        {
                            int fps = Math.Clamp(BitConverter.ToInt32(packet.Data, 0), 1, 60);
                            if (fps != viewer.FrameRate)
                            {
                                viewer.FrameRate = fps;
                                viewer.EncoderDirty = true; // 码率按帧率分配，需要重建编码器
                                UpdateCaptureRate();
                            }
                        }
                        break;

                    case MessageType.ChangeQuality:
                        {
                            var msg = QualityMessage.Deserialize(packet.Data);
                            if (msg.Quality != viewer.Quality)
                            {
                                viewer.Quality = msg.Quality;
                                viewer.EncoderDirty = true; // 分辨率和码率变了，重建编码器（下一帧是关键帧）
                            }
                        }
                        break;

                    case MessageType.Disconnect:
                        RemoveViewer(viewer.Id, "已断开");
                        break;

                    case MessageType.MouseMove:
                        HandleMouseMove(viewer, packet.Data);
                        break;

                    case MessageType.MouseClick:
                        HandleMouseClick(viewer, packet.Data);
                        break;

                    case MessageType.MouseWheel:
                        HandleMouseWheel(viewer, packet.Data);
                        break;

                    case MessageType.KeyPress:
                        HandleKeyPress(viewer, packet.Data);
                        break;

                    case MessageType.FileListRequest:
                        HandleFileListRequest(viewer, packet.Data);
                        break;

                    case MessageType.FileDownloadRequest:
                        _ = HandleFileDownloadRequestAsync(viewer, packet.Data);
                        break;

                    case MessageType.FileUploadRequest:
                        HandleFileUploadRequest(viewer, packet.Data);
                        break;

                    case MessageType.FileData:
                        HandleFileData(viewer, packet.Data);
                        break;

                    case MessageType.FileUploadComplete:
                        HandleFileUploadComplete(viewer, packet.Data);
                        break;

                    case MessageType.FileTransferError:
                        {
                            // 客户端取消了传输
                            var msg = FileTransferErrorMessage.Deserialize(packet.Data);
                            if (msg != null) _fileTransferManager.Cancel(viewer.Id, msg.TransferId);
                        }
                        break;

                    case MessageType.SendSas:
                        HandleSendSas(viewer);
                        break;

                    case MessageType.SelectMonitor:
                        if (packet.Data.Length >= 4)
                        {
                            int index = BitConverter.ToInt32(packet.Data, 0);
                            if (index >= 0 && index < _screenCapture.Monitors.Length && index != _screenCapture.SelectedIndex)
                            {
                                _screenCapture.SelectMonitor(index);
                                OnStatusChanged($"{viewer.Name} 切换到屏幕{index + 1}");
                                // 截屏线程下一帧会刷新尺寸并触发 MonitorsChanged 广播
                            }
                        }
                        break;

                    case MessageType.Annotation:
                        HandleAnnotation(viewer, packet.Data);
                        break;

                    case MessageType.Heartbeat:
                        _ = SafeSendAsync(viewer, new Packet { Type = MessageType.Heartbeat, Data = Array.Empty<byte>() });
                        break;
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged($"处理消息失败: {ex.Message}");
            }
        }

        private void HandleConnect(ViewerSession viewer, byte[] data)
        {
            var msg = ConnectMessage.Deserialize(data);
            if (msg == null || viewer.Authenticated || viewer.AwaitingApproval) return;

            bool passwordOk = msg.Password == Password;
            if (passwordOk && ViewerCount < MaxViewers && ApprovalHandler != null)
            {
                viewer.AwaitingApproval = true;
                _ = ApproveAndJoinAsync(viewer, msg);
                return;
            }

            CompleteConnect(viewer, msg, !passwordOk ? "FAIL" : ViewerCount >= MaxViewers ? "FULL" : "OK");
        }

        /// <summary>等本机点"允许"。30 秒没人点按拒绝处理</summary>
        private async Task ApproveAndJoinAsync(ViewerSession viewer, ConnectMessage msg)
        {
            await SafeSendAsync(viewer, new Packet { Type = MessageType.PendingApproval, Data = Array.Empty<byte>() });

            string name = string.IsNullOrWhiteSpace(msg.NickName) ? msg.DeviceName : msg.NickName.Trim();
            OnStatusChanged($"{name}（{viewer.Address}）请求连接，等待本机确认");

            bool allowed = false;
            try
            {
                var ask = ApprovalHandler!(name, viewer.Address);
                var done = await Task.WhenAny(ask, Task.Delay(ApprovalTimeoutMs));
                allowed = done == ask && ask.Result;
            }
            catch (Exception ex)
            {
                OnStatusChanged($"接入确认出错: {ex.Message}");
            }

            viewer.AwaitingApproval = false;
            if (viewer.Removed) return; // 等待期间对方已经取消

            // 等待期间可能已经满员
            CompleteConnect(viewer, msg, !allowed ? "DENIED" : ViewerCount >= MaxViewers ? "FULL" : "OK");
        }

        private void CompleteConnect(ViewerSession viewer, ConnectMessage msg, string result)
        {
            bool authenticated = result == "OK";
            string failReason = result;

            if (authenticated)
            {
                viewer.Name = MakeUniqueName(viewer.Id,
                    string.IsNullOrWhiteSpace(msg.NickName) ? msg.DeviceName : msg.NickName.Trim());
                viewer.Authenticated = true;
            }

            var response = new ConnectMessage
            {
                DeviceId = DeviceId,
                DeviceName = Environment.MachineName,
                UserName = Environment.UserName,
                Password = authenticated ? "OK" : failReason
            };

            _ = SafeSendAsync(viewer, new Packet
            {
                Type = MessageType.ConnectResponse,
                Data = response.Serialize()
            });

            if (!authenticated)
            {
                OnStatusChanged(failReason switch
                {
                    "FULL" => $"拒绝连接: 已达到 {MaxViewers} 人上限 ({viewer.Address})",
                    "DENIED" => $"已拒绝连接请求 ({viewer.Address})",
                    _ => $"客户端认证失败 ({viewer.Address})"
                });

                if (_useRelay)
                {
                    // 中继模式下由中继断开这个客户端；这里只清理本地状态
                    _viewers.TryRemove(viewer.Id, out _);
                    viewer.Dispose();
                    _ = KickFromRelayAsync(viewer.Id);
                }
                else
                {
                    // 先让回应发出去再断开
                    _ = Task.Delay(500).ContinueWith(_ => RemoveViewer(viewer.Id, null));
                }
                return;
            }

            OnStatusChanged($"{viewer.Name} 已加入（{viewer.Address}），当前 {ViewerCount} 人在线");
            ClientConnected?.Invoke();

            _ = SendSystemInfoAsync(viewer);
            _ = SafeSendAsync(viewer, new Packet { Type = MessageType.MonitorList, Data = CurrentMonitorList().Serialize() });
            StartViewerStream(viewer);
            BroadcastViewerList();
            BroadcastActivity(new InputActivityMessage { ViewerId = viewer.Id, Name = viewer.Name, Kind = ActivityKind.Joined });
        }

        /// <summary>同名的人加上编号，免得大家分不清是谁在操作</summary>
        private string MakeUniqueName(int selfId, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "访客";
            if (name.Length > 20) name = name[..20];

            var taken = _viewers.Values.Where(v => v.Id != selfId && v.Authenticated).Select(v => v.Name).ToHashSet();
            if (!taken.Contains(name)) return name;

            for (int i = 2; ; i++)
            {
                string candidate = $"{name} ({i})";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private async Task KickFromRelayAsync(int clientId)
        {
            await Task.Delay(500); // 先让失败回应到达客户端
            var relay = _relayConnection;
            if (relay == null) return;
            try
            {
                await relay.SendPacketAsync(RelayEnvelope.ToClients(new[] { clientId },
                    new Packet { Type = MessageType.Disconnect, Data = Array.Empty<byte>() }));
            }
            catch
            {
                // 中继断开由 Disconnected 事件处理
            }
        }

        private void RemoveViewer(int id, string? reason)
        {
            if (!_viewers.TryRemove(id, out var viewer)) return;

            bool wasAuthenticated = viewer.Authenticated;
            ReleaseHeldInput(viewer);
            _fileTransferManager.CancelViewer(id);
            viewer.Dispose();

            lock (_inputLock)
            {
                if (_controllerId == id) _controllerId = 0;
            }

            if (_viewers.IsEmpty)
            {
                StopCapture();
            }
            UpdateCaptureRate();

            if (!wasAuthenticated) return;

            if (reason != null)
            {
                OnStatusChanged($"{viewer.Name} {reason}，当前 {ViewerCount} 人在线");
            }
            BroadcastViewerList();
            BroadcastActivity(new InputActivityMessage { ViewerId = id, Name = viewer.Name, Kind = ActivityKind.Left });
            ClientDisconnected?.Invoke();

            // 没人在看了，被控端屏幕上的标注也清掉
            if (ViewerCount == 0) AnnotationReceived?.Invoke(new AnnotationMessage { Kind = AnnotationKind.Clear });
        }

        #endregion

        #region 推流

        private void StartViewerStream(ViewerSession viewer)
        {
            EnsureCapture();
            UpdateCaptureRate();

            // 每个访问者一个编码线程：编码器、码率、流控各自独立，一个人网速慢不会拖慢别人
            var thread = new Thread(() => ViewerStreamLoop(viewer))
            {
                IsBackground = true,
                Name = $"Stream-{viewer.Id}"
            };
            thread.Start();
        }

        private void EnsureCapture()
        {
            lock (_streamLock)
            {
                if (_captureCts != null) return;

                var cts = new CancellationTokenSource();
                _captureCts = cts;
                var thread = new Thread(() => CaptureLoop(cts.Token))
                {
                    IsBackground = true,
                    Name = "ScreenCapture"
                };
                thread.Start();
            }
        }

        private void StopCapture()
        {
            lock (_streamLock)
            {
                _captureCts?.Cancel();
                _captureCts?.Dispose();
                _captureCts = null;
            }
        }

        /// <summary>截屏频率取所有人里最高的帧率，没人要的帧不截</summary>
        private void UpdateCaptureRate()
        {
            int fps = 1;
            foreach (var v in _viewers.Values)
            {
                if (v.Authenticated) fps = Math.Max(fps, v.FrameRate);
            }
            _captureIntervalMs = 1000 / Math.Max(fps, 15);
        }

        private void CaptureLoop(CancellationToken ct)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                long start = clock.ElapsedMilliseconds;
                try
                {
                    // 无人值守时跟上输入桌面（锁屏 / 登录界面 / UAC），切换后大家都要一个关键帧
                    if (Unattended.DesktopSwitcher.SyncThread())
                    {
                        foreach (var v in _viewers.Values) v.KeyFrameRequested = true;
                    }
                    _screenCapture.Capture();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 锁屏、切换到 UAC 安全桌面时截屏会失败，稍后重试
                    OnStatusChanged($"截屏失败: {ex.Message}");
                    ct.WaitHandle.WaitOne(1000);
                    continue;
                }

                int wait = _captureIntervalMs - (int)(clock.ElapsedMilliseconds - start);
                if (wait > 0) ct.WaitHandle.WaitOne(wait);
            }
        }

        /// <summary>
        /// 各档位的编码缩放比例和码率（按 1080p 估算，其他分辨率按像素数折算）。
        /// 跨运营商走中继时带宽通常只有几 Mbps，低档位降分辨率比压码率画面更清楚。
        /// </summary>
        private static (double Scale, long BitRate) GetProfile(Quality quality, int width, int height)
        {
            var (scale, bitRate1080p) = quality switch
            {
                Quality.Low => (0.5, 1_000_000L),
                Quality.Medium => (0.75, 2_000_000L),
                Quality.High => (1.0, 4_000_000L),
                Quality.Ultra => (1.0, 8_000_000L),
                _ => (0.75, 2_000_000L)
            };

            double pixelRatio = Math.Clamp(width * (double)height / (1920 * 1080), 0.25, 4.0);
            return (scale, (long)(bitRate1080p * Math.Sqrt(pixelRatio)));
        }

        private H264Encoder CreateEncoder(ViewerSession viewer, int sw, int sh, ISet<string> failed)
        {
            var (scale, bitRate) = GetProfile(viewer.Quality, sw, sh);

            var encoder = H264Encoder.Create(sw, sh, (int)(sw * scale), (int)(sh * scale),
                viewer.FrameRate, bitRate, failed, null);

            OnStatusChanged($"[{viewer.Name}] 视频编码: {encoder.Name}（{(encoder.IsHardware ? "硬件" : "软件")}）" +
                            $"{encoder.Width}x{encoder.Height} {bitRate / 1000} kbps");
            return encoder;
        }

        private void ViewerStreamLoop(ViewerSession viewer)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var failedEncoders = new HashSet<string>();
            H264Encoder? encoder = null;
            long lastVersion = -1;
            int refineLeft = 0;
            bool hasFrame = false;

            viewer.KeyFrameRequested = true;

            try
            {
                while (!viewer.Removed && _isRunning)
                {
                    try
                    {
                        if (!viewer.TryOpenWindow())
                        {
                            viewer.AckEvent.WaitOne(200);
                            continue;
                        }

                        long start = clock.ElapsedMilliseconds;
                        bool changed = false;

                        using (var snap = _screenCapture.AcquireLatest())
                        {
                            if (snap.Version > 0)
                            {
                                if (encoder == null || viewer.EncoderDirty ||
                                    encoder.SourceWidth != snap.Width || encoder.SourceHeight != snap.Height)
                                {
                                    viewer.EncoderDirty = false;
                                    encoder?.Dispose();
                                    encoder = null;
                                    encoder = CreateEncoder(viewer, snap.Width, snap.Height, failedEncoders);
                                    viewer.KeyFrameRequested = true;
                                    lastVersion = -1;
                                }

                                if (snap.Version != lastVersion)
                                {
                                    // 只在颜色转换期间持有读锁，编码时截屏线程可以继续发布新画面
                                    encoder.LoadFrame(snap.Pixels, snap.Stride);
                                    lastVersion = snap.Version;
                                    changed = true;
                                    hasFrame = true;
                                }
                            }
                        }

                        bool forceKey = viewer.KeyFrameRequested;
                        if (changed) refineLeft = RefineFrames;

                        // 画面静止且已经补清楚，就什么都不发，几乎不占带宽
                        if (encoder != null && hasFrame && (changed || forceKey || refineLeft > 0))
                        {
                            viewer.KeyFrameRequested = false;
                            if (!changed) refineLeft--;

                            byte[]? data;
                            bool isKey;
                            try
                            {
                                data = encoder.EncodeLoaded(forceKey, out isKey);
                            }
                            catch (Exception ex)
                            {
                                // 硬件编码器运行中出错（驱动问题、显卡被占用等），换下一个编码器
                                OnStatusChanged($"[{viewer.Name}] 编码器 {encoder.Name} 出错，切换编码器: {ex.Message}");
                                failedEncoders.Add(encoder.Name);
                                encoder.Dispose();
                                encoder = null;
                                lastVersion = -1;
                                continue;
                            }

                            if (data != null)
                            {
                                var msg = new VideoFrameMessage
                                {
                                    Width = _screenCapture.ScreenWidth,   // 真实分辨率，客户端据此换算鼠标坐标
                                    Height = _screenCapture.ScreenHeight,
                                    IsKeyFrame = isKey,
                                    Data = data,
                                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };

                                viewer.OnFrameSending(data.Length);
                                viewer.SendAsync(new Packet
                                {
                                    Type = MessageType.VideoFrame,
                                    Data = msg.Serialize()
                                }).GetAwaiter().GetResult();
                            }
                        }

                        // 按这个人的帧率上限补足间隔
                        int wait = 1000 / viewer.FrameRate - (int)(clock.ElapsedMilliseconds - start);
                        if (wait > 0)
                        {
                            Thread.Sleep(wait);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // 连接已释放
                    }
                    catch (Exception ex)
                    {
                        if (viewer.Removed) break;
                        OnStatusChanged($"[{viewer.Name}] 推流出错: {ex.Message}");
                        Thread.Sleep(2000);
                        failedEncoders.Clear(); // 所有编码器都失败时，稍后从头再试
                    }
                }
            }
            finally
            {
                encoder?.Dispose();
            }
        }

        #endregion

        #region 输入

        /// <summary>
        /// 记录谁在操作。换人时通知大家，方便多人协作时知道鼠标键盘现在在谁手里
        /// </summary>
        private void MarkController(ViewerSession viewer)
        {
            bool changed;
            lock (_inputLock)
            {
                changed = _controllerId != viewer.Id;
                _controllerId = viewer.Id;
            }

            if (changed)
            {
                BroadcastViewerList();
                BroadcastActivity(new InputActivityMessage { ViewerId = viewer.Id, Name = viewer.Name, Kind = ActivityKind.TakeControl });
            }
        }

        private void HandleMouseMove(ViewerSession viewer, byte[] data)
        {
            var msg = MouseMessage.Deserialize(data);
            if (msg == null) return;

            lock (_inputLock)
            {
                // 别人正按着鼠标拖动时，不让其他人的移动把指针抢走
                if (_controllerId != viewer.Id && IsOtherDragging(viewer)) return;
                // 客户端坐标相对当前显示器，换算成虚拟桌面坐标
                _inputSimulator.MoveMouse(msg.X + _screenCapture.ScreenLeft, msg.Y + _screenCapture.ScreenTop);
            }
        }

        private void HandleMouseClick(ViewerSession viewer, byte[] data)
        {
            var msg = MouseMessage.Deserialize(data);
            if (msg == null) return;

            lock (_inputLock)
            {
                if (msg.EventType == MouseEventType.Down)
                {
                    if (IsOtherDragging(viewer)) return;
                    viewer.HeldButtons.Add(msg.Button);
                }
                else if (msg.EventType == MouseEventType.Up)
                {
                    // 只放行自己按下过的键，否则会打断别人的拖动
                    if (!viewer.HeldButtons.Remove(msg.Button)) return;
                }

                _inputSimulator.SimulateMouseClick(msg.X + _screenCapture.ScreenLeft, msg.Y + _screenCapture.ScreenTop, msg.Button, msg.EventType);
            }

            MarkController(viewer);

            if (msg.EventType != MouseEventType.Up)
            {
                BroadcastActivity(new InputActivityMessage
                {
                    ViewerId = viewer.Id,
                    Name = viewer.Name,
                    Kind = ActivityKind.Click,
                    Button = msg.Button,
                    X = msg.X,
                    Y = msg.Y
                });
            }
        }

        private bool IsOtherDragging(ViewerSession viewer)
        {
            foreach (var v in _viewers.Values)
            {
                if (v.Id != viewer.Id && v.HeldButtons.Count > 0) return true;
            }
            return false;
        }

        private void HandleMouseWheel(ViewerSession viewer, byte[] data)
        {
            var msg = MouseWheelMessage.Deserialize(data);
            if (msg == null) return;

            lock (_inputLock)
            {
                _inputSimulator.SimulateMouseWheel(msg.Delta);
            }
            MarkController(viewer);
        }

        private void HandleKeyPress(ViewerSession viewer, byte[] data)
        {
            var msg = KeyMessage.Deserialize(data);
            if (msg == null || msg.KeyCode <= 0 || msg.KeyCode > 255) return;

            KeyModifiers modifiers;
            lock (_inputLock)
            {
                if (msg.IsDown)
                {
                    viewer.HeldKeys.Add(msg.KeyCode);
                }
                else if (!viewer.HeldKeys.Remove(msg.KeyCode))
                {
                    return; // 不是自己按下的键，不替别人松开
                }

                _inputSimulator.SimulateKeyPress(msg.KeyCode, msg.IsDown);
                modifiers = GetModifiers(viewer.HeldKeys);
            }

            MarkController(viewer);

            // 只通知按下，并跳过单独的修饰键（它们会作为组合键的一部分显示）
            if (msg.IsDown && !IsModifierKey(msg.KeyCode))
            {
                BroadcastActivity(new InputActivityMessage
                {
                    ViewerId = viewer.Id,
                    Name = viewer.Name,
                    Kind = ActivityKind.Key,
                    KeyCode = msg.KeyCode,
                    Modifiers = modifiers
                });
            }
        }

        private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

        private static KeyModifiers GetModifiers(HashSet<int> held)
        {
            var m = KeyModifiers.None;
            foreach (int vk in held)
            {
                switch (vk)
                {
                    case 0x10: case 0xA0: case 0xA1: m |= KeyModifiers.Shift; break;
                    case 0x11: case 0xA2: case 0xA3: m |= KeyModifiers.Ctrl; break;
                    case 0x12: case 0xA4: case 0xA5: m |= KeyModifiers.Alt; break;
                    case 0x5B: case 0x5C: m |= KeyModifiers.Win; break;
                }
            }
            return m;
        }

        /// <summary>访问者断线时把他按住的键和鼠标键都松开，否则被控端会一直处于按住状态</summary>
        private void ReleaseHeldInput(ViewerSession viewer)
        {
            lock (_inputLock)
            {
                foreach (int vk in viewer.HeldKeys)
                {
                    _inputSimulator.SimulateKeyPress(vk, false);
                }
                viewer.HeldKeys.Clear();

                foreach (var button in viewer.HeldButtons)
                {
                    _inputSimulator.ReleaseMouseButton(button);
                }
                viewer.HeldButtons.Clear();
            }
        }

        #endregion

        #region 广播

        private IReadOnlyList<ViewerSession> AuthenticatedViewers() =>
            _viewers.Values.Where(v => v.Authenticated && !v.Removed).OrderBy(v => v.Id).ToList();

        private void BroadcastViewerList()
        {
            var viewers = AuthenticatedViewers();
            var infos = viewers.Select(v => v.ToInfo()).ToArray();
            int controller;
            lock (_inputLock)
            {
                controller = _controllerId;
            }

            ViewersChanged?.Invoke(infos, controller);

            // 每个人收到的列表里 YourId 不同，要分开发
            foreach (var v in viewers)
            {
                var msg = new ViewerListMessage { YourId = v.Id, ControllerId = controller, Viewers = infos };
                _ = SafeSendAsync(v, new Packet { Type = MessageType.ViewerList, Data = msg.Serialize() });
            }
        }

        private void BroadcastActivity(InputActivityMessage activity)
        {
            activity.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Activity?.Invoke(activity);

            var packet = new Packet { Type = MessageType.InputActivity, Data = activity.Serialize() };
            var targets = AuthenticatedViewers();
            if (targets.Count == 0) return;

            var relay = _relayConnection;
            if (_useRelay && relay != null)
            {
                // 中继模式：一个包带上所有收件人，由中继分发，省一半上行带宽
                _ = SafeSendAsync(relay, RelayEnvelope.ToClients(targets.Select(v => v.Id).ToList(), packet));
            }
            else
            {
                foreach (var v in targets)
                {
                    _ = SafeSendAsync(v, packet);
                }
            }
        }

        #endregion

        #region 文件

        private void HandleFileListRequest(ViewerSession viewer, byte[] data)
        {
            var msg = FileListRequestMessage.Deserialize(data);
            var response = _fileTransferManager.GetFileList(msg.Path);

            _ = SafeSendAsync(viewer, new Packet
            {
                Type = MessageType.FileListResponse,
                Data = response.Serialize()
            });
        }

        private void SendFileError(ViewerSession viewer, string transferId, string error)
        {
            OnStatusChanged($"[{viewer.Name}] 文件传输失败: {error}");
            var msg = new FileTransferErrorMessage { TransferId = transferId, Error = error };
            _ = SafeSendAsync(viewer, new Packet { Type = MessageType.FileTransferError, Data = msg.Serialize() });
        }

        private async Task HandleFileDownloadRequestAsync(ViewerSession viewer, byte[] data)
        {
            var msg = FileTransferRequestMessage.Deserialize(data);
            if (msg == null) return;

            string? error = _fileTransferManager.OpenRead(viewer.Id, msg.TransferId, msg.FilePath, out string path);
            if (error != null)
            {
                SendFileError(viewer, msg.TransferId, error);
                return;
            }
            OnStatusChanged($"[{viewer.Name}] 下载文件 {path}");

            try
            {
                while (!viewer.Removed)
                {
                    // 读到 null：读完了，或者客户端取消了
                    var chunk = await _fileTransferManager.ReadChunkAsync(viewer.Id, msg.TransferId);
                    if (chunk == null) break;

                    // 等每块写进连接再读下一块，慢链路上不会把内存撑大
                    await viewer.SendAsync(new Packet { Type = MessageType.FileData, Data = chunk.Serialize() });
                }

                if (_fileTransferManager.Complete(viewer.Id, msg.TransferId))
                {
                    await SafeSendAsync(viewer, new Packet
                    {
                        Type = MessageType.FileComplete,
                        Data = System.Text.Encoding.UTF8.GetBytes(msg.TransferId)
                    });
                }
            }
            catch (Exception ex)
            {
                _fileTransferManager.Cancel(viewer.Id, msg.TransferId);
                if (!viewer.Removed) SendFileError(viewer, msg.TransferId, ex.Message);
            }
        }

        private void HandleFileUploadRequest(ViewerSession viewer, byte[] data)
        {
            var msg = FileTransferRequestMessage.Deserialize(data);
            if (msg == null) return;

            string? error = _fileTransferManager.OpenWrite(viewer.Id, msg.TransferId, msg.FilePath, out string path);
            if (error != null)
            {
                SendFileError(viewer, msg.TransferId, error);
                return;
            }
            OnStatusChanged($"[{viewer.Name}] 上传文件到 {path}（{msg.FileSize / 1024} KB）");
        }

        private void HandleFileData(ViewerSession viewer, byte[] data)
        {
            var msg = FileDataMessage.Deserialize(data);
            if (msg == null) return;

            // 包按顺序到达，在接收线程里同步写，保证块的顺序
            string? error = _fileTransferManager.WriteChunk(viewer.Id, msg.TransferId, msg.Offset, msg.Data);
            if (error != null) SendFileError(viewer, msg.TransferId, error);
        }

        private void HandleFileUploadComplete(ViewerSession viewer, byte[] data)
        {
            string transferId = System.Text.Encoding.UTF8.GetString(data);
            if (_fileTransferManager.Complete(viewer.Id, transferId))
            {
                OnStatusChanged($"[{viewer.Name}] 文件上传完成");
            }
        }

        #endregion

        #region Ctrl+Alt+Del / 标注

        private void HandleSendSas(ViewerSession viewer)
        {
            string result;
            var handler = SasHandler;
            if (handler == null)
            {
                result = "对方未开启无人值守模式，无法发送 Ctrl+Alt+Del";
            }
            else
            {
                try
                {
                    result = handler();
                }
                catch (Exception ex)
                {
                    result = $"发送 Ctrl+Alt+Del 失败: {ex.Message}";
                }
            }

            OnStatusChanged(result.Length == 0 ? $"{viewer.Name} 发送了 Ctrl+Alt+Del" : $"{viewer.Name} 请求 Ctrl+Alt+Del：{result}");
            _ = SafeSendAsync(viewer, new Packet { Type = MessageType.SasResult, Data = System.Text.Encoding.UTF8.GetBytes(result) });
            if (result.Length == 0)
            {
                MarkController(viewer);
                BroadcastActivity(new InputActivityMessage
                {
                    ViewerId = viewer.Id,
                    Name = viewer.Name,
                    Kind = ActivityKind.Key,
                    KeyCode = 0x2E, // Delete
                    Modifiers = KeyModifiers.Ctrl | KeyModifiers.Alt
                });
            }
        }

        private const int MaxAnnotationPoints = 20_000;

        private void HandleAnnotation(ViewerSession viewer, byte[] data)
        {
            var msg = AnnotationMessage.Deserialize(data);
            if (msg == null || msg.Points.Length > MaxAnnotationPoints) return;

            // 名字和 ID 以服务端为准，不信任客户端填的
            msg.ViewerId = viewer.Id;
            msg.Name = viewer.Name;
            AnnotationReceived?.Invoke(msg);

            var packet = new Packet { Type = MessageType.Annotation, Data = msg.Serialize() };
            var targets = AuthenticatedViewers();
            var relay = _relayConnection;
            if (_useRelay && relay != null)
            {
                _ = SafeSendAsync(relay, RelayEnvelope.ToClients(targets.Select(v => v.Id).ToList(), packet));
            }
            else
            {
                foreach (var v in targets) _ = SafeSendAsync(v, packet);
            }
        }

        #endregion

        private async Task SendSystemInfoAsync(ViewerSession viewer)
        {
            var info = new SystemInfoMessage
            {
                OSVersion = Environment.OSVersion.ToString(),
                ComputerName = Environment.MachineName,
                UserName = Environment.UserName,
                ScreenWidth = _screenCapture.ScreenWidth,
                ScreenHeight = _screenCapture.ScreenHeight
            };

            await SafeSendAsync(viewer, new Packet
            {
                Type = MessageType.SystemInfo,
                Data = info.Serialize()
            });
        }

        private static async Task SafeSendAsync(ViewerSession viewer, Packet packet)
        {
            if (viewer.Removed) return;
            try
            {
                await viewer.SendAsync(packet).ConfigureAwait(false);
            }
            catch
            {
                // 连接断开由 Disconnected 事件处理
            }
        }

        private static async Task SafeSendAsync(NetworkConnection connection, Packet packet)
        {
            try
            {
                await connection.SendPacketAsync(packet).ConfigureAwait(false);
            }
            catch
            {
                // 连接断开由 Disconnected 事件处理
            }
        }

        private void RemoveAllViewers()
        {
            foreach (var id in _viewers.Keys.ToList())
            {
                RemoveViewer(id, null);
            }
        }

        private void OnRelayDisconnected()
        {
            OnStatusChanged("与中继服务器断开连接");
            IsRegistered = false;
            RemoveAllViewers();
            StopCapture();
            _relayConnection?.Dispose();
            _relayConnection = null;
            _isRunning = false;
            ClientDisconnected?.Invoke();
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(status);
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();
            RemoveAllViewers();
            StopCapture();
            _relayConnection?.Dispose();
            _relayConnection = null;
            _fileTransferManager.CancelAllTransfers();

            OnStatusChanged("服务已停止");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _screenCapture.Dispose();
            _cts?.Dispose();
        }
    }
}
