using System;

namespace RemoteControl.Common
{
    // 消息类型枚举
    public enum MessageType : byte
    {
        // 连接相关
        Connect = 1,
        ConnectResponse = 2,
        Disconnect = 3,
        Heartbeat = 4,

        // 屏幕控制
        ScreenData = 10,      // 旧版 JPEG 画面，已不再使用
        ScreenRequest = 11,   // 旧版逐帧请求，已不再使用
        FrameAck = 12,        // 客户端解码完一帧后回执，服务端据此控制在途帧数
        VideoFrame = 13,      // H.264 编码的一帧（Annex-B）
        RequestKeyFrame = 14, // 客户端解码出错时请求关键帧

        // 鼠标键盘
        MouseMove = 20,
        MouseClick = 21,
        MouseWheel = 22,
        KeyPress = 23,

        // 文件传输
        FileListRequest = 30,
        FileListResponse = 31,
        FileDownloadRequest = 32,
        FileUploadRequest = 33,
        FileData = 34,
        FileComplete = 35,

        // 系统信息
        SystemInfo = 40,

        // 控制指令
        ChangeQuality = 50,
        ChangeScale = 51,
        ChangeFrameRate = 52, // 数据为 4 字节 int

        // 多人协作
        ViewerList = 60,      // 服务端 -> 客户端：当前在线的客户端列表
        InputActivity = 61,   // 服务端 -> 客户端：谁在操作、按了什么键、点了哪里

        // 系统操作
        SendSas = 80,         // 客户端 -> 服务端：发送 Ctrl+Alt+Del（需无人值守服务）
        SasResult = 81,       // 服务端 -> 客户端：UTF8 文本，空串表示成功，否则为失败原因

        // 多显示器
        MonitorList = 82,     // 服务端 -> 客户端：MonitorListMessage
        SelectMonitor = 83,   // 客户端 -> 服务端：4 字节 int 显示器序号

        // 白板标注（服务端原样广播给所有访问者，并在被控端屏幕上绘制）
        Annotation = 84,      // AnnotationMessage

        // 接入确认
        PendingApproval = 85, // 服务端 -> 客户端：密码正确，等待被控端点"允许"

        // 文件传输（续）
        FileUploadComplete = 86, // 客户端 -> 服务端：UTF8 TransferId，上传完毕
        FileTransferError = 87,  // 双向：FileTransferErrorMessage

        // 中继内部封装（只在中继和被控端之间出现，客户端看不到）
        RelayFromClient = 70, // 中继 -> 被控端：[4 字节客户端ID][原始包]
        RelayToClients = 71,  // 被控端 -> 中继：[2 字节数量][N 个 4 字节客户端ID][原始包]
    }

    // 协作动态类型
    public enum ActivityKind : byte
    {
        Joined = 0,       // 加入
        Left = 1,         // 离开
        TakeControl = 2,  // 开始操作鼠标键盘
        Key = 3,          // 按键
        Click = 4,        // 鼠标点击
        Setting = 5       // 修改画质 / 帧率
    }

    // 按键时按住的修饰键
    [Flags]
    public enum KeyModifiers : byte
    {
        None = 0,
        Shift = 1,
        Ctrl = 2,
        Alt = 4,
        Win = 8
    }

    // 鼠标按钮
    public enum MouseButton : byte
    {
        Left = 0,
        Right = 1,
        Middle = 2
    }

    // 鼠标事件类型
    public enum MouseEventType : byte
    {
        Down = 0,
        Up = 1,
        DoubleClick = 2
    }

    // 画质档位（实际的编码分辨率和码率见 RemoteServer.GetProfile）
    public enum Quality : byte
    {
        Low = 30,      // 流畅：50% 分辨率
        Medium = 60,   // 标准：75% 分辨率
        High = 85,     // 高清：原始分辨率
        Ultra = 95     // 超清：原始分辨率，高码率
    }

    // 缩放模式
    public enum ScaleMode : byte
    {
        Original = 0,  // 原始尺寸
        Fit = 1,       // 自适应缩放
        Custom = 2     // 自定义比例
    }
}
