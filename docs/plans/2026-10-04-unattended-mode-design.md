# 无人值守模式设计

## 目标

锁屏、登录界面、UAC 弹窗时也能远程看到画面并输入，能发送 Ctrl+Alt+Del。开机后无人登录也能连。

## 已确认的约束

- 只跟随物理控制台会话（`WTSGetActiveConsoleSessionId`），不跟随 RDP 会话
- 无人值守时只用固定强密码：至少 8 位，同时含字母和数字；临时密码停用
- 服务负责连接；桌面上的被控端窗口只显示状态，不另开连接

## 方案：同一个 exe，三种启动方式

| 启动方式 | 身份 | 作用 |
|---|---|---|
| 无参数 | 当前用户 | 普通被控端界面。检测到服务已安装时只显示状态 |
| `--service` | LocalSystem，session 0 | Windows 服务 `LinkDeskService`，保证控制台会话里始终有一个 agent 在运行 |
| `--agent` | SYSTEM，控制台会话 | 无界面，运行 `RemoteServer`（中继注册、推流、输入）。可以发送 SendSAS |

### 服务（ServiceHost）

- 通过 `StartServiceCtrlDispatcher` 和 `RegisterServiceCtrlHandlerEx` 实现，不引入额外的 NuGet 包
- 订阅 `SERVICE_CONTROL_SESSIONCHANGE`。控制台会话变化（登录、注销、切换用户）时结束旧 agent，在新会话里重新启动
- 启动 agent 的步骤：复制自身 SYSTEM 令牌（`DuplicateTokenEx`）→ `SetTokenInformation(TokenSessionId)` → `CreateProcessAsUser`，桌面设为 `winsta0\winlogon`
- agent 退出后 3 秒重新拉起，1 分钟内连续失败 5 次就改为每 30 秒重试一次

### Agent

- 读取 ProgramData 里的 server.json（中继地址、识别码、固定密码），调用 `StartRelayModeAsync`
- `SasHandler` 调用 `sas.dll!SendSAS(FALSE)`。服务安装时把注册表 `HKLM\...\Policies\System\SoftwareSASGeneration` 设为 1（允许服务发送）
- 截屏线程和输入注入前调用 `DesktopSwitcher.SyncThread()`：`OpenInputDesktop` 后，如果和当前线程桌面不同，就 `SetThreadDesktop`。这样在 Default、Winlogon 和 UAC 安全桌面之间切换时，截屏和输入都会自动跟过去
- 输入注入在接收线程里执行，所以注入前也要切换桌面；`SetThreadDesktop` 要求线程没有窗口，agent 不创建任何窗口
- 白板标注叠加层和接入确认弹窗在 agent 里不启用。无人值守本来就不需要确认，标注只显示在控制端

### 状态通道（桌面界面 ↔ agent）

- 命名管道 `\\.\pipe\LinkDeskAgent`，一行一个 JSON
- agent 每秒推送一次状态：是否已注册中继、在线访问者列表、最近的日志
- 管道 ACL 只允许 SYSTEM 和 Administrators 连接

### 安装和卸载

- 被控端界面勾选"无人值守模式"后，先要求设置固定密码，再执行 `sc create LinkDeskService binPath= "<exe> --service" start= auto obj= LocalSystem` 和 `sc start`
- 取消勾选时执行 `sc stop` 和 `sc delete`
- 界面进程本身已经是管理员（manifest 是 requireAdministrator），不需要再提权

## 出错和恢复

| 情况 | 处理 |
|---|---|
| agent 崩溃 | 服务 3 秒后重新拉起；访问者看到断线，重连即可 |
| 切换用户或注销 | 服务收到 SESSIONCHANGE，在新会话里重启 agent |
| 无人登录（开机后） | 控制台会话是登录界面，agent 照常运行，可以远程登录 |
| SendSAS 失败 | 返回具体原因（例如策略不允许），控制端提示 |
| 桌面界面和服务同时开启 | 服务已安装时，界面不再启动自己的 RemoteServer，识别码不会冲突 |

## 测试

- 单元：密码强度校验；命令行参数解析
- 手动（需要管理员和一台可锁屏的机器）：安装服务 → Win+L 锁屏 → 远程连接能看到锁屏界面 → 点 Ctrl+Alt+Del → 输入密码解锁；UAC 弹窗能看到并点击；注销后重新登录，连接能自动恢复；卸载后服务和注册表都已清理

## 不做（YAGNI）

- RDP 会话跟随
- 多个 agent 同时运行
- 服务自动更新
