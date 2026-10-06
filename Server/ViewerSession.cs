using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    /// <summary>
    /// 一个访问者（控制端）的会话状态。每个访问者有自己的编码器、画质、帧率和流控，
    /// 一个人网速慢只会让他自己降帧，不影响其他人。
    /// </summary>
    internal sealed class ViewerSession : IDisposable
    {
        // 在途数据（已发出未确认）同时限制帧数和字节数：链路慢时自动降帧，
        // 不会在中继里越积越多造成延迟；链路好时允许多帧在途，不受 RTT 限制帧率
        private const int MaxInFlightFrames = 6;
        private const int MaxInFlightBytes = 384 * 1024;
        private const int AckTimeoutMs = 5000;

        private readonly Func<Packet, Task> _send;

        public int Id { get; }
        public string Address { get; }
        public string Name { get; set; } = "";
        public NetworkConnection? Connection { get; }   // 直连模式下自己的连接；中继模式为 null

        public volatile bool Authenticated;
        public volatile bool Removed;

        // 推流设置（接收线程写，推流线程读）
        public volatile Quality Quality = Quality.Medium;
        public volatile int FrameRate = 30;
        public volatile bool EncoderDirty;
        public volatile bool KeyFrameRequested = true;


        // 密码正确，正在等被控端点"允许"
        public volatile bool AwaitingApproval;

        // 输入状态，持有 RemoteServer 的输入锁时访问。离开时要把按住的键和鼠标键松开，避免卡键
        public readonly HashSet<int> HeldKeys = new();
        public readonly HashSet<MouseButton> HeldButtons = new();

        /// <summary>收到回执时触发，推流线程据此醒来发下一帧</summary>
        public readonly AutoResetEvent AckEvent = new(false);

        private readonly object _flowLock = new();
        private readonly Queue<int> _inFlight = new();
        private int _inFlightBytes;
        private long _oldestSendTick;

        public ViewerSession(int id, string address, Func<Packet, Task> send, NetworkConnection? connection)
        {
            Id = id;
            Address = address;
            _send = send;
            Connection = connection;
        }

        public Task SendAsync(Packet packet) => _send(packet);

        /// <summary>
        /// 能否再发一帧。回执超时（丢了或对方卡死）就清零重来，并要求下一帧是关键帧
        /// </summary>
        public bool TryOpenWindow()
        {
            lock (_flowLock)
            {
                if (_inFlight.Count < MaxInFlightFrames && _inFlightBytes < MaxInFlightBytes)
                {
                    return true;
                }

                if (Environment.TickCount64 - _oldestSendTick > AckTimeoutMs)
                {
                    _inFlight.Clear();
                    _inFlightBytes = 0;
                    KeyFrameRequested = true;
                    return true;
                }

                return false;
            }
        }

        /// <summary>先记账再发送，否则回执可能比记账先到</summary>
        public void OnFrameSending(int bytes)
        {
            lock (_flowLock)
            {
                if (_inFlight.Count == 0) _oldestSendTick = Environment.TickCount64;
                _inFlight.Enqueue(bytes);
                _inFlightBytes += bytes;
            }
        }

        /// <summary>回执按发送顺序到达，每个回执对应最早的一帧</summary>
        public void OnAck()
        {
            lock (_flowLock)
            {
                if (_inFlight.Count > 0)
                {
                    _inFlightBytes -= _inFlight.Dequeue();
                }
                _oldestSendTick = Environment.TickCount64;
            }
            AckEvent.Set();
        }

        public ViewerInfo ToInfo() => new() { Id = Id, Name = Name, Address = Address };

        /// <summary>
        /// 标记离开并关闭连接。编码器归推流线程所有，由它看到 Removed 后自己释放
        /// </summary>
        public void Dispose()
        {
            Removed = true;
            Authenticated = false;
            AckEvent.Set(); // 叫醒推流线程让它退出
            Connection?.Dispose();
        }
    }
}
