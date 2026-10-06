using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteControl.Server.Unattended;
using RemoteControl.Ui;

namespace RemoteControl.Server
{
    /// <summary>开启无人值守前设置固定密码：输入两次，至少 8 位，字母加数字</summary>
    public sealed class FixedPasswordDialog : LinkDeskWindow
    {
        private readonly PasswordBox _first = new() { MaxLength = 32 };
        private readonly PasswordBox _second = new() { MaxLength = 32 };
        private readonly TextBlock _error = new();

        public string Password { get; private set; } = "";

        public FixedPasswordDialog()
        {
            ChromeTitle = "设置无人值守密码";
            Width = 380;
            Height = 400;

            var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
            panel.Children.Add(UiKit.Text("无人值守需要固定密码", 16, "Txt", bold: true));
            panel.Children.Add(new TextBlock
            {
                Text = "开启后，只要知道识别码和这个密码，就能在没人看管时远程操作本机，包括锁屏后解锁。请使用不容易猜到的密码，不要告诉不信任的人。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = UiKit.Br("Warn"),
                Margin = new Thickness(0, 8, 0, 16)
            });

            panel.Children.Add(Label("固 定 密 码（至少 8 位，字母加数字）"));
            panel.Children.Add(UiKit.Field(_first, "输入密码", fontSize: 14));
            var again = Label("再 输 入 一 次");
            again.Margin = new Thickness(0, 12, 0, 8);
            panel.Children.Add(again);
            panel.Children.Add(UiKit.Field(_second, "确认密码", fontSize: 14));

            _error.FontSize = 12;
            _error.Foreground = UiKit.Br("Danger");
            _error.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(_error);

            var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.Children.Add(UiKit.Button("GhostButton", "取 消", (_, _) => { DialogResult = false; }));
            var ok = UiKit.Button("PrimaryButton", "开 启", (_, _) => Confirm());
            Grid.SetColumn(ok, 2);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Body = panel;

            _second.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
            Loaded += (_, _) => _first.Focus();
        }

        private static TextBlock Label(string text)
        {
            var t = UiKit.Caption(text);
            t.Margin = new Thickness(0, 0, 0, 8);
            return t;
        }

        private void Confirm()
        {
            string? error = UnattendedSettings.ValidatePassword(_first.Password);
            if (error == null && _first.Password != _second.Password) error = "两次输入的密码不一样";
            if (error != null)
            {
                _error.Text = error;
                return;
            }
            Password = _first.Password;
            DialogResult = true;
        }
    }
}
