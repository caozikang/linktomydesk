using System;
using System.Collections.Concurrent;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemoteControl.Client;
using RemoteControl.Common;
using RemoteControl.Server;

// 多人访问端到端自测：本机起中继 + 被控端 + 3 个控制端，走真实 TCP。
// 只注入 F24 键（普通键盘没有这个键，对桌面没有影响），不注入鼠标点击。
const int RelayPort = 26888;
const int DirectPort = 25900;
const string DeviceId = "900000001";
const string Password = "pw1234";
const int F24 = 0x87;

Console.OutputEncoding = Encoding.UTF8; // 中文输出在 Git Bash 里不乱码

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
    if (!ok) failures++;
}

async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 8000)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (cond()) return true;
        await Task.Delay(50);
    }
    return cond();
}

// ---------------- 中继模式 ----------------
Console.WriteLine("== 中继模式 ==");
var relay = new RemoteControl.RelayServer.RelayServer();
_ = Task.Run(() => relay.StartAsync(RelayPort));
await Task.Delay(500);

var server = new RemoteServer { DeviceId = DeviceId, Password = Password };
server.StatusChanged += s => { if (s.Contains("失败") || s.Contains("出错")) Console.WriteLine("    server: " + s); };
_ = Task.Run(() => server.StartRelayModeAsync("127.0.0.1", RelayPort));
Check(await WaitUntil(() => server.IsRegistered), "被控端注册到中继（收到中继确认）");

var alice = new Probe("Alice");
var bob = new Probe("Bob");
var eve = new Probe("Eve");

await alice.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, Password);
Check(await WaitUntil(() => alice.Connected), "Alice 连接成功");

await bob.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, Password);
Check(await WaitUntil(() => bob.Connected), "Bob 同时连接成功");

await eve.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, "wrong");
Check(await WaitUntil(() => eve.Disconnected) && !eve.Connected, "Eve 密码错误被拒绝");

Check(await WaitUntil(() => alice.LastList?.Viewers.Length == 2 && bob.LastList?.Viewers.Length == 2),
    $"双方看到 2 人在线（Alice 看到 {alice.LastList?.Viewers.Length}，Bob 看到 {bob.LastList?.Viewers.Length}）");
Check(alice.LastList != null && bob.LastList != null && alice.LastList.YourId != bob.LastList.YourId &&
      alice.LastList.Viewers.Any(v => v.Name == "Bob") && bob.LastList.Viewers.Any(v => v.Name == "Alice"),
    "列表里有对方名字，且各自 ID 不同");
Check(server.ViewerCount == 2, $"被控端统计 {server.ViewerCount} 人");

Check(await WaitUntil(() => alice.Frames >= 3 && bob.Frames >= 3),
    $"两人都收到并解码了画面（Alice {alice.Frames} 帧，Bob {bob.Frames} 帧）");

// 画面静止时帧数应该很快停止增长（补清晰度的帧发完后不再发）
await Task.Delay(2500);
int f1 = alice.Frames;
await Task.Delay(1500);
Console.WriteLine($"    静止 1.5 秒内 Alice 新增 {alice.Frames - f1} 帧（桌面有变化时会大于 0）");

// Alice 按键，Bob 应该看到
await alice.Client.SendKeyPressAsync(F24, true);
await alice.Client.SendKeyPressAsync(F24, false);
Check(await WaitUntil(() => bob.Activities.Any(a => a.Kind == ActivityKind.Key && a.KeyCode == F24 && a.Name == "Alice")),
    "Bob 看到 Alice 按下 F24");
Check(bob.Activities.Any(a => a.Kind == ActivityKind.TakeControl && a.Name == "Alice"), "Bob 看到 Alice 开始操作");
Check(await WaitUntil(() => bob.LastList?.ControllerId == alice.LastList?.YourId), "在线列表标出 Alice 正在操作");
Check(alice.LastList != null && alice.Activities.Any(a => a.Kind == ActivityKind.Key && a.ViewerId == alice.LastList.YourId), "Alice 自己也收到自己的按键动态");

var keyEvt = bob.Activities.FirstOrDefault(a => a.Kind == ActivityKind.Key);
if (keyEvt != null) Console.WriteLine($"    动态文字: {keyEvt.Name} {ActivityFormatter.Describe(keyEvt)}");

// Bob 按键，Alice 看到，操作者切换
await bob.Client.SendKeyPressAsync(F24, true);
await bob.Client.SendKeyPressAsync(F24, false);
Check(await WaitUntil(() => alice.Activities.Any(a => a.Kind == ActivityKind.Key && a.Name == "Bob")), "Alice 看到 Bob 按键");
Check(await WaitUntil(() => alice.LastList?.ControllerId == bob.LastList?.YourId), "操作者切换为 Bob");

// Alice 修改画质不影响 Bob
int bobBefore = bob.Frames;
await alice.Client.SetQualityAsync(Quality.Low);
await Task.Delay(1500);
Check(bob.Connected && !bob.Disconnected, "Alice 改画质后 Bob 仍在线");

// ---- 新功能 ----
Check(await WaitUntil(() => alice.Monitors?.Monitors.Length > 0), $"收到显示器列表（{alice.Monitors?.Monitors.Length} 个）");

// 普通模式下没有 SasHandler，应该收到失败原因
await alice.Client.SendSasAsync();
Check(await WaitUntil(() => alice.SasResult != null) && alice.SasResult!.Contains("无人值守"), $"Ctrl+Alt+Del 回复：{alice.SasResult}");
server.SasHandler = () => "";
alice.SasResult = null;
await alice.Client.SendSasAsync();
Check(await WaitUntil(() => alice.SasResult == ""), "有 SasHandler 时 Ctrl+Alt+Del 成功");
server.SasHandler = null;

// 标注：Alice 画一笔，Bob 收到，名字由服务端填
AnnotationMessage? serverAnnotation = null;
server.AnnotationReceived += m => serverAnnotation = m;
await alice.Client.SendAnnotationAsync(new AnnotationMessage { Kind = AnnotationKind.Stroke, Name = "伪造", Points = new[] { 0.1f, 0.1f, 0.5f, 0.5f } });
Check(await WaitUntil(() => bob.Annotations.Any(a => a.Kind == AnnotationKind.Stroke && a.Name == "Alice" && a.Points.Length == 4)), "Bob 收到 Alice 的标注（名字以服务端为准）");
Check(serverAnnotation?.Name == "Alice", "被控端也收到标注");
await bob.Client.SendAnnotationAsync(new AnnotationMessage { Kind = AnnotationKind.Clear });
Check(await WaitUntil(() => alice.Annotations.Any(a => a.Kind == AnnotationKind.Clear && a.Name == "Bob")), "Bob 清空标注，Alice 收到");

// 文件：上传到临时目录，再下载回来对比
string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linktomydesk-test-" + Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(tempDir);
string src = System.IO.Path.Combine(tempDir, "src.bin");
var payload = new byte[300 * 1024 + 123]; // 多于 4 块，最后一块不满
Random.Shared.NextBytes(payload);
System.IO.File.WriteAllBytes(src, payload);

string remoteDir = System.IO.Path.Combine(tempDir, "remote");
var up = alice.Client.UploadFile(src, remoteDir + "\\up.bin");
Check(up != null && await WaitUntil(() => up.Finished) && up.Error == null, $"上传完成 {up?.Error}");
await Task.Delay(300); // 等服务端处理 FileUploadComplete 关闭文件
string uploaded = System.IO.Path.Combine(remoteDir, "up.bin");
Check(System.IO.File.Exists(uploaded) && System.IO.File.ReadAllBytes(uploaded).AsSpan().SequenceEqual(payload), "上传内容一致");

var up2 = alice.Client.UploadFile(src, remoteDir + "\\up.bin");
Check(up2 != null && await WaitUntil(() => up2.Finished) && up2.Error == null, "同名再传一次");
await Task.Delay(300);
Check(System.IO.File.Exists(System.IO.Path.Combine(remoteDir, "up (1).bin")), "同名文件不覆盖，另存为 up (1).bin");

string downloaded = System.IO.Path.Combine(tempDir, "down.bin");
var down = await alice.Client.DownloadFileAsync(uploaded, payload.Length, downloaded);
Check(down != null && await WaitUntil(() => down.Finished) && down.Error == null, $"下载完成 {down?.Error}");
Check(System.IO.File.Exists(downloaded) && System.IO.File.ReadAllBytes(downloaded).AsSpan().SequenceEqual(payload), "下载内容一致");

var missing = await alice.Client.DownloadFileAsync(System.IO.Path.Combine(tempDir, "nope.bin"), 10, System.IO.Path.Combine(tempDir, "nope-local.bin"));
Check(missing != null && await WaitUntil(() => missing.Finished) && missing.Error != null && !System.IO.File.Exists(System.IO.Path.Combine(tempDir, "nope-local.bin")),
    $"下载不存在的文件报错并删除本地半截文件：{missing?.Error}");

FileListResponseMessage? desktopList = null;
alice.Client.FileListReceived += m => desktopList = m;
await alice.Client.RequestFileListAsync("~desktop");
Check(await WaitUntil(() => desktopList != null) && !desktopList!.Path.StartsWith("~"), $"~desktop 解析为 {desktopList?.Path}");

try { System.IO.Directory.Delete(tempDir, true); } catch { }

// 接入确认：拒绝的人连不上，允许的人可以
server.ApprovalHandler = (name, _) => Task.FromResult(name != "Mallory");
var mallory = new Probe("Mallory");
await mallory.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, Password);
Check(await WaitUntil(() => mallory.Disconnected) && !mallory.Connected, "接入确认：拒绝 Mallory");
var carol = new Probe("Carol");
await carol.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, Password);
Check(await WaitUntil(() => carol.Connected), "接入确认：允许 Carol");
carol.Client.Dispose();
mallory.Client.Dispose();
Check(await WaitUntil(() => server.ViewerCount == 2), "Carol 离开后剩 2 人");
server.ApprovalHandler = null;

// Alice 离开
alice.Client.Dispose();
Check(await WaitUntil(() => bob.LastList?.Viewers.Length == 1), "Alice 离开后 Bob 看到只剩 1 人");
Check(await WaitUntil(() => bob.Activities.Any(a => a.Kind == ActivityKind.Left && a.Name == "Alice")), "Bob 看到 Alice 离开");
Check(await WaitUntil(() => server.ViewerCount == 1), "被控端统计剩 1 人");

int bf = bob.Frames;
Check(!bob.Disconnected, "Bob 连接不受影响");

// 同名自动加编号
var alice2 = new Probe("Bob");
await alice2.Client.ConnectRelayAsync("127.0.0.1", RelayPort, DeviceId, Password);
Check(await WaitUntil(() => alice2.LastList?.Viewers.Length == 2), "第二个 Bob 加入");
Check(alice2.LastList?.Viewers.Any(v => v.Name == "Bob (2)") == true, "同名自动改为 \"Bob (2)\"");

bob.Client.Dispose();
alice2.Client.Dispose();
eve.Client.Dispose();
Check(await WaitUntil(() => server.ViewerCount == 0), "全部离开后被控端 0 人");

server.Dispose();
relay.Stop();

// ---------------- 直连模式 ----------------
Console.WriteLine("== 直连模式 ==");
var direct = new RemoteServer { DeviceId = DeviceId, Password = Password };
_ = Task.Run(() => direct.StartAsync(DirectPort));
Check(await WaitUntil(() => direct.IsRunning), "直连服务启动");

var d1 = new Probe("甲");
var d2 = new Probe("乙");
await d1.Client.ConnectDirectAsync("127.0.0.1", DirectPort, Password);
await d2.Client.ConnectDirectAsync("127.0.0.1", DirectPort, Password);
Check(await WaitUntil(() => d1.Connected && d2.Connected), "两人直连成功");
Check(await WaitUntil(() => d1.LastList?.Viewers.Length == 2 && d2.LastList?.Viewers.Length == 2), "双方看到 2 人在线");
Check(await WaitUntil(() => d1.Frames >= 3 && d2.Frames >= 3), $"两人都收到画面（{d1.Frames} / {d2.Frames} 帧）");

await d2.Client.SendKeyPressAsync(F24, true);
await d2.Client.SendKeyPressAsync(F24, false);
Check(await WaitUntil(() => d1.Activities.Any(a => a.Kind == ActivityKind.Key && a.Name == "乙")), "甲看到乙按键");

d1.Client.Dispose();
Check(await WaitUntil(() => d2.LastList?.Viewers.Length == 1), "甲离开后乙看到只剩 1 人");
d2.Client.Dispose();
direct.Dispose();

Console.WriteLine(failures == 0 ? "全部通过" : $"失败 {failures} 项");
return failures == 0 ? 0 : 1;

sealed class Probe
{
    public readonly RemoteClientWithRelay Client;
    public volatile bool Connected;
    public volatile bool Disconnected;
    public int Frames;
    public volatile ViewerListMessage? LastList;
    public readonly ConcurrentQueue<InputActivityMessage> Activities = new();

    public Probe(string name)
    {
        Client = new RemoteClientWithRelay { NickName = name };
        Client.Connected += () => Connected = true;
        Client.Disconnected += () => Disconnected = true;
        Client.FrameReady += () =>
        {
            Interlocked.Increment(ref Frames);
            Client.Frames.TakeLatest();
        };
        Client.ViewerListReceived += m => LastList = m;
        Client.ActivityReceived += a => Activities.Enqueue(a);
        Client.MonitorListReceived += m => Monitors = m;
        Client.SasResultReceived += r => SasResult = r;
        Client.AnnotationReceived += a => Annotations.Enqueue(a);
    }

    public volatile MonitorListMessage? Monitors;
    public volatile string? SasResult;
    public readonly ConcurrentQueue<AnnotationMessage> Annotations = new();
}
