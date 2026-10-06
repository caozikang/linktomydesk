using System;
using FFmpeg.AutoGen;
using RemoteControl.Media;

namespace RemoteControl.Client
{
    /// <summary>
    /// 把收到的 H.264 码流（Annex-B）直接封装成 MP4，不解码不重编码，几乎不占 CPU。
    /// 从第一个关键帧开始写（关键帧里带 SPS/PPS，用作 MP4 的 avcC 头）。
    /// 非线程安全，由调用方加锁。
    /// </summary>
    public sealed unsafe class Mp4Recorder : IDisposable
    {
        private AVFormatContext* _fmt;
        private AVStream* _stream;
        private AVPacket* _packet;
        private bool _headerWritten;
        private bool _disposed;
        private long _startMs = -1;
        private long _lastPts = -1;

        public string Path { get; }

        public Mp4Recorder(string path)
        {
            Path = path;
            FFmpegSetup.Initialize();
            _packet = ffmpeg.av_packet_alloc();
        }

        /// <param name="data">一帧 Annex-B 数据</param>
        /// <param name="width">编码分辨率（解码出来的画面尺寸）</param>
        /// <param name="timestampMs">被控端发送时间，用作播放时间轴</param>
        public void Write(byte[] data, bool isKeyFrame, int width, int height, long timestampMs)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Mp4Recorder));

            if (!_headerWritten)
            {
                if (!isKeyFrame || width <= 0 || height <= 0) return; // 等关键帧
                Open(data, width, height);
                _startMs = timestampMs;
            }

            // 时间戳必须递增，同一毫秒的两帧往后挪 1ms
            long pts = Math.Max(timestampMs - _startMs, 0);
            if (pts <= _lastPts) pts = _lastPts + 1;
            _lastPts = pts;

            fixed (byte* p = data)
            {
                // av_interleaved_write_frame 会接管数据，这里先复制一份到 FFmpeg 的缓冲区
                FFmpegSetup.Check(ffmpeg.av_new_packet(_packet, data.Length), "分配录制数据包");
                Buffer.MemoryCopy(p, _packet->data, data.Length, data.Length);
            }
            _packet->stream_index = _stream->index;
            _packet->pts = _packet->dts = ffmpeg.av_rescale_q(pts, new AVRational { num = 1, den = 1000 }, _stream->time_base);
            if (isKeyFrame) _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            int ret = ffmpeg.av_interleaved_write_frame(_fmt, _packet);
            ffmpeg.av_packet_unref(_packet);
            FFmpegSetup.Check(ret, "写入录制文件");
        }

        private void Open(byte[] keyFrame, int width, int height)
        {
            AVFormatContext* fmt = null;
            FFmpegSetup.Check(ffmpeg.avformat_alloc_output_context2(&fmt, null, "mp4", Path), "创建 MP4 文件");
            _fmt = fmt;

            _stream = ffmpeg.avformat_new_stream(_fmt, null);
            var par = _stream->codecpar;
            par->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            par->codec_id = AVCodecID.AV_CODEC_ID_H264;
            par->width = width;
            par->height = height;
            _stream->time_base = new AVRational { num = 1, den = 1000 };

            // extradata 用 Annex-B 格式的 SPS/PPS，mp4 封装器会自己转成 avcC，并把每帧转成长度前缀格式
            int headerLength = ParameterSetsLength(keyFrame);
            if (headerLength > 0)
            {
                par->extradata = (byte*)ffmpeg.av_mallocz((ulong)(headerLength + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                fixed (byte* p = keyFrame)
                {
                    Buffer.MemoryCopy(p, par->extradata, headerLength, headerLength);
                }
                par->extradata_size = headerLength;
            }

            FFmpegSetup.Check(ffmpeg.avio_open(&_fmt->pb, Path, ffmpeg.AVIO_FLAG_WRITE), "打开录制文件");

            // faststart：结束时把索引移到文件头，录到一半的文件也更容易被播放器识别
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            int ret = ffmpeg.avformat_write_header(_fmt, &options);
            ffmpeg.av_dict_free(&options);
            FFmpegSetup.Check(ret, "写入 MP4 文件头");
            _headerWritten = true;
        }

        /// <summary>关键帧开头的 SPS/PPS/SEI 等参数集长度（到第一个图像切片为止）</summary>
        private static int ParameterSetsLength(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                // 起始码 00 00 01（4 字节起始码 00 00 00 01 也会在这里匹配到后 3 字节）
                if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;

                int type = data[i + 3] & 0x1F;
                if (type == 1 || type == 5)
                {
                    int start = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                    return start;
                }
            }
            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_headerWritten) ffmpeg.av_write_trailer(_fmt);
            }
            finally
            {
                if (_fmt != null)
                {
                    if (_fmt->pb != null) ffmpeg.avio_closep(&_fmt->pb);
                    ffmpeg.avformat_free_context(_fmt);
                    _fmt = null;
                }

                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }
        }
    }
}
