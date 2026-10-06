using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RemoteControl.Common
{
    // 网络消息包结构
    public class Packet
    {
        public MessageType Type { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();

        // 序列化为字节数组
        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // 写入类型 (1 byte)
            writer.Write((byte)Type);

            // 写入数据长度 (4 bytes)
            writer.Write(Data.Length);

            // 写入数据
            writer.Write(Data);

            return ms.ToArray();
        }

        // 从字节数组反序列化
        public static Packet? Deserialize(byte[] buffer, int offset, int count)
        {
            if (count < 5) return null; // 至少需要 1 + 4 字节

            using var ms = new MemoryStream(buffer, offset, count);
            using var reader = new BinaryReader(ms);

            var packet = new Packet
            {
                Type = (MessageType)reader.ReadByte(),
            };

            int dataLength = reader.ReadInt32();
            if (dataLength < 0 || dataLength > count - 5) return null;

            packet.Data = reader.ReadBytes(dataLength);

            return packet;
        }
    }

    // 连接消息
    public class ConnectMessage
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string NickName { get; set; } = "";   // 客户端显示给其他人的名字，可为空

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static ConnectMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<ConnectMessage>(Encoding.UTF8.GetString(data));
    }

    // 在线客户端列表
    public class ViewerInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
    }

    public class ViewerListMessage
    {
        public int YourId { get; set; }   // 收件人自己的 ID，界面据此标出"我"
        public int ControllerId { get; set; }  // 最近在操作的客户端，0 表示没人
        public ViewerInfo[] Viewers { get; set; } = Array.Empty<ViewerInfo>();

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static ViewerListMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<ViewerListMessage>(Encoding.UTF8.GetString(data));
    }

    // 协作动态：谁做了什么
    public class InputActivityMessage
    {
        public int ViewerId { get; set; }
        public string Name { get; set; } = "";
        public ActivityKind Kind { get; set; }
        public int KeyCode { get; set; }             // Kind = Key：虚拟键码
        public KeyModifiers Modifiers { get; set; }  // Kind = Key：同时按住的修饰键
        public MouseButton Button { get; set; }      // Kind = Click
        public int X { get; set; }
        public int Y { get; set; }
        public string Text { get; set; } = "";       // Kind = Setting：说明文字
        public long Timestamp { get; set; }

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static InputActivityMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<InputActivityMessage>(Encoding.UTF8.GetString(data));
    }

    /// <summary>
    /// 中继和被控端之间的封装：一条中继连接上承载多个客户端，靠客户端ID区分
    /// </summary>
    public static class RelayEnvelope
    {
        public static Packet FromClient(int clientId, Packet inner)
        {
            byte[] body = inner.Serialize();
            var data = new byte[4 + body.Length];
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), clientId);
            Buffer.BlockCopy(body, 0, data, 4, body.Length);
            return new Packet { Type = MessageType.RelayFromClient, Data = data };
        }

        public static bool TryReadFromClient(Packet packet, out int clientId, out Packet inner)
        {
            clientId = 0;
            inner = null!;
            if (packet.Type != MessageType.RelayFromClient || packet.Data.Length < 4 + 5) return false;

            clientId = BitConverter.ToInt32(packet.Data, 0);
            var p = Packet.Deserialize(packet.Data, 4, packet.Data.Length - 4);
            if (p == null) return false;
            inner = p;
            return true;
        }

        public static Packet ToClients(IReadOnlyList<int> clientIds, Packet inner)
        {
            if (clientIds.Count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(clientIds));

            byte[] body = inner.Serialize();
            int header = 2 + clientIds.Count * 4;
            var data = new byte[header + body.Length];
            BitConverter.TryWriteBytes(data.AsSpan(0, 2), (ushort)clientIds.Count);
            for (int i = 0; i < clientIds.Count; i++)
            {
                BitConverter.TryWriteBytes(data.AsSpan(2 + i * 4, 4), clientIds[i]);
            }
            Buffer.BlockCopy(body, 0, data, header, body.Length);
            return new Packet { Type = MessageType.RelayToClients, Data = data };
        }

        public static bool TryReadToClients(Packet packet, out int[] clientIds, out Packet inner)
        {
            clientIds = Array.Empty<int>();
            inner = null!;
            if (packet.Type != MessageType.RelayToClients || packet.Data.Length < 2) return false;

            int count = BitConverter.ToUInt16(packet.Data, 0);
            int header = 2 + count * 4;
            if (packet.Data.Length < header + 5) return false;

            clientIds = new int[count];
            for (int i = 0; i < count; i++)
            {
                clientIds[i] = BitConverter.ToInt32(packet.Data, 2 + i * 4);
            }

            var p = Packet.Deserialize(packet.Data, header, packet.Data.Length - header);
            if (p == null) return false;
            inner = p;
            return true;
        }
    }

    // 屏幕数据消息
    public class ScreenDataMessage
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(Width);
            writer.Write(Height);
            writer.Write(Timestamp);
            writer.Write(ImageData.Length);
            writer.Write(ImageData);

            return ms.ToArray();
        }

        public static ScreenDataMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var msg = new ScreenDataMessage
            {
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Timestamp = reader.ReadInt64(),
            };

            int imageLength = reader.ReadInt32();
            msg.ImageData = reader.ReadBytes(imageLength);

            return msg;
        }
    }

    // H.264 视频帧消息
    public class VideoFrameMessage
    {
        public int Width { get; set; }        // 被控端真实分辨率（鼠标坐标换算用），不是编码分辨率
        public int Height { get; set; }
        public long Timestamp { get; set; }
        public bool IsKeyFrame { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(Data.Length + 32);
            using var writer = new BinaryWriter(ms);

            writer.Write(Width);
            writer.Write(Height);
            writer.Write(Timestamp);
            writer.Write(IsKeyFrame);
            writer.Write(Data.Length);
            writer.Write(Data);

            return ms.ToArray();
        }

        public static VideoFrameMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var msg = new VideoFrameMessage
            {
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                Timestamp = reader.ReadInt64(),
                IsKeyFrame = reader.ReadBoolean(),
            };

            int length = reader.ReadInt32();
            msg.Data = reader.ReadBytes(length);

            return msg;
        }
    }

    // 鼠标事件消息
    public class MouseMessage
    {
        public int X { get; set; }
        public int Y { get; set; }
        public MouseButton Button { get; set; }
        public MouseEventType EventType { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(X);
            writer.Write(Y);
            writer.Write((byte)Button);
            writer.Write((byte)EventType);

            return ms.ToArray();
        }

        public static MouseMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            return new MouseMessage
            {
                X = reader.ReadInt32(),
                Y = reader.ReadInt32(),
                Button = (MouseButton)reader.ReadByte(),
                EventType = (MouseEventType)reader.ReadByte(),
            };
        }
    }

    // 鼠标滚轮消息
    public class MouseWheelMessage
    {
        public int Delta { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(Delta);
            return ms.ToArray();
        }

        public static MouseWheelMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            return new MouseWheelMessage { Delta = reader.ReadInt32() };
        }
    }

    // 键盘消息
    public class KeyMessage
    {
        public int KeyCode { get; set; }
        public bool IsDown { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(KeyCode);
            writer.Write(IsDown);

            return ms.ToArray();
        }

        public static KeyMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            return new KeyMessage
            {
                KeyCode = reader.ReadInt32(),
                IsDown = reader.ReadBoolean(),
            };
        }
    }

    // 文件信息
    public class FileInfo
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime LastModified { get; set; }
    }

    // 文件列表请求
    public class FileListRequestMessage
    {
        public string Path { get; set; } = "";

        public byte[] Serialize() => Encoding.UTF8.GetBytes(Path);
        public static FileListRequestMessage Deserialize(byte[] data) =>
            new() { Path = Encoding.UTF8.GetString(data) };
    }

    // 文件列表响应
    public class FileListResponseMessage
    {
        public string Path { get; set; } = "";
        public FileInfo[] Files { get; set; } = Array.Empty<FileInfo>();

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static FileListResponseMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<FileListResponseMessage>(Encoding.UTF8.GetString(data));
    }

    // 文件传输请求
    public class FileTransferRequestMessage
    {
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public string TransferId { get; set; } = "";

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static FileTransferRequestMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<FileTransferRequestMessage>(Encoding.UTF8.GetString(data));
    }

    // 文件数据块
    public class FileDataMessage
    {
        public string TransferId { get; set; } = "";
        public long Offset { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            byte[] idBytes = Encoding.UTF8.GetBytes(TransferId);
            writer.Write(idBytes.Length);
            writer.Write(idBytes);
            writer.Write(Offset);
            writer.Write(Data.Length);
            writer.Write(Data);

            return ms.ToArray();
        }

        public static FileDataMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            int idLength = reader.ReadInt32();
            string transferId = Encoding.UTF8.GetString(reader.ReadBytes(idLength));
            long offset = reader.ReadInt64();
            int dataLength = reader.ReadInt32();

            return new FileDataMessage
            {
                TransferId = transferId,
                Offset = offset,
                Data = reader.ReadBytes(dataLength),
            };
        }
    }

    // 系统信息
    public class SystemInfoMessage
    {
        public string OSVersion { get; set; } = "";
        public string ComputerName { get; set; } = "";
        public string UserName { get; set; } = "";
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static SystemInfoMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<SystemInfoMessage>(Encoding.UTF8.GetString(data));
    }

    // 显示器信息（坐标为虚拟桌面物理像素）
    public class MonitorInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }
    }

    public class MonitorListMessage
    {
        public MonitorInfo[] Monitors { get; set; } = Array.Empty<MonitorInfo>();
        public int Current { get; set; }

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static MonitorListMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<MonitorListMessage>(Encoding.UTF8.GetString(data));
    }

    public enum AnnotationKind : byte
    {
        Stroke = 0,  // 一笔（Points 为 0~1 的归一化坐标 x0,y0,x1,y1...）
        Clear = 1    // 清空所有标注
    }

    // 白板标注：坐标相对当前显示器归一化，和分辨率、缩放无关
    public class AnnotationMessage
    {
        public AnnotationKind Kind { get; set; }
        public int ViewerId { get; set; }      // 服务端广播时填写
        public string Name { get; set; } = ""; // 服务端广播时填写
        public uint Color { get; set; } = 0xFFF87171; // ARGB
        public float Thickness { get; set; } = 3;
        public float[] Points { get; set; } = Array.Empty<float>();

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static AnnotationMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<AnnotationMessage>(Encoding.UTF8.GetString(data));
    }

    public class FileTransferErrorMessage
    {
        public string TransferId { get; set; } = "";
        public string Error { get; set; } = "";

        public byte[] Serialize() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
        public static FileTransferErrorMessage? Deserialize(byte[] data) =>
            JsonConvert.DeserializeObject<FileTransferErrorMessage>(Encoding.UTF8.GetString(data));
    }

    // 画质设置消息
    public class QualityMessage
    {
        public Quality Quality { get; set; }

        public byte[] Serialize() => new[] { (byte)Quality };
        public static QualityMessage Deserialize(byte[] data) =>
            new() { Quality = (Quality)data[0] };
    }

    // 缩放设置消息
    public class ScaleMessage
    {
        public ScaleMode Mode { get; set; }
        public float CustomScale { get; set; } = 1.0f;

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((byte)Mode);
            writer.Write(CustomScale);
            return ms.ToArray();
        }

        public static ScaleMessage? Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            return new ScaleMessage
            {
                Mode = (ScaleMode)reader.ReadByte(),
                CustomScale = reader.ReadSingle(),
            };
        }
    }
}
