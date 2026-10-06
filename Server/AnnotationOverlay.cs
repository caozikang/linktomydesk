using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    /// <summary>
    /// 被控端屏幕上的白板标注层：置顶、透明、鼠标穿透、不抢焦点，盖住当前共享的显示器。
    /// 透明 WPF 窗口是分层窗口，CopyFromScreen 不带 CAPTUREBLT 时截不到它，
    /// 所以远程画面里不会重复出现这些线，控制端自己在画面上画。
    /// </summary>
    public sealed class AnnotationOverlay : Window
    {
        private readonly Canvas _canvas = new();
        private readonly List<AnnotationMessage> _strokes = new();
        private MonitorInfo? _monitor;

        public AnnotationOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            Content = _canvas;
            SourceInitialized += (_, _) => MakeClickThrough();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        }

        /// <summary>收到一条标注（界面线程调用）</summary>
        public void Apply(AnnotationMessage msg, MonitorInfo? monitor)
        {
            if (msg.Kind == AnnotationKind.Clear)
            {
                _strokes.Clear();
                _canvas.Children.Clear();
                Hide();
                return;
            }

            if (monitor == null) return;
            _strokes.Add(msg);
            if (_monitor == null || !SameRect(_monitor, monitor))
            {
                _monitor = monitor;
                PlaceOn(monitor);
            }
            else if (!IsVisible)
            {
                PlaceOn(monitor);
            }
            Draw(msg);
        }

        private static bool SameRect(MonitorInfo a, MonitorInfo b) =>
            a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height;

        /// <summary>用物理像素定位（WPF 的 Left/Top 是逻辑单位，多显示器不同 DPI 时会错位）</summary>
        private void PlaceOn(MonitorInfo m)
        {
            if (!IsVisible) Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, m.Left, m.Top, m.Width, m.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            UpdateLayout();
            _canvas.Children.Clear();
            foreach (var s in _strokes) Draw(s);
        }

        private void Draw(AnnotationMessage msg)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            var brush = new SolidColorBrush(Color.FromArgb((byte)(msg.Color >> 24), (byte)(msg.Color >> 16), (byte)(msg.Color >> 8), (byte)msg.Color));
            brush.Freeze();
            var line = new Polyline
            {
                Stroke = brush,
                // 控制端按画面显示尺寸画 3 像素，这里按被控端逻辑像素画，粗细接近
                StrokeThickness = Math.Clamp(msg.Thickness, 1, 20),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            for (int i = 0; i + 1 < msg.Points.Length; i += 2)
            {
                line.Points.Add(new Point(Math.Clamp(msg.Points[i], 0, 1) * w, Math.Clamp(msg.Points[i + 1], 0, 1) * h));
            }
            _canvas.Children.Add(line);
        }
    }
}