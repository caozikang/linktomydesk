using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace RemoteControl.Ui
{
    /// <summary>
    /// 简连 LinkToMyDesk 风格的界面积木（配色和样式在 Theme.xaml），被控端和控制端共用
    /// </summary>
    internal static class UiKit
    {
        public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

        // Win10 起系统自带 Segoe MDL2 Assets，Win11 另有 Segoe Fluent Icons；老系统退回 Segoe UI Symbol
        public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

        public static Brush Br(string key) => (Brush)Application.Current.FindResource(key);
        public static Style St(string key) => (Style)Application.Current.FindResource(key);

        public static TextBlock Text(string text, double size = 13, string brush = "Txt", bool bold = false) => new()
        {
            Text = text,
            FontSize = size,
            Foreground = Br(brush),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        /// <summary>小号灰色标签（.lbl）</summary>
        public static TextBlock Caption(string text) => Text(text, 11, "Sub");

        public static TextBlock Icon(string glyph, double size = 14) => new()
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        public static Button Button(string style, object content, RoutedEventHandler? click = null, string? tip = null)
        {
            var b = new Button { Style = St(style), Content = content, ToolTip = tip };
            if (click != null) b.Click += click;
            return b;
        }

        /// <summary>
        /// 圆角输入框（.input），带占位提示；trailing 放在右侧（例如"连接"按钮）
        /// </summary>
        public static Border Field(Control input, string placeholder, UIElement? trailing = null, double fontSize = 14)
        {
            input.FontSize = fontSize;
            var hint = new TextBlock
            {
                Text = placeholder,
                Foreground = Br("Placeholder"),
                FontSize = 13,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            void Update(bool empty) => hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            switch (input)
            {
                case TextBox tb:
                    tb.Style ??= St("BareTextBox");
                    tb.TextChanged += (_, _) => Update(tb.Text.Length == 0);
                    Update(tb.Text.Length == 0);
                    break;
                case PasswordBox pb:
                    pb.Style ??= St("BarePasswordBox");
                    pb.PasswordChanged += (_, _) => Update(pb.Password.Length == 0);
                    Update(pb.Password.Length == 0);
                    break;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var inputLayer = new Grid();
            inputLayer.Children.Add(input);
            inputLayer.Children.Add(hint);
            grid.Children.Add(inputLayer);

            if (trailing != null)
            {
                Grid.SetColumn(trailing, 1);
                if (trailing is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 4, 4);
                grid.Children.Add(trailing);
            }

            // 点输入框外围（圆角内的空白）也能聚焦
            var border = new Border { Style = St("FieldBorder"), Child = grid, Cursor = Cursors.IBeam };
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (!input.IsKeyboardFocusWithin) { input.Focus(); e.Handled = true; }
            };
            return border;
        }

        /// <summary>带右上角青色光晕的信息卡片（.mycard）</summary>
        public static Border Card(UIElement child)
        {
            var glowBrush = new RadialGradientBrush(Color.FromArgb(0x2E, 0x2D, 0xD4, 0xBF), Colors.Transparent);
            var glow = new Ellipse
            {
                Width = 110,
                Height = 110,
                Fill = glowBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -30, -30, 0),
                IsHitTestVisible = false
            };

            var layer = new Grid();
            layer.Children.Add(glow);
            layer.Children.Add(new Border { Padding = new Thickness(18), Child = child });

            var card = new Border
            {
                Background = Br("CardGradient"),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = layer
            };

            // 按圆角裁剪，光晕不会溢出卡片
            card.SizeChanged += (_, _) => layer.Clip = new RectangleGeometry(
                new Rect(0, 0, Math.Max(0, card.ActualWidth - 2), Math.Max(0, card.ActualHeight - 2)), 13, 13);
            return card;
        }

        /// <summary>小标签（.chip）</summary>
        public static Border Chip(TextBlock text, string fg = "Acc", string bg = "AccSoft")
        {
            text.FontSize = 10;
            text.Foreground = Br(fg);
            return new Border
            {
                Background = Br(bg),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text
            };
        }

        /// <summary>悬浮工具栏分隔线（.fsep）</summary>
        public static Rectangle Separator() => new()
        {
            Width = 1,
            Height = 20,
            Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>带光晕的状态点（.status-dot）</summary>
        public static Ellipse StatusDot(double size = 7) => new()
        {
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        public static void SetDot(Ellipse dot, string brushKey, bool glow = true)
        {
            var brush = Br(brushKey);
            dot.Fill = brush;
            dot.Effect = glow && brush is SolidColorBrush sc
                ? new System.Windows.Media.Effects.DropShadowEffect { Color = sc.Color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 }
                : null;
        }

        /// <summary>转圈的加载环（.ring）</summary>
        public static FrameworkElement Spinner(double size = 84, double thickness = 3)
        {
            double r = size / 2, inner = r - thickness / 2;
            var grid = new Grid { Width = size, Height = size };
            grid.Children.Add(new Ellipse { Stroke = Br("Line"), StrokeThickness = thickness });

            // 1/4 圆弧，从正上方到正右方
            var figure = new PathFigure { StartPoint = new Point(r, thickness / 2) };
            figure.Segments.Add(new ArcSegment(new Point(size - thickness / 2, r), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true));
            var arc = new Path
            {
                Stroke = Br("Acc"),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new PathGeometry(new[] { figure })
            };
            var rotate = new RotateTransform(0, r, r);
            arc.RenderTransform = rotate;
            grid.Children.Add(arc);

            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            return grid;
        }

        public static string Greeting()
        {
            int h = DateTime.Now.Hour;
            return h < 5 ? "夜深了" : h < 11 ? "早上好" : h < 13 ? "中午好" : h < 18 ? "下午好" : "晚上好";
        }

        /// <summary>"938471205" → "938 471 205"</summary>
        public static string FormatId(string id)
        {
            id = id.Replace(" ", "");
            if (id.Length != 9) return id;
            return $"{id[..3]} {id.Substring(3, 3)} {id[6..]}";
        }

        /// <summary>字符之间插入细空格，模拟 CSS letter-spacing（WPF 没有字间距属性）</summary>
        public static string Spaced(string s) => string.Join(" ", s.ToCharArray());

        // 每个访问者一个固定颜色，列表、动态、浮动提示里都用这个颜色，方便区分是谁（深色背景上用亮色）
        private static readonly Color[] ViewerColors =
        {
            Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0x34, 0xD3, 0x99),
            Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0x2D, 0xD4, 0xBF),
            Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0x81, 0x8C, 0xF8),
            Color.FromRgb(0xFB, 0x92, 0x3C)
        };

        public static Brush ViewerBrush(int viewerId)
        {
            var brush = new SolidColorBrush(ViewerColors[(viewerId & int.MaxValue) % ViewerColors.Length]);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Segoe MDL2 Assets / Segoe Fluent Icons 里的图标（两套字体码位相同）</summary>
    internal static class Glyph
    {
        public const string Refresh = "\uE72C";
        public const string Copy = "\uE8C8";
        public const string FullScreen = "\uE740";
        public const string BackToWindow = "\uE73F";
        public const string Fit = "\uE9A6";
        public const string Keyboard = "\uE765";
        public const string People = "\uE716";
        public const string Monitor = "\uE7F4";
        public const string Power = "\uE7E8";
        public const string Folder = "\uE8B7";
        public const string Document = "\uE8A5";
        public const string Transfer = "\uE8AB";     // .fbtn \u4F20\u8F93\u6587\u4EF6
        public const string Pen = "\uEDC6";
        public const string Record = "\uE7C8";
        public const string ChevronLeft = "\uE76B";
        public const string ChevronRight = "\uE76C";
        public const string Close = "\uE711";
        public const string Up = "\uE74A";
        public const string Download = "\uE896";
        public const string Upload = "\uE898";
        public const string Network = "\uE968";
        public const string Lock = "\uE72E";
        public const string Home = "\uE80F";
        public const string Settings = "\uE713";
    }

    /// <summary>
    /// 无系统边框的深色窗口：自绘标题栏（Logo + 标题 + 右上角圆点按钮），支持最大化和全屏
    /// </summary>
    public class LinkToMyDeskWindow : Window
    {
        private const double TitleBarHeight = 44;

        private readonly WindowChrome _chrome;
        private readonly Border _frame;
        private readonly Border _titleBar;
        private readonly TextBlock _titleText;
        private readonly Button _maxButton;
        private readonly ContentControl _body = new() { Focusable = false };

        private WindowState _stateBeforeFullScreen;
        private ResizeMode _resizeBeforeFullScreen;

        public bool IsFullScreen { get; private set; }

        protected LinkToMyDeskWindow()
        {
            Background = UiKit.Br("Panel");
            Foreground = UiKit.Br("Txt");
            FontFamily = UiKit.UiFont;
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanMinimize;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _chrome = new WindowChrome
            {
                CaptionHeight = TitleBarHeight,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, _chrome);

            // 标题栏（.titlebar）
            var logo = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(7),
                Background = UiKit.Br("LogoGradient"),
                Child = new TextBlock
                {
                    Text = "L",
                    FontSize = 12,
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = UiKit.Br("AccText"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            _titleText = UiKit.Text("", 13, "Sub");
            _titleText.VerticalAlignment = VerticalAlignment.Center;
            _titleText.Margin = new Thickness(10, 0, 10, 0);

            var minButton = ChromeButton("WinDotButton", "最小化", (_, _) => WindowState = WindowState.Minimized);
            _maxButton = ChromeButton("WinDotButton", "最大化", (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            var closeButton = ChromeButton("WinCloseButton", "关闭", (_, _) => Close());

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(minButton);
            buttons.Children.Add(_maxButton);
            buttons.Children.Add(closeButton);

            var titleGrid = new Grid { Margin = new Thickness(14, 0, 11, 0) };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.Children.Add(logo);
            Grid.SetColumn(_titleText, 1);
            titleGrid.Children.Add(_titleText);
            Grid.SetColumn(buttons, 2);
            titleGrid.Children.Add(buttons);

            _titleBar = new Border
            {
                Height = TitleBarHeight,
                Background = new SolidColorBrush(Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF)),
                BorderBrush = UiKit.Br("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = titleGrid
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(_titleBar);
            Grid.SetRow(_body, 1);
            root.Children.Add(_body);

            _frame = new Border { BorderBrush = UiKit.Br("Line"), BorderThickness = new Thickness(1), Child = root };
            base.Content = _frame;

            StateChanged += (_, _) => UpdateFrame();
            SourceInitialized += (_, _) => ApplyWindows11Style();
            UpdateMaxButton();
        }

        private static Button ChromeButton(string style, string tip, RoutedEventHandler click)
        {
            var b = UiKit.Button(style, null!, click, tip);
            b.Focusable = false;
            WindowChrome.SetIsHitTestVisibleInChrome(b, true); // 标题栏区域默认不响应点击
            return b;
        }

        /// <summary>窗口标题（任务栏）和标题栏文字</summary>
        protected string ChromeTitle
        {
            get => _titleText.Text;
            set
            {
                _titleText.Text = value;
                Title = value;
            }
        }

        protected UIElement Body
        {
            set => _body.Content = value;
        }

        protected void SetResizable(bool resizable)
        {
            if (IsFullScreen) SetFullScreen(false);
            if (!resizable && WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.CanMinimize;
            UpdateMaxButton();
        }

        private void UpdateMaxButton()
        {
            _maxButton.Visibility = ResizeMode == ResizeMode.CanResize ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 全屏：隐藏标题栏，盖住任务栏。标题栏拖动区域也要关掉，否则顶部 44 像素点不到远程画面
        /// </summary>
        public void SetFullScreen(bool on)
        {
            if (on == IsFullScreen) return;

            if (on)
            {
                _stateBeforeFullScreen = WindowState;
                _resizeBeforeFullScreen = ResizeMode;
                IsFullScreen = true;
                _titleBar.Visibility = Visibility.Collapsed;
                _chrome.CaptionHeight = 0;
                _chrome.ResizeBorderThickness = new Thickness(0);

                // 先还原再最大化，已经最大化的窗口改样式不会重新布局
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            else
            {
                IsFullScreen = false;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = _resizeBeforeFullScreen;
                _titleBar.Visibility = Visibility.Visible;
                _chrome.CaptionHeight = TitleBarHeight;
                _chrome.ResizeBorderThickness = new Thickness(6);
                WindowState = _stateBeforeFullScreen;
            }

            UpdateFrame();
        }

        private void UpdateFrame()
        {
            if (IsFullScreen)
            {
                _frame.Margin = new Thickness(0);
                _frame.BorderThickness = new Thickness(0);
            }
            else if (WindowState == WindowState.Maximized)
            {
                // 无边框窗口最大化时会向屏幕外多出一圈缩放边框，要缩进来，否则四周内容被裁掉
                _frame.Margin = SystemParameters.WindowResizeBorderThickness;
                _frame.BorderThickness = new Thickness(0);
            }
            else
            {
                _frame.Margin = new Thickness(0);
                _frame.BorderThickness = new Thickness(1);
            }

            _maxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void ApplyWindows11Style()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE = 圆角（仅 Win11 生效）
            }
            catch
            {
                // Win10 / Server 2016 没有这些属性，忽略
            }
        }
    }
}
