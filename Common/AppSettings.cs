using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RemoteControl.Common
{
    /// <summary>
    /// 界面上的连接设置，保存在 %APPDATA%\RemoteControl\{name}.json，下次启动自动填回
    /// </summary>
    public class AppSettings
    {
        public bool UseRelay { get; set; } = true;
        public string RelayHost { get; set; } = "";
        public int RelayPort { get; set; } = 16888;

        // 直连模式
        public string DirectHost { get; set; } = "127.0.0.1";
        public int DirectPort { get; set; } = 5900;

        // 服务端：固定设备ID，避免每次启动都变
        // 客户端：上次连接的目标设备ID
        public string DeviceId { get; set; } = "";

        // 客户端：多人访问时显示给其他人的名字
        public string NickName { get; set; } = "";

        // 服务端：新连接需要本机点"允许"（无人值守模式下忽略）
        public bool RequireApproval { get; set; } = true;
        // 服务端：无人值守模式（安装为系统服务，可远程解锁、发送 Ctrl+Alt+Del）
        public bool Unattended { get; set; }
        // 服务端：固定密码（无人值守模式使用，空表示用随机临时密码）
        public string FixedPassword { get; set; } = "";

        // 客户端：悬浮工具栏位置（相对画面区域左上角，NaN 表示默认居中）和是否收起
        public double FloatBarX { get; set; } = double.NaN;
        public double FloatBarY { get; set; } = double.NaN;
        public bool FloatBarCollapsed { get; set; }

        // 客户端：最近连接的设备
        public List<RecentDevice> RecentDevices { get; set; } = new();

        /// <summary>
        /// 服务端设置放在 ProgramData：无人值守时由 SYSTEM 进程读取，和桌面上的被控端界面共用一份
        /// </summary>
        private static string GetPath(string name)
        {
            var folder = name == "server" ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.ApplicationData;
            string dir = Path.Combine(Environment.GetFolderPath(folder), "RemoteControl");
            return Path.Combine(dir, name + ".json");
        }

        public static AppSettings Load(string name)
        {
            try
            {
                string path = GetPath(name);
                if (!File.Exists(path))
                {
                    // 旧版本放在当前用户的 AppData 里，读出来沿用，识别码不会变
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteControl", name + ".json");
                }
                if (File.Exists(path))
                {
                    return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                }
            }
            catch
            {
                // 文件损坏就用默认值
            }
            return new AppSettings();
        }

        public void Save(string name)
        {
            try
            {
                string path = GetPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // 保存失败不影响使用
            }
        }
    }

    public class RecentDevice
    {
        public string DeviceId { get; set; } = "";   // 识别码，或直连时的 IP
        public bool UseRelay { get; set; } = true;
        public string Name { get; set; } = "";       // 对方计算机名
        public DateTime LastConnected { get; set; }
    }
}
