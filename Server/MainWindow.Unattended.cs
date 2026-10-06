using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using RemoteControl.Common;
using RemoteControl.Server.Unattended;
using RemoteControl.Ui;

namespace RemoteControl.Server
{
    /// <summary>
    /// 无人值守：安装 / 卸载服务；服务装着时界面不自己开连接，只显示 agent 推过来的状态
    /// </summary>
    public partial class MainWindow
    {
        private StatusPipeClient? _agentStatus;
        private bool _agentMode;

        /// <summary>窗口打开时调用：服务已安装就进入"只显示状态"模式</summary>
        private void InitUnattended()
        {
            if (ServiceInstaller.IsInstalled()) EnterAgentMode();
        }

        private async void ToggleUnattended(bool on)
        {
            _unattendedCheck.IsEnabled = false;
            try
            {
                if (on) await EnableUnattendedAsync();
                else await DisableUnattendedAsync();
            }
            finally
            {
                _unattendedCheck.IsEnabled = true;
                _unattendedCheck.IsChecked = ServiceInstaller.IsInstalled();
            }
        }

        private async Task EnableUnattendedAsync()
        {
            // 只支持中继：没人登录时本机也要能被识别码找到
            string relayHost = _relayHostBox.Text.Trim();
            if (_relayRadio.IsChecked != true || string.IsNullOrEmpty(relayHost) || !TryParsePort(_relayPortBox.Text, out int relayPort))
            {
                MessageBox.Show(this, "无人值守只支持中继方式，请先选择「中继」并填写中继服务器地址和端口。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new FixedPasswordDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            // 本界面自己开着的连接先停掉，否则和服务用同一个识别码注册会互相顶掉
            if (_server != null) StopButton_Click(this, new RoutedEventArgs());

            _settings.UseRelay = true;
            _settings.RelayHost = relayHost;
            _settings.RelayPort = relayPort;
            _settings.FixedPassword = dialog.Password;
            _settings.Unattended = true;
            _settings.Save(SettingsName);

            SetStatus("正在安装无人值守服务…", "Warn");
            string? error = await Task.Run(ServiceInstaller.Install);
            if (error != null)
            {
                _settings.Unattended = false;
                _settings.Save(SettingsName);
                SetStatus("无人值守服务安装失败", "Danger");
                OnStatusChanged(error);
                MessageBox.Show(this, error, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            OnStatusChanged("无人值守服务已安装，开机自动运行");
            EnterAgentMode();
        }

        private async Task DisableUnattendedAsync()
        {
            var answer = MessageBox.Show(this, "关闭无人值守后，锁屏和没人登录时将无法远程连接本机。确定关闭吗？", "关闭无人值守",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            SetStatus("正在卸载无人值守服务…", "Warn");
            string? error = await Task.Run(ServiceInstaller.Uninstall);
            if (error != null)
            {
                SetStatus("卸载失败", "Danger");
                MessageBox.Show(this, error, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 固定密码一起清掉，下次开启重新设置
            _settings.Unattended = false;
            _settings.FixedPassword = "";
            _settings.Save(SettingsName);
            OnStatusChanged("无人值守服务已卸载");
            LeaveAgentMode();
        }

        /// <summary>
        /// 服务负责连接：隐藏开启按钮、密码改为不可见、连上 agent 的状态管道
        /// </summary>
        private void EnterAgentMode()
        {
            _agentMode = true;
            SetSettingsEnabled(false);
            _unattendedCheck.IsEnabled = true;
            _startButton.Visibility = Visibility.Collapsed;
            _stopButton.Visibility = Visibility.Collapsed;
            _refreshButton.Visibility = Visibility.Collapsed;
            _passwordBox.Text = "••••••••";
            SetChip("无人值守 · 固定密码", true);
            SetStatus("正在连接无人值守服务…", "Warn");
            _subtitle.Text = "无人值守已开启 · 锁屏和登录界面也能远程操作";

            _agentStatus?.Dispose();
            _agentStatus = new StatusPipeClient();
            _agentStatus.StatusReceived += s => Dispatcher.BeginInvoke(() => OnAgentStatus(s));
            _agentStatus.Start();
        }

        private void LeaveAgentMode()
        {
            _agentMode = false;
            _agentStatus?.Dispose();
            _agentStatus = null;
            _refreshButton.Visibility = Visibility.Visible;
            _passwordBox.Text = Random.Shared.Next(100_000, 1_000_000).ToString();
            ResetUI();
        }

        private void OnAgentStatus(AgentStatus? s)
        {
            if (!_agentMode) return;

            if (s == null)
            {
                SetStatus("无人值守服务未响应（正在重启或未运行）", "Warn");
                return;
            }

            SetStatus(s.Registered ? $"已连接中继节点 · {s.RelayHost}（无人值守）" : $"正在连接中继节点 · {s.RelayHost}", s.Registered ? "Ok" : "Warn");
            foreach (var line in s.NewLogs) AppendLog(line);
            ShowViewers(s.Viewers, 0);
        }
    }
}
