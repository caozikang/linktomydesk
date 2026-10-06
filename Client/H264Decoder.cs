using System;
using FFmpeg.AutoGen;
using RemoteControl.Media;

namespace RemoteControl.Client
{
    /// <summary>
    /// H.264 解码 + 转 BGRA。按低延迟配置：来一帧解一帧，不做帧重排缓冲。
    /// 非线程安全，只在网络接收线程里使用。
    /// </summary>
    public sealed unsafe class H264Decoder : IDisposable
    {
        private AVCodecContext* _ctx;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _sws;
        private int _swsWidth, _swsHeight;
        private AVPixelFormat _swsFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private bool _disposed;
        private byte[] _input = Array.Empty<byte>();

        public H264Decoder()
        {
            FFmpegSetup.Initialize();

            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                throw new InvalidOperationException("FFmpeg 中没有 H.264 解码器");
            }

            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            // 切片多线程不会引入额外的帧延迟（帧多线程会缓冲若干帧）
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
            _ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 8);

            FFmpegSetup.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "打开 H.264 解码器");

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
        }

        /// <summary>
        /// 解码一段 Annex-B 码流。解出画面时写入 target 并返回 true。
        /// </summary>
        public bool Decode(byte[] data, FrameBuffer target)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));

            // FFmpeg 解码器会越界读取输入末尾最多 AV_INPUT_BUFFER_PADDING_SIZE 字节，必须补零
            int needed = data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
            if (_input.Length < needed)
            {
                _input = new byte[needed * 2];
            }
            Buffer.BlockCopy(data, 0, _input, 0, data.Length);
            Array.Clear(_input, data.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);

            bool gotFrame = false;
            fixed (byte* p = _input)
            {
                _packet->data = p;
                _packet->size = data.Length;
                int ret = ffmpeg.avcodec_send_packet(_ctx, _packet);
                _packet->data = null;
                _packet->size = 0;
                FFmpegSetup.Check(ret, "解码");
            }

            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (ret == FFmpegSetup.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF) break;
                FFmpegSetup.Check(ret, "读取解码画面");

                try
                {
                    if ((_frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0)
                    {
                        throw new FFmpegException("画面数据损坏", 0);
                    }

                    ConvertToBgra(target);
                    gotFrame = true;
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }

            return gotFrame;
        }

        /// <summary>最近一次解出的画面尺寸（编码分辨率，可能比被控端屏幕小）</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        private void ConvertToBgra(FrameBuffer target)
        {
            int w = _frame->width, h = _frame->height;
            Width = w;
            Height = h;
            var format = (AVPixelFormat)_frame->format;

            if (_sws == null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
            {
                if (_sws != null) ffmpeg.sws_freeContext(_sws);
                // 分辨率不变，只做颜色转换，用最快的算法即可
                _sws = ffmpeg.sws_getContext(w, h, format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_FAST_BILINEAR, null, null, null);
                if (_sws == null)
                {
                    throw new InvalidOperationException("创建颜色转换上下文失败");
                }
                _swsWidth = w;
                _swsHeight = h;
                _swsFormat = format;
            }

            byte[] pixels = target.BeginWrite(w, h);
            int stride = w * 4;

            fixed (byte* dst = pixels)
            {
                var srcData = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3] };
                var srcStride = new[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3] };
                var dstData = new byte*[] { dst, null, null, null };
                var dstStride = new[] { stride, 0, 0, 0 };
                ffmpeg.sws_scale(_sws, srcData, srcStride, 0, h, dstData, dstStride);
            }

            target.EndWrite();
        }

        /// <summary>丢弃解码器内部状态（出错后等下一个关键帧时调用）</summary>
        public void Flush()
        {
            if (!_disposed) ffmpeg.avcodec_flush_buffers(_ctx);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;

            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;

            var ctx = _ctx;
            ffmpeg.avcodec_free_context(&ctx);
            _ctx = null;
        }
    }

    /// <summary>
    /// 三缓冲：解码线程写后台缓冲，界面线程读前台缓冲，中间是"最新一帧"。
    /// 界面来不及显示时自动只显示最新帧，两边都不用等对方，也不用每帧分配 8MB 数组。
    /// </summary>
    public sealed class FrameBuffer
    {
        private readonly object _lock = new();
        private Frame _back = new();
        private Frame _pending = new();
        private Frame _front = new();
        private bool _hasPending;

        public sealed class Frame
        {
            public byte[] Pixels = Array.Empty<byte>();
            public int Width;
            public int Height;
            public int Stride => Width * 4;
        }

        /// <summary>解码线程：取得写入缓冲区</summary>
        public byte[] BeginWrite(int width, int height)
        {
            int size = width * height * 4;
            if (_back.Pixels.Length != size)
            {
                _back.Pixels = new byte[size];
            }
            _back.Width = width;
            _back.Height = height;
            return _back.Pixels;
        }

        /// <summary>解码线程：写完，发布为最新帧</summary>
        public void EndWrite()
        {
            lock (_lock)
            {
                (_back, _pending) = (_pending, _back);
                _hasPending = true;
            }
        }

        /// <summary>界面线程：取最新帧，没有新帧返回 null。返回的对象在下次调用前有效</summary>
        public Frame? TakeLatest()
        {
            lock (_lock)
            {
                if (!_hasPending) return null;
                (_front, _pending) = (_pending, _front);
                _hasPending = false;
                return _front;
            }
        }
    }
}
