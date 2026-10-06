# 简连 LinkDesk 开发进度

更新时间：2026-10-03

## 目标

类 ToDesk 的 Windows 远程控制软件，支持 Win10/11、Windows Server 2016–2025。双方都在内网，经自建中继服务器（Linux，默认端口 16888）连接。

已上线的基础能力：
- H.264 推流（硬件编码优先），画面自适应缩放和 1:1 原始尺寸
- 多人同时访问，互相能看到对方的操作和按键
- LinkDesk 深色界面（参考 `linkdesk.html`）

本轮目标（用户提出）：
1. 工具栏加 Ctrl+Alt+Del 按钮，锁屏后也能输入账号密码解锁
2. 顶部工具栏不挡画面：可收起，可拖到任意位置
3. 补齐原型里的全部功能：传输文件、白板标注、屏幕切换、会话录制、对方确认后接入、无人值守模式、设备列表

## 已完成（客户端可编译，0 错误；尚未联调）

| 模块 | 文件 | 内容 |
|---|---|---|
| 协议 | `Common/Enums.cs`、`Common/Messages.cs` | 新消息：SendSas、SasResult、MonitorList、SelectMonitor、Annotation、PendingApproval、FileUploadComplete、FileTransferError；新数据结构 MonitorInfo、AnnotationMessage、FileTransferErrorMessage |
| 设置 | `Common/AppSettings.cs` | 接入确认、无人值守、固定密码、工具栏位置和收起状态、最近设备。被控端设置改存 ProgramData（SYSTEM 服务也能读），自动迁移旧配置，识别码不变 |
| 客户端网络 | `Client/RemoteClientWithRelay.cs` | 文件上传、下载、取消和进度；断线时清理未完成的文件；Ctrl+Alt+Del、切换屏幕、标注收发；拒绝接入和等待确认的提示 |
| 录制 | `Client/Mp4Recorder.cs` | H.264 直接封装为 MP4，不重新编码，保存到"视频\LinkDesk" |
| 工具栏 | `Client/SessionFeatures.cs` | 左侧手柄拖动，双击或拖回顶部中间时归位；右侧按钮收起；位置会记住，窗口缩小时自动拉回可见区域。新增传输文件、Ctrl+Alt+Del、白板标注、录制按钮和屏幕下拉框 |
| 白板标注 | `Client/SessionFeatures.cs` | 用各人颜色画线，坐标归一化，不受缩放影响；右键清空，所有人同步 |
| 文件传输 | `Client/FileTransferWindow.cs` | 双栏浏览（本机/远程），可多选上传下载，有进度和速度，可取消；同名文件不覆盖；拖文件进画面会上传到对方桌面 |
| 设备列表 | `Client/SessionFeatures.cs` | 主页显示最近 8 台设备，点一下填入识别码 |

## 被控端（已完成，编译 0 错误）

| 功能 | 文件 | 内容 |
|---|---|---|
| 多显示器 | `Server/ScreenCapture.cs` | 枚举显示器（物理像素，主屏排第一），截取选中的那块屏；显示器拔插或改分辨率时自动跟上并广播；鼠标坐标加上显示器偏移 |
| Ctrl+Alt+Del | `Server/RemoteServerWithRelay.cs` | 新增 `SasHandler` 接口；没装无人值守服务时回复失败原因 |
| 白板标注 | `Server/RemoteServerWithRelay.cs`、`Server/AnnotationOverlay.cs` | 广播给所有人，名字以服务端为准；被控端屏幕上叠加一层置顶、鼠标可穿透的透明窗口；换屏或所有人离开时清空 |
| 文件 | `Server/FileTransferManager.cs` | 按访问者区分传输；上传会自动建目录，同名不覆盖；`~desktop` 解析为登录用户的桌面（SYSTEM 下也能解析）；出错回 FileTransferError，客户端取消后停止，半截文件删除 |
| 接入确认 | `Server/ApprovalDialog.cs`、`Server/MainWindow.xaml.cs` | 新增 `ApprovalHandler` 接口；弹窗选"允许/拒绝"，30 秒倒计时结束自动拒绝；主界面加了"对方确认后接入"和"无人值守模式"开关 |
| 测试 | `Tests/MultiViewerTest/Program.cs` | 新增用例：显示器列表、Ctrl+Alt+Del 回复、标注广播和清空、上传下载内容比对、同名不覆盖、下载失败清理、`~desktop` 解析、接入确认的允许和拒绝。2026-10-04 运行通过：中继和直连两种模式全部通过，旧用例没有回归 |

## 无人值守（进行中，方案 A 已确认）

设计文档：`docs/plans/2026-10-04-unattended-mode-design.md`

| 部分 | 文件 | 状态 |
|---|---|---|
代码已全部写完，被控端编译 0 错误。MultiViewerTest 回归测试全部通过（确认桌面切换的挂钩不影响普通模式）。

| 部分 | 文件 |
|---|---|
| 切换输入桌面（锁屏、登录界面、UAC），接入截屏线程和 `InputSimulator` | `Server/Unattended/DesktopSwitcher.cs` |
| 发送 Ctrl+Alt+Del，设置注册表 SoftwareSASGeneration | `Server/Unattended/SasSender.cs` |
| agent（`--agent`）：断线后逐步加长间隔重连，重连前重新读设置，写日志到 `ProgramData\RemoteControl\agent.log` | `Server/Unattended/AgentHost.cs` |
| 服务（`--service`）：用 P/Invoke 实现，不加 NuGet 包；会话变化或 agent 崩溃时重新拉起，连续失败会放慢 | `Server/Unattended/ServiceHost.cs`、`AgentLauncher.cs` |
| 安装和卸载（sc.exe，参数逐个传入；路径带引号，防止路径劫持；崩溃后自动重启） | `Server/Unattended/ServiceInstaller.cs` |
| 固定密码校验（8–32 位，字母加数字，不能有空格） | `Server/Unattended/UnattendedSettings.cs`、`Server/FixedPasswordDialog.cs` |
| 状态管道（只允许 SYSTEM 和管理员连接） | `Server/Unattended/StatusPipe.cs` |
| 启动参数分发（同一个 exe，三种启动方式） | `Server/App.xaml.cs`、csproj 里的 `StartupObject` |
| 界面：勾选后先设密码再安装；服务装着时只显示状态，隐藏"开启"按钮，密码显示为圆点 | `Server/MainWindow.Unattended.cs` |

**还没验证（需要管理员权限，并在真实机器上操作）**：安装服务、Win+L 锁屏后远程看到锁屏界面、发送 Ctrl+Alt+Del、输入密码解锁、UAC 弹窗、注销后重新登录能自动恢复、卸载后清理干净。安装服务会改系统配置，需要你本人操作或明确授权后才能做。

另外：被控端日志不再打印明文密码（无人值守时日志会写进文件）。

## 状态记录

- 2026-10-04：被控端后台 Agent 中断，没有交付代码；被控端改为在主会话里直接实现。被控端和客户端当前都能编译（0 错误）
- 无人值守方案已确认：只跟随物理控制台会话；只用固定强密码（至少 8 位，字母加数字）；服务负责连接，桌面窗口只显示状态。实现方式待确认（推荐：同一个 exe，加 `--service` / `--agent` 启动参数）

## 未完成（接下来按顺序做）

1. ~~被控端功能~~：已完成，见上方"被控端"一节（以下为原计划）
   - 处理 SendSas：普通模式下回复"需开启无人值守模式"
   - 多显示器：枚举并发送 MonitorList，支持切换截屏区域，鼠标坐标加上显示器偏移
   - 标注：广播给所有访问者，在被控端屏幕上叠加一层置顶、鼠标可穿透的透明窗口
   - 文件：补全上传（FileUploadRequest/FileData/FileUploadComplete），支持 `~desktop` 路径，出错时回复 FileTransferError，下载支持取消
   - 接入确认：密码正确后弹窗"允许/拒绝"，30 秒无响应视为拒绝
   - 界面：加入"对方确认后接入"和"无人值守模式"开关
2. **无人值守服务**（锁屏解锁和 Ctrl+Alt+Del 的前提）
   - 被控端程序加 `--service` 启动参数，注册为 Windows 服务（SYSTEM，开机自启）
   - 服务用 `WTSGetActiveConsoleSessionId` 加 `CreateProcessAsUser` 在当前会话启动 SYSTEM 权限的被控进程，切换用户或注销后自动重启
   - 被控进程每帧 `OpenInputDesktop`/`SetThreadDesktop`，在登录界面、锁屏和 UAC 安全桌面上也能截屏和注入输入
   - 用 `SendSAS` 发送 Ctrl+Alt+Del（需要注册表 `SoftwareSASGeneration`）
   - 安装和卸载放在被控端界面开关里（需管理员权限）；无人值守模式强制使用固定的强密码
3. **验证**：编译全部项目，用 `Tests/MultiViewerTest` 联调文件传输、标注和多显示器，在锁屏状态下实测解锁，更新发布包

## 风险和说明

- 无人值守服务以 SYSTEM 运行，只要有识别码和密码就能远程解锁这台电脑，密码要设得足够强
- 白板标注和录制只在客户端编译过，还没有实际运行验证
- 中继只做透明转发，新增消息类型不需要重新部署中继
