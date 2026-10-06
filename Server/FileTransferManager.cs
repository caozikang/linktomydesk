using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RemoteControl.Common;

namespace RemoteControl.Server
{
    /// <summary>
    /// 文件浏览和传输。每个传输按"访问者 ID + 传输 ID"区分，访问者离开时清理他的所有传输。
    /// 路径里的 "~desktop" 表示当前登录用户的桌面（无人值守时进程是 SYSTEM，不能用自己的桌面）
    /// </summary>
    public class FileTransferManager
    {
        public const string DesktopToken = "~desktop";
        private const int ChunkSize = 64 * 1024;

        private sealed class Transfer
        {
            public FileStream Stream = null!;
            public string Path = "";
            public bool IsUpload;
        }

        private readonly ConcurrentDictionary<(int Viewer, string Id), Transfer> _transfers = new();

        #region 路径

        /// <summary>把 "~desktop\xxx" 换成真实路径</summary>
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith(DesktopToken, StringComparison.OrdinalIgnoreCase)) return path;
            string rest = path[DesktopToken.Length..].TrimStart('\\', '/');
            string desktop = InteractiveUserDesktop();
            return rest.Length == 0 ? desktop : Path.Combine(desktop, rest);
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetFolderPath(IntPtr hwnd, int csidl, IntPtr token, uint flags, System.Text.StringBuilder path);

        private const int CSIDL_DESKTOPDIRECTORY = 0x10;

        /// <summary>
        /// 当前登录用户的桌面。普通模式下就是自己的桌面；
        /// SYSTEM 进程取控制台会话里登录用户的桌面，没人登录时退回公共桌面
        /// </summary>
        private static string InteractiveUserDesktop()
        {
            if (!Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            if (WTSQueryUserToken(WTSGetActiveConsoleSessionId(), out IntPtr token))
            {
                try
                {
                    var sb = new System.Text.StringBuilder(260);
                    if (SHGetFolderPath(IntPtr.Zero, CSIDL_DESKTOPDIRECTORY, token, 0, sb) == 0) return sb.ToString();
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        }

        /// <summary>目标文件已存在时加 (1)、(2)，不覆盖对方的文件</summary>
        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        #endregion

        public FileListResponseMessage GetFileList(string path)
        {
            string real = ResolvePath(path);
            var response = new FileListResponseMessage { Path = real };

            try
            {
                if (string.IsNullOrEmpty(real))
                {
                    // 驱动器列表
                    response.Files = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new Common.FileInfo
                        {
                            Name = d.Name,
                            IsDirectory = true,
                            Size = d.TotalSize,
                            LastModified = DateTime.Now
                        }).ToArray();
                    return response;
                }

                var dirInfo = new DirectoryInfo(real);
                if (!dirInfo.Exists) return response;

                var files = new List<Common.FileInfo>
                {
                    // 父目录（盘符根目录的上一级是驱动器列表，客户端自己处理）
                    new() { Name = "..", IsDirectory = true }
                };

                // 隐藏和系统文件不列出
                const FileAttributes skip = FileAttributes.Hidden | FileAttributes.System;
                foreach (var dir in dirInfo.EnumerateDirectories().Where(d => (d.Attributes & skip) == 0))
                {
                    files.Add(new Common.FileInfo { Name = dir.Name, IsDirectory = true, LastModified = dir.LastWriteTime });
                }
                foreach (var file in dirInfo.EnumerateFiles().Where(f => (f.Attributes & skip) == 0))
                {
                    files.Add(new Common.FileInfo { Name = file.Name, Size = file.Length, LastModified = file.LastWriteTime });
                }
                response.Files = files.ToArray();
            }
            catch (Exception)
            {
                // 没有权限等，返回空列表，客户端会提示打不开
            }

            return response;
        }

        #region 传输

        /// <summary>打开要下载的文件。成功返回 null，否则返回失败原因</summary>
        public string? OpenRead(int viewerId, string transferId, string path, out string realPath)
        {
            realPath = ResolvePath(path);
            try
            {
                var stream = new FileStream(realPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, useAsync: true);
                _transfers[(viewerId, transferId)] = new Transfer { Stream = stream, Path = realPath };
                return null;
            }
            catch (Exception ex)
            {
                return $"无法读取 {Path.GetFileName(realPath)}: {ex.Message}";
            }
        }

        /// <summary>读下一块，读完或已取消返回 null</summary>
        public async Task<FileDataMessage?> ReadChunkAsync(int viewerId, string transferId)
        {
            if (!_transfers.TryGetValue((viewerId, transferId), out var t)) return null;

            long offset = t.Stream.Position;
            byte[] buffer = new byte[ChunkSize];
            int read;
            try
            {
                read = await t.Stream.ReadAsync(buffer.AsMemory());
            }
            catch (ObjectDisposedException)
            {
                return null; // 读的同时被取消
            }
            if (read == 0) return null;

            if (read < buffer.Length) Array.Resize(ref buffer, read);
            return new FileDataMessage { TransferId = transferId, Offset = offset, Data = buffer };
        }

        /// <summary>
        /// 打开上传目标。目录不存在就创建，同名文件不覆盖。成功返回 null，否则返回失败原因
        /// </summary>
        public string? OpenWrite(int viewerId, string transferId, string path, out string realPath)
        {
            realPath = ResolvePath(path);
            try
            {
                string? dir = Path.GetDirectoryName(realPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(Path.GetFileName(realPath)))
                {
                    return "目标路径无效";
                }
                Directory.CreateDirectory(dir);
                realPath = UniquePath(realPath);

                var stream = new FileStream(realPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _transfers[(viewerId, transferId)] = new Transfer { Stream = stream, Path = realPath, IsUpload = true };
                return null;
            }
            catch (Exception ex)
            {
                return $"无法写入 {Path.GetFileName(realPath)}: {ex.Message}";
            }
        }

        /// <summary>写一块。成功返回 null，失败时删除半截文件并返回原因</summary>
        public string? WriteChunk(int viewerId, string transferId, long offset, byte[] data)
        {
            if (!_transfers.TryGetValue((viewerId, transferId), out var t) || !t.IsUpload) return null; // 已取消，后面的块忽略

            try
            {
                if (t.Stream.Position != offset) t.Stream.Position = offset;
                t.Stream.Write(data, 0, data.Length);
                return null;
            }
            catch (Exception ex)
            {
                Cancel(viewerId, transferId);
                return $"写入失败: {ex.Message}";
            }
        }

        /// <summary>正常结束，关闭文件。传输不存在（已取消）返回 false</summary>
        public bool Complete(int viewerId, string transferId)
        {
            if (!_transfers.TryRemove((viewerId, transferId), out var t)) return false;
            t.Stream.Dispose();
            return true;
        }

        /// <summary>取消传输。上传到一半的文件删掉</summary>
        public void Cancel(int viewerId, string transferId)
        {
            if (!_transfers.TryRemove((viewerId, transferId), out var t)) return;
            t.Stream.Dispose();
            if (t.IsUpload)
            {
                try
                {
                    File.Delete(t.Path);
                }
                catch
                {
                    // 删除失败就留着
                }
            }
        }

        /// <summary>访问者离开时取消他的所有传输</summary>
        public void CancelViewer(int viewerId)
        {
            foreach (var key in _transfers.Keys.Where(k => k.Viewer == viewerId).ToList())
            {
                Cancel(key.Viewer, key.Id);
            }
        }

        public void CancelAllTransfers()
        {
            foreach (var key in _transfers.Keys.ToList())
            {
                Cancel(key.Viewer, key.Id);
            }
        }

        #endregion
    }
}