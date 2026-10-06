using System;
using System.Runtime.InteropServices;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// 让当前线程跟上"正在接收输入的桌面"：锁屏、登录界面、UAC 弹窗时输入桌面会从 Default 换成 Winlogon。
    /// 只有 SYSTEM 进程能打开 Winlogon 桌面；普通进程调用时打不开就什么都不做。
    /// SetThreadDesktop 要求线程上没有窗口和钩子，所以只在截屏线程和网络接收线程里调用。
    /// </summary>
    internal static class DesktopSwitcher
    {
        private const uint DESKTOP_ALL = 0x01FF; // GENERIC_ALL 对桌面对象的具体权限
        private const int UOI_NAME = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr obj, int index, System.Text.StringBuilder info, int length, out int needed);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        /// <summary>只在无人值守 agent 里开启，普通被控端不切桌面</summary>
        public static bool Enabled { get; set; }

        [ThreadStatic] private static IntPtr _current;
        [ThreadStatic] private static string? _currentName;

        /// <summary>当前线程所在桌面的名字（Default / Winlogon），调试和日志用</summary>
        public static string? CurrentName => _currentName;

        /// <summary>
        /// 输入桌面变了就把当前线程切过去。返回 true 表示刚刚切换过（调用方可以据此请求关键帧）
        /// </summary>
        public static bool SyncThread()
        {
            if (!Enabled) return false;

            IntPtr input = OpenInputDesktop(0, false, DESKTOP_ALL);
            if (input == IntPtr.Zero) return false;

            string name = NameOf(input);
            if (name == _currentName)
            {
                CloseDesktop(input);
                return false;
            }

            if (!SetThreadDesktop(input))
            {
                CloseDesktop(input);
                return false;
            }

            // 旧句柄是自己打开的才关；线程最初的桌面句柄不归我们管
            if (_current != IntPtr.Zero) CloseDesktop(_current);
            _current = input;
            _currentName = name;
            return true;
        }

        private static string NameOf(IntPtr desktop)
        {
            var sb = new System.Text.StringBuilder(64);
            return GetUserObjectInformation(desktop, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : "";
        }
    }
}
