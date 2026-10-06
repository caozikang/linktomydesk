using System;
using System.Runtime.InteropServices;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    public class InputSimulator
    {
        // Windows API 常量
        private const int MOUSEEVENTF_MOVE = 0x0001;
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;
        private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const int MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const int MOUSEEVENTF_WHEEL = 0x0800;
        private const int MOUSEEVENTF_ABSOLUTE = 0x8000;

        private const int KEYEVENTF_KEYDOWN = 0x0000;
        private const int KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // 注入发生在网络线程池线程上，每次注入前让当前线程跟上输入桌面（无人值守时锁屏界面也能输入）。
        // 线程池线程没有窗口，可以 SetThreadDesktop；切换状态按线程记录，开销只是一次 OpenInputDesktop
        private static void SyncDesktop() => Unattended.DesktopSwitcher.SyncThread();

        public void MoveMouse(int x, int y)
        {
            SyncDesktop();
            SetCursorPos(x, y);
        }

        public void SimulateMouseClick(int x, int y, MouseButton button, MouseEventType eventType)
        {
            SyncDesktop();
            SetCursorPos(x, y);

            int downFlag = 0;
            int upFlag = 0;

            switch (button)
            {
                case MouseButton.Left:
                    downFlag = MOUSEEVENTF_LEFTDOWN;
                    upFlag = MOUSEEVENTF_LEFTUP;
                    break;
                case MouseButton.Right:
                    downFlag = MOUSEEVENTF_RIGHTDOWN;
                    upFlag = MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseButton.Middle:
                    downFlag = MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = MOUSEEVENTF_MIDDLEUP;
                    break;
            }

            switch (eventType)
            {
                case MouseEventType.Down:
                    mouse_event(downFlag, x, y, 0, 0);
                    break;
                case MouseEventType.Up:
                    mouse_event(upFlag, x, y, 0, 0);
                    break;
                case MouseEventType.DoubleClick:
                    mouse_event(downFlag, x, y, 0, 0);
                    mouse_event(upFlag, x, y, 0, 0);
                    System.Threading.Thread.Sleep(50);
                    mouse_event(downFlag, x, y, 0, 0);
                    mouse_event(upFlag, x, y, 0, 0);
                    break;
            }
        }

        /// <summary>在当前位置松开鼠标键（访问者断线时清理按住状态用，不移动指针）</summary>
        public void ReleaseMouseButton(MouseButton button)
        {
            int flag = button switch
            {
                MouseButton.Right => MOUSEEVENTF_RIGHTUP,
                MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                _ => MOUSEEVENTF_LEFTUP
            };
            SyncDesktop();
            mouse_event(flag, 0, 0, 0, 0);
        }

        public void SimulateMouseWheel(int delta)
        {
            SyncDesktop();
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, 0);
        }

        public void SimulateKeyPress(int keyCode, bool isDown)
        {
            if (keyCode < 0 || keyCode > 255) return;
            SyncDesktop();

            byte vk = (byte)keyCode;
            if (isDown)
            {
                keybd_event(vk, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else
            {
                keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
            }
        }
    }
}
