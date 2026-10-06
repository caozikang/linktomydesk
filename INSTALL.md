# 安装和编译指南

## 环境准备

### 1. 安装 .NET 6.0 SDK

**下载地址**: https://dotnet.microsoft.com/download/dotnet/6.0

选择 ".NET 6.0 SDK" 进行下载和安装（x64 版本）

安装完成后，打开命令提示符验证：
```bash
dotnet --version
```

应显示类似 `6.0.xxx` 的版本号

### 2. 安装 Visual Studio（可选）

如果你想使用 IDE 进行开发：
- 下载 Visual Studio 2022 Community（免费）
- 安装时选择 ".NET 桌面开发" 工作负载

## 编译项目

### 方法一：使用批处理脚本（推荐）

1. 打开项目目录
2. 双击运行 `build.bat`
3. 等待编译完成

### 方法二：手动命令行编译

```bash
# 进入项目目录
cd RemoteControl

# 还原依赖包
dotnet restore

# 编译整个解决方案
dotnet build -c Release

# 发布服务端
dotnet publish Server/RemoteControl.Server.csproj -c Release -r win-x64 --self-contained false -o ./publish/Server

# 发布客户端
dotnet publish Client/RemoteControl.Client.csproj -c Release -r win-x64 --self-contained false -o ./publish/Client
```

### 方法三：使用 Visual Studio

1. 打开 `RemoteControl.sln`
2. 右键点击解决方案 → "生成解决方案"
3. 右键点击 `RemoteControl.Server` → "发布"
4. 右键点击 `RemoteControl.Client` → "发布"

## 运行程序

### 启动服务端（被控端）

**注意：服务端必须以管理员权限运行**

#### 方法一：使用批处理脚本
双击运行 `run-server.bat`（会自动请求管理员权限）

#### 方法二：手动启动
1. 进入 `publish\Server` 目录
2. 右键点击 `RemoteControl.Server.exe`
3. 选择"以管理员身份运行"

### 启动客户端（控制端）

#### 方法一：使用批处理脚本
双击运行 `run-client.bat`

#### 方法二：手动启动
直接运行 `publish\Client\RemoteControl.Client.exe`

## 使用步骤

### 1. 配置服务端

1. 启动服务端程序（管理员权限）
2. 点击"启动服务"按钮
3. 记录显示的信息：
   - **设备 ID**: 例如 `A1B2C3D4E`
   - **连接密码**: 默认 `123456`
   - **监听端口**: 默认 `5900`

### 2. 配置防火墙

如果无法连接，需要在 Windows 防火墙中开放端口：

```powershell
# 以管理员身份运行 PowerShell
New-NetFirewallRule -DisplayName "RemoteControl" -Direction Inbound -Protocol TCP -LocalPort 5900 -Action Allow
```

或者手动配置：
1. 控制面板 → Windows Defender 防火墙 → 高级设置
2. 入站规则 → 新建规则
3. 规则类型：端口
4. 协议：TCP，特定端口：5900
5. 操作：允许连接

### 3. 连接到服务端

在客户端程序中：
1. **主机**: 输入服务端 IP 地址
   - 同一台电脑测试：`127.0.0.1`
   - 局域网：`192.168.x.x`（服务端的内网 IP）
   - 公网：需要配置端口转发
2. **端口**: `5900`（默认）
3. **密码**: `123456`（默认）
4. 点击"连接"按钮

### 4. 远程控制

连接成功后：
- **鼠标控制**: 直接在屏幕显示区域移动鼠标和点击
- **键盘控制**: 在窗口获得焦点时，键盘输入会发送到远程
- **画质调整**: 使用工具栏的"画质"下拉菜单
- **缩放模式**: 选择"自适应"或"原始尺寸"
- **帧率调整**: 选择 15/30/60 FPS

## 网络配置

### 局域网连接

1. 确保两台电脑在同一局域网
2. 查看服务端 IP：
   ```bash
   ipconfig
   ```
   查找 "IPv4 地址"
3. 客户端使用该 IP 连接

### 公网连接（需要路由器配置）

1. 在路由器中配置端口转发：
   - 外部端口：5900
   - 内部 IP：服务端局域网 IP
   - 内部端口：5900
   - 协议：TCP

2. 查询公网 IP：访问 https://www.ip.cn/

3. 客户端使用公网 IP 连接

### 使用 VPN（最安全）

推荐使用 ZeroTier、Tailscale 等 VPN 方案组建虚拟局域网

## 性能调优

### 局域网环境（推荐设置）
- 帧率：60 FPS
- 画质：超高
- 缩放：根据需要

### 广域网/公网环境（推荐设置）
- 帧率：15-30 FPS
- 画质：中或高
- 缩放：自适应

### 低带宽环境
- 帧率：15 FPS
- 画质：低或中
- 缩放：自适应

## 故障排除

### 编译错误

**错误：找不到 .NET SDK**
- 解决：安装 .NET 6.0 SDK

**错误：NuGet 包还原失败**
- 解决：检查网络连接，或配置 NuGet 镜像源

**错误：SharpDX 相关错误**
- 解决：手动安装：`dotnet add package SharpDX.DXGI`

### 连接问题

**无法连接到服务端**
1. 检查服务端是否已启动
2. 检查 IP 地址是否正确
3. 检查防火墙设置
4. 使用 `ping` 测试网络连通性
5. 使用 `telnet 192.168.x.x 5900` 测试端口连通性

**连接后立即断开**
- 检查密码是否正确
- 查看服务端日志

### 性能问题

**画面卡顿**
1. 降低帧率
2. 降低画质
3. 关闭其他占用带宽的程序
4. 检查 CPU 占用情况

**延迟高**
1. 检查网络延迟：`ping 服务端IP`
2. 使用有线连接代替 Wi-Fi
3. 关闭后台下载任务

**服务端 CPU 占用高**
1. 降低客户端请求的帧率
2. 降低画质设置
3. 关闭不必要的后台程序

### 输入控制问题

**鼠标/键盘无响应**
1. 确认服务端以管理员权限运行
2. 检查是否有安全软件拦截
3. 在服务端任务管理器中确认进程权限

**鼠标位置不准确**
1. 检查缩放模式设置
2. 尝试切换到"原始尺寸"模式
3. 重新连接

## 安全建议

1. **修改默认密码**：在代码中修改 `Password` 属性
2. **使用强密码**：至少 8 位，包含字母数字特殊字符
3. **限制访问**：仅在受信任网络中使用
4. **定期更新**：及时更新操作系统和安全补丁
5. **审计日志**：定期检查连接日志
6. **使用加密**：生产环境建议配合 VPN 使用

## 开发和调试

### 查看日志

服务端和客户端的状态信息会显示在各自的窗口中

### 修改端口

在 `RemoteServer.cs` 中修改：
```csharp
public int Port { get; private set; } = 5900;  // 修改此处
```

### 修改默认密码

在 `RemoteServer.cs` 中修改：
```csharp
public string Password { get; set; } = "123456";  // 修改此处
```

### 调试模式运行

```bash
# 服务端
dotnet run --project Server/RemoteControl.Server.csproj

# 客户端
dotnet run --project Client/RemoteControl.Client.csproj
```

## 系统要求详细说明

### 支持的操作系统
- Windows 10 (1809 或更高版本)
- Windows 11
- Windows Server 2016
- Windows Server 2019
- Windows Server 2022

### 硬件要求

**最低配置**:
- CPU: 双核 2.0 GHz
- 内存: 2 GB RAM
- 网络: 1 Mbps

**推荐配置**:
- CPU: 四核 2.5 GHz 或更高
- 内存: 4 GB RAM 或更高
- 网络: 10 Mbps 或更高（局域网 100 Mbps+）

### 软件依赖

- .NET 6.0 Runtime（Desktop）- 自动包含在编译输出中
- Visual C++ Redistributable（通常系统已安装）

## 常见问题

**Q: 支持 Mac 或 Linux 吗？**
A: 目前仅支持 Windows。跨平台支持需要重写屏幕捕获和输入模拟部分。

**Q: 可以同时连接多个客户端吗？**
A: 当前版本仅支持一个客户端连接。新连接会断开旧连接。

**Q: 传输是加密的吗？**
A: 当前版本未加密。建议在受信任网络或 VPN 中使用。

**Q: 支持多显示器吗？**
A: 当前仅支持主显示器。多显示器支持在开发计划中。

**Q: 可以传输音频吗？**
A: 当前不支持音频传输。

**Q: 文件传输功能在哪里？**
A: 文件传输后端已实现，前端 UI 开发中。

## 技术支持

如遇到问题：
1. 查看本文档的故障排除部分
2. 检查 README.md 中的已知限制
3. 提交详细的错误信息和日志

---

祝使用愉快！
