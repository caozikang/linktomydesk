using System;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media.Imaging;
using RemoteControl.Common;

namespace RemoteControl.Client
{
    public class RemoteClient : IDisposable
    {
        private NetworkConnection? _connection;
        private Timer? _screenRequestTimer;
        private Timer? _heartbeatTimer;
        private bool _disposed;
        private int _frameRate = 30; // 默认30帧

        public event Action<BitmapImage>? ScreenDataReceived;
        public event Action<SystemInfoMessage>? SystemInfoReceived;
        public event Action<FileListResponseMessage>? FileListReceived;
        public event Action<string>? StatusChanged;
        public event Action? Connected;
        public event Action? Disconnected;

        public bool IsConnected => _connection?.IsConnected ?? false;
        public int RemoteScreenWidth { get; private set; }
        public int RemoteScreenHeight { get; private set; }
        public Quality CurrentQuality { get; private set; } = Quality.High;
        public ScaleMode CurrentScaleMode { get; private set; } = ScaleMode.Fit;

        public async Task<bool> ConnectAsync(string host, int port, string deviceId, string password)
        {
            try
            {
                OnStatusChanged($"正在连接到 {host}:{port}...");

                _connection = await NetworkConnection.ConnectAsync(host, port, 5000);
                if (_connection == null)
                {
                    OnStatusChanged("连接失败");
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

        private void OnPacketReceived(Packet packet)
        {
            try
            {
                switch (packet.Type)
                {
                    case MessageType.ConnectResponse:
                        HandleConnectResponse(packet.Data);
                        break;

                    case MessageType.ScreenData:
                        HandleScreenData(packet.Data);
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

                    case MessageType.Heartbeat:
                        // 心跳响应
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

            if (msg.Password == "OK")
            {
                OnStatusChanged($"连接成功! 远程设备: {msg.DeviceName}");
                StartScreenCapture();
                StartHeartbeat();
                Connected?.Invoke();
            }
            else
            {
                OnStatusChanged("认证失败，密码错误");
                _connection?.Dispose();
            }
        }

        private void HandleScreenData(byte[] data)
        {
            var msg = ScreenDataMessage.Deserialize(data);
            if (msg == null) return;

            RemoteScreenWidth = msg.Width;
            RemoteScreenHeight = msg.Height;

            try
            {
                using var ms = new MemoryStream(msg.ImageData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ScreenDataReceived?.Invoke(bitmap);
            }
            catch (Exception ex)
            {
                OnStatusChanged($"解析屏幕数据失败: {ex.Message}");
            }
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

        private void HandleFileData(byte[] data)
        {
            var msg = FileDataMessage.Deserialize(data);
            if (msg != null)
            {
                // 处理文件数据
                OnStatusChanged($"接收文件数据: {msg.Offset} 字节");
            }
        }

        private void HandleFileComplete(byte[] data)
        {
            string transferId = System.Text.Encoding.UTF8.GetString(data);
            OnStatusChanged($"文件传输完成: {transferId}");
        }

        private void StartScreenCapture()
        {
            _screenRequestTimer = new Timer(1000.0 / _frameRate);
            _screenRequestTimer.Elapsed += async (s, e) =>
            {
                if (_connection != null)
                {
                    await _connection.SendPacketAsync(new Packet
                    {
                        Type = MessageType.ScreenRequest,
                        Data = Array.Empty<byte>()
                    });
                }
            };
            _screenRequestTimer.Start();
        }

        private void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(5000); // 5秒心跳
            _heartbeatTimer.Elapsed += async (s, e) =>
            {
                if (_connection != null)
                {
                    await _connection.SendPacketAsync(new Packet
                    {
                        Type = MessageType.Heartbeat,
                        Data = Array.Empty<byte>()
                    });
                }
            };
            _heartbeatTimer.Start();
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

        public void SetFrameRate(int fps)
        {
            _frameRate = Math.Clamp(fps, 1, 60);
            if (_screenRequestTimer != null)
            {
                _screenRequestTimer.Stop();
                _screenRequestTimer.Interval = 1000.0 / _frameRate;
                _screenRequestTimer.Start();
            }
        }

        private void OnDisconnected()
        {
            OnStatusChanged("连接已断开");
            _screenRequestTimer?.Stop();
            _heartbeatTimer?.Stop();
            Disconnected?.Invoke();
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(status);
        }

        public void Disconnect()
        {
            if (_connection != null)
            {
                _connection.SendPacketAsync(new Packet
                {
                    Type = MessageType.Disconnect,
                    Data = Array.Empty<byte>()
                }).Wait();

                _connection.Dispose();
                _connection = null;
            }

            _screenRequestTimer?.Stop();
            _heartbeatTimer?.Stop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
            _screenRequestTimer?.Dispose();
            _heartbeatTimer?.Dispose();
        }
    }
}
