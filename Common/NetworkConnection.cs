using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteControl.Common
{
    public class NetworkConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public event Action<Packet>? PacketReceived;
        public event Action? Disconnected;

        public bool IsConnected => _client.Connected;
        public string RemoteEndPoint => _client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

        public NetworkConnection(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _client.NoDelay = true; // 关闭 Nagle，降低鼠标键盘延迟
            _stream = _client.GetStream();
        }

        private int _started;

        /// <summary>
        /// 开始接收数据。必须在订阅 PacketReceived / Disconnected 之后调用，否则首包可能丢失。
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public static async Task<NetworkConnection?> ConnectAsync(string host, int port, int timeoutMs = 5000)
        {
            try
            {
                var client = new TcpClient();
                using var cts = new CancellationTokenSource(timeoutMs);
                await client.ConnectAsync(host, port, cts.Token);
                return new NetworkConnection(client);
            }
            catch
            {
                return null;
            }
        }

        public async Task SendPacketAsync(Packet packet)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetworkConnection));

            byte[] data = packet.Serialize();

            // ConfigureAwait(false)：UI 线程同步等待发送时不会死锁
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] lengthBuffer = new byte[5]; // 1 byte type + 4 bytes length
            byte[] dataBuffer = new byte[10 * 1024 * 1024]; // 10MB buffer

            try
            {
                while (!ct.IsCancellationRequested && _client.Connected)
                {
                    // 读取消息头（类型 + 长度）
                    int totalRead = 0;
                    while (totalRead < 5)
                    {
                        int read = await _stream.ReadAsync(lengthBuffer, totalRead, 5 - totalRead, ct);
                        if (read == 0)
                        {
                            OnDisconnected();
                            return;
                        }
                        totalRead += read;
                    }

                    // 解析长度
                    int dataLength = BitConverter.ToInt32(lengthBuffer, 1);
                    if (dataLength < 0 || dataLength > dataBuffer.Length)
                    {
                        OnDisconnected();
                        return;
                    }

                    // 读取完整数据包
                    totalRead = 0;
                    while (totalRead < dataLength)
                    {
                        int read = await _stream.ReadAsync(dataBuffer, totalRead, dataLength - totalRead, ct);
                        if (read == 0)
                        {
                            OnDisconnected();
                            return;
                        }
                        totalRead += read;
                    }

                    // 构造完整包并反序列化
                    byte[] fullPacket = new byte[5 + dataLength];
                    Array.Copy(lengthBuffer, 0, fullPacket, 0, 5);
                    Array.Copy(dataBuffer, 0, fullPacket, 5, dataLength);

                    var packet = Packet.Deserialize(fullPacket, 0, fullPacket.Length);
                    if (packet != null)
                    {
                        PacketReceived?.Invoke(packet);
                    }
                }
            }
            catch (Exception)
            {
                OnDisconnected();
            }
        }

        private void OnDisconnected()
        {
            if (!_disposed)
            {
                Disconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _stream.Dispose();
            _client.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }
    }
}
