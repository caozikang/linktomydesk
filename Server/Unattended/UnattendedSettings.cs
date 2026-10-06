using System.Linq;
using RemoteControl.Common;

namespace RemoteControl.Server.Unattended
{
    /// <summary>无人值守模式的设置校验（界面开启前和 agent 启动时都要过一遍）</summary>
    internal static class UnattendedSettings
    {
        public const string Name = "server";

        /// <summary>固定密码：至少 8 位，同时包含字母和数字</summary>
        public static string? ValidatePassword(string? password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8) return "固定密码至少 8 位";
            if (password.Length > 32) return "固定密码最多 32 位";
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "固定密码要同时包含字母和数字";
            if (password.Any(char.IsWhiteSpace)) return "固定密码不能包含空格";
            return null;
        }

        /// <summary>返回 null 表示设置可用，否则返回缺什么</summary>
        public static string? Validate(AppSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.DeviceId)) return "还没有生成识别码，请先打开一次被控端";
            if (string.IsNullOrWhiteSpace(s.RelayHost)) return "无人值守只支持中继方式，请先填写中继服务器地址";
            if (s.RelayPort is < 1 or > 65535) return "中继端口无效";
            return ValidatePassword(s.FixedPassword);
        }
    }
}
