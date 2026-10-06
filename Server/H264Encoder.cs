using System;
using System.Collections.Generic;
using System.IO;
using FFmpeg.AutoGen;
using RemoteControl.Media;

namespace RemoteControl.Server
{
    /// <summary>
    /// 屏幕画面 H.264 编码器：优先硬件编码（NVIDIA / Intel / AMD），都不可用时退回 x264 软件编码。
    /// 参数按远程桌面场景调成低延迟：无 B 帧、零缓冲、小 VBV，每送一帧立即出一帧。
    /// 非线程安全，只在推流线程里使用。
    /// </summary>
    public sealed unsafe class H264Encoder : IDisposable
    {
        // 按优先级尝试。显卡不存在时 avcodec_open2 会直接失败，开销很小
        private static readonly string[] EncoderNames =
        {
            "h264_nvenc",  // NVIDIA
            "h264_qsv",    // Intel 核显
            "h264_amf",    // AMD
            "libx264",     // 软件编码，无显卡的服务器 / 虚拟机
            // 不用 h264_mf：实测在无 MF 硬件编码的机器上会直接让进程崩溃（0xc0000409），且 libx264 总是可用
        };

        private AVCodecContext* _ctx;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _sws;
        private long _pts;
        private bool _disposed;

        private readonly int _srcWidth;
        private readonly int _srcHeight;

        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public bool IsHardware => Name != "libx264";

        private H264Encoder(string name, AVCodecContext* ctx, int srcWidth, int srcHeight)
        {
            Name = name;
            _ctx = ctx;
            Width = ctx->width;
            Height = ctx->height;
            _srcWidth = srcWidth;
            _srcHeight = srcHeight;

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _frame->width = Width;
            _frame->height = Height;
            FFmpegSetup.Check(ffmpeg.av_frame_get_buffer(_frame, 32), "分配编码帧");

            _packet = ffmpeg.av_packet_alloc();

            // BGRX -> NV12，同时缩放到编码分辨率
            _sws = ffmpeg.sws_getContext(srcWidth, srcHeight, AVPixelFormat.AV_PIX_FMT_BGR0,
                Width, Height, AVPixelFormat.AV_PIX_FMT_NV12,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null)
            {
                throw new InvalidOperationException("创建颜色转换上下文失败");
            }
        }

        /// <summary>
        /// 按优先级创建第一个可用的编码器。skip 里是已经确认不可用的编码器名。
        /// </summary>
        public static H264Encoder Create(int srcWidth, int srcHeight, int width, int height,
            int fps, long bitRate, ISet<string>? skip, Action<string>? log)
        {
            FFmpegSetup.Initialize();

            // H.264 要求宽高为偶数
            width = Math.Max(2, width & ~1);
            height = Math.Max(2, height & ~1);

            var errors = new List<string>();
            foreach (var name in EncoderNames)
            {
                if (skip != null && skip.Contains(name)) continue;

                var codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) continue;

                var ctx = ffmpeg.avcodec_alloc_context3(codec);
                Configure(ctx, name, width, height, fps, bitRate);

                int ret = ffmpeg.avcodec_open2(ctx, codec, null);
                if (ret < 0)
                {
                    errors.Add($"{name}: {FFmpegSetup.ErrorText(ret)}");
                    ffmpeg.avcodec_free_context(&ctx);
                    continue;
                }

                try
                {
                    return new H264Encoder(name, ctx, srcWidth, srcHeight);
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {ex.Message}");
                    ffmpeg.avcodec_free_context(&ctx);
                }
            }

            throw new InvalidOperationException("没有可用的 H.264 编码器 (" + string.Join("; ", errors) + ")");
        }

        private static void Configure(AVCodecContext* ctx, string name, int width, int height, int fps, long bitRate)
        {
            ctx->width = width;
            ctx->height = height;
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            ctx->time_base = new AVRational { num = 1, den = fps };
            ctx->framerate = new AVRational { num = fps, den = 1 };

            ctx->bit_rate = bitRate;
            ctx->rc_max_rate = bitRate;
            // VBV 约 250ms：单帧最大约为 1/4 秒的数据量，慢链路上不会卡太久；
            // 再小的话关键帧（新连接、切画质）会糊得明显，要好几秒才补清楚
            ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitRate / 4);

            // 关键帧只在需要时强制插入（新连接、解码出错），平时靠 P 帧，最省带宽
            ctx->gop_size = fps * 60;
            ctx->max_b_frames = 0;
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            void* opt = ctx->priv_data;
            switch (name)
            {
                case "h264_nvenc":
                    ffmpeg.av_opt_set(opt, "preset", "p1", 0);
                    ffmpeg.av_opt_set(opt, "tune", "ull", 0);
                    ffmpeg.av_opt_set(opt, "rc", "cbr", 0);
                    ffmpeg.av_opt_set(opt, "zerolatency", "1", 0);
                    ffmpeg.av_opt_set(opt, "delay", "0", 0);
                    ffmpeg.av_opt_set(opt, "forced-idr", "1", 0);
                    break;

                case "h264_qsv":
                    ffmpeg.av_opt_set(opt, "preset", "veryfast", 0);
                    ffmpeg.av_opt_set(opt, "async_depth", "1", 0);
                    ffmpeg.av_opt_set(opt, "look_ahead", "0", 0);
                    ffmpeg.av_opt_set(opt, "forced_idr", "1", 0);
                    break;

                case "h264_amf":
                    ffmpeg.av_opt_set(opt, "usage", "ultralowlatency", 0);
                    ffmpeg.av_opt_set(opt, "quality", "speed", 0);
                    ffmpeg.av_opt_set(opt, "rc", "cbr", 0);
                    ffmpeg.av_opt_set(opt, "forced_idr", "1", 0);
                    break;

                case "libx264":
                    ffmpeg.av_opt_set(opt, "preset", "ultrafast", 0);
                    ffmpeg.av_opt_set(opt, "tune", "zerolatency", 0);
                    ffmpeg.av_opt_set(opt, "forced-idr", "1", 0);
                    ctx->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                    break;

                case "h264_mf":
                    ffmpeg.av_opt_set(opt, "rate_control", "cbr", 0);
                    ffmpeg.av_opt_set(opt, "scenario", "display_remoting", 0);
                    ffmpeg.av_opt_set(opt, "hw_encoding", "1", 0);
                    break;
            }
        }

        /// <summary>
        /// 编码一帧 BGRX 像素。返回 Annex-B 码流（含关键帧时的 SPS/PPS），编码器暂时没输出时返回 null。
        /// </summary>
        public byte[]? Encode(byte[] bgrx, int stride, bool forceKeyFrame, out bool isKeyFrame)
        {
            LoadFrame(bgrx, stride);
            return EncodeLoaded(forceKeyFrame, out isKeyFrame);
        }

        /// <summary>
        /// 把 BGRX 像素转换进编码器的输入帧。多人共享截屏缓冲区时，只需在这一步持有读锁
        /// </summary>
        public void LoadFrame(byte[] bgrx, int stride)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Encoder));

            // 编码器可能还引用着上一帧的缓冲区
            FFmpegSetup.Check(ffmpeg.av_frame_make_writable(_frame), "准备编码帧");

            fixed (byte* src = bgrx)
            {
                var srcData = new byte*[] { src, null, null, null };
                var srcStride = new[] { stride, 0, 0, 0 };
                var dstData = new byte*[] { _frame->data[0], _frame->data[1], null, null };
                var dstStride = new[] { _frame->linesize[0], _frame->linesize[1], 0, 0 };
                ffmpeg.sws_scale(_sws, srcData, srcStride, 0, _srcHeight, dstData, dstStride);
            }
        }

        /// <summary>
        /// 编码 LoadFrame 载入的画面（没有新画面时重复编码上一帧，用于静止后补清晰度）
        /// </summary>
        public byte[]? EncodeLoaded(bool forceKeyFrame, out bool isKeyFrame)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(H264Encoder));
            isKeyFrame = false;

            _frame->pts = _pts++;
            _frame->pict_type = forceKeyFrame ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;

            FFmpegSetup.Check(ffmpeg.avcodec_send_frame(_ctx, _frame), "编码");

            // 低延迟配置下一帧进一帧出，这里把能取到的包全部拼起来
            MemoryStream? output = null;
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_packet(_ctx, _packet);
                if (ret == FFmpegSetup.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF) break;
                FFmpegSetup.Check(ret, "读取编码数据");

                try
                {
                    output ??= new MemoryStream(_packet->size);
                    output.Write(new ReadOnlySpan<byte>(_packet->data, _packet->size));
                    if ((_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0) isKeyFrame = true;
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            return output?.ToArray();
        }

        public int SourceWidth => _srcWidth;
        public int SourceHeight => _srcHeight;

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
}
