using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteControl.Common;
using RemoteControl.Ui;

namespace RemoteControl.Server
{
    /// <summary>
    /// 被控端主窗口（简连 LinkToMyDesk 风格）：本机识别码卡片 + 连接方式 + 访问者 / 日志
    /// </summary>
    public partial class MainWindow : LinkToMyDeskWindow
    {
        private const string SettingsName = "server";
        private const int MaxLogLines = 500;

        private RemoteServer? _server;
        private readonly AppSettings _settings = AppSettings.Load(SettingsName);

        // 设置
        private readonly RadioButton _relayRadio = new() { Content = "中继（推荐）", GroupName = "Mode" };
        private readonly RadioButton _directRadio = new() { Content = "直连", GroupName = "Mode" };
        private readonly TextBox _relayHostBox = new();
        private readonly TextBox _relayPortBox = new() { Width = 70 };
        private readonly TextBox _directPortBox = new();
        private readonly Grid _relayFields = new();
        private readonly Border _directField;
        private readonly CheckBox _approvalCheck = new();
        private readonly CheckBox _unattendedCheck = new();
        private AnnotationOverlay? _overlay;

        // 识别码卡片
        private readonly TextBlock _idText = new();
        private readonly TextBox _passwordBox = new();
        private readonly TextBlock _passwordChip = new();
        private readonly Border _passwordChipBorder;
        private readonly Button _refreshButton;

        // 主按钮
        private readonly Button _startButton;
        private readonly Button _stopButton;

        // 访问者 / 日志
        private readonly RadioButton _viewersTab = new() { GroupName = "Tab", IsChecked = true };
        private readonly RadioButton _logTab = new() { Content = "运行日志", GroupName = "Tab" };
        private readonly ListBox _viewerList = new();
        private readonly TextBlock _viewerEmpty = new();
        private readonly ListBox _logList = new();

        // 底部状态
        private readonly Ellipse _statusDot = UiKit.StatusDot();
        private readonly TextBlock _statusText = new();
        private readonly TextBlock _subtitle = new();

        public MainWindow()
        {
            // 设备ID 首次生成后固定下来，客户端才能一直用同一个ID连
            if (string.IsNullOrWhiteSpace(_settings.DeviceId))
            {
                _settings.DeviceId = RemoteServer.GenerateDeviceId();
                _settings.Save(SettingsName);
            }

            ChromeTitle = "简连 LinkToMyDesk · 被控端";
            Width = 420;
            Height = 700;
            MinHeight = 600;

            _directField = UiKit.Field(_directPortBox, "监听端口，如 5900");
            _passwordChipBorder = UiKit.Chip(_passwordChip);
            _refreshButton = UiKit.Button("IconButton", UiKit.Icon(Glyph.Refresh, 12), (_, _) => RefreshPassword(), "换一个密码");
            _startButton = UiKit.Button("PrimaryButton", "开 启 远 程 协 助", StartButton_Click);
            _stopButton = UiKit.Button("DangerGhostButton", "停止服务", StopButton_Click);

            Body = BuildBody();
            ResetUI();
            InitUnattended();
        }

        #region 界面

        private UIElement BuildBody()
        {
            var root = new Grid { Margin = new Thickness(30, 26, 30, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 问候
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 识别码卡片
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 连接方式
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 按钮
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 访问者 / 日志
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 底栏

            // 问候（.greet）
            var greet = new StackPanel();
            greet.Children.Add(UiKit.Text($"{UiKit.Greeting()}，{Environment.UserName}", 20, "Txt", bold: true));
            _subtitle.FontSize = 12;
            _subtitle.Foreground = UiKit.Br("Sub");
            _subtitle.Margin = new Thickness(0, 5, 0, 0);
            greet.Children.Add(_subtitle);
            Add(root, greet, 0);

            Add(root, BuildIdCard(), 1);
            Add(root, BuildSettings(), 2);

            // 开启 / 停止
            var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            _startButton.Height = 44;
            _stopButton.Height = 44;
            buttons.Children.Add(_startButton);
            buttons.Children.Add(_stopButton);
            Add(root, buttons, 3);

            Add(root, BuildTabs(), 4);

            // 底栏（.footer）
            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            var status = new StackPanel { Orientation = Orientation.Horizontal };
            status.Children.Add(_statusDot);
            _statusText.FontSize = 12;
            _statusText.Foreground = UiKit.Br("Sub");
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(_statusText);
            footer.Children.Add(status);
            Add(root, footer, 5);

            return root;
        }

        private static void Add(Grid grid, UIElement child, int row)
        {
            Grid.SetRow(child, row);
            grid.Children.Add(child);
        }

        /// <summary>本机识别码 + 密码卡片（.mycard）</summary>
        private Border BuildIdCard()
        {
            var content = new StackPanel();
            content.Children.Add(UiKit.Caption("本 机 识 别 码"));

            _idText.Text = UiKit.FormatId(_settings.DeviceId);
            _idText.FontSize = 26;
            _idText.FontWeight = FontWeights.Bold;
            _idText.Foreground = UiKit.Br("Txt");
            _idText.Margin = new Thickness(0, 6, 0, 10);
            System.Windows.Documents.Typography.SetNumeralAlignment(_idText, FontNumeralAlignment.Tabular);
            content.Children.Add(_idText);

            // 密码直接显示：要口头报给对方。未启动时可以改成自己想要的
            _passwordBox.Style = UiKit.St("BareTextBox");
            _passwordBox.Padding = new Thickness(0);
            _passwordBox.FontSize = 15;
            _passwordBox.FontWeight = FontWeights.SemiBold;
            _passwordBox.Foreground = UiKit.Br("Acc");
            _passwordBox.MaxLength = 16;
            _passwordBox.MinWidth = 70;
            _passwordBox.ToolTip = "连接密码（未启动时可点击修改）";
            _passwordBox.Text = Random.Shared.Next(100_000, 1_000_000).ToString(); // 每次启动随机 6 位
            System.Windows.Documents.Typography.SetNumeralAlignment(_passwordBox, FontNumeralAlignment.Tabular);

            var copyButton = UiKit.Button("IconButton", UiKit.Icon(Glyph.Copy, 12), (_, _) => CopyCredentials(), "复制识别码和密码");
            copyButton.Margin = new Thickness(6, 0, 0, 0);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(_passwordBox);
            _passwordChipBorder.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(_passwordChipBorder, 1);
            row.Children.Add(_passwordChipBorder);
            Grid.SetColumn(_refreshButton, 3);
            row.Children.Add(_refreshButton);
            Grid.SetColumn(copyButton, 4);
            row.Children.Add(copyButton);
            content.Children.Add(row);

            var card = UiKit.Card(content);
            card.Margin = new Thickness(0, 22, 0, 0);
            return card;
        }

        private UIElement BuildSettings()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };

            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = "连 接 方 式",
                FontSize = 11,
                Foreground = UiKit.Br("Sub"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var segment = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _relayRadio.Style = UiKit.St("SegmentRadio");
            _directRadio.Style = UiKit.St("SegmentRadio");
            _relayRadio.ToolTip = "双方都在内网时使用，经中继服务器转发";
            _directRadio.ToolTip = "局域网内，或本机有公网 IP / 端口转发时使用";
            segment.Children.Add(_relayRadio);
            segment.Children.Add(_directRadio);
            header.Children.Add(segment);
            panel.Children.Add(header);

            // 中继：地址 + 端口
            _relayFields.Margin = new Thickness(0, 10, 0, 0);
            _relayFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _relayFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            var hostField = UiKit.Field(_relayHostBox, "中继服务器地址");
            var portField = UiKit.Field(_relayPortBox, "端口");
            portField.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(portField, 1);
            _relayFields.Children.Add(hostField);
            _relayFields.Children.Add(portField);
            panel.Children.Add(_relayFields);

            _directField.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(_directField);

            // 接入选项（.opts）
            var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _approvalCheck.Style = UiKit.St("DotCheckBox");
            _approvalCheck.Content = "对方确认后接入";
            _approvalCheck.ToolTip = "有人连接时弹窗询问，点「允许」后才能看到和操作本机";
            _approvalCheck.IsChecked = _settings.RequireApproval;
            _approvalCheck.Click += (_, _) =>
            {
                _settings.RequireApproval = _approvalCheck.IsChecked == true;
                _settings.Save(SettingsName);
                ApplyApprovalSetting();
            };
            opts.Children.Add(_approvalCheck);

            _unattendedCheck.Style = UiKit.St("DotCheckBox");
            _unattendedCheck.Content = "无人值守模式";
            _unattendedCheck.Margin = new Thickness(18, 0, 0, 0);
            _unattendedCheck.ToolTip = "安装为系统服务：开机自动运行，锁屏、登录界面也能远程操作并发送 Ctrl+Alt+Del。需要设置固定密码";
            // 以服务是否真的装着为准，设置文件只是记录
            _unattendedCheck.IsChecked = Unattended.ServiceInstaller.IsInstalled();
            _unattendedCheck.Click += (_, _) => ToggleUnattended(_unattendedCheck.IsChecked == true);
            opts.Children.Add(_unattendedCheck);
            panel.Children.Add(opts);

            // 填入上次的设置
            _relayHostBox.Text = _settings.RelayHost;
            _relayPortBox.Text = _settings.RelayPort.ToString();
            _directPortBox.Text = _settings.DirectPort.ToString();

            _relayRadio.Checked += (_, _) => UpdateModeVisibility();
            _directRadio.Checked += (_, _) => UpdateModeVisibility();
            _relayRadio.IsChecked = _settings.UseRelay;
            _directRadio.IsChecked = !_settings.UseRelay;
            UpdateModeVisibility();

            return panel;
        }

        private UIElement BuildTabs()
        {
            var grid = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, 0, 0, 8) };
            _viewersTab.Style = UiKit.St("SegmentRadio");
            _logTab.Style = UiKit.St("SegmentRadio");
            tabs.Children.Add(_viewersTab);
            tabs.Children.Add(_logTab);
            grid.Children.Add(tabs);

            var box = new Border
            {
                Background = UiKit.Br("Panel2"),
                BorderBrush = UiKit.Br("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 6, 8)
            };
            var content = new Grid();

            _viewerList.Style = UiKit.St("PlainList");
            _viewerEmpty.Text = "暂无访问者";
            _viewerEmpty.FontSize = 12;
            _viewerEmpty.Foreground = UiKit.Br("Placeholder");
            _viewerEmpty.HorizontalAlignment = HorizontalAlignment.Center;
            _viewerEmpty.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(_viewerEmpty);
            content.Children.Add(_viewerList);

            _logList.Style = UiKit.St("PlainList");
            _logList.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei");
            _logList.FontSize = 11;
            content.Children.Add(_logList);

            box.Child = content;
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            _viewersTab.Checked += (_, _) => UpdateTab();
            _logTab.Checked += (_, _) => UpdateTab();
            UpdateTab();
            UpdateViewerTab(0);

            return grid;
        }

        private void UpdateTab()
        {
            bool viewers = _viewersTab.IsChecked == true;
            _viewerList.Visibility = viewers ? Visibility.Visible : Visibility.Collapsed;
            _viewerEmpty.Visibility = viewers && _viewerList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _logList.Visibility = viewers ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateViewerTab(int count)
        {
            _viewersTab.Content = count > 0 ? $"访问者 · {count}" : "访问者";
        }

        private void UpdateModeVisibility()
        {
            bool relay = _relayRadio.IsChecked == true;
            _relayFields.Visibility = relay ? Visibility.Visible : Visibility.Collapsed;
            _directField.Visibility = relay ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetSettingsEnabled(bool enabled)
        {
            _relayRadio.IsEnabled = enabled;
            _directRadio.IsEnabled = enabled;
            _relayHostBox.IsEnabled = enabled;
            _relayPortBox.IsEnabled = enabled;
            _directPortBox.IsEnabled = enabled;
            _passwordBox.IsReadOnly = !enabled;
            _passwordBox.Cursor = enabled ? System.Windows.Input.Cursors.IBeam : System.Windows.Input.Cursors.Arrow;
        }

        private void SetStatus(string text, string dotBrush, bool glow = true)
        {
            _statusText.Text = text;
            UiKit.SetDot(_statusDot, dotBrush, glow);
        }

        private void SetChip(string text, bool active)
        {
            _passwordChip.Text = text;
            _passwordChip.Foreground = UiKit.Br(active ? "Acc" : "Sub");
            _passwordChipBorder.Background = active ? UiKit.Br("AccSoft") : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        }

        private void RefreshPassword()
        {
            _passwordBox.Text = Random.Shared.Next(100_000, 1_000_000).ToString();

            // 运行中换密码：已连接的人不受影响，之后的新连接要用新密码
            if (_server != null)
            {
                _server.Password = _passwordBox.Text;
                OnStatusChanged("已更换连接密码，新连接需使用新密码");
            }
        }

        private void CopyCredentials()
        {
            try
            {
                Clipboard.SetText($"识别码：{UiKit.FormatId(_settings.DeviceId)}\n密码：{_passwordBox.Text.Trim()}");
                OnStatusChanged("识别码和密码已复制到剪贴板");
            }
            catch
            {
                // 剪贴板被其他程序占用，忽略
            }
        }

        #endregion

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            bool useRelay = _relayRadio.IsChecked == true;
            string relayHost = _relayHostBox.Text.Trim();
            string password = _passwordBox.Text.Trim();

            // 输入校验
            if (password.Length < 4)
            {
                MessageBox.Show(this, "连接密码至少 4 位", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (useRelay && string.IsNullOrEmpty(relayHost))
            {
                MessageBox.Show(this, "请填写中继服务器地址（IP 或域名）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!TryParsePort(useRelay ? _relayPortBox.Text : _directPortBox.Text, out int port))
            {
                MessageBox.Show(this, "端口必须是 1-65535 之间的数字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存设置，下次启动自动填回
            _settings.UseRelay = useRelay;
            if (useRelay)
            {
                _settings.RelayHost = relayHost;
                _settings.RelayPort = port;
            }
            else
            {
                _settings.DirectPort = port;
            }
            _settings.Save(SettingsName);

            var server = new RemoteServer
            {
                DeviceId = _settings.DeviceId,
                Password = password
            };
            server.StatusChanged += OnStatusChanged;
            server.ViewersChanged += OnViewersChanged;
            server.Activity += OnActivity;
            server.AnnotationReceived += msg => Dispatcher.BeginInvoke(() =>
            {
                if (_server != server) return;
                _overlay ??= new AnnotationOverlay();
                var monitors = server.Monitors;
                int index = msg.Kind == AnnotationKind.Clear ? -1 : CurrentMonitorIndex(server);
                _overlay.Apply(msg, index >= 0 && index < monitors.Length ? monitors[index] : null);
            });
            _server = server;
            ApplyApprovalSetting();

            SetSettingsEnabled(false);
            _startButton.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Visible;
            SetChip("密码有效", true);

            if (useRelay)
            {
                SetStatus($"正在连接中继节点 · {relayHost}", "Warn");
                _subtitle.Text = "正在连接中继服务器…";
            }
            else
            {
                SetStatus($"直连模式 · 监听端口 {port}", "Ok");
                _subtitle.Text = $"本机就绪 · 等待连接（端口 {port}）";
            }

            // StartAsync / StartRelayModeAsync 会一直运行到服务停止，不能在这里 await 后再更新界面
            Task task = useRelay
                ? server.StartRelayModeAsync(relayHost, port)
                : server.StartAsync(port);
            _ = WatchServerAsync(server, task);

            if (useRelay) _ = WatchRegistrationAsync(server, relayHost);
        }

        /// <summary>中继确认注册后，底栏才显示"已连接"（此时对方才能用识别码连进来）</summary>
        private async Task WatchRegistrationAsync(RemoteServer server, string relayHost)
        {
            for (int i = 0; i < 150 && _server == server; i++)
            {
                if (server.IsRegistered)
                {
                    SetStatus($"已连接中继节点 · {relayHost}", "Ok");
                    _subtitle.Text = $"本机就绪 · {Environment.MachineName}";
                    return;
                }
                await Task.Delay(100);
            }
        }

        /// <summary>
        /// 服务结束（主动停止、中继断线、端口被占用等）后把界面恢复到可重新启动的状态
        /// </summary>
        private async Task WatchServerAsync(RemoteServer server, Task runTask)
        {
            try
            {
                await runTask;
            }
            catch (Exception ex)
            {
                OnStatusChanged($"服务异常退出: {ex.Message}");
            }

            // 用户已经点了停止或又启动了新实例，就不用管了
            if (_server != server) return;

            server.Dispose();
            _server = null;
            ResetUI();
            SetStatus("服务已断开，请检查网络后重新开启", "Danger");
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var server = _server;
            _server = null;
            server?.Stop();
            server?.Dispose();
            ResetUI();
        }

        private void ResetUI()
        {
            SetSettingsEnabled(true);
            _startButton.Visibility = Visibility.Visible;
            _stopButton.Visibility = Visibility.Collapsed;
            ChromeTitle = "简连 LinkToMyDesk · 被控端";
            SetChip("服务未开启", false);
            SetStatus("未开启 · 开启后对方可用识别码连接本机", "Line", glow: false);
            _subtitle.Text = $"本机设备「{Environment.MachineName}」";
            _viewerList.Items.Clear();
            UpdateViewerTab(0);
            UpdateTab();
            _overlay?.Apply(new AnnotationMessage { Kind = AnnotationKind.Clear }, null);
        }

        private static int CurrentMonitorIndex(RemoteServer server) => server.SelectedMonitor;

        /// <summary>勾选"对方确认后接入"时挂上确认弹窗；运行中改了也立即生效（已连接的人不受影响）</summary>
        private void ApplyApprovalSetting()
        {
            var server = _server;
            if (server == null) return;

            server.ApprovalHandler = _approvalCheck.IsChecked != true ? null : (name, address) => Dispatcher.Invoke(() =>
            {
                var dialog = new ApprovalDialog(name, address);
                var tcs = new TaskCompletionSource<bool>();
                dialog.Closed += (_, _) => tcs.TrySetResult(dialog.Allowed);
                dialog.Show();
                dialog.Activate();
                return tcs.Task;
            });
        }

        private static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text.Trim(), out port) && port >= 1 && port <= 65535;
        }

        private void OnStatusChanged(string status)
        {
            // BeginInvoke：推流线程和网络线程都会打日志，不能等界面
            Dispatcher.BeginInvoke(() => AppendLog($"{DateTime.Now:HH:mm:ss}  {status}"));
        }

        /// <summary>加一行日志（界面线程）。text 开头是 "HH:mm:ss  " 时间戳</summary>
        private void AppendLog(string text)
        {
            string time = text.Length > 10 && text[2] == ':' ? text[..10] : "";
            string body = text[time.Length..];

            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.Add(new System.Windows.Documents.Run(time) { Foreground = UiKit.Br("Placeholder") });
            line.Inlines.Add(new System.Windows.Documents.Run(body)
            {
                Foreground = body.Contains("失败") || body.Contains("出错") || body.Contains("断开")
                    ? UiKit.Br("Danger")
                    : UiKit.Br("Sub")
            });

            _logList.Items.Add(line);
            while (_logList.Items.Count > MaxLogLines) _logList.Items.RemoveAt(0);
            _logList.ScrollIntoView(line);
        }

        private void OnViewersChanged(IReadOnlyList<ViewerInfo> viewers, int controllerId)
        {
            // BeginInvoke：可能在网络线程里、持有锁时触发
            Dispatcher.BeginInvoke(() =>
            {
                if (_server == null) return;
                ShowViewers(viewers, controllerId);
            });
        }

        /// <summary>刷新访问者列表（界面线程）。无人值守时数据来自 agent</summary>
        private void ShowViewers(IReadOnlyList<ViewerInfo> viewers, int controllerId)
        {
            ChromeTitle = viewers.Count > 0 ? $"简连 LinkToMyDesk · {viewers.Count} 人访问中" : "简连 LinkToMyDesk · 被控端";
            UpdateViewerTab(viewers.Count);

            _viewerList.Items.Clear();
            foreach (var v in viewers)
            {
                _viewerList.Items.Add(ViewerRow(v, v.Id == controllerId));
            }
            UpdateTab();
        }

        private static UIElement ViewerRow(ViewerInfo v, bool controlling)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 6, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 头像：名字首字 + 个人颜色
            var avatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = UiKit.ViewerBrush(v.Id),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(v.Name) ? "?" : v.Name[..1].ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiKit.Br("AccText"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            row.Children.Add(avatar);

            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(UiKit.Text(v.Name, 13, "Txt", bold: true));
            info.Children.Add(UiKit.Text(v.Address, 11, "Sub"));
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            if (controlling)
            {
                var chip = UiKit.Chip(new TextBlock { Text = "操作中" });
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }

            return row;
        }

        private void OnActivity(InputActivityMessage a)
        {
            // 按键可能很多，只记加入、离开、换人，逐键记录在控制端的动态栏里看
            if (a.Kind is ActivityKind.Key or ActivityKind.Click) return;
            OnStatusChanged($"{a.Name} {ActivityFormatter.Describe(a)}");
        }

        protected override void OnClosed(EventArgs e)
        {
            _agentStatus?.Dispose();
            _overlay?.Close();
            _server?.Dispose();
            base.OnClosed(e);
        }
    }
}
