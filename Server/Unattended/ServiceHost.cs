using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// Windows 服务（--service，LocalSystem，session 0）。本身不连网络，只负责在控制台会话里
    /// 保持一个 SYSTEM 身份的 agent（--agent）在运行：登录、注销、切换用户时换到新会话重新拉起，崩溃了也重新拉起
    /// </summary>
    internal static class ServiceHost
    {
        public const string ServiceName = "LinkToMyDeskService";

        /// <summary>改名前的服务名：安装 / 卸载时顺带清理，避免留下两个服务</summary>
        public const string LegacyServiceName = "LinkDeskService";

        #region P/Invoke

        private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
        private const int SERVICE_STOPPED = 1;
        private const int SERVICE_START_PENDING = 2;
        private const int SERVICE_STOP_PENDING = 3;
        private const int SERVICE_RUNNING = 4;
        private const int SERVICE_ACCEPT_STOP = 0x1;
        private const int SERVICE_ACCEPT_SHUTDOWN = 0x4;
        private const int SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
        private const int SERVICE_CONTROL_STOP = 1;
        private const int SERVICE_CONTROL_SHUTDOWN = 5;
        private const int SERVICE_CONTROL_SESSIONCHANGE = 14;
        private const int NO_ERROR = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_TABLE_ENTRY
        {
            public string? lpServiceName;
            public ServiceMainProc? lpServiceProc;
        }

        private delegate void ServiceMainProc(int argc, IntPtr argv);
        private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerEx handler, IntPtr context);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

        #endregion

        // 委托必须一直被引用，否则会被 GC 回收，系统回调时进程崩溃
        private static readonly ServiceMainProc MainProc = ServiceMain;
        private static readonly HandlerEx Handler = OnControl;

        private static IntPtr _statusHandle;
        private static SERVICE_STATUS _status;
        private static readonly ManualResetEvent StopEvent = new(false);
        private static readonly AutoResetEvent SessionChanged = new(false);

        /// <summary>由 SCM 启动时调用；在命令行直接运行会返回错误（1063）</summary>
        public static int Run()
        {
            var table = new[]
            {
                new SERVICE_TABLE_ENTRY { lpServiceName = ServiceName, lpServiceProc = MainProc },
                new SERVICE_TABLE_ENTRY()
            };
            if (!StartServiceCtrlDispatcher(table))
            {
                int err = Marshal.GetLastWin32Error();
                AgentLog.Write($"服务分发失败（{err}），--service 只能由服务管理器启动");

                // 1063：不是由服务管理器启动的（用户手动带了 --service），告诉他正确的开启方式
                const int ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;
                if (err == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
                {
                    System.Windows.MessageBox.Show(
                        "--service 参数只能由 Windows 服务管理器使用，不能手动运行。\n\n" +
                        "开启无人值守的方法：\n" +
                        "1. 以管理员身份直接运行本程序（不带参数）\n" +
                        "2. 连接方式选「中继」，填好中继服务器地址\n" +
                        "3. 勾选「无人值守模式」，设置固定密码\n\n" +
                        $"程序会自动安装并启动服务（服务名 {ServiceName}）。",
                        "简连 LinkToMyDesk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                return err;
            }
            return 0;
        }

        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, Handler, IntPtr.Zero);
            if (_statusHandle == IntPtr.Zero) return;

            _status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
            ReportStatus(SERVICE_START_PENDING, 3000);
            AgentLog.Write("服务启动");

            try
            {
                SasSender.EnsurePolicy();
            }
            catch (Exception ex)
            {
                AgentLog.Write("设置 SoftwareSASGeneration 失败，Ctrl+Alt+Del 可能不可用: " + ex.Message);
            }

            ReportStatus(SERVICE_RUNNING, 0);
            try
            {
                Supervise();
            }
            catch (Exception ex)
            {
                AgentLog.Write("服务异常: " + ex);
            }

            AgentLauncher.StopAgent();
            AgentLog.Write("服务停止");
            ReportStatus(SERVICE_STOPPED, 0);
        }

        /// <summary>
        /// 监管循环：agent 不在、退出了、或控制台会话换了，就重新拉起。
        /// 连续失败时逐步放慢，避免设置有误时疯狂重启
        /// </summary>
        private static void Supervise()
        {
            int failures = 0;
            var waits = new WaitHandle[] { StopEvent, SessionChanged };

            while (true)
            {
                uint session = AgentLauncher.ConsoleSession();
                bool sessionMoved = AgentLauncher.RunningSession is uint running && running != session;

                // 0xFFFFFFFF：切换用户的瞬间没有控制台会话，等一会儿再看
                if (session != uint.MaxValue && (sessionMoved || !AgentLauncher.IsAgentRunning))
                {
                    if (sessionMoved) AgentLog.Write($"控制台会话变为 {session}，重启 agent");
                    AgentLauncher.StopAgent();

                    try
                    {
                        AgentLauncher.StartAgent(session);
                        failures = 0;
                    }
                    catch (Win32Exception ex)
                    {
                        failures++;
                        AgentLog.Write($"启动 agent 失败（第 {failures} 次）: {ex.Message}");
                    }
                }

                // agent 刚启动就退出（例如设置不完整）也算失败
                if (AgentLauncher.ExitedQuickly) failures++;

                int wait = failures switch
                {
                    0 => 3000,
                    < 5 => 3000 * failures,
                    _ => 30_000
                };
                int signaled = WaitHandle.WaitAny(waits, wait);
                if (signaled == 0) return; // 停止
            }
        }

        private static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case SERVICE_CONTROL_STOP:
                case SERVICE_CONTROL_SHUTDOWN:
                    ReportStatus(SERVICE_STOP_PENDING, 5000);
                    StopEvent.Set();
                    return NO_ERROR;

                case SERVICE_CONTROL_SESSIONCHANGE:
                    // 控制台连接、断开、登录、注销都会来，统一交给监管循环判断
                    SessionChanged.Set();
                    return NO_ERROR;

                default:
                    return NO_ERROR; // 包括 INTERROGATE
            }
        }

        private static void ReportStatus(int state, int waitHint)
        {
            _status.dwCurrentState = state;
            _status.dwWaitHint = waitHint;
            _status.dwWin32ExitCode = NO_ERROR;
            _status.dwControlsAccepted = state == SERVICE_START_PENDING
                ? 0
                : SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE;
            _status.dwCheckPoint = state is SERVICE_RUNNING or SERVICE_STOPPED ? 0 : _status.dwCheckPoint + 1;
            SetServiceStatus(_statusHandle, ref _status);
        }
    }
}