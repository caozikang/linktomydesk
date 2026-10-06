using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteControl.Common;

// 界面截图：在屏幕外打开被控端 / 控制端窗口，渲染成 PNG，用来和 linkdesk.html 原型对比。
// 不连接网络、不启动服务、不注入输入。
static class Program
{
    static string OutDir = "";

    [STAThread]
    static void Main(string[] args)
    {
        OutDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(OutDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/UiSnapshot;component/Theme.xaml")
        });
        app.Startup += (_, _) =>
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(OutDir, "error.txt"), ex.ToString());
            }
            app.Shutdown();
        };
        app.Run();
    }

    static void Run()
    {
        // ---- 被控端：未开启 ----
        var server = new RemoteControl.Server.MainWindow();
        Show(server);
        Save(server, "server-idle.png");

        // 模拟"已开启 + 两人访问"：只改界面，不真正启动服务
        Invoke(server, "SetSettingsEnabled", false);
        SetField(server, "_server", new RemoteControl.Server.RemoteServer());
        Get<Button>(server, "_startButton").Visibility = Visibility.Collapsed;
        Get<Button>(server, "_stopButton").Visibility = Visibility.Visible;
        Invoke(server, "SetChip", "密码有效", true);
        Invoke(server, "SetStatus", "已连接中继节点 · relay.example.com", "Ok", true);
        Invoke(server, "OnViewersChanged", new[]
        {
            new ViewerInfo { Id = 1, Name = "Alice", Address = "203.0.113.10" },
            new ViewerInfo { Id = 2, Name = "Bob", Address = "198.51.100.20" }
        }, 1);
        Pump();
        Save(server, "server-running.png");
        SetField(server, "_server", null);
        server.Close();

        // ---- 控制端：主屏 ----
        var client = new RemoteControl.Client.MainWindow();
        Show(client);
        Save(client, "client-home.png");

        // ---- 控制端：连接中 ----
        SetField(client, "_target", "582 104 337");
        Invoke(client, "ShowConnecting");
        Get<TextBlock>(client, "_connectingDetail").Text = "已连接到中继服务器，正在连接设备 582104337...";
        Pump();
        Save(client, "client-connecting.png");

        // ---- 控制端：会话（用被控端截图当假画面） ----
        Invoke(client, "ShowSession");
        client.Width = 1280;
        client.Height = 780;
        var fake = new BitmapImage(new Uri(Path.Combine(OutDir, "server-running.png")));
        Get<Image>(client, "_screenImage").Source = fake;
        Invoke(client, "OnViewerList", null, new ViewerListMessage
        {
            YourId = 2,
            ControllerId = 1,
            Viewers = new[]
            {
                new ViewerInfo { Id = 1, Name = "Alice", Address = "111.19.x.x" },
                new ViewerInfo { Id = 2, Name = "Bob", Address = "101.88.x.x" },
                new ViewerInfo { Id = 3, Name = "张工", Address = "36.110.x.x" }
            }
        });
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        void Act(int id, string name, ActivityKind kind, int key = 0, KeyModifiers mods = KeyModifiers.None) =>
            Invoke(client, "OnActivity", null, new InputActivityMessage
            {
                ViewerId = id, Name = name, Kind = kind, KeyCode = key, Modifiers = mods, X = 812, Y = 440, Timestamp = now
            });
        Act(3, "张工", ActivityKind.Joined);
        Act(1, "Alice", ActivityKind.TakeControl);
        Act(1, "Alice", ActivityKind.Click);
        Act(1, "Alice", ActivityKind.Key, 0x43, KeyModifiers.Ctrl);
        Act(1, "Alice", ActivityKind.Key, 0x56, KeyModifiers.Ctrl);
        Act(1, "Alice", ActivityKind.Key, 0x0D);
        Get<TextBlock>(client, "_latencyText").Text = "38ms · 1.8MB/s";
        Get<System.Windows.Shapes.Ellipse>(client, "_latencyDot").Fill = (Brush)Application.Current.FindResource("Ok");
        Get<Border>(client, "_floatBar").BeginAnimation(UIElement.OpacityProperty, null);
        Get<Border>(client, "_floatBar").Opacity = 1;
        Pump();
        Save(client, "client-session.png");

        Invoke(client, "ShowHome");
        client.Close();
    }

    static void Show(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.Show();
        Pump();
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    static void Save(Window w, string name)
    {
        Pump();
        var root = (FrameworkElement)w.Content;
        root.UpdateLayout();
        int width = (int)Math.Ceiling(root.ActualWidth);
        int height = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        // 窗口背景不在 Content 里，先铺一层
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        rtb.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(OutDir, name));
        enc.Save(fs);
    }

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy;

    static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Any)!.GetValue(o)!;
    static void SetField(object o, string field, object? value) => o.GetType().GetField(field, Any)!.SetValue(o, value);

    static void Invoke(object o, string method, params object?[] args)
    {
        var m = o.GetType().GetMethod(method, Any) ?? throw new MissingMethodException(method);
        m.Invoke(o, args);
        Pump();
    }
}
