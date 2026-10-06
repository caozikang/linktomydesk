using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace RemoteControl.Media
{
    /// <summary>
    /// 定位并加载 FFmpeg 原生库（来自 Sdcb.FFmpeg.runtime.windows-x64 包）。
    /// 被控端和控制端共用此文件（csproj 里以链接方式引用）。
    /// </summary>
    internal static class FFmpegSetup
    {
        private static readonly object Gate = new();
        private static bool _initialized;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void Initialize()
        {
            lock (Gate)
            {
                if (_initialized) return;

                // 发布时（-r win-x64）DLL 在程序目录；直接 build 时在 runtimes\win-x64\native
                string baseDir = AppContext.BaseDirectory;
                string[] candidates =
                {
                    baseDir,
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "ffmpeg"),
                };

                string? root = null;
                foreach (var dir in candidates)
                {
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "avcodec-*.dll").Length > 0)
                    {
                        root = dir;
                        break;
                    }
                }

                if (root == null)
                {
                    throw new DllNotFoundException("未找到 FFmpeg 组件（avcodec-*.dll），请使用完整的发布目录");
                }

                // avcodec 依赖 avutil 等，要让系统在同一目录里找依赖
                SetDllDirectory(root);
                ffmpeg.RootPath = root;
                DynamicallyLoadedBindings.Initialize();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                _initialized = true;
            }
        }

        public static unsafe string ErrorText(int error)
        {
            const int size = 256;
            byte* buffer = stackalloc byte[size];
            ffmpeg.av_strerror(error, buffer, size);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"错误码 {error}";
        }

        public static int Check(int result, string action)
        {
            if (result < 0)
            {
                throw new FFmpegException($"{action}失败: {ErrorText(result)}", result);
            }
            return result;
        }

        public static readonly int AVERROR_EAGAIN = -ffmpeg.EAGAIN;
    }

    internal sealed class FFmpegException : Exception
    {
        public int ErrorCode { get; }

        public FFmpegException(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
