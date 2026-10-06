# 简连 LinkToMyDesk

官网：[www.linktomydesk.com](https://www.linktomydesk.com)

开源的 Windows 远程控制软件，类似 ToDesk / AnyDesk。双方都在内网也能连：被控端和控制端都主动连接一台自建的中继服务器，用 9 位识别码加密码接入。

**使用说明（图文）：打开 [docs/guide.html](docs/guide.html)**

## 功能

- **H.264 推流**：优先使用硬件编码（NVENC / QSV / AMF），没有可用硬件时改用软件编码。画面静止时不发数据
- **自适应缩放 / 1:1 原始尺寸**，支持全屏，可调画质和帧率（15/30/60）
- **多人同时访问**：所有人看同一块屏幕，在线成员、谁在操作、按了什么键都实时显示
- **文件传输**：左右双栏浏览，上传、下载、取消，显示进度；把文件拖进画面直接传到对方桌面
- **Ctrl+Alt+Del**：锁屏后也能输入账号密码解锁（需开启无人值守）
- **无人值守模式**：作为系统服务运行，开机自动启动；锁屏、登录界面、UAC 弹窗都能远程操作
- **多显示器切换**
- **白板标注**：在画面上画线，所有人和被控端屏幕上都能看到
- **会话录制**：直接保存为 MP4，不重新编码
- **对方确认后接入**：有人连接时被控端弹窗选"允许 / 拒绝"
- **可拖动、可收起的悬浮工具栏**；主页显示最近连接的设备

## 组成

| 程序 | 运行在 | 作用 |
|---|---|---|
| `RemoteControl.Server.exe` | 被控的 Windows 电脑 | 被控端（需管理员权限） |
| `RemoteControl.Client.exe` | 你自己的 Windows 电脑 | 控制端 |
| `RemoteControl.RelayServer` | 有公网 IP 的 Linux / Windows 服务器 | 中继，只转发数据，不保存任何内容 |

支持 Windows 10/11、Windows Server 2016–2025。中继服务器可以部署在任意一台云主机上（包括 CentOS 7，运行在 .NET 6 上）。

## 编译

需要 [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)。

```bash
dotnet restore
dotnet publish Server/RemoteControl.Server.csproj -c Release -r win-x64 --self-contained false -o publish/Server
dotnet publish Client/RemoteControl.Client.csproj -c Release -r win-x64 --self-contained false -o publish/Client
dotnet publish RelayServer/RemoteControl.RelayServer.csproj -c Release -r linux-x64 --self-contained false -o publish-linux/RelayServer
```

FFmpeg 运行库由 NuGet 包 `Sdcb.FFmpeg.runtime.windows-x64` 自动带上，不需要另外安装。

## 部署中继

```bash
# 在 Linux 服务器上（需要 .NET 6 运行时）
cd publish-linux/RelayServer
dotnet RemoteControl.RelayServer.dll 16888
```

记得在防火墙和云厂商的安全组里放行 TCP 16888。要以 systemd 服务方式长期运行，见 [deploy-linux-service.sh](deploy-linux-service.sh) 和 [LINUX_DEPLOY.md](LINUX_DEPLOY.md)。

## 测试

```bash
dotnet run --project Tests/MultiViewerTest   # 本机起中继+被控端+多个控制端，端到端测试协议和多人协作
dotnet run --project Tests/CodecTest         # 编码器/解码器
```

## 协议

基于 TCP 的二进制协议：`[1 字节类型][4 字节长度][数据]`，消息类型见 [Common/Enums.cs](Common/Enums.cs)。中继只做透明转发，新增消息类型不需要更新中继。

## 安全说明

- **通信目前没有加密。** 跨公网使用时，建议给中继加 TLS，或者通过 VPN 使用
- 密码只在被控端本地校验，不经过中继保存
- 无人值守模式以 SYSTEM 身份运行，只要有识别码和密码就能远程解锁电脑。一定要用强密码（程序强制至少 8 位，字母加数字）
- 中继服务器地址需要自己部署、自己填写，程序里没有内置任何公共中继

## 许可证

[MIT](LICENSE)

**免责声明**：请只在你拥有或获得授权的电脑上使用本软件，并遵守当地法律法规。
