# NAT 穿透和中继服务器方案

## 问题说明

当服务端和客户端都在 NAT 后面（家庭路由器、公司防火墙），无法直接建立连接时，需要通过中继服务器进行转发。

## 解决方案架构

```
┌─────────────┐         ┌──────────────────┐         ┌─────────────┐
│  服务端      │         │   中继服务器      │         │   客户端     │
│ (被控端)     │◄───────►│  (公网服务器)     │◄───────►│  (控制端)    │
│  NAT 后面    │  连接1   │   有公网 IP       │  连接2   │  NAT 后面    │
└─────────────┘         └──────────────────┘         └─────────────┘
        │                        │                           │
        │  1. 注册设备ID         │                           │
        │───────────────────────►│                           │
        │                        │                           │
        │                        │  2. 请求连接设备ID         │
        │                        │◄──────────────────────────┤
        │                        │                           │
        │  3. 转发客户端数据     │                           │
        │◄───────────────────────┤                           │
        │                        │                           │
        │  4. 转发服务端数据     │                           │
        │───────────────────────►│───────────────────────────►│
```

## 实现组件

### 1. 中继服务器 (RelayServer)

**位置**: `RelayServer/Program.cs`

**功能**:
- 监听公网端口（默认 16888）
- 管理设备注册（服务端注册设备ID）
- 转发客户端和服务端之间的所有数据
- 会话管理和超时清理

**部署**: 需要部署在有公网 IP 的服务器上

### 2. 服务端中继模式 (RemoteServerWithRelay)

**位置**: `Server/RemoteServerWithRelay.cs`

**功能**:
- 支持两种模式：直连模式 / 中继模式
- 中继模式：主动连接到中继服务器并注册设备ID
- 通过中继服务器接收客户端请求

### 3. 客户端中继模式 (RemoteClientWithRelay)

**位置**: `Client/RemoteClientWithRelay.cs`

**功能**:
- 支持两种模式：直连模式 / 中继模式
- 中继模式：连接到中继服务器并指定目标设备ID
- 通过中继服务器与服务端通信

## 使用步骤

### 步骤 1: 部署中继服务器

在有公网 IP 的服务器上（如阿里云、腾讯云）：

```bash
# 编译中继服务器
cd RemoteControl
dotnet publish RelayServer/RemoteControl.RelayServer.csproj -c Release -o ./publish/RelayServer

# 运行（Linux/Mac）
cd publish/RelayServer
dotnet RemoteControl.RelayServer.dll

# 运行（Windows）
RemoteControl.RelayServer.exe
```

**开放防火墙端口**: 16888 (TCP)

### 步骤 2: 服务端使用中继模式

修改服务端代码，使用中继模式：

```csharp
// 在 MainWindow.xaml.cs 中
private async void StartButton_Click(object sender, RoutedEventArgs e)
{
    _server = new RemoteServerWithRelay();
    
    // 选择模式
    bool useRelay = true; // 改为 true 使用中继模式
    
    if (useRelay)
    {
        // 中继模式 - 连接到中继服务器
        string relayHost = "your-server-ip.com"; // 你的中继服务器地址
        int relayPort = 16888;
        await _server.StartRelayModeAsync(relayHost, relayPort);
    }
    else
    {
        // 直连模式 - 监听本地端口
        await _server.StartAsync(5900);
    }
    
    // 记录设备ID，告知客户端
    string deviceId = _server.DeviceId; // 例如: A1B2C3D4E
}
```

**记录设备ID**: 服务端启动后会生成唯一的设备ID，客户端需要这个ID才能连接

### 步骤 3: 客户端使用中继模式

修改客户端代码，使用中继模式连接：

```csharp
// 在 MainWindow.xaml.cs 中
private async void ConnectButton_Click(object sender, RoutedEventArgs e)
{
    _client = new RemoteClientWithRelay();
    
    bool useRelay = true; // 改为 true 使用中继模式
    
    if (useRelay)
    {
        // 中继模式
        string relayHost = "your-server-ip.com"; // 中继服务器地址
        int relayPort = 16888;
        string deviceId = "A1B2C3D4E"; // 服务端的设备ID
        string password = "123456";
        
        await _client.ConnectRelayAsync(relayHost, relayPort, deviceId, password);
    }
    else
    {
        // 直连模式
        string serverHost = "192.168.1.100";
        int serverPort = 5900;
        string password = "123456";
        
        await _client.ConnectDirectAsync(serverHost, serverPort, password);
    }
}
```

## 配置说明

### 中继服务器配置

**默认端口**: 16888  
**超时时间**: 5 分钟无心跳自动断开  
**清理周期**: 每 30 秒清理一次超时会话

修改配置在 `RelayServer/Program.cs`:

```csharp
var timeout = TimeSpan.FromMinutes(5); // 修改超时时间
await Task.Delay(30000, ct); // 修改清理周期
```

### 识别协议

中继服务器通过第一个连接包的 `UserName` 字段识别连接类型：

- **服务端**: `UserName = "SERVER:" + 实际用户名`
- **客户端**: `UserName = "CLIENT:" + 实际用户名`

## 成本估算

### 中继服务器要求

**最低配置** (支持 10 个并发连接):
- CPU: 1 核
- 内存: 1GB
- 带宽: 5 Mbps
- 成本: ~$5-10/月

**推荐配置** (支持 50 个并发连接):
- CPU: 2 核
- 内存: 2GB
- 带宽: 20 Mbps
- 成本: ~$15-30/月

**高性能配置** (支持 200 个并发连接):
- CPU: 4 核
- 内存: 4GB
- 带宽: 100 Mbps
- 成本: ~$50-100/月

### 带宽消耗

每个远程控制会话：
- 低画质 15 FPS: ~2 Mbps
- 中画质 30 FPS: ~5 Mbps
- 高画质 30 FPS: ~10 Mbps
- 超高画质 60 FPS: ~20 Mbps

## 安全加固

### 1. 添加设备认证 Token

```csharp
// 生成注册令牌
var token = GenerateSecureToken();

// 服务端注册时携带
var registerMsg = new ConnectMessage {
    DeviceId = DeviceId,
    Password = token // 使用 token 而非明文密码
};
```

### 2. 限制连接速率

```csharp
// 在 RelayServer 中添加
private readonly Dictionary<string, int> _connectionAttempts = new();

private bool CheckRateLimit(string ip) {
    if (!_connectionAttempts.ContainsKey(ip))
        _connectionAttempts[ip] = 0;
    
    _connectionAttempts[ip]++;
    return _connectionAttempts[ip] < 10; // 每分钟最多10次
}
```

### 3. IP 白名单

```csharp
private readonly HashSet<string> _allowedIPs = new() {
    "1.2.3.4",
    "5.6.7.8"
};

private bool IsAllowed(string ip) {
    return _allowedIPs.Contains(ip);
}
```

## 部署示例

### Docker 部署

创建 `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
COPY publish/RelayServer .
EXPOSE 16888
ENTRYPOINT ["dotnet", "RemoteControl.RelayServer.dll"]
```

构建和运行:

```bash
docker build -t remote-relay .
docker run -d -p 16888:16888 --name relay-server remote-relay
```

### Systemd 服务 (Linux)

创建 `/etc/systemd/system/remote-relay.service`:

```ini
[Unit]
Description=Remote Control Relay Server
After=network.target

[Service]
Type=simple
User=relay
WorkingDirectory=/opt/remote-relay
ExecStart=/usr/bin/dotnet /opt/remote-relay/RemoteControl.RelayServer.dll
Restart=always

[Install]
WantedBy=multi-user.target
```

启动服务:

```bash
sudo systemctl daemon-reload
sudo systemctl enable remote-relay
sudo systemctl start remote-relay
```

## 监控和日志

### 查看实时状态

中继服务器每 10 秒自动输出状态：

```
========================================
在线设备数: 3
========================================
设备ID: A1B2C3D4E
  服务端: 在线
  客户端: 已连接
  最后活动: 2秒前

设备ID: F9G8H7I6J
  服务端: 在线
  客户端: 未连接
  最后活动: 15秒前
```

### 日志记录

所有操作都会输出到控制台，可以重定向到文件：

```bash
# Linux/Mac
./RemoteControl.RelayServer > relay.log 2>&1 &

# Windows
RemoteControl.RelayServer.exe > relay.log 2>&1
```

## 故障排除

### 服务端无法连接到中继服务器

1. 检查中继服务器是否运行
2. 检查防火墙规则
3. 测试端口: `telnet relay-server-ip 16888`
4. 检查网络连接

### 客户端提示"设备不在线"

1. 确认服务端已成功连接到中继服务器
2. 检查设备ID是否正确
3. 查看中继服务器日志确认注册状态

### 连接延迟高

1. 选择地理位置更近的中继服务器
2. 使用更高带宽的服务器
3. 降低画质和帧率

### 频繁断线

1. 检查网络稳定性
2. 增加心跳间隔
3. 查看中继服务器负载

## 高级优化

### 1. UDP 打洞（P2P 直连）

实现 STUN/TURN 协议，尝试建立 P2P 连接，失败后回退到中继：

```
客户端 ──► STUN 服务器 ◄── 服务端
   │                        │
   └────► 尝试 P2P 直连 ◄────┘
              │
           失败？使用中继
```

### 2. 多中继服务器

部署多个地理位置分散的中继服务器，自动选择最近的：

```csharp
string[] relayServers = {
    "relay-us.example.com",
    "relay-eu.example.com",
    "relay-asia.example.com"
};

// 选择延迟最低的
var bestRelay = await FindBestRelayServer(relayServers);
```

### 3. 负载均衡

使用 Nginx 或云负载均衡器分发连接：

```nginx
upstream relay_backend {
    least_conn;
    server relay1:16888;
    server relay2:16888;
    server relay3:16888;
}

server {
    listen 16888;
    proxy_pass relay_backend;
}
```

## 总结

通过中继服务器方案，你可以：
- ✅ 无需端口转发或 DMZ 配置
- ✅ 穿透任何 NAT 和防火墙
- ✅ 支持动态 IP 环境
- ✅ 集中管理和监控
- ✅ 易于扩展和维护

**推荐**: 对于个人和小团队，部署一台云服务器作为中继服务器即可满足需求。
