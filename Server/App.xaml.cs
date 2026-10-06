using System;
using System.Linq;
using System.Windows;

namespace RemoteControl.Server
{
    public partial class App : Application
    {
        /// <summary>
        /// 同一个 exe 三种启动方式：
        /// 无参数 = 被控端界面；--service = 无人值守服务（由服务管理器启动）；--agent = 服务在控制台会话里拉起的 SYSTEM 被控进程
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
            {
                return Unattended.ServiceHost.Run();
            }
            if (args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase)))
            {
                // agent 不创建任何窗口：SetThreadDesktop 要求线程上没有窗口
                return Unattended.AgentHost.Run();
            }

            var app = new App();
            app.InitializeComponent(); // App.xaml 改成了 Page，这里手动加载主题资源
            return app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
