using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RemoteControl.Common;

namespace RemoteControl.Server.Unattended
{
    /// <summary>agent 推给界面的状态（一行一个 JSON）</summary>
    public sealed class AgentStatus
    {
        public bool Registered { get; set; }
        public string RelayHost { get; set; } = "";
        public ViewerInfo[] Viewers { get; set; } = Array.Empty<ViewerInfo>();
        public string[] NewLogs { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// agent 端：命名管道服务，每秒把状态推给连上来的界面。
    /// 只允许 SYSTEM 和管理员连接（界面本身就以管理员运行）
    /// </summary>
    internal sealed class StatusPipeServer : IDisposable
    {
        public const string PipeName = "LinkDeskAgent";

        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Queue<string> _pendingLogs = new();
        private bool _registered;
        private string _relayHost = "";
        private ViewerInfo[] _viewers = Array.Empty<ViewerInfo>();

        public void SetRegistered(bool registered, string relayHost)
        {
            lock (_gate)
            {
                _registered = registered;
                _relayHost = relayHost;
            }
        }

        public void SetViewers(IReadOnlyList<ViewerInfo> viewers)
        {
            lock (_gate) _viewers = viewers.ToArray();
        }

        public void Log(string text)
        {
            lock (_gate)
            {
                _pendingLogs.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");
                while (_pendingLogs.Count > 200) _pendingLogs.Dequeue();
            }
        }

        public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        private static PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                    await pipe.WaitForConnectionAsync(ct);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                    // 刚连上的界面先拿到完整状态，之后每秒推一次
                    while (pipe.IsConnected && !ct.IsCancellationRequested)
                    {
                        AgentStatus snapshot;
                        lock (_gate)
                        {
                            snapshot = new AgentStatus
                            {
                                Registered = _registered,
                                RelayHost = _relayHost,
                                Viewers = _viewers,
                                NewLogs = _pendingLogs.ToArray()
                            };
                            _pendingLogs.Clear();
                        }
                        await writer.WriteLineAsync(JsonConvert.SerializeObject(snapshot));
                        await Task.Delay(1000, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // 界面关闭，等下一个
                }
                catch (Exception ex)
                {
                    AgentLog.Write("状态管道出错: " + ex.Message);
                    await Task.Delay(2000, ct).ContinueWith(_ => { });
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    /// <summary>界面端：连上 agent 的状态管道，收到状态就回调（后台线程）</summary>
    internal sealed class StatusPipeClient : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        /// <summary>收到状态（后台线程触发）；null 表示和 agent 断开</summary>
        public event Action<AgentStatus?>? StatusReceived;

        public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", StatusPipeServer.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(2000, ct);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;
                        var status = JsonConvert.DeserializeObject<AgentStatus>(line);
                        if (status != null) StatusReceived?.Invoke(status);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // agent 还没启动或刚重启，稍后重连
                }

                if (ct.IsCancellationRequested) break;
                StatusReceived?.Invoke(null);
                await Task.Delay(1500, ct).ContinueWith(_ => { });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
