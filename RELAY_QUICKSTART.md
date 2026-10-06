# NAT 穿透解决方案 - 快速指南

## 问题
服务端和客户端都在 NAT 后面（家庭路由器、公司防火墙），无法直接连接。

## 解决方案
使用中继服务器（Relay Server）进行数据转发。

## 架构图

```
家庭网络A (NAT)              云服务器 (公网)           家庭网络B (NAT)
┌─────────────┐              ┌─────────────┐           ┌─────────────┐
│  服务端      │─────连接1───►│  中继服务器  │◄───连接2──│   客户端     │
│ (被控端)     │              │  16888端口   │           │  (控制端)    │
└─────────────┘              └─────────────┘           └─────────────┘
                                    │
                             转发所有数据包
```

## 三步完成部署

### 步骤 1: 部署中继服务器（云服务器）

**购买云服务器**（任选一家）:
- 阿里云轻量应用服务器 (~¥50/月)
- 腾讯云轻量应用服务器 (~¥50/月)
- 搬瓦工 VPS (~$5/月)
- AWS EC2 / DigitalOcean Droplet

**配置要求**:
- 操作系统: Windows Server 2019+ 或 Ubuntu 20.04+ (推荐 Linux)
- CPU: 1核
- 内存: 512MB - 1GB
- 带宽: 5 Mbps
- 端口: 开放 TCP 16888

**部署中继服务器**:

#### 方式 A: Linux 自动部署 (推荐) ⭐

```bash
# 1. 上传项目到服务器
scp -r RemoteControl root@your-server-ip:/root/

# 2. SSH 登录服务器
ssh root@your-server-ip

# 3. 进入项目目录
cd /root/RemoteControl

# 4. 运行自动部署脚本
chmod +x deploy-linux.sh deploy-linux-service.sh
sudo bash deploy-linux.sh

# 5. 配置系统服务
sudo bash deploy-linux-service.sh

# 6. 启动服务
sudo systemctl start remote-relay
sudo systemctl enable remote-relay  # 开机自启

# 7. 查看状态
sudo systemctl status remote-relay
```

详细的 Linux 部署文档: [LINUX_DEPLOY.md](LINUX_DEPLOY.md)

#### 方式 B: Linux 手动部署

```bash
# 1. 安装 .NET 6.0 Runtime
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-runtime-6.0

# 2. 在本地编译
cd RemoteControl
dotnet publish RelayServer/RemoteControl.RelayServer.csproj -c Release -r linux-x64 --self-contained false -o ./publish-linux/RelayServer

# 3. 上传到服务器
scp -r ./publish-linux/RelayServer root@your-server-ip:/opt/remote-relay/

# 4. 在服务器上运行
ssh root@your-server-ip
cd /opt/remote-relay
chmod +x RemoteControl.RelayServer

# 后台运行
nohup ./RemoteControl.RelayServer > relay.log 2>&1 &

# 或配置 Systemd 服务（见 LINUX_DEPLOY.md）
```

#### 方式 C: Windows 部署

```bash
# 1. 在云服务器上安装 .NET 6.0 Runtime
# 下载: https://dotnet.microsoft.com/download/dotnet/6.0

# 2. 上传编译好的文件到服务器
# 或者直接在服务器上编译:
cd RemoteControl
build-relay.bat

# 3. 运行中继服务器
run-relay.bat
```

**开放防火墙**:
```bash
# Linux (Ubuntu)
sudo ufw allow 16888/tcp

# Windows
netsh advfirewall firewall add rule name="RelayServer" dir=in action=allow protocol=TCP localport=16888

# 阿里云/腾讯云：还需在控制台的安全组中开放 16888 端口
```

**记录服务器地址**: 例如 `123.45.67.89` 或 `relay.yourdomain.com`

### 步骤 2: 服务端（被控端）使用中继模式

在 `Server/MainWindow.xaml.cs` 的 `StartButton_Click` 方法中修改：

```csharp
private async void StartButton_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // 使用支持中继的服务器类
        _server = new RemoteServerWithRelay();
        _server.StatusChanged += OnStatusChanged;
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;

        // ★★★ 改这里：选择中继模式 ★★★
        bool useRelay = true; // true=中继模式，false=直连模式
        
        if (useRelay)
        {
            // 中继模式 - 连接到你的云服务器
            string relayHost = "123.45.67.89"; // 改为你的服务器IP
            int relayPort = 16888;
            await _server.StartRelayModeAsync(relayHost, relayPort);
        }
        else
        {
            // 直连模式 - 需要端口转发
            await _server.StartAsync(5900);
        }

        var deviceIdLabel = (TextBlock)FindName("DeviceIdLabel");
        var passwordLabel = (TextBlock)FindName("PasswordLabel");
        var startButton = (Button)FindName("StartButton");
        var stopButton = (Button)FindName("StopButton");

        deviceIdLabel.Text = $"设备ID: {_server.DeviceId}"; // ★ 记下这个ID
        passwordLabel.Text = $"连接密码: {_server.Password}";

        startButton.IsEnabled = false;
        stopButton.IsEnabled = true;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

**重要**: 启动后记录显示的 **设备ID**（如 `A1B2C3D4E`），客户端需要用这个ID连接。

### 步骤 3: 客户端（控制端）使用中继模式

在 `Client/MainWindow.xaml.cs` 的 `ConnectButton_Click` 方法中修改：

```csharp
private async void ConnectButton_Click(object sender, RoutedEventArgs e)
{
    var connectButton = (Button)FindName("ConnectButton");
    var disconnectButton = (Button)FindName("DisconnectButton");

    connectButton.IsEnabled = false;

    // 使用支持中继的客户端类
    _client = new RemoteClientWithRelay();
    _client.StatusChanged += OnStatusChanged;
    _client.ScreenDataReceived += OnScreenDataReceived;
    _client.Connected += OnConnected;
    _client.Disconnected += OnDisconnected;

    // ★★★ 改这里：选择中继模式 ★★★
    bool useRelay = true; // true=中继模式，false=直连模式
    
    bool success;
    if (useRelay)
    {
        // 中继模式
        string relayHost = "123.45.67.89";  // 改为你的服务器IP
        int relayPort = 16888;
        string deviceId = "A1B2C3D4E";      // 改为服务端显示的设备ID
        string password = "123456";
        
        success = await _client.ConnectRelayAsync(relayHost, relayPort, deviceId, password);
    }
    else
    {
        // 直连模式
        var hostTextBox = (TextBox)FindName("HostTextBox");
        var portTextBox = (TextBox)FindName("PortTextBox");
        var passwordBox = (PasswordBox)FindName("PasswordBox");
        
        string host = hostTextBox.Text;
        int.TryParse(portTextBox.Text, out int port);
        string password = passwordBox.Password;
        
        success = await _client.ConnectDirectAsync(host, port, password);
    }

    if (!success)
    {
        connectButton.IsEnabled = true;
        _client.Dispose();
        _client = null;
    }
    else
    {
        disconnectButton.IsEnabled = true;
    }
}
```

## 完整使用流程

1. **在云服务器上**: 运行 `run-relay.bat` 或 `./RemoteControl.RelayServer`
2. **被控端电脑**: 修改代码启用中继模式，编译并运行，记录设备ID
3. **控制端电脑**: 修改代码填入设备ID，编译并运行，点击连接

## 验证连接

**中继服务器日志应显示**:
```
[2024-01-01 10:00:00] 新连接: 1.2.3.4:12345
[2024-01-01 10:00:00] 服务端注册成功: A1B2C3D4E (DESKTOP-PC)
[2024-01-01 10:00:05] 新连接: 5.6.7.8:54321
[2024-01-01 10:00:05] 客户端连接成功: A1B2C3D4E <- 5.6.7.8:54321

========================================
在线设备数: 1
========================================
设备ID: A1B2C3D4E
  服务端: 在线
  客户端: 已连接
  最后活动: 2秒前
```

## 费用估算

**个人使用** (1-5台设备):
- 云服务器: ¥50-100/月
- 1核1G, 5M带宽即可

**小团队** (10-20台设备):
- 云服务器: ¥100-200/月
- 2核2G, 10M带宽

**企业使用** (50+设备):
- 云服务器: ¥500+/月
- 4核4G, 50M带宽
- 考虑负载均衡和高可用

## 常见问题

**Q: 必须用云服务器吗？**
A: 是的，需要一台有公网IP的服务器。家庭宽带通常没有公网IP。

**Q: 可以用免费服务器吗？**
A: 不推荐。免费服务器性能差且不稳定，影响使用体验。

**Q: Windows 还是 Linux 服务器？**
A: **推荐 Linux**（Ubuntu 22.04）。更稳定、成本更低、性能更好，且有自动部署脚本。

**Q: 延迟会增加吗？**
A: 会增加往返延迟（RTT），但选择地理位置近的服务器可将影响降到最低（通常 +20-50ms）。

**Q: 中继服务器能看到我的数据吗？**
A: 当前版本可以。建议后续添加端到端加密（见 `RELAY_SETUP.md` 的安全加固章节）。

**Q: 支持多少个并发连接？**
A: 取决于服务器配置。1核512M约支持5个，1核1G约支持10个，2核2G约支持50个。

## 故障排除

**服务端无法连接到中继服务器**:
```bash
# 1. 检查中继服务器是否运行
# 在服务器上执行: netstat -an | grep 16888

# 2. 测试端口连通性
telnet 123.45.67.89 16888

# 3. 检查防火墙
# Windows: 控制面板 > 防火墙 > 允许应用
# Linux: sudo ufw status
```

**客户端提示"设备不在线"**:
- 确认服务端已成功连接（查看服务端窗口日志）
- 检查设备ID是否正确（区分大小写）
- 查看中继服务器日志确认注册状态

**连接成功但画面卡顿**:
- 降低帧率（30 FPS → 15 FPS）
- 降低画质（高 → 中）
- 检查服务器带宽和CPU占用
- 考虑升级服务器配置

## 下一步

详细文档请查看 `RELAY_SETUP.md`，包括：
- Docker 部署
- Systemd 服务配置
- 负载均衡
- 安全加固
- 监控和日志

---

现在你可以穿透任何 NAT 和防火墙了！🎉
