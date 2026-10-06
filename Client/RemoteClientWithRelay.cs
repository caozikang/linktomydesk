using System;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using RemoteControl.Common;
using RemoteControl.Media;

namespace RemoteControl.Client
{
    /// <summary>
    /// 远程客户端 - 支持直连和中继模式
    /// </summary>
    public class RemoteClientWithRelay : IDisposable
    {
        private NetworkConnection? _connection;
        private Timer? _heartbeatTimer;
        private volatile bool _disposed;
        private int _frameRate = 30;
        private bool _useRelay;

        /// <summary>解码出新画面（在网络线程触发，画面从 Frames 取）</summary>
        public event Action? FrameReady;

        /// <summary>解码后的画面，界面线程用 TakeLatest 取最新一帧</summary>
        public FrameBuffer Frames { get; } = new();

        private readonly object _decoderLock = new();
        private H264Decoder? _decoder;
        private bool _waitingForKeyFrame = true;
        private long _lastKeyRequestTick;
        /// <summary>在线访问者列表变化（网络线程触发）</summary>
        public event Action<ViewerListMessage>? ViewerListReceived;

        /// <summary>别人（或自己）的操作动态（网络线程触发）</summary>
        public event Action<InputActivityMessage>? ActivityReceived;

        /// <summary>显示给其他访问者的名字，连接前设置</summary>
        public string NickName { get; set; } = "";

        /// <summary>被控端分配给自己的访问者 ID（收到列表后才有值）</summary>
        public int MyViewerId { get; private set; }

        public event Action<SystemInfoMessage>? SystemInfoReceived;
        public event Action<FileListResponseMessage>? FileListReceived;
        public event Action<string>? StatusChanged;
        public event Action? Connected;
        public event Action? Disconnected;

        // 连接统计（悬浮工具栏显示延迟和速率）
        private long _bytesReceived;
        private long _heartbeatSentTick;

        /// <summary>累计收到的字节数</summary>
        public long BytesReceived => System.Threading.Interlocked.Read(ref _bytesReceived);

        /// <summary>最近一次心跳往返时间（经中继时含中继转发），-1 表示还没测到</summary>
        public int LatencyMs { get; private set; } = -1;

        public bool IsConnected => _connection?.IsConnected ?? false;
        public int RemoteScreenWidth { get; private set; }
        public int RemoteScreenHeight { get; private set; }
        public Quality CurrentQuality { get; private set; } = Quality.High;
        public ScaleMode CurrentScaleMode { get; private set; } = ScaleMode.Fit;
        public bool UseRelay => _useRelay;

        /// <summary>对方计算机名（认证成功后才有值）</summary>
        public string RemoteDeviceName { get; private set; } = "";

        /// <summary>
        /// 直连模式连接
        /// </summary>
        public async Task<bool> ConnectDirectAsync(string host, int port, string password)
        {
            try
            {
                _useRelay = false;
                OnStatusChanged($"正在连接到 {host}:{port}... (直连模式)");

                _connection = await NetworkConnection.ConnectAsync(host, port, 5000);
                if (_connection == null)
                {
                    OnStatusChanged($"无法连接 {host}:{port}，请确认被控端已启动直连模式且防火墙放行该端口");
                    return false;
                }
                if (_disposed)
                {
                    // 连接过程中用户点了断开
                    _connection.Dispose();
                    _connection = null;
                    return false;
                }

                _connection.PacketReceived += OnPacketReceived;
                _connection.Disconnected += OnDisconnected;
                _connection.Start();

                // 发送连接请求
                var connectMsg = new ConnectMessage
                {
                    DeviceId = Environment.MachineName,
                    DeviceName = Environment.MachineName,
                    NickName = NickName,
                    UserName = Environment.UserName,
                    Password = password
                };

                await _connection.SendPacketAsync(new Packet
                {
                    Type = MessageType.Connect,
                    Data = connectMsg.Serialize()
                });

                OnStatusChanged("等待服务端响应...");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged($"连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 中继模式连接
        /// </summary>
        public async Task<bool> ConnectRelayAsync(string relayHost, int relayPort, string deviceId, string password)
        {
            try
            {
                _useRelay = true;
                OnStatusChanged($"正在连接到中继服务器 {relayHost}:{relayPort}...");

                _connection = await NetworkConnection.ConnectAsync(relayHost, relayPort, 10000);
                if (_connection == null)
                {
                    OnStatusChanged($"连接中继服务器失败，请检查地址和端口，以及服务器防火墙是否放行 {relayPort}");
                    return false;
                }
                if (_disposed)
                {
                    // 连接过程中用户点了断开
                    _connection.Dispose();
                    _connection = null;
                    return false;
                }

                _connection.PacketReceived += OnPacketReceived;
                _connection.Disconnected += OnDisconnected;
                _connection.Start();

                OnStatusChanged($"已连接到中继服务器，正在连接设备 {deviceId}...");

                // 发送连接请求（通过中继）
                var connectMsg = new ConnectMessage
                {
                    DeviceId = deviceId,
                    DeviceName = Environment.MachineName,
                    NickName = NickName,
                    UserName = "CLIENT:" + Environment.UserName,
                    Password = password
                };

                await _connection.SendPacketAsync(new Packet
                {
                    Type = MessageType.Connect,
                    Data = connectMsg.Serialize()
                });

                OnStatusChanged("等待服务端响应...");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged($"连接失败: {ex.Message}");
                return false;
            }
        }

        private void OnPacketReceived(Packet packet)
        {
            System.Threading.Interlocked.Add(ref _bytesReceived, packet.Data.Length + 5);

            try
            {
                switch (packet.Type)
                {
                    case MessageType.ConnectResponse:
                        HandleConnectResponse(packet.Data);
                        break;

                    case MessageType.VideoFrame:
                        HandleVideoFrame(packet.Data);
                        break;

                    case MessageType.SystemInfo:
                        HandleSystemInfo(packet.Data);
                        break;

                    case MessageType.FileListResponse:
                        HandleFileListResponse(packet.Data);
                        break;

                    case MessageType.FileData:
                        HandleFileData(packet.Data);
                        break;

                    case MessageType.FileComplete:
                        HandleFileComplete(packet.Data);
                        break;

                    case MessageType.ViewerList:
                        HandleViewerList(packet.Data);
                        break;

                    case MessageType.InputActivity:
                        HandleInputActivity(packet.Data);
                        break;

                    case MessageType.FileTransferError:
                        HandleFileTransferError(packet.Data);
                        break;

                    case MessageType.MonitorList:
                        {
                            var msg = MonitorListMessage.Deserialize(packet.Data);
                            if (msg != null) MonitorListReceived?.Invoke(msg);
                        }
                        break;

                    case MessageType.Annotation:
                        {
                            var msg = AnnotationMessage.Deserialize(packet.Data);
                            if (msg != null) AnnotationReceived?.Invoke(msg);
                        }
                        break;

                    case MessageType.SasResult:
                        SasResultReceived?.Invoke(System.Text.Encoding.UTF8.GetString(packet.Data));
                        break;

                    case MessageType.PendingApproval:
                        OnStatusChanged("密码正确，等待对方在被控端点击「允许」…");
                        break;

                    case MessageType.Heartbeat:
                        // 被控端对每个心跳都会回一个，据此算往返延迟
                        long sent = System.Threading.Interlocked.Exchange(ref _heartbeatSentTick, 0);
                        if (sent != 0)
                        {
                            LatencyMs = (int)(Environment.TickCount64 - sent);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged($"处理消息失败: {ex.Message}");
            }
        }

        private void HandleConnectResponse(byte[] data)
        {
            var msg = ConnectMessage.Deserialize(data);
            if (msg == null) return;

            // 只认服务端的认证结果（Password 字段 OK/FAIL），中继只会回 ERROR
            if (msg.Password == "OK")
            {
                RemoteDeviceName = msg.DeviceName;
                string mode = _useRelay ? "中继模式" : "直连模式";
                OnStatusChanged($"连接成功! 远程设备: {msg.DeviceName} ({mode})");
                // 画面由被控端主动推送，这里只需告诉它帧率上限
                _ = SendFrameRateAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                StartHeartbeat();
                Connected?.Invoke();
            }
            else if (msg.UserName == "ERROR")
            {
                FailConnection($"连接失败: {msg.Password}");
            }
            else if (msg.Password == "FULL")
            {
                FailConnection("连接失败: 访问人数已满");
            }
            else if (msg.Password == "DENIED")
            {
                FailConnection("对方拒绝了连接请求");
            }
            else
            {
                FailConnection("认证失败，密码错误");
            }
        }

        private void HandleViewerList(byte[] data)
        {
            var msg = ViewerListMessage.Deserialize(data);
            if (msg == null) return;
            MyViewerId = msg.YourId;
            ViewerListReceived?.Invoke(msg);
        }

        private void HandleInputActivity(byte[] data)
        {
            var msg = InputActivityMessage.Deserialize(data);
            if (msg != null)
            {
                ActivityReceived?.Invoke(msg);
            }
        }

        private void HandleVideoFrame(byte[] data)
        {
            try
            {
                var msg = VideoFrameMessage.Deserialize(data);
                if (msg == null) return;

                RemoteScreenWidth = msg.Width;
                RemoteScreenHeight = msg.Height;

                // 出错后丢弃 P 帧，直到关键帧到来，否则会花屏
                if (_waitingForKeyFrame && !msg.IsKeyFrame)
                {
                    RequestKeyFrame();
                    return;
                }

                try
                {
                    bool decoded;
                    lock (_decoderLock) // Dispose 可能在界面线程同时发生
                    {
                        if (_disposed) return;
                        _decoder ??= new H264Decoder();

                        // 在网络线程解码，不占 UI 线程
                        decoded = _decoder.Decode(msg.Data, Frames);
                    }

                    if (decoded)
                    {
                        RecordFrame(msg);
                        _waitingForKeyFrame = false;
                        FrameReady?.Invoke();
                    }
                }
                catch (FFmpegException ex)
                {
                    OnStatusChanged($"画面解码出错，正在重新同步: {ex.Message}");
                    lock (_decoderLock)
                    {
                        _decoder?.Flush();
                    }
                    _waitingForKeyFrame = true;
                    RequestKeyFrame();
                }
                catch (DllNotFoundException ex)
                {
                    OnStatusChanged(ex.Message);
                }
            }
            finally
            {
                // 每一帧都要回执（包括丢弃和解码失败的），服务端按帧计数做流控。
                // 解码完就回执，不等界面刷新：界面只显示最新帧，慢了会自动跳帧
                AckFrame();
            }
        }

        private void RequestKeyFrame()
        {
            // 关键帧比较大，限制请求频率，避免在慢链路上连续请求把带宽占满
            long now = Environment.TickCount64;
            if (now - _lastKeyRequestTick < 1000) return;
            _lastKeyRequestTick = now;
            _ = SafeSendAsync(MessageType.RequestKeyFrame);
        }

        private void HandleSystemInfo(byte[] data)
        {
            var msg = SystemInfoMessage.Deserialize(data);
            if (msg != null)
            {
                RemoteScreenWidth = msg.ScreenWidth;
                RemoteScreenHeight = msg.ScreenHeight;
                SystemInfoReceived?.Invoke(msg);
            }
        }

        private void HandleFileListResponse(byte[] data)
        {
            var msg = FileListResponseMessage.Deserialize(data);
            if (msg != null)
            {
                FileListReceived?.Invoke(msg);
            }
        }

        #region 文件传输

        private const int FileChunkSize = 64 * 1024;

        /// <summary>一个正在进行的传输（下载写本地文件，上传读本地文件）</summary>
        public sealed class FileTransfer
        {
            public string Id { get; init; } = "";
            public bool IsUpload { get; init; }
            public string LocalPath { get; init; } = "";
            public string RemotePath { get; init; } = "";
            public long TotalBytes { get; init; }
            public long DoneBytes;
            public string? Error;
            public bool Finished;
            internal FileStream? Stream;
            internal readonly System.Threading.CancellationTokenSource Cts = new();
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileTransfer> _transfers = new();

        /// <summary>传输进度变化（网络线程触发，界面自己节流刷新）</summary>
        public event Action<FileTransfer>? TransferProgress;

        /// <summary>传输结束：成功时 Error 为 null</summary>
        public event Action<FileTransfer>? TransferFinished;

        /// <summary>
        /// 下载远程文件到本地。服务端按 64KB 一块推送，最后发 FileComplete
        /// </summary>
        public async Task<FileTransfer?> DownloadFileAsync(string remotePath, long size, string localPath)
        {
            var connection = _connection;
            if (connection == null) return null;

            var t = new FileTransfer
            {
                Id = Guid.NewGuid().ToString("N"),
                LocalPath = localPath,
                RemotePath = remotePath,
                TotalBytes = size
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                t.Stream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex)
            {
                t.Error = $"无法创建本地文件: {ex.Message}";
                FinishTransfer(t, deleteLocal: false);
                return t;
            }

            _transfers[t.Id] = t;
            var msg = new FileTransferRequestMessage { TransferId = t.Id, FilePath = remotePath, FileSize = size };
            try
            {
                await connection.SendPacketAsync(new Packet { Type = MessageType.FileDownloadRequest, Data = msg.Serialize() });
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
                FinishTransfer(t, deleteLocal: true);
            }
            return t;
        }

        /// <summary>
        /// 上传本地文件到远程路径。等每块写进 TCP 再发下一块，慢链路上不会把画面挤掉太多
        /// </summary>
        public FileTransfer? UploadFile(string localPath, string remotePath)
        {
            var connection = _connection;
            if (connection == null) return null;

            long size;
            try
            {
                size = new System.IO.FileInfo(localPath).Length;
            }
            catch (Exception ex)
            {
                OnStatusChanged($"无法读取文件: {ex.Message}");
                return null;
            }

            var t = new FileTransfer
            {
                Id = Guid.NewGuid().ToString("N"),
                IsUpload = true,
                LocalPath = localPath,
                RemotePath = remotePath,
                TotalBytes = size
            };
            _transfers[t.Id] = t;
            _ = Task.Run(() => RunUploadAsync(connection, t));
            return t;
        }

        private async Task RunUploadAsync(NetworkConnection connection, FileTransfer t)
        {
            try
            {
                var request = new FileTransferRequestMessage { TransferId = t.Id, FilePath = t.RemotePath, FileSize = t.TotalBytes };
                await connection.SendPacketAsync(new Packet { Type = MessageType.FileUploadRequest, Data = request.Serialize() });

                using var fs = new FileStream(t.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[FileChunkSize];
                long offset = 0;
                int read;
                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, t.Cts.Token)) > 0)
                {
                    if (t.Error != null) return; // 服务端报错，已在 HandleFileTransferError 里结束

                    var chunk = new FileDataMessage { TransferId = t.Id, Offset = offset, Data = buffer.AsSpan(0, read).ToArray() };
                    await connection.SendPacketAsync(new Packet { Type = MessageType.FileData, Data = chunk.Serialize() });
                    offset += read;
                    t.DoneBytes = offset;
                    TransferProgress?.Invoke(t);
                }

                await connection.SendPacketAsync(new Packet
                {
                    Type = MessageType.FileUploadComplete,
                    Data = System.Text.Encoding.UTF8.GetBytes(t.Id)
                });
                FinishTransfer(t, deleteLocal: false);
            }
            catch (OperationCanceledException)
            {
                t.Error ??= "已取消";
                FinishTransfer(t, deleteLocal: false);
            }
            catch (Exception ex)
            {
                t.Error ??= ex.Message;
                FinishTransfer(t, deleteLocal: false);
            }
        }

        /// <summary>取消传输。下载会删除写了一半的本地文件</summary>
        public void CancelTransfer(string id)
        {
            if (!_transfers.TryGetValue(id, out var t)) return;
            t.Error ??= "已取消";
            t.Cts.Cancel();
            var error = new FileTransferErrorMessage { TransferId = id, Error = "已取消" };
            _ = SafeSendAsync(MessageType.FileTransferError, error.Serialize());
            if (!t.IsUpload) FinishTransfer(t, deleteLocal: true);
        }

        private void FinishTransfer(FileTransfer t, bool deleteLocal)
        {
            if (t.Finished) return;
            t.Finished = true;
            _transfers.TryRemove(t.Id, out _);

            try
            {
                t.Stream?.Dispose();
                if (deleteLocal && !t.IsUpload && File.Exists(t.LocalPath)) File.Delete(t.LocalPath);
            }
            catch
            {
                // 删除失败就留着
            }
            t.Stream = null;
            TransferFinished?.Invoke(t);
        }

        private void HandleFileData(byte[] data)
        {
            var msg = FileDataMessage.Deserialize(data);
            if (msg == null || !_transfers.TryGetValue(msg.TransferId, out var t) || t.Stream == null) return;

            try
            {
                // 包按顺序到达，直接顺序写
                if (t.Stream.Position != msg.Offset) t.Stream.Position = msg.Offset;
                t.Stream.Write(msg.Data, 0, msg.Data.Length);
                t.DoneBytes = msg.Offset + msg.Data.Length;
                TransferProgress?.Invoke(t);
            }
            catch (Exception ex)
            {
                t.Error = $"写入本地文件失败: {ex.Message}";
                CancelTransfer(t.Id);
                FinishTransfer(t, deleteLocal: true);
            }
        }

        private void HandleFileComplete(byte[] data)
        {
            string transferId = System.Text.Encoding.UTF8.GetString(data);
            if (_transfers.TryGetValue(transferId, out var t))
            {
                FinishTransfer(t, deleteLocal: false);
            }
        }

        private void HandleFileTransferError(byte[] data)
        {
            var msg = FileTransferErrorMessage.Deserialize(data);
            if (msg == null || !_transfers.TryGetValue(msg.TransferId, out var t)) return;
            t.Error ??= msg.Error;
            t.Cts.Cancel();
            FinishTransfer(t, deleteLocal: !t.IsUpload);
        }

        private void CancelAllTransfers()
        {
            foreach (var t in _transfers.Values)
            {
                t.Error ??= "连接已断开";
                t.Cts.Cancel();
                FinishTransfer(t, deleteLocal: !t.IsUpload);
            }
        }

        #endregion

        #region 系统操作 / 多显示器 / 标注

        public event Action<MonitorListMessage>? MonitorListReceived;
        public event Action<AnnotationMessage>? AnnotationReceived;

        /// <summary>Ctrl+Alt+Del 的执行结果：空串表示成功</summary>
        public event Action<string>? SasResultReceived;

        public Task SendSasAsync() => SafeSendAsync(MessageType.SendSas, Array.Empty<byte>());

        public Task SelectMonitorAsync(int index) => SafeSendAsync(MessageType.SelectMonitor, BitConverter.GetBytes(index));

        public Task SendAnnotationAsync(AnnotationMessage msg) => SafeSendAsync(MessageType.Annotation, msg.Serialize());

        /// <summary>会话录制：把收到的 H.264 码流直接封装成 MP4，不重新编码</summary>
        private readonly object _recorderLock = new();
        private Mp4Recorder? _recorder;

        public bool IsRecording
        {
            get { lock (_recorderLock) return _recorder != null; }
        }

        /// <summary>开始录制，从下一个关键帧开始写（请求一次关键帧）</summary>
        public void StartRecording(string path)
        {
            lock (_recorderLock)
            {
                _recorder?.Dispose();
                _recorder = new Mp4Recorder(path);
            }
            _lastKeyRequestTick = 0;
            RequestKeyFrame();
        }

        /// <summary>停止录制，返回文件路径（没有在录制返回 null）</summary>
        public string? StopRecording()
        {
            lock (_recorderLock)
            {
                var r = _recorder;
                _recorder = null;
                if (r == null) return null;
                r.Dispose();
                return r.Path;
            }
        }

        private void RecordFrame(VideoFrameMessage msg)
        {
            lock (_recorderLock)
            {
                if (_recorder == null) return;
                try
                {
                    int w, h;
                    lock (_decoderLock)
                    {
                        w = _decoder?.Width ?? 0;
                        h = _decoder?.Height ?? 0;
                    }
                    _recorder.Write(msg.Data, msg.IsKeyFrame, w, h, msg.Timestamp);
                }
                catch (Exception ex)
                {
                    OnStatusChanged($"录制出错，已停止: {ex.Message}");
                    _recorder.Dispose();
                    _recorder = null;
                }
            }
        }

        #endregion

        /// <summary>
        /// 界面显示完一帧后调用，通知被控端可以发下一帧（流控，见服务端 StreamLoop）
        /// </summary>
        public void AckFrame()
        {
            _ = SafeSendAsync(MessageType.FrameAck);
        }

        private Task SendFrameRateAsync()
        {
            var connection = _connection;
            if (connection == null) return Task.CompletedTask;

            return connection.SendPacketAsync(new Packet
            {
                Type = MessageType.ChangeFrameRate,
                Data = BitConverter.GetBytes(_frameRate)
            });
        }

        private void StartHeartbeat()
        {
            // 2 秒一次：心跳包只有 5 字节，顺便用来测延迟
            _heartbeatTimer = new Timer(2000);
            _heartbeatTimer.Elapsed += async (s, e) => await SendHeartbeatAsync();
            _heartbeatTimer.Start();
            _ = SendHeartbeatAsync();
        }

        private Task SendHeartbeatAsync()
        {
            // 上一个还没回来就不重新计时，免得延迟被算小
            System.Threading.Interlocked.CompareExchange(ref _heartbeatSentTick, Environment.TickCount64, 0);
            return SafeSendAsync(MessageType.Heartbeat);
        }

        public async Task SendMouseMoveAsync(int x, int y)
        {
            if (_connection == null) return;

            var msg = new MouseMessage { X = x, Y = y };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.MouseMove,
                Data = msg.Serialize()
            });
        }

        public async Task SendMouseClickAsync(int x, int y, MouseButton button, MouseEventType eventType)
        {
            if (_connection == null) return;

            var msg = new MouseMessage { X = x, Y = y, Button = button, EventType = eventType };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.MouseClick,
                Data = msg.Serialize()
            });
        }

        public async Task SendMouseWheelAsync(int delta)
        {
            if (_connection == null) return;

            var msg = new MouseWheelMessage { Delta = delta };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.MouseWheel,
                Data = msg.Serialize()
            });
        }

        public async Task SendKeyPressAsync(int keyCode, bool isDown)
        {
            if (_connection == null) return;

            var msg = new KeyMessage { KeyCode = keyCode, IsDown = isDown };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.KeyPress,
                Data = msg.Serialize()
            });
        }

        public async Task RequestFileListAsync(string path)
        {
            if (_connection == null) return;

            var msg = new FileListRequestMessage { Path = path };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.FileListRequest,
                Data = msg.Serialize()
            });
        }

        public async Task SetQualityAsync(Quality quality)
        {
            if (_connection == null) return;

            CurrentQuality = quality;
            var msg = new QualityMessage { Quality = quality };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.ChangeQuality,
                Data = msg.Serialize()
            });

            OnStatusChanged($"画质已设置为: {quality}");
        }

        public async Task SetScaleModeAsync(ScaleMode mode, float customScale = 1.0f)
        {
            if (_connection == null) return;

            CurrentScaleMode = mode;
            var msg = new ScaleMessage { Mode = mode, CustomScale = customScale };
            await _connection.SendPacketAsync(new Packet
            {
                Type = MessageType.ChangeScale,
                Data = msg.Serialize()
            });

            OnStatusChanged($"缩放模式已设置为: {mode}");
        }

        public async Task SetFrameRateAsync(int fps)
        {
            _frameRate = Math.Clamp(fps, 1, 60);
            try
            {
                await SendFrameRateAsync();
                OnStatusChanged($"帧率上限已设置为: {_frameRate} FPS");
            }
            catch
            {
                // 连接断开由 Disconnected 事件处理
            }
        }

        private void OnDisconnected()
        {
            string mode = _useRelay ? "中继" : "直连";
            OnStatusChanged($"连接已断开 ({mode}模式)");
            _heartbeatTimer?.Stop();
            CancelAllTransfers();
            Disconnected?.Invoke();
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(status);
        }

        public void Disconnect()
        {
            _heartbeatTimer?.Stop();

            var connection = _connection;
            _connection = null;
            if (connection == null) return;

            try
            {
                // 连接可能已经断了，发不出去就算了，最多等 1 秒
                connection.SendPacketAsync(new Packet
                {
                    Type = MessageType.Disconnect,
                    Data = Array.Empty<byte>()
                }).Wait(1000);
            }
            catch
            {
                // 忽略
            }

            connection.Dispose();
        }

        /// <summary>
        /// 认证失败 / 设备不在线：通知界面复位，再关闭连接
        /// （先 Dispose 的话 NetworkConnection 不会再触发 Disconnected，界面会卡在"连接中"）
        /// </summary>
        private void FailConnection(string reason)
        {
            OnStatusChanged(reason);
            Disconnected?.Invoke();
            Disconnect();
        }

        /// <summary>
        /// 定时器回调是 async void，异常会直接让进程崩溃，这里统一吞掉
        /// </summary>
        private Task SafeSendAsync(MessageType type) => SafeSendAsync(type, Array.Empty<byte>());

        private async Task SafeSendAsync(MessageType type, byte[] data)
        {
            var connection = _connection;
            if (connection == null) return;

            try
            {
                await connection.SendPacketAsync(new Packet { Type = type, Data = data });
            }
            catch
            {
                // 连接断开由 Disconnected 事件处理
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
            _heartbeatTimer?.Dispose();
            CancelAllTransfers();
            StopRecording();

            lock (_decoderLock)
            {
                _decoder?.Dispose();
                _decoder = null;
            }
        }
    }
}
