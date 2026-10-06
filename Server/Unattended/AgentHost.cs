using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RemoteControl.Common;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// 无人值守 agent（--agent）：由服务以 SYSTEM 身份在控制台会话里启动，没有界面。
    /// 读 ProgramData 里的设置，连中继、推流、注入输入，并能发送 Ctrl+Alt+Del。
    /// 中继断线后自动重连；状态通过命名管道推给桌面上的被控端界面
    /// </summary>
    internal static class AgentHost
    {
        public static int Run()
        {
            DesktopSwitcher.Enabled = true;
            AgentLog.Write("agent 启动");

            var settings = AppSettings.Load(UnattendedSettings.Name);
            string? error = UnattendedSettings.Validate(settings);
            if (error != null)
            {
                AgentLog.Write("设置不完整，退出: " + error);
                return 2;
            }

            using var status = new StatusPipeServer();
            status.Start();

            // 服务结束 agent 时直接 TerminateProcess，这里不需要处理退出信号
            int delay = 2000;
            while (true)
            {
                using var server = new RemoteServer
                {
                    DeviceId = settings.DeviceId,
                    Password = settings.FixedPassword,
                    SasHandler = SasSender.Send
                };
                server.StatusChanged += s =>
                {
                    AgentLog.Write(s);
                    status.Log(s);
                };
                server.ViewersChanged += (viewers, _) => status.SetViewers(viewers);

                var run = server.StartRelayModeAsync(settings.RelayHost, settings.RelayPort);
                // 等注册成功，用来区分"连上了又断"和"根本连不上"，决定重连间隔
                bool registered = SpinWait.SpinUntil(() => server.IsRegistered || run.IsCompleted, 15_000) && server.IsRegistered;
                status.SetRegistered(registered, settings.RelayHost);
                if (registered) delay = 2000;

                try
                {
                    run.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    AgentLog.Write("中继连接异常: " + ex.Message);
                }

                status.SetRegistered(false, settings.RelayHost);
                status.SetViewers(Array.Empty<ViewerInfo>());
                AgentLog.Write($"与中继断开，{delay / 1000} 秒后重连");
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 60_000);

                // 重连前重新读设置：界面上可能改了中继地址或密码
                var latest = AppSettings.Load(UnattendedSettings.Name);
                if (UnattendedSettings.Validate(latest) == null) settings = latest;
            }
        }
    }

    /// <summary>agent 日志：ProgramData\RemoteControl\agent.log，超过 1MB 轮换一次</summary>
    internal static class AgentLog
    {
        private static readonly object Gate = new();
        private const long MaxBytes = 1024 * 1024;

        public static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl", "agent.log");

        public static void Write(string text)
        {
            lock (Gate)
            {
                try
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    var info = new System.IO.FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Copy(path, path + ".1", true);
                        File.Delete(path);
                    }
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{Environment.ProcessId}] {text}{Environment.NewLine}");
                }
                catch
                {
                    // 写日志失败不影响运行
                }
            }
        }
    }
}
