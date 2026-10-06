using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteControl.Server.Unattended
{
    /// <summary>
    /// 发送 Ctrl+Alt+Del（安全注意序列）。只有 SYSTEM 服务或 SYSTEM 进程能发，
    /// 而且注册表 SoftwareSASGeneration 要允许服务发送（安装服务时设置）
    /// </summary>
    internal static class SasSender
    {
        private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string PolicyValue = "SoftwareSASGeneration";

        [DllImport("sas.dll")]
        private static extern void SendSAS(bool asUser);

        /// <summary>返回空串表示已发送，否则返回失败原因</summary>
        public static string Send()
        {
            try
            {
                // FALSE：以服务身份发送（调用方必须是 SYSTEM）
                SendSAS(false);
                return "";
            }
            catch (DllNotFoundException)
            {
                return "系统缺少 sas.dll（Windows Server Core 等精简版），无法发送 Ctrl+Alt+Del";
            }
            catch (Exception ex)
            {
                return $"发送 Ctrl+Alt+Del 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 允许服务发送 SAS：1 = 服务，3 = 服务和轻松访问程序。已经允许服务的就不改
        /// </summary>
        public static void EnsurePolicy()
        {
            using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, true);
            int current = key.GetValue(PolicyValue) is int v ? v : 0;
            if ((current & 1) == 0)
            {
                key.SetValue(PolicyValue, current | 1, RegistryValueKind.DWord);
            }
        }
    }
}
