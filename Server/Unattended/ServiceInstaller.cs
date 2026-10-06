using System;
using System.Diagnostics;
using System.Text;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// 安装 / 卸载无人值守服务（调用 sc.exe）。被控端界面是管理员身份运行，不需要再提权
    /// </summary>
    internal static class ServiceInstaller
    {
        public static bool IsInstalled() => Sc(new[] { "query", ServiceHost.ServiceName }, out _) == 0;

        public static bool IsRunning() =>
            Sc(new[] { "query", ServiceHost.ServiceName }, out string output) == 0 && output.Contains("RUNNING");

        /// <summary>安装并启动。返回 null 表示成功，否则是失败原因</summary>
        public static string? Install()
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return "找不到程序路径";

            if (IsInstalled()) Uninstall();

            // 路径带引号：路径里有空格时不加引号，系统可能先执行 C:\Program.exe 之类被劫持的文件
            string binPath = $"\"{exe}\" --service";
            if (Sc(new[] { "create", ServiceHost.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem",
                    "DisplayName=", "简连 LinkDesk 无人值守" }, out string output) != 0)
            {
                return "创建服务失败：" + output.Trim();
            }

            Sc(new[] { "description", ServiceHost.ServiceName, "简连 LinkDesk 远程控制：锁屏和登录界面也能远程操作" }, out _);
            // 崩溃后 5 秒自动重启（连续三次都重启），计数一天清零
            Sc(new[] { "failure", ServiceHost.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000" }, out _);

            if (Sc(new[] { "start", ServiceHost.ServiceName }, out output) != 0)
            {
                return "服务已安装，但启动失败：" + output.Trim();
            }
            return null;
        }

        /// <summary>停止并删除服务。返回 null 表示成功（本来就没装也算成功）</summary>
        public static string? Uninstall()
        {
            if (!IsInstalled()) return null;

            Sc(new[] { "stop", ServiceHost.ServiceName }, out _);
            // 等服务停下来再删，否则会变成"标记为删除"直到重启
            for (int i = 0; i < 20 && IsRunning(); i++) System.Threading.Thread.Sleep(250);

            return Sc(new[] { "delete", ServiceHost.ServiceName }, out string output) == 0 ? null : "删除服务失败：" + output.Trim();
        }

        /// <summary>参数逐个传给 sc.exe，不拼命令行字符串，路径里有特殊字符也安全</summary>
        private static int Sc(string[] args, out string output)
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "sc.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi)!;
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15_000);
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }
    }
}
