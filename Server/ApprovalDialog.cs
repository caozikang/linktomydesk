using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteControl.Ui;

namespace RemoteControl.Server
{
    /// <summary>
    /// "对方请求控制本机"确认窗口：允许 / 拒绝，倒计时结束自动拒绝
    /// </summary>
    public sealed class ApprovalDialog : LinkToMyDeskWindow
    {
        private const int TimeoutSeconds = 30;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Button _denyButton;
        private int _left = TimeoutSeconds;

        public bool Allowed { get; private set; }

        public ApprovalDialog(string name, string address)
        {
            ChromeTitle = "连接请求";
            Width = 380;
            Height = 270;
            Topmost = true;
            ShowInTaskbar = true;

            var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            panel.Children.Add(UiKit.Text($"{name} 请求远程控制本机", 16, "Txt", bold: true));
            var addr = UiKit.Text($"来自 {address}", 12, "Sub");
            addr.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(addr);
            var tip = new TextBlock
            {
                Text = "允许后对方可以看到你的屏幕、操作鼠标键盘和传输文件。不认识的人请拒绝。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = UiKit.Br("Warn"),
                Margin = new Thickness(0, 14, 0, 0)
            };
            panel.Children.Add(tip);

            var buttons = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            _denyButton = UiKit.Button("DangerGhostButton", "", (_, _) => Finish(false));
            var allow = UiKit.Button("PrimaryButton", "允 许", (_, _) => Finish(true));
            buttons.Children.Add(_denyButton);
            Grid.SetColumn(allow, 2);
            buttons.Children.Add(allow);
            panel.Children.Add(buttons);
            Body = panel;

            UpdateCountdown();
            _timer.Tick += (_, _) =>
            {
                _left--;
                if (_left <= 0) Finish(false);
                else UpdateCountdown();
            };
            _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }

        private void UpdateCountdown() => _denyButton.Content = $"拒绝（{_left}）";

        private void Finish(bool allowed)
        {
            Allowed = allowed;
            _timer.Stop();
            Close();
        }
    }
}
