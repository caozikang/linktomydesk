using System.Text;

namespace RemoteControl.Common
{
    /// <summary>
    /// 把协作动态转成给人看的文字，被控端日志和控制端动态栏共用
    /// </summary>
    public static class ActivityFormatter
    {
        public static string Describe(InputActivityMessage a) => a.Kind switch
        {
            ActivityKind.Joined => "加入了",
            ActivityKind.Left => "离开了",
            ActivityKind.TakeControl => "开始操作",
            ActivityKind.Key => "按下 " + KeyText(a.KeyCode, a.Modifiers),
            ActivityKind.Click => $"{ButtonText(a.Button)}点击 ({a.X}, {a.Y})",
            ActivityKind.Setting => a.Text,
            _ => ""
        };

        public static string ButtonText(MouseButton button) => button switch
        {
            MouseButton.Right => "右键",
            MouseButton.Middle => "中键",
            _ => "左键"
        };

        /// <summary>例如 "Ctrl+Shift+S"、"Enter"、"A"</summary>
        public static string KeyText(int vk, KeyModifiers modifiers)
        {
            var sb = new StringBuilder();
            if (modifiers.HasFlag(KeyModifiers.Ctrl)) sb.Append("Ctrl+");
            if (modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
            if (modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
            if (modifiers.HasFlag(KeyModifiers.Win)) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(int vk)
        {
            // 数字和字母的虚拟键码就是 ASCII
            if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
            if (vk is >= 0x60 and <= 0x69) return "小键盘" + (vk - 0x60);
            if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);

            return vk switch
            {
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x10 or 0xA0 or 0xA1 => "Shift",
                0x11 or 0xA2 or 0xA3 => "Ctrl",
                0x12 or 0xA4 or 0xA5 => "Alt",
                0x13 => "Pause",
                0x14 => "CapsLock",
                0x1B => "Esc",
                0x20 => "空格",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "←",
                0x26 => "↑",
                0x27 => "→",
                0x28 => "↓",
                0x2C => "PrintScreen",
                0x2D => "Insert",
                0x2E => "Delete",
                0x5B or 0x5C => "Win",
                0x5D => "菜单键",
                0x6A => "小键盘*",
                0x6B => "小键盘+",
                0x6D => "小键盘-",
                0x6E => "小键盘.",
                0x6F => "小键盘/",
                0x90 => "NumLock",
                0x91 => "ScrollLock",
                0xBA => ";",
                0xBB => "=",
                0xBC => ",",
                0xBD => "-",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDB => "[",
                0xDC => "\\",
                0xDD => "]",
                0xDE => "'",
                0xE5 => "输入法",
                _ => $"键{vk:X2}"
            };
        }
    }
}
