using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteControl.Common;
using RemoteControl.Ui;
using MouseButton = RemoteControl.Common.MouseButton;

namespace RemoteControl.Client
{
    /// <summary>
    /// 控制端主窗口（简连 LinkDesk 风格），三个界面：
    /// ① 主屏：输入对方识别码和密码  ② 连接中  ③ 远程会话（画面 + 悬浮工具栏 + 协作面板）
    /// </summary>
    public partial class MainWindow : LinkDeskWindow
    {
        private const string SettingsName = "client";
        private const double HomeWidth = 420;
        private const double HomeHeight = 640;

        private RemoteClientWithRelay? _client;
        private ScaleMode _currentScaleMode = ScaleMode.Fit;
        private readonly AppSettings _settings = AppSettings.Load(SettingsName);
        private bool _sessionActive;
        private string _target = "";

        // 三个界面
        private readonly Grid _homeView = new();
        private readonly Grid _connectingView = new();
        private readonly Grid _sessionView = new();

        public MainWindow()
        {
            ChromeTitle = "简连 LinkDesk";
            Width = HomeWidth;
            Height = HomeHeight;

            BuildHome();
            BuildConnecting();
            BuildSession();

            var root = new Grid();
            root.Children.Add(_homeView);
            root.Children.Add(_connectingView);
            root.Children.Add(_sessionView);
            Body = root;

            // 会话中所有按键都发给远程（用 Preview 抢在 Tab 焦点切换、Alt 菜单之前）
            PreviewKeyDown += Window_PreviewKeyDown;
            PreviewKeyUp += Window_PreviewKeyUp;

            _statsTimer.Tick += (_, _) => UpdateStats();

            ShowHome();
        }

        #region ① 主屏

        private readonly RadioButton _relayRadio = new() { Content = "识别码连接", GroupName = "Mode" };
        private readonly RadioButton _directRadio = new() { Content = "IP 直连", GroupName = "Mode" };
        private readonly TextBox _deviceIdBox = new();
        private readonly TextBox _directHostBox = new();
        private readonly PasswordBox _passwordBox = new();
        private readonly TextBox _relayHostBox = new();
        private readonly TextBox _relayPortBox = new();
        private readonly TextBox _directPortBox = new();
        private readonly TextBox _nickNameBox = new() { MaxLength = 20 };
        private readonly TextBlock _targetLabel = new();
        private Border _deviceIdField = null!;
        private Border _directHostField = null!;
        private Border _directSettings = null!;
        private readonly StackPanel _settingsPanel = new() { Margin = new Thickness(0, 16, 0, 0) };
        private readonly Grid _relaySettings = new();
        private readonly TextBlock _homeError = new();
        private readonly TextBlock _greeting = new();
        private readonly Ellipse _homeDot = UiKit.StatusDot();
        private readonly TextBlock _homeStatus = new();

        private void BuildHome()
        {
            var root = new Grid { Margin = new Thickness(30, 30, 30, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            var main = new StackPanel();
            scroll.Content = main;
            root.Children.Add(scroll);

            // 问候（.greet）
            _greeting.FontSize = 20;
            _greeting.FontWeight = FontWeights.SemiBold;
            _greeting.Foreground = UiKit.Br("Txt");
            main.Children.Add(_greeting);
            main.Children.Add(new TextBlock
            {
                Text = "远程协助 · 支持多人同时访问同一台设备",
                FontSize = 12,
                Foreground = UiKit.Br("Sub"),
                Margin = new Thickness(0, 5, 0, 0)
            });

            // 连接方式
            var segment = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, 22, 0, 0) };
            _relayRadio.Style = UiKit.St("SegmentRadio");
            _directRadio.Style = UiKit.St("SegmentRadio");
            _relayRadio.ToolTip = "经中继服务器连接，双方都在内网也能连";
            _directRadio.ToolTip = "局域网内，或对方有公网 IP / 端口转发时使用";
            segment.Children.Add(_relayRadio);
            segment.Children.Add(_directRadio);
            main.Children.Add(segment);

            // 对方识别码 + 连接按钮（.connect .input .gobtn）
            _targetLabel.FontSize = 11;
            _targetLabel.Foreground = UiKit.Br("Sub");
            _targetLabel.Margin = new Thickness(0, 14, 0, 10);
            main.Children.Add(_targetLabel);

            _deviceIdBox.FontWeight = FontWeights.SemiBold;
            _deviceIdBox.MaxLength = 13;
            Typography.SetNumeralAlignment(_deviceIdBox, FontNumeralAlignment.Tabular);
            _deviceIdBox.LostFocus += (_, _) => _deviceIdBox.Text = UiKit.FormatId(_deviceIdBox.Text);

            // 两个模式各有一个"连接"按钮（同一个按钮不能同时放进两个输入框）
            var goButton = UiKit.Button("PrimaryButton", "连 接", ConnectButton_Click);
            goButton.MinHeight = 0;
            var goButton2 = UiKit.Button("PrimaryButton", "连 接", ConnectButton_Click);
            goButton2.MinHeight = 0;

            _deviceIdField = UiKit.Field(_deviceIdBox, "输入对方识别码，如 123 456 789", goButton, fontSize: 17);
            _directHostField = UiKit.Field(_directHostBox, "对方 IP 地址，如 192.168.1.20", goButton2, fontSize: 15);
            main.Children.Add(_deviceIdField);
            main.Children.Add(_directHostField);

            // 连接密码
            var pwLabel = UiKit.Caption("连 接 密 码");
            pwLabel.Margin = new Thickness(0, 16, 0, 10);
            main.Children.Add(pwLabel);
            main.Children.Add(UiKit.Field(_passwordBox, "对方被控端上显示的密码", fontSize: 15));

            _homeError.FontSize = 12;
            _homeError.Foreground = UiKit.Br("Danger");
            _homeError.TextWrapping = TextWrapping.Wrap;
            _homeError.Margin = new Thickness(2, 10, 0, 0);
            _homeError.Visibility = Visibility.Collapsed;
            main.Children.Add(_homeError);

            // 设置（默认收起；还没填中继地址时自动展开）
            BuildSettingsPanel();
            main.Children.Add(_settingsPanel);

            // 最近连接过的设备（设备列表），点一下填入识别码
            main.Children.Add(BuildRecentPanel());

            // 底栏（.footer）
            var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            var status = new StackPanel { Orientation = Orientation.Horizontal };
            status.Children.Add(_homeDot);
            _homeStatus.FontSize = 12;
            _homeStatus.Foreground = UiKit.Br("Sub");
            _homeStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            _homeStatus.MaxWidth = 250;
            status.Children.Add(_homeStatus);
            footer.Children.Add(status);
            var settingsLink = UiKit.Button("LinkButton", "设置", (_, _) =>
                _settingsPanel.Visibility = _settingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
            settingsLink.HorizontalAlignment = HorizontalAlignment.Right;
            settingsLink.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(settingsLink);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            // 填入上次的设置
            _deviceIdBox.Text = UiKit.FormatId(_settings.DeviceId);
            _directHostBox.Text = _settings.DirectHost;
            _relayHostBox.Text = _settings.RelayHost;
            _relayPortBox.Text = _settings.RelayPort.ToString();
            _directPortBox.Text = _settings.DirectPort.ToString();
            _nickNameBox.Text = string.IsNullOrWhiteSpace(_settings.NickName) ? Environment.UserName : _settings.NickName;
            _nickNameBox.TextChanged += (_, _) => UpdateGreeting();
            _relayHostBox.TextChanged += (_, _) => UpdateHomeStatus();

            _relayRadio.Checked += (_, _) => UpdateModeVisibility();
            _directRadio.Checked += (_, _) => UpdateModeVisibility();
            _relayRadio.IsChecked = _settings.UseRelay;
            _directRadio.IsChecked = !_settings.UseRelay;

            _settingsPanel.Visibility = string.IsNullOrWhiteSpace(_settings.RelayHost) && _settings.UseRelay
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 在输入框里按回车直接连接
            foreach (var box in new Control[] { _deviceIdBox, _directHostBox, _passwordBox, _relayHostBox, _relayPortBox, _directPortBox, _nickNameBox })
            {
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && !_sessionActive && _client == null)
                    {
                        ConnectButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                    }
                };
            }

            UpdateModeVisibility();
            UpdateGreeting();
            _homeView.Children.Add(root);
        }

        private void BuildSettingsPanel()
        {
            var divider = new Border { Height = 1, Background = UiKit.Br("Line"), Margin = new Thickness(0, 0, 0, 14) };
            _settingsPanel.Children.Add(divider);

            var relayLabel = UiKit.Caption("中 继 服 务 器");
            relayLabel.Margin = new Thickness(0, 0, 0, 8);
            _relaySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _relaySettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            _relaySettings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _relaySettings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(relayLabel, 2);
            _relaySettings.Children.Add(relayLabel);
            var hostField = UiKit.Field(_relayHostBox, "地址，如 relay.example.com", fontSize: 13);
            var portField = UiKit.Field(_relayPortBox, "端口", fontSize: 13);
            portField.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetRow(hostField, 1);
            Grid.SetRow(portField, 1);
            Grid.SetColumn(portField, 1);
            _relaySettings.Children.Add(hostField);
            _relaySettings.Children.Add(portField);
            _settingsPanel.Children.Add(_relaySettings);

            // 直连端口
            var directLabel = UiKit.Caption("直 连 端 口");
            directLabel.Margin = new Thickness(0, 0, 0, 8);
            var directPanel = new StackPanel();
            directPanel.Children.Add(directLabel);
            directPanel.Children.Add(UiKit.Field(_directPortBox, "被控端监听端口，默认 5900", fontSize: 13));
            _directSettings = new Border { Child = directPanel };
            _settingsPanel.Children.Add(_directSettings);

            var nickLabel = UiKit.Caption("我 的 名 字（多人访问时其他人看到的名字）");
            nickLabel.Margin = new Thickness(0, 14, 0, 8);
            _settingsPanel.Children.Add(nickLabel);
            _settingsPanel.Children.Add(UiKit.Field(_nickNameBox, "例如：张工", fontSize: 13));
        }

        private void UpdateGreeting()
        {
            string name = _nickNameBox.Text.Trim();
            _greeting.Text = string.IsNullOrEmpty(name) ? UiKit.Greeting() : $"{UiKit.Greeting()}，{name}";
        }

        private void UpdateModeVisibility()
        {
            bool relay = _relayRadio.IsChecked == true;
            _targetLabel.Text = relay ? "对 方 识 别 码" : "对 方 地 址";
            _deviceIdField.Visibility = relay ? Visibility.Visible : Visibility.Collapsed;
            _directHostField.Visibility = relay ? Visibility.Collapsed : Visibility.Visible;
            _relaySettings.Visibility = relay ? Visibility.Visible : Visibility.Collapsed;
            _directSettings.Visibility = relay ? Visibility.Collapsed : Visibility.Visible;
            UpdateHomeStatus();
        }

        private void UpdateHomeStatus()
        {
            if (_relayRadio.IsChecked == true)
            {
                string host = _relayHostBox.Text.Trim();
                if (string.IsNullOrEmpty(host))
                {
                    _homeStatus.Text = "未设置中继服务器";
                    UiKit.SetDot(_homeDot, "Warn");
                }
                else
                {
                    _homeStatus.Text = $"中继节点 · {host}";
                    UiKit.SetDot(_homeDot, "Ok");
                }
            }
            else
            {
                _homeStatus.Text = "IP 直连模式";
                UiKit.SetDot(_homeDot, "Ok");
            }
        }

        private void ShowHomeError(string? message)
        {
            _homeError.Text = message ?? "";
            _homeError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        #endregion

        #region ② 连接中

        private readonly TextBlock _connectingTitle = new();
        private readonly TextBlock _connectingDetail = new();

        private void BuildConnecting()
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var spinner = UiKit.Spinner();
            spinner.Margin = new Thickness(0, 0, 0, 22);
            panel.Children.Add(spinner);

            _connectingTitle.FontSize = 16;
            _connectingTitle.FontWeight = FontWeights.SemiBold;
            _connectingTitle.Foreground = UiKit.Br("Txt");
            _connectingTitle.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(_connectingTitle);

            _connectingDetail.FontSize = 12;
            _connectingDetail.Foreground = UiKit.Br("Sub");
            _connectingDetail.HorizontalAlignment = HorizontalAlignment.Center;
            _connectingDetail.TextAlignment = TextAlignment.Center;
            _connectingDetail.TextWrapping = TextWrapping.Wrap;
            _connectingDetail.MaxWidth = 320;
            _connectingDetail.Margin = new Thickness(0, 10, 0, 22);
            panel.Children.Add(_connectingDetail);

            var cancel = UiKit.Button("DangerGhostButton", "取消连接", DisconnectButton_Click);
            cancel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(cancel);

            _connectingView.Children.Add(panel);
        }

        #endregion

        #region ③ 远程会话

        private readonly Grid _screenHost = new() { Focusable = true, ClipToBounds = true };
        private readonly ScrollViewer _screenScroll = new() { Focusable = false };
        private readonly Image _screenImage = new() { Stretch = Stretch.Uniform, Cursor = Cursors.Cross };
        private readonly Border _floatBar = new();
        private readonly TextBlock _remoteIdLabel = new();
        private readonly ToggleButton _fullScreenButton = new();
        private readonly ToggleButton _scaleButton = new();
        private readonly ToggleButton _peopleButton = new() { IsChecked = true };
        private readonly ComboBox _qualityCombo = new();
        private readonly ComboBox _fpsCombo = new();
        private readonly Ellipse _latencyDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _latencyText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _sidePanel = new();
        private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private long _lastBytes;
        private long _lastStatsTick;

        private void BuildSession()
        {
            _sessionView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _sessionView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 画面背景：浅浅的棋盘格 + 青色斜光（.session）
            var checker = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 40, 40),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new DrawingGroup
                {
                    Children =
                    {
                        new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1D)), null, new RectangleGeometry(new Rect(0, 0, 40, 40))),
                        new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)), null, new RectangleGeometry(new Rect(0, 0, 20, 20))),
                        new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)), null, new RectangleGeometry(new Rect(20, 20, 20, 20)))
                    }
                }
            };
            checker.Freeze();
            _screenHost.Background = checker;
            _screenHost.Children.Add(new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(Color.FromArgb(0x0D, 0x2D, 0xD4, 0xBF), Colors.Transparent, new Point(0, 0), new Point(0.4, 0.4))
            });

            // 远程画面
            _screenImage.MouseMove += ScreenImage_MouseMove;
            _screenImage.MouseDown += ScreenImage_MouseDown;
            _screenImage.MouseUp += ScreenImage_MouseUp;
            _screenImage.MouseWheel += ScreenImage_MouseWheel;
            RenderOptions.SetBitmapScalingMode(_screenImage, BitmapScalingMode.HighQuality);
            // 画面上叠一层标注画布，跟着画面一起滚动缩放
            var imageLayer = new Grid();
            imageLayer.Children.Add(_screenImage);
            imageLayer.Children.Add(BuildAnnotationCanvas());
            _screenScroll.Content = imageLayer;
            _screenHost.Children.Add(_screenScroll);

            // 输入法不要在本地弹出候选框，按键原样发给远程
            InputMethod.SetIsInputMethodEnabled(_screenHost, false);
            _screenHost.MouseDown += (_, _) => _screenHost.Focus();

            // 从资源管理器拖文件进来 = 上传到对方桌面
            _screenHost.AllowDrop = true;
            _screenHost.Drop += ScreenHost_Drop;

            // 底部会话信息（.remote-id）
            _remoteIdLabel.FontSize = 11;
            _remoteIdLabel.Foreground = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            _remoteIdLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _remoteIdLabel.VerticalAlignment = VerticalAlignment.Bottom;
            _remoteIdLabel.Margin = new Thickness(0, 0, 0, 14);
            _remoteIdLabel.IsHitTestVisible = false;
            _screenHost.Children.Add(_remoteIdLabel);

            _screenHost.Children.Add(_activityToast);

            BuildFloatBar();
            _screenHost.Children.Add(_floatBar);

            _sessionView.Children.Add(_screenHost);

            BuildSidePanel();
            Grid.SetColumn(_sidePanel, 1);
            _sessionView.Children.Add(_sidePanel);
        }

        /// <summary>顶部居中的悬浮工具栏（.floatbar）。鼠标离开后变淡，少挡画面</summary>
        private void BuildFloatBar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal };

            _fullScreenButton.Style = UiKit.St("FloatToggle");
            _fullScreenButton.Content = UiKit.Icon(Glyph.FullScreen);
            _fullScreenButton.ToolTip = "全屏";
            _fullScreenButton.Click += (_, _) =>
            {
                SetFullScreen(_fullScreenButton.IsChecked == true);
                _fullScreenButton.Content = UiKit.Icon(IsFullScreen ? Glyph.BackToWindow : Glyph.FullScreen);
                _fullScreenButton.ToolTip = IsFullScreen ? "退出全屏" : "全屏";
                _screenHost.Focus();
            };
            bar.Children.Add(_fullScreenButton);

            _scaleButton.Style = UiKit.St("FloatToggle");
            _scaleButton.Content = UiKit.Icon(Glyph.Fit);
            _scaleButton.ToolTip = "原始尺寸（1:1）";
            _scaleButton.Click += (_, _) =>
            {
                _currentScaleMode = _scaleButton.IsChecked == true ? ScaleMode.Original : ScaleMode.Fit;
                _scaleButton.ToolTip = _currentScaleMode == ScaleMode.Original ? "自适应窗口" : "原始尺寸（1:1）";
                if (_client?.IsConnected == true) _ = _client.SetScaleModeAsync(_currentScaleMode);
                ApplyScaleMode();
            };
            bar.Children.Add(_scaleButton);

            _peopleButton.Style = UiKit.St("FloatToggle");
            _peopleButton.Content = UiKit.Icon(Glyph.People);
            _peopleButton.ToolTip = "在线成员 / 操作动态";
            _peopleButton.Click += (_, _) =>
            {
                _sidePanel.Visibility = _peopleButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            };
            bar.Children.Add(_peopleButton);

            // 传输文件、Ctrl+Alt+Del、白板标注、录制（SessionFeatures.cs）
            AddFeatureButtons(bar);

            bar.Children.Add(UiKit.Separator());

            // 屏幕选择（.fscreen），只有一个显示器时不显示
            bar.Children.Add(BuildMonitorCombo());

            // 画质（.fscreen 下拉风格）
            _qualityCombo.Style = UiKit.St("FloatCombo");
            _qualityCombo.ToolTip = "画质（线路差时选流畅）";
            foreach (var q in new[] { "流畅", "标准", "高清", "超清" }) _qualityCombo.Items.Add(q);
            _qualityCombo.SelectedIndex = 1; // 与被控端默认档位一致，跨运营商线路建议用标准或流畅
            _qualityCombo.SelectionChanged += QualityCombo_SelectionChanged;
            bar.Children.Add(_qualityCombo);

            _fpsCombo.Style = UiKit.St("FloatCombo");
            _fpsCombo.ToolTip = "帧率上限";
            foreach (var f in new[] { "15 帧", "30 帧", "60 帧" }) _fpsCombo.Items.Add(f);
            _fpsCombo.SelectedIndex = 1;
            _fpsCombo.SelectionChanged += FpsCombo_SelectionChanged;
            bar.Children.Add(_fpsCombo);

            bar.Children.Add(UiKit.Separator());

            // 延迟 · 速率（.latency）
            var latency = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _latencyText.Foreground = UiKit.Br("FloatFg");
            latency.Children.Add(_latencyDot);
            latency.Children.Add(_latencyText);
            latency.ToolTip = "往返延迟 · 接收速率";
            bar.Children.Add(latency);

            bar.Children.Add(UiKit.Separator());

            var disconnect = UiKit.Button("FloatButton", UiKit.Icon(Glyph.Power), DisconnectButton_Click, "断开");
            disconnect.Foreground = UiKit.Br("Danger");
            bar.Children.Add(disconnect);

            // 左侧拖动手柄 + 收起按钮，工具栏可以拖到任意位置，不挡画面
            _floatBar.Child = BuildFloatBarShell(bar);
            _floatBar.Padding = new Thickness(6, 7, 10, 7);
            _floatBar.CornerRadius = new CornerRadius(14);
            _floatBar.Background = new SolidColorBrush(Color.FromArgb(0xD1, 0x14, 0x1A, 0x24));
            _floatBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            _floatBar.BorderThickness = new Thickness(1);
            // 用 Margin 定位（左上角），位置由 PlaceFloatBar 计算并记住
            _floatBar.HorizontalAlignment = HorizontalAlignment.Left;
            _floatBar.VerticalAlignment = VerticalAlignment.Top;
            InitFloatBarPlacement();
            _floatBar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.45, Color = Colors.Black };

            _floatBar.MouseEnter += (_, _) => FadeFloatBar(1);
            _floatBar.MouseLeave += (_, _) =>
            {
                if (!_qualityCombo.IsDropDownOpen && !_fpsCombo.IsDropDownOpen && !_monitorCombo.IsDropDownOpen && !_draggingBar) FadeFloatBar(0.35);
            };
            _monitorCombo.DropDownClosed += (_, _) => { if (!_floatBar.IsMouseOver) FadeFloatBar(0.35); _screenHost.Focus(); };
            _qualityCombo.DropDownClosed += (_, _) => { if (!_floatBar.IsMouseOver) FadeFloatBar(0.35); _screenHost.Focus(); };
            _fpsCombo.DropDownClosed += (_, _) => { if (!_floatBar.IsMouseOver) FadeFloatBar(0.35); _screenHost.Focus(); };
        }

        private void FadeFloatBar(double to)
        {
            _floatBar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to >= 1 ? 120 : 600)));
        }

        private void UpdateStats()
        {
            var client = _client;
            if (client == null || !_sessionActive) return;

            long now = Environment.TickCount64;
            long bytes = client.BytesReceived;
            double seconds = Math.Max(0.001, (now - _lastStatsTick) / 1000.0);
            double rate = (bytes - _lastBytes) / seconds;
            _lastBytes = bytes;
            _lastStatsTick = now;

            string rateText = rate >= 1024 * 1024 ? $"{rate / 1024 / 1024:F1}MB/s" : $"{rate / 1024:F0}KB/s";
            int latency = client.LatencyMs;
            _latencyText.Text = latency >= 0 ? $"{latency}ms · {rateText}" : $"-- · {rateText}";
            _latencyDot.Fill = UiKit.Br(latency < 0 ? "Sub" : latency < 80 ? "Ok" : latency < 200 ? "Warn" : "Danger");
        }

        #endregion

        #region 界面切换

        private void ShowHome()
        {
            _sessionActive = false;
            _statsTimer.Stop();
            if (IsFullScreen) SetFullScreen(false);
            _fullScreenButton.IsChecked = false;
            _fullScreenButton.Content = UiKit.Icon(Glyph.FullScreen);

            _homeView.Visibility = Visibility.Visible;
            _connectingView.Visibility = Visibility.Collapsed;
            _sessionView.Visibility = Visibility.Collapsed;

            ChromeTitle = "简连 LinkDesk";
            SetResizable(false);
            ResizeCentered(HomeWidth, HomeHeight);
            ClearCollaborationUI();
            ResetSessionFeatures();

            _screenImage.Source = null;
            _screenBitmap = null;
        }

        private void ShowConnecting()
        {
            _homeView.Visibility = Visibility.Collapsed;
            _connectingView.Visibility = Visibility.Visible;
            _sessionView.Visibility = Visibility.Collapsed;
            ChromeTitle = "正在建立连接";
            _connectingTitle.Text = $"正在连接 {_target}";
            _connectingDetail.Text = "";
        }

        private void ShowSession()
        {
            _sessionActive = true;
            _homeView.Visibility = Visibility.Collapsed;
            _connectingView.Visibility = Visibility.Collapsed;
            _sessionView.Visibility = Visibility.Visible;

            ChromeTitle = $"简连 LinkDesk · {_target}";
            SetResizable(true);

            // 会话窗口尽量大，但不超过工作区
            var area = SystemParameters.WorkArea;
            ResizeCentered(Math.Min(1360, area.Width * 0.9), Math.Min(860, area.Height * 0.9));

            _lastBytes = _client?.BytesReceived ?? 0;
            _lastStatsTick = Environment.TickCount64;
            _latencyText.Text = "-- · 0KB/s";
            _latencyDot.Fill = UiKit.Br("Sub");
            _statsTimer.Start();

            FadeFloatBar(1);
            _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_sessionActive && !_floatBar.IsMouseOver) FadeFloatBar(0.35);
            }));

            UpdateRemoteLabel(1);
            _screenHost.Focus();
        }

        private void ResizeCentered(double width, double height)
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            var area = SystemParameters.WorkArea;
            Width = width;
            Height = height;
            Left = area.Left + (area.Width - width) / 2;
            Top = area.Top + (area.Height - height) / 2;
        }

        private int _viewerCount = 1;

        private void UpdateRemoteLabel(int viewers)
        {
            _viewerCount = viewers;
            string mode = _client?.UseRelay == true ? "中继" : "直连";
            _remoteIdLabel.Text = UiKit.Spaced("REMOTE") + "  " + UiKit.Spaced("SESSION") +
                                  $"  ·  {_target}  ·  {mode}" + (viewers > 1 ? $"  ·  {viewers} 人在线" : "") +
                                  (_client?.IsRecording == true ? "  ·  录制中" : "");
        }

        #endregion

        #region 多人协作面板

        private const int MaxActivityItems = 200;

        private readonly ListBox _viewerList = new();
        private readonly ListBox _activityList = new();
        private readonly TextBlock _viewerHeader = new();
        private readonly Border _activityToast = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private int _myViewerId;

        private void BuildSidePanel()
        {
            var grid = new Grid { Margin = new Thickness(16, 16, 8, 16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 40 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _viewerHeader.FontSize = 11;
            _viewerHeader.Foreground = UiKit.Br("Sub");
            _viewerHeader.Margin = new Thickness(0, 0, 0, 10);
            grid.Children.Add(_viewerHeader);

            _viewerList.Style = UiKit.St("PlainList");
            _viewerList.MaxHeight = 240;
            Grid.SetRow(_viewerList, 1);
            grid.Children.Add(_viewerList);

            var activityHeader = UiKit.Caption(UiKit.Spaced("操作动态"));
            activityHeader.Margin = new Thickness(0, 18, 0, 10);
            Grid.SetRow(activityHeader, 2);
            grid.Children.Add(activityHeader);

            _activityList.Style = UiKit.St("PlainList");
            _activityList.FontSize = 12;
            Grid.SetRow(_activityList, 3);
            grid.Children.Add(_activityList);

            _sidePanel.Width = 250;
            _sidePanel.Background = UiKit.Br("Panel");
            _sidePanel.BorderBrush = UiKit.Br("Line");
            _sidePanel.BorderThickness = new Thickness(1, 0, 0, 0);
            _sidePanel.Child = grid;

            // 画面左上角浮动显示别人最新的一条操作，不用看侧栏也知道别人在干什么
            _activityToast.Background = new SolidColorBrush(Color.FromArgb(0xD1, 0x14, 0x1A, 0x24));
            _activityToast.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            _activityToast.BorderThickness = new Thickness(1);
            _activityToast.CornerRadius = new CornerRadius(10);
            _activityToast.Padding = new Thickness(12, 7, 12, 7);
            _activityToast.Margin = new Thickness(16, 70, 0, 0);
            _activityToast.HorizontalAlignment = HorizontalAlignment.Left;
            _activityToast.VerticalAlignment = VerticalAlignment.Top;
            _activityToast.IsHitTestVisible = false; // 不挡鼠标操作
            _activityToast.Visibility = Visibility.Collapsed;
            _activityToast.Child = new TextBlock { Foreground = UiKit.Br("Txt"), FontSize = 13 };

            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                _activityToast.Visibility = Visibility.Collapsed;
            };

            ClearCollaborationUI();
        }

        private void OnViewerList(RemoteClientWithRelay client, ViewerListMessage msg)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                _myViewerId = msg.YourId;

                _viewerHeader.Text = UiKit.Spaced("在线成员") + $"  ·  {msg.Viewers.Length}";
                UpdateRemoteLabel(msg.Viewers.Length);

                _viewerList.Items.Clear();
                foreach (var v in msg.Viewers)
                {
                    _viewerList.Items.Add(ViewerRow(v, v.Id == msg.YourId, v.Id == msg.ControllerId));
                }
            });
        }

        private static UIElement ViewerRow(ViewerInfo v, bool me, bool controlling)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 8, 4), ToolTip = v.Address };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 头像：名字首字 + 个人颜色
            row.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = UiKit.ViewerBrush(v.Id),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(v.Name) ? "?" : v.Name[..1].ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiKit.Br("AccText"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var name = UiKit.Text(me ? $"{v.Name}（我）" : v.Name, 13, "Txt", bold: controlling);
            name.Margin = new Thickness(10, 0, 6, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            if (controlling)
            {
                var chip = UiKit.Chip(new TextBlock { Text = "操作中" });
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }

            return row;
        }

        private void OnActivity(RemoteClientWithRelay client, InputActivityMessage a)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;

                bool mine = a.ViewerId == _myViewerId;
                string who = mine ? "我" : a.Name;
                string what = ActivityFormatter.Describe(a);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(a.Timestamp).LocalDateTime;

                // 连续按键合并到同一行，打字时动态栏不会被刷屏
                if (a.Kind == ActivityKind.Key && _activityList.Items.Count > 0 &&
                    _activityList.Items[^1] is TextBlock last && last.Tag is int lastViewer && lastViewer == a.ViewerId)
                {
                    last.Inlines.Add(new Run(" " + ActivityFormatter.KeyText(a.KeyCode, a.Modifiers)) { Foreground = UiKit.Br("Txt") });
                }
                else
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 8, 3), Foreground = UiKit.Br("Sub") };
                    line.Inlines.Add(new Run($"{time:HH:mm:ss}  ") { Foreground = UiKit.Br("Placeholder") });
                    line.Inlines.Add(new Run(who) { Foreground = UiKit.ViewerBrush(a.ViewerId), FontWeight = FontWeights.SemiBold });
                    line.Inlines.Add(new Run(" " + what) { Foreground = a.Kind == ActivityKind.Key ? UiKit.Br("Txt") : UiKit.Br("Sub") });
                    line.Tag = a.Kind == ActivityKind.Key ? a.ViewerId : null;
                    _activityList.Items.Add(line);
                    while (_activityList.Items.Count > MaxActivityItems)
                    {
                        _activityList.Items.RemoveAt(0);
                    }
                }
                _activityList.ScrollIntoView(_activityList.Items[^1]);

                // 别人的操作在画面上浮动提示；自己的就不打扰了
                if (!mine)
                {
                    var toastText = (TextBlock)_activityToast.Child;
                    toastText.Inlines.Clear();
                    toastText.Inlines.Add(new Run(a.Name) { Foreground = UiKit.ViewerBrush(a.ViewerId), FontWeight = FontWeights.SemiBold });
                    toastText.Inlines.Add(new Run(" " + what));
                    _activityToast.Visibility = Visibility.Visible;
                    _toastTimer.Stop();
                    _toastTimer.Start();
                }
            });
        }

        private void ClearCollaborationUI()
        {
            _viewerList.Items.Clear();
            _activityList.Items.Clear();
            _viewerHeader.Text = UiKit.Spaced("在线成员");
            _activityToast.Visibility = Visibility.Collapsed;
            _myViewerId = 0;
        }

        #endregion

        #region 连接

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) return;

            bool useRelay = _relayRadio.IsChecked == true;
            string password = _passwordBox.Password;
            string relayHost = _relayHostBox.Text.Trim();
            string deviceId = _deviceIdBox.Text.Replace(" ", "").Trim(); // 允许 "123 456 789" 这种带空格的写法
            string directHost = _directHostBox.Text.Trim();

            // 输入校验：错误直接显示在密码框下方
            string? error = null;
            if (!TryParsePort(useRelay ? _relayPortBox.Text : _directPortBox.Text, out int port))
                error = "端口必须是 1-65535 之间的数字";
            else if (useRelay && string.IsNullOrEmpty(relayHost))
                error = "请先在「设置」里填写中继服务器地址";
            else if (useRelay && string.IsNullOrEmpty(deviceId))
                error = "请输入对方的识别码";
            else if (!useRelay && string.IsNullOrEmpty(directHost))
                error = "请输入对方的 IP 地址";
            else if (string.IsNullOrEmpty(password))
                error = "请输入连接密码";

            if (error != null)
            {
                ShowHomeError(error);
                if (error.Contains("设置")) _settingsPanel.Visibility = Visibility.Visible;
                return;
            }
            ShowHomeError(null);

            // 保存设置（不保存密码）
            _settings.UseRelay = useRelay;
            if (useRelay)
            {
                _settings.RelayHost = relayHost;
                _settings.RelayPort = port;
                _settings.DeviceId = deviceId;
            }
            else
            {
                _settings.DirectHost = directHost;
                _settings.DirectPort = port;
            }
            _settings.NickName = _nickNameBox.Text.Trim();
            _settings.Save(SettingsName);

            _target = useRelay ? UiKit.FormatId(deviceId) : directHost;
            _lastStatus = null;
            ShowConnecting();

            var client = new RemoteClientWithRelay { NickName = _settings.NickName };
            client.ViewerListReceived += msg => OnViewerList(client, msg);
            client.ActivityReceived += a => OnActivity(client, a);
            client.StatusChanged += s => OnStatusChanged(client, s);
            client.FrameReady += () => OnFrameReady(client);
            client.Connected += () => OnConnected(client);
            client.Disconnected += () => OnDisconnected(client);
            AttachFeatureEvents(client);
            _client = client;

            bool success = useRelay
                ? await client.ConnectRelayAsync(relayHost, port, deviceId, password)
                : await client.ConnectDirectAsync(directHost, port, password);

            // 等待期间用户点了取消，或者已经被 OnDisconnected 复位
            if (_client != client) return;

            if (!success)
            {
                _client = null;
                client.Dispose();
                ShowHome();
                ShowHomeError(_lastStatus ?? "连接失败");
            }
        }

        private static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text.Trim(), out port) && port >= 1 && port <= 65535;
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            var client = _client;
            _client = null;
            client?.Dispose();
            ShowHome();
        }

        private void OnConnected(RemoteClientWithRelay client)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                ShowSession();
                RememberDevice(client);

                // 把界面上当前选的画质和帧率同步给被控端
                _ = client.SetQualityAsync(IndexToQuality(_qualityCombo.SelectedIndex));
                _ = client.SetFrameRateAsync(IndexToFps(_fpsCombo.SelectedIndex));
            });
        }

        private static Quality IndexToQuality(int index) => index switch
        {
            0 => Quality.Low,
            1 => Quality.Medium,
            2 => Quality.High,
            3 => Quality.Ultra,
            _ => Quality.Medium
        };

        private static int IndexToFps(int index) => index switch
        {
            0 => 15,
            2 => 60,
            _ => 30
        };

        private void OnDisconnected(RemoteClientWithRelay client)
        {
            // BeginInvoke：可能在网络线程里、且 Disconnect() 还没返回时触发，不能同步等 UI
            Dispatcher.BeginInvoke(() =>
            {
                // 旧连接迟到的断开通知，不能把新连接也关掉
                if (_client != client) return;

                bool wasInSession = _sessionActive;
                _client = null;
                client.Dispose();
                ShowHome();
                ShowHomeError(wasInSession ? "连接已断开" : _lastStatus ?? "连接失败");
            });
        }

        private string? _lastStatus;

        private void OnStatusChanged(RemoteClientWithRelay client, string status)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                _lastStatus = status;
                _connectingDetail.Text = status;
            });
        }

        #endregion

        #region 画面

        private WriteableBitmap? _screenBitmap;
        private int _renderQueued;

        private void OnFrameReady(RemoteClientWithRelay client)
        {
            // 同一时间只排队一次刷新：界面慢了就合并成一次，只显示最新帧，不会越积越多
            if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;

            // BeginInvoke：不阻塞网络接收线程
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (client != _client) return;

                var frame = client.Frames.TakeLatest();
                if (frame == null) return;

                // 分辨率不变时复用同一个 WriteableBitmap，只拷贝像素，不重新创建图片
                bool sizeChanged = _screenBitmap == null ||
                                   _screenBitmap.PixelWidth != frame.Width ||
                                   _screenBitmap.PixelHeight != frame.Height;
                if (sizeChanged)
                {
                    _screenBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    _screenImage.Source = _screenBitmap;
                }

                _screenBitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);

                if (sizeChanged)
                {
                    ApplyScaleMode();
                }
            });
        }

        private void ApplyScaleMode()
        {
            // 自适应模式要禁用滚动条，否则 ScrollViewer 给的是无限空间，Uniform 不会缩放
            bool original = _currentScaleMode == ScaleMode.Original;
            _screenScroll.HorizontalScrollBarVisibility = original ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            _screenScroll.VerticalScrollBarVisibility = original ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            if (original && _client != null && _client.RemoteScreenWidth > 0)
            {
                // 原始尺寸按被控端真实分辨率显示（低画质档位传输的是缩小图，这里放大回 1:1）。
                // 按本机 DPI 换算，保证是物理像素 1:1
                var dpi = VisualTreeHelper.GetDpi(this);
                _screenImage.Stretch = Stretch.Fill;
                _screenImage.Width = _client.RemoteScreenWidth / dpi.DpiScaleX;
                _screenImage.Height = _client.RemoteScreenHeight / dpi.DpiScaleY;
                _screenImage.HorizontalAlignment = HorizontalAlignment.Left;
                _screenImage.VerticalAlignment = VerticalAlignment.Top;
            }
            else
            {
                _screenImage.Stretch = Stretch.Uniform;
                _screenImage.Width = double.NaN;
                _screenImage.Height = double.NaN;
                _screenImage.HorizontalAlignment = HorizontalAlignment.Center;
                _screenImage.VerticalAlignment = VerticalAlignment.Center;
            }

            // 对齐方式变了但尺寸可能没变，SizeChanged 不会触发，这里手动同步标注层
            SyncAnnotationLayer();
        }

        #endregion

        #region 鼠标键盘

        private bool _isMouseCaptured;
        private Point _lastMousePosition;
        private const int MouseMoveIntervalMs = 16;
        private long _lastMouseSendTick;
        private bool _mouseMovePending;

        private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_client?.IsConnected != true || _screenImage.Source == null) return;

            var pos = e.GetPosition(_screenImage);
            _lastMousePosition = pos;

            // WPF 的 MouseMove 每秒能触发上百次，每次都发会和画面数据抢中继带宽。
            // 限制为每 16ms（约 60 次/秒）发一次最新位置，中间的位置直接丢弃。
            long now = Environment.TickCount64;
            if (now - _lastMouseSendTick < MouseMoveIntervalMs)
            {
                if (!_mouseMovePending)
                {
                    // 保证停下时最后一个位置一定会发出去
                    _mouseMovePending = true;
                    _ = FlushMouseMoveAsync();
                }
                return;
            }

            SendMouseMove(pos);
        }

        private void SendMouseMove(Point pos)
        {
            if (_client?.IsConnected != true) return;

            _lastMouseSendTick = Environment.TickCount64;
            var remotePos = TransformToRemoteCoordinates(pos);
            _ = _client.SendMouseMoveAsync(remotePos.X, remotePos.Y);
        }

        private async Task FlushMouseMoveAsync()
        {
            await Task.Delay(MouseMoveIntervalMs);
            _mouseMovePending = false;
            SendMouseMove(_lastMousePosition);
        }

        private static MouseButton ToRemoteButton(System.Windows.Input.MouseButton button) => button switch
        {
            System.Windows.Input.MouseButton.Right => MouseButton.Right,
            System.Windows.Input.MouseButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left
        };

        private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_client?.IsConnected != true || _screenImage.Source == null) return;

            _screenHost.Focus();
            var remotePos = TransformToRemoteCoordinates(e.GetPosition(_screenImage));
            _ = _client.SendMouseClickAsync(remotePos.X, remotePos.Y, ToRemoteButton(e.ChangedButton), MouseEventType.Down);

            if (!_isMouseCaptured)
            {
                _screenImage.CaptureMouse();
                _isMouseCaptured = true;
            }
            e.Handled = true;
        }

        private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_client?.IsConnected != true || _screenImage.Source == null) return;

            var remotePos = TransformToRemoteCoordinates(e.GetPosition(_screenImage));
            _ = _client.SendMouseClickAsync(remotePos.X, remotePos.Y, ToRemoteButton(e.ChangedButton), MouseEventType.Up);

            if (_isMouseCaptured)
            {
                _screenImage.ReleaseMouseCapture();
                _isMouseCaptured = false;
            }
            e.Handled = true;
        }

        private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_client?.IsConnected != true) return;

            _ = _client.SendMouseWheelAsync(e.Delta);
            e.Handled = true; // 原始尺寸模式下不让本地 ScrollViewer 跟着滚
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_sessionActive || _client?.IsConnected != true) return;

            int keyCode = RealVirtualKey(e);
            if (keyCode == 0) return;
            _ = _client.SendKeyPressAsync(keyCode, true);
            e.Handled = true; // Tab 不切换本地焦点，Alt、F10 不触发本地窗口菜单
        }

        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_sessionActive || _client?.IsConnected != true) return;

            int keyCode = RealVirtualKey(e);
            if (keyCode == 0) return;
            _ = _client.SendKeyPressAsync(keyCode, false);
            e.Handled = true;
        }

        /// <summary>
        /// 按住 Alt 时 WPF 的 e.Key 是 Key.System，真正的键在 SystemKey 里；
        /// 输入法处理中的键在 ImeProcessedKey 里
        /// </summary>
        private static int RealVirtualKey(KeyEventArgs e)
        {
            Key key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                _ => e.Key
            };
            return KeyInterop.VirtualKeyFromKey(key);
        }

        private (int X, int Y) TransformToRemoteCoordinates(Point localPos)
        {
            if (_screenImage.Source == null || _client == null)
                return (0, 0);

            if (_screenImage.ActualWidth <= 0 || _screenImage.ActualHeight <= 0)
                return (0, 0);

            // 按被控端真实分辨率换算（传输画面可能被缩小过，但坐标以真实分辨率为准）
            double scaleX = _client.RemoteScreenWidth / _screenImage.ActualWidth;
            double scaleY = _client.RemoteScreenHeight / _screenImage.ActualHeight;

            int remoteX = Math.Clamp((int)(localPos.X * scaleX), 0, Math.Max(0, _client.RemoteScreenWidth - 1));
            int remoteY = Math.Clamp((int)(localPos.Y * scaleY), 0, Math.Max(0, _client.RemoteScreenHeight - 1));

            return (remoteX, remoteY);
        }

        #endregion

        #region 画质 / 帧率

        private void QualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_client?.IsConnected != true) return;
            _ = _client.SetQualityAsync(IndexToQuality(_qualityCombo.SelectedIndex));
        }

        private void FpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_client?.IsConnected != true) return;
            _ = _client.SetFrameRateAsync(IndexToFps(_fpsCombo.SelectedIndex));
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _client?.Dispose();
            base.OnClosed(e);
        }
    }
}
