using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    /// <summary>枚举显示器（物理像素，主显示器排第一）</summary>
    internal static class MonitorEnumerator
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const int MONITORINFOF_PRIMARY = 1;

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        public static MonitorInfo[] GetMonitors()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref info))
                {
                    var r = info.rcMonitor;
                    list.Add(new MonitorInfo
                    {
                        Name = info.szDevice.Replace(@"\\.\", ""),
                        Left = r.Left,
                        Top = r.Top,
                        Width = r.Right - r.Left,
                        Height = r.Bottom - r.Top,
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }
                return true;
            }, IntPtr.Zero);

            // 主显示器在前，其余从左到右，序号稳定
            list.Sort((a, b) => a.IsPrimary != b.IsPrimary ? (a.IsPrimary ? -1 : 1) : a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
            for (int i = 0; i < list.Count; i++) list[i].Index = i;
            return list.ToArray();
        }
    }

    /// <summary>
    /// 截取主屏幕（含鼠标指针）为 BGRX 像素，供多个访问者的编码线程共享。
    /// Capture 只能由一个截屏线程调用；AcquireLatest 可在任意线程调用。
    /// </summary>
    public sealed class ScreenCapture : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private readonly Snapshot _snapshot;
        private Bitmap? _bitmap;
        private bool _disposed;

        // _latest 是已发布的最新画面（读线程在读锁内使用），_scratch 是截屏线程的工作缓冲
        private byte[] _latest = Array.Empty<byte>();
        private byte[] _scratch = Array.Empty<byte>();
        private int _latestWidth, _latestHeight, _latestStride;
        private long _version;

        /// <summary>当前显示器的真实分辨率（鼠标坐标以此为准，与传输分辨率无关）</summary>
        public int ScreenWidth { get; private set; }
        public int ScreenHeight { get; private set; }

        /// <summary>当前显示器在虚拟桌面里的左上角（鼠标坐标要加上这个偏移）</summary>
        public int ScreenLeft { get; private set; }
        public int ScreenTop { get; private set; }

        private volatile int _selectedIndex;
        private MonitorInfo[] _monitors = Array.Empty<MonitorInfo>();

        /// <summary>显示器列表或当前显示器变了（截屏线程触发）</summary>
        public event Action? MonitorsChanged;

        public ScreenCapture()
        {
            _snapshot = new Snapshot(this);
            UpdateScreenSize();
        }

        public int SelectedIndex => _selectedIndex;

        public MonitorInfo[] Monitors => _monitors;

        /// <summary>切换要截的显示器，下一帧生效</summary>
        public void SelectMonitor(int index)
        {
            _selectedIndex = Math.Max(0, index);
        }

        /// <summary>
        /// 刷新显示器列表和当前显示器区域。显示器拔插、改分辨率时自动跟上
        /// </summary>
        public void UpdateScreenSize()
        {
            // 进程是 PerMonitorV2 DPI 感知（见 app.manifest），拿到的都是物理像素
            var monitors = MonitorEnumerator.GetMonitors();
            if (monitors.Length == 0) return;

            int index = Math.Clamp(_selectedIndex, 0, monitors.Length - 1);
            var m = monitors[index];
            bool changed = index != _selectedIndex || !SameLayout(monitors, _monitors) ||
                           m.Left != ScreenLeft || m.Top != ScreenTop || m.Width != ScreenWidth || m.Height != ScreenHeight;

            _selectedIndex = index;
            _monitors = monitors;
            ScreenLeft = m.Left;
            ScreenTop = m.Top;
            ScreenWidth = m.Width;
            ScreenHeight = m.Height;

            if (changed) MonitorsChanged?.Invoke();
        }

        private static bool SameLayout(MonitorInfo[] a, MonitorInfo[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Left != b[i].Left || a[i].Top != b[i].Top || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 截一帧。画面有变化时发布为最新画面（Version 加一）。只能在截屏线程里调用
        /// </summary>
        public void Capture()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScreenCapture));

            UpdateScreenSize();
            int w = ScreenWidth, h = ScreenHeight;
            if (w <= 0 || h <= 0) return;

            if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            }

            using (var graphics = Graphics.FromImage(_bitmap))
            {
                graphics.CopyFromScreen(ScreenLeft, ScreenTop, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                // CopyFromScreen 不带鼠标指针，自己画上去，大家才能看到别人的鼠标在哪
                DrawCursor(graphics, ScreenLeft, ScreenTop);
            }

            var data = _bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            int stride = Math.Abs(data.Stride);
            int size = stride * h;
            try
            {
                if (_scratch.Length != size)
                {
                    _scratch = new byte[size];
                }
                Marshal.Copy(data.Scan0, _scratch, 0, size);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }

            // 只有截屏线程会写 _latest，这里不加锁读是安全的。1080p 约 8MB，比较 1-2ms
            bool changed = _latest.Length != size || _latestWidth != w ||
                           !_scratch.AsSpan().SequenceEqual(_latest);
            if (!changed) return;

            // 写锁只包住交换缓冲区这一步，读线程最多等一次颜色转换的时间
            _lock.EnterWriteLock();
            try
            {
                (_latest, _scratch) = (_scratch, _latest);
                _latestWidth = w;
                _latestHeight = h;
                _latestStride = stride;
                _version++;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 取得最新画面的只读视图，用完必须 Dispose（持有读锁期间截屏线程不能发布新画面）。
        /// 同一线程不能嵌套调用。
        /// </summary>
        public Snapshot AcquireLatest()
        {
            _lock.EnterReadLock();
            return _snapshot;
        }

        public sealed class Snapshot : IDisposable
        {
            private readonly ScreenCapture _owner;

            internal Snapshot(ScreenCapture owner) => _owner = owner;

            /// <summary>0 表示还没有画面</summary>
            public long Version => _owner._version;
            public byte[] Pixels => _owner._latest;
            public int Width => _owner._latestWidth;
            public int Height => _owner._latestHeight;
            public int Stride => _owner._latestStride;

            public void Dispose() => _owner._lock.ExitReadLock();
        }

        #region 鼠标指针

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        private const int CURSOR_SHOWING = 0x0001;
        private const int DI_NORMAL = 0x0003;

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private static void DrawCursor(Graphics graphics, int originX, int originY)
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            {
                return;
            }

            int hotX = 0, hotY = 0;
            if (GetIconInfo(info.hCursor, out var icon))
            {
                hotX = icon.xHotspot;
                hotY = icon.yHotspot;
                // GetIconInfo 会创建位图副本，必须释放，否则每帧泄漏 GDI 句柄
                if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
                if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
            }

            IntPtr hdc = graphics.GetHdc();
            try
            {
                DrawIconEx(hdc, info.ptScreenPos.X - originX - hotX, info.ptScreenPos.Y - originY - hotY, info.hCursor,
                    0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bitmap?.Dispose();
            _bitmap = null;
            // _lock 不释放：编码线程可能还在 AcquireLatest，交给 GC
        }
    }
}
