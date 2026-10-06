using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// 服务里用：复制自己的 SYSTEM 令牌、改成控制台会话，在 winsta0\winlogon 桌面上启动 agent。
    /// 只能在 LocalSystem 服务里调用（需要 SE_TCB_NAME 才能改令牌的会话 ID）
    /// </summary>
    internal static class AgentLauncher
    {
        #region P/Invoke

        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenSessionId = 12;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint NORMAL_PRIORITY_CLASS = 0x20;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmd, IntPtr procAttr, IntPtr threadAttr,
            bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        #endregion

        private static Process? _agent;
        private static long _startTick;

        /// <summary>agent 所在的会话；没在运行时为 null</summary>
        public static uint? RunningSession { get; private set; }

        public static bool IsAgentRunning
        {
            get
            {
                try
                {
                    return _agent != null && !_agent.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool _quickExitReported;

        /// <summary>
        /// 上一个 agent 启动后 10 秒内就退出了（用来放慢重启）。每个 agent 只报告一次
        /// </summary>
        public static bool ExitedQuickly
        {
            get
            {
                if (_agent == null || IsAgentRunning || _quickExitReported) return false;
                bool quick;
                try
                {
                    quick = (_agent.ExitTime - _agent.StartTime).TotalSeconds < 10;
                }
                catch
                {
                    quick = Environment.TickCount64 - _startTick < 10_000;
                }
                if (quick) _quickExitReported = true;
                return quick;
            }
        }

        public static uint ConsoleSession() => WTSGetActiveConsoleSessionId();

        public static void StartAgent(uint session)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out IntPtr own))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(own, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");

                // 把 SYSTEM 令牌放进控制台会话，agent 才能看到并操作那个会话的桌面
                if (!SetTokenInformation(token, TokenSessionId, ref session, sizeof(uint)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");

                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径");
                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    // 从登录桌面起步，agent 里再按输入桌面切换
                    lpDesktop = @"winsta0\winlogon"
                };

                if (!CreateProcessAsUser(token, exe, $"\"{exe}\" --agent", IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW | NORMAL_PRIORITY_CLASS, IntPtr.Zero, AppContext.BaseDirectory, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");

                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                _agent = Process.GetProcessById(pi.dwProcessId);
                _startTick = Environment.TickCount64;
                _quickExitReported = false;
                RunningSession = session;
                AgentLog.Write($"已在会话 {session} 启动 agent（PID {pi.dwProcessId}）");
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(own);
            }
        }

        public static void StopAgent()
        {
            var agent = _agent;
            RunningSession = null;
            if (agent == null) return;

            try
            {
                if (!agent.HasExited)
                {
                    agent.Kill();
                    agent.WaitForExit(3000);
                }
            }
            catch
            {
                // 已经退出
            }
        }
    }
}