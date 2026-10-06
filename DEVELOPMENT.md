# 远程控制软件 - 开发文档

## 架构设计

### 整体架构

```
┌─────────────────┐         TCP/IP           ┌─────────────────┐
│                 │◄───────────────────────►│                 │
│  客户端 (控制端)  │  自定义二进制协议         │  服务端 (被控端)  │
│                 │                         │                 │
└─────────────────┘                         └─────────────────┘
       │                                           │
       ├─ RemoteClient                            ├─ RemoteServer
       ├─ MainWindow (WPF)                        ├─ MainWindow (WPF)
       └─ NetworkConnection                       ├─ ScreenCapture
                                                  ├─ InputSimulator
                                                  ├─ FileTransferManager
                                                  └─ NetworkConnection
```

### 三层架构

1. **Common 层** - 共享组件
   - 网络通信协议
   - 消息定义
   - 枚举和常量

2. **Server 层** - 服务端（被控端）
   - 屏幕捕获
   - 输入模拟
   - 文件系统访问

3. **Client 层** - 客户端（控制端）
   - 用户界面
   - 远程控制
   - 显示渲染

## 核心模块

### 1. 网络通信 (NetworkConnection)

**职责**: 封装 TCP 连接，处理消息收发

**关键功能**:
- 异步连接建立
- 消息帧的发送和接收
- 自动重连（计划中）
- 连接状态管理

**数据包格式**:
```
┌──────────┬──────────────┬─────────────┐
│  类型    │   数据长度    │   数据内容   │
│ (1 byte) │  (4 bytes)   │  (N bytes)  │
└──────────┴──────────────┴─────────────┘
```

**代码示例**:
```csharp
// 发送数据包
await connection.SendPacketAsync(new Packet {
    Type = MessageType.ScreenData,
    Data = screenData
});

// 接收数据包
connection.PacketReceived += (packet) => {
    HandlePacket(packet);
};
```

### 2. 屏幕捕获 (ScreenCapture)

**职责**: 高效捕获屏幕内容并压缩

**技术实现**:
- 使用 `Graphics.CopyFromScreen` API
- JPEG 压缩（可配置质量）
- 支持多种画质档位

**性能优化**:
- 复用 Bitmap 对象（可选）
- 使用内存流避免磁盘 IO
- 可扩展为 DirectX 捕获（更高性能）

**代码示例**:
```csharp
var capture = new ScreenCapture();
capture.SetQuality(Quality.High);
byte[] imageData = capture.CaptureScreen();
```

### 3. 输入模拟 (InputSimulator)

**职责**: 将远程输入转换为本地输入事件

**Windows API**:
- `mouse_event` - 鼠标事件
- `keybd_event` - 键盘事件
- `SetCursorPos` - 光标位置

**支持的操作**:
- 鼠标移动
- 鼠标点击（左/右/中键）
- 鼠标滚轮
- 键盘按键（支持组合键）

**代码示例**:
```csharp
var simulator = new InputSimulator();
simulator.MoveMouse(100, 200);
simulator.SimulateMouseClick(100, 200, MouseButton.Left, MouseEventType.Down);
simulator.SimulateKeyPress(0x41, true); // A 键按下
```

### 4. 文件传输 (FileTransferManager)

**职责**: 管理文件浏览和传输

**功能**:
- 目录浏览（驱动器/文件夹/文件）
- 分块传输（64KB 每块）
- 传输进度跟踪
- 多文件传输管理

**传输流程**:
```
客户端                                服务端
   │                                    │
   ├─ FileListRequest ─────────────────►│
   │◄───────────────── FileListResponse─┤
   │                                    │
   ├─ FileDownloadRequest ──────────────►│
   │◄───────────────────── FileData[1]──┤
   │◄───────────────────── FileData[2]──┤
   │◄───────────────────── FileData[N]──┤
   │◄─────────────────── FileComplete───┤
```

## 通信协议

### 消息类型

| 类型 | 编号 | 方向 | 说明 |
|------|------|------|------|
| Connect | 1 | C→S | 连接请求 |
| ConnectResponse | 2 | S→C | 连接响应 |
| Disconnect | 3 | C→S | 断开连接 |
| Heartbeat | 4 | C↔S | 心跳保活 |
| ScreenRequest | 11 | C→S | 请求屏幕数据 |
| ScreenData | 10 | S→C | 屏幕图像数据 |
| MouseMove | 20 | C→S | 鼠标移动 |
| MouseClick | 21 | C→S | 鼠标点击 |
| MouseWheel | 22 | C→S | 鼠标滚轮 |
| KeyPress | 23 | C→S | 键盘按键 |
| FileListRequest | 30 | C→S | 请求文件列表 |
| FileListResponse | 31 | S→C | 文件列表响应 |
| FileData | 34 | S→C | 文件数据块 |
| FileComplete | 35 | S→C | 文件传输完成 |
| ChangeQuality | 50 | C→S | 更改画质 |
| ChangeScale | 51 | C→S | 更改缩放模式 |

### 连接流程

```
客户端                                服务端
   │                                    │
   ├─ TCP Connect ─────────────────────►│
   │                                    │
   ├─ Connect(password) ───────────────►│
   │                                    │ (验证密码)
   │                                    │
   │◄────────── ConnectResponse(OK) ───┤
   │                                    │
   │◄────────────── SystemInfo ────────┤
   │                                    │
   ├─ ScreenRequest ───────────────────►│
   │◄──────────── ScreenData ───────────┤
   │                                    │
   ├─ MouseMove/Click/KeyPress ────────►│
   │                                    │
   ├─ Heartbeat ──────────────────────►│
   │◄───────────────────── Heartbeat ──┤
   │                                    │
```

## 关键技术点

### 1. 坐标转换

客户端显示尺寸与服务端屏幕尺寸可能不同，需要坐标转换：

```csharp
double scaleX = remoteScreenWidth / localDisplayWidth;
double scaleY = remoteScreenHeight / localDisplayHeight;

int remoteX = (int)(localX * scaleX);
int remoteY = (int)(localY * scaleY);
```

### 2. 帧率控制

使用 Timer 定期请求屏幕数据：

```csharp
_timer = new Timer(1000.0 / frameRate);
_timer.Elapsed += async (s, e) => {
    await RequestScreenData();
};
```

### 3. 心跳机制

定期发送心跳包检测连接状态：

```csharp
_heartbeatTimer = new Timer(5000); // 5秒
_heartbeatTimer.Elapsed += async (s, e) => {
    await SendHeartbeat();
};
```

### 4. 图像压缩

使用 JPEG 编码器，可配置质量参数：

```csharp
var encoder = GetEncoder(ImageFormat.Jpeg);
var encoderParams = new EncoderParameters(1);
encoderParams.Param[0] = new EncoderParameter(
    Encoder.Quality, 
    (long)qualityValue
);
bitmap.Save(stream, encoder, encoderParams);
```

## 性能优化建议

### 1. 屏幕捕获优化

**当前实现**: `Graphics.CopyFromScreen`
- 优点：简单，兼容性好
- 缺点：性能一般，CPU 占用较高

**优化方案**: Windows Desktop Duplication API
- 优点：GPU 加速，性能高，CPU 占用低
- 缺点：需要 Windows 8+ 和 DirectX 11

**实现示例**:
```csharp
// 使用 SharpDX 实现 Desktop Duplication
using SharpDX.DXGI;
using SharpDX.Direct3D11;

var adapter = new Adapter1(0);
var device = new Device(adapter);
var output = adapter.GetOutput(0).QueryInterface<Output1>();
var duplication = output.DuplicateOutput(device);
```

### 2. 网络优化

**压缩优化**:
- 使用增量编码（只传输变化区域）
- H.264 视频编码（需要硬件支持）
- 自适应画质（根据带宽调整）

**并发优化**:
- 使用 UDP 传输屏幕数据（可接受丢包）
- TCP 用于控制命令
- 多线程处理

### 3. 渲染优化

**客户端**:
- 使用 WPF 的 WriteableBitmap 减少内存分配
- GPU 加速渲染
- 异步解码图像

## 扩展功能实现

### 1. 多显示器支持

```csharp
var screens = Screen.AllScreens;
foreach (var screen in screens) {
    var bitmap = new Bitmap(
        screen.Bounds.Width,
        screen.Bounds.Height
    );
    var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(
        screen.Bounds.X,
        screen.Bounds.Y,
        0, 0,
        screen.Bounds.Size
    );
}
```

### 2. 剪贴板同步

```csharp
// 监控剪贴板变化
private void MonitorClipboard() {
    var clipboardText = Clipboard.GetText();
    SendClipboardData(clipboardText);
}

// 接收并设置剪贴板
private void OnClipboardReceived(string text) {
    Clipboard.SetText(text);
}
```

### 3. 文件拖放传输

```csharp
// 启用拖放
screenImage.AllowDrop = true;
screenImage.Drop += OnFileDrop;

private void OnFileDrop(object sender, DragEventArgs e) {
    if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var file in files) {
            UploadFile(file);
        }
    }
}
```

### 4. 音频传输

使用 NAudio 库：

```csharp
// 服务端：捕获音频
var waveIn = new WasapiLoopbackCapture();
waveIn.DataAvailable += (s, e) => {
    SendAudioData(e.Buffer, e.BytesRecorded);
};
waveIn.StartRecording();

// 客户端：播放音频
var waveOut = new WaveOutEvent();
var bufferedProvider = new BufferedWaveProvider(waveFormat);
waveOut.Init(bufferedProvider);
waveOut.Play();
```

### 5. 加密传输

使用 TLS/SSL：

```csharp
// 使用 SslStream 包装 NetworkStream
var sslStream = new SslStream(
    networkStream,
    false,
    ValidateServerCertificate
);
await sslStream.AuthenticateAsClientAsync("server.com");
```

## 测试建议

### 单元测试

```csharp
[TestClass]
public class MessageSerializationTests
{
    [TestMethod]
    public void TestMouseMessageSerialization()
    {
        var msg = new MouseMessage {
            X = 100,
            Y = 200,
            Button = MouseButton.Left,
            EventType = MouseEventType.Down
        };
        
        var data = msg.Serialize();
        var deserialized = MouseMessage.Deserialize(data);
        
        Assert.AreEqual(msg.X, deserialized.X);
        Assert.AreEqual(msg.Y, deserialized.Y);
    }
}
```

### 性能测试

```csharp
// 测试屏幕捕获性能
var stopwatch = Stopwatch.StartNew();
for (int i = 0; i < 100; i++) {
    var data = screenCapture.CaptureScreen();
}
stopwatch.Stop();
Console.WriteLine($"平均捕获时间: {stopwatch.ElapsedMilliseconds / 100}ms");
```

### 压力测试

- 长时间连接稳定性测试（24小时+）
- 多客户端并发连接测试
- 网络波动场景测试
- 高分辨率屏幕测试（4K）

## 部署建议

### 1. 独立部署

使用 `--self-contained true` 选项：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

优点：无需安装 .NET Runtime
缺点：文件体积大（~70MB）

### 2. 依赖框架部署

使用 `--self-contained false` 选项：

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

优点：文件体积小（~5MB）
缺点：需要安装 .NET 6.0 Runtime

### 3. 制作安装包

使用 WiX Toolset 或 Inno Setup：

```iss
[Setup]
AppName=RemoteControl
AppVersion=1.0
DefaultDirName={pf}\RemoteControl
OutputBaseFilename=RemoteControl-Setup

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{group}\RemoteControl Server"; Filename: "{app}\Server\RemoteControl.Server.exe"
Name: "{group}\RemoteControl Client"; Filename: "{app}\Client\RemoteControl.Client.exe"
```

## 安全加固

### 1. 加密通信

实现 TLS 加密或自定义加密：

```csharp
// 使用 AES 加密
using (var aes = Aes.Create()) {
    aes.Key = deriveKey(password);
    aes.IV = iv;
    
    var encryptor = aes.CreateEncryptor();
    var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
}
```

### 2. 身份验证

改进认证机制：

```csharp
// 使用哈希 + 盐值
var salt = GenerateSalt();
var hashedPassword = HashPassword(password, salt);

// 服务端存储
SaveCredentials(hashedPassword, salt);

// 验证
var inputHash = HashPassword(inputPassword, salt);
bool authenticated = (inputHash == hashedPassword);
```

### 3. 访问控制

添加 IP 白名单：

```csharp
private readonly HashSet<string> _allowedIPs = new() {
    "192.168.1.100",
    "192.168.1.101"
};

private bool IsAllowed(string remoteIP) {
    return _allowedIPs.Contains(remoteIP);
}
```

### 4. 审计日志

记录所有操作：

```csharp
private void LogAction(string action, string user, string details) {
    var entry = $"[{DateTime.Now}] {user} - {action}: {details}";
    File.AppendAllText("audit.log", entry + "\n");
}
```

## 故障诊断

### 启用详细日志

```csharp
public enum LogLevel { Debug, Info, Warning, Error }

private void Log(LogLevel level, string message) {
    var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
    Console.WriteLine(logEntry);
    File.AppendAllText("app.log", logEntry + "\n");
}
```

### 性能监控

```csharp
private void MonitorPerformance() {
    var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
    var memCounter = new PerformanceCounter("Memory", "Available MBytes");
    
    Log(LogLevel.Info, $"CPU: {cpuCounter.NextValue()}%, Memory: {memCounter.NextValue()}MB");
}
```

## 贡献指南

### 代码规范

- 使用 C# 命名约定
- 添加 XML 文档注释
- 遵循 SOLID 原则
- 编写单元测试

### 提交规范

```
feat: 添加新功能
fix: 修复 bug
docs: 文档更新
refactor: 代码重构
test: 测试相关
chore: 构建/工具相关
```

---

如有疑问，欢迎提交 Issue 或 PR！
