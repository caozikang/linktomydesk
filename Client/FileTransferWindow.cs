using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteControl.Common;
using RemoteControl.Ui;

namespace RemoteControl.Client
{
    /// <summary>
    /// 文件传输窗口：左边本机、右边远程，双击进入目录，选中后点中间的箭头上传 / 下载，底部显示进度。
    /// 也支持从资源管理器把文件拖到右边直接上传。
    /// </summary>
    public sealed class FileTransferWindow : LinkDeskWindow
    {
        /// <summary>远程路径里的占位符：被控端换成当前登录用户的桌面</summary>
        public const string RemoteDesktopToken = "~desktop";

        private readonly RemoteClientWithRelay _client;
        private readonly Pane _local;
        private readonly Pane _remote;
        private readonly StackPanel _transferList = new();
        private readonly Dictionary<string, TransferRow> _rows = new();
        private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        public FileTransferWindow(RemoteClientWithRelay client, string target)
        {
            _client = client;
            ChromeTitle = $"传输文件 · {target}";
            Width = 940;
            Height = 620;
            MinWidth = 720;
            MinHeight = 420;
            SetResizable(true);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _local = new Pane("本 机", LoadLocal);
            _remote = new Pane("远 程", path => _ = _client.RequestFileListAsync(path));
            _local.ExtraButton(Glyph.Home, "桌面", () => _local.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)));
            _remote.ExtraButton(Glyph.Home, "对方桌面", () => _remote.Navigate(RemoteDesktopToken));

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var panes = new Grid();
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panes.Children.Add(_local.Root);

            var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            var upload = UiKit.Button("PrimaryButton", UiKit.Icon(Glyph.ChevronRight, 14), (_, _) => UploadSelected(), "上传到远程当前目录");
            var download = UiKit.Button("GhostButton", UiKit.Icon(Glyph.ChevronLeft, 14), (_, _) => DownloadSelected(), "下载到本机当前目录");
            foreach (var b in new[] { upload, download })
            {
                b.Width = 44;
                b.MinHeight = 0;
                b.Height = 38;
                b.Padding = new Thickness(0);
                b.Margin = new Thickness(0, 6, 0, 6);
                arrows.Children.Add(b);
            }
            Grid.SetColumn(arrows, 1);
            panes.Children.Add(arrows);
            Grid.SetColumn(_remote.Root, 2);
            panes.Children.Add(_remote.Root);
            root.Children.Add(panes);

            // 传输进度（最近的在上面）
            var transfers = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            transfers.Children.Add(new TextBlock { Text = UiKit.Spaced("传输列表"), FontSize = 11, Foreground = UiKit.Br("Sub"), Margin = new Thickness(0, 0, 0, 8) });
            transfers.Children.Add(new ScrollViewer
            {
                MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
                Content = _transferList
            });
            Grid.SetRow(transfers, 1);
            root.Children.Add(transfers);
            Body = root;

            // 拖文件到右边 = 上传到远程当前目录
            _remote.Root.AllowDrop = true;
            _remote.Root.Drop += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                foreach (var f in ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(File.Exists))
                {
                    StartUpload(f);
                }
            };

            _client.FileListReceived += OnRemoteList;
            _client.TransferProgress += OnTransferChanged;
            _client.TransferFinished += OnTransferChanged;
            _progressTimer.Tick += (_, _) => RefreshRows();
            _progressTimer.Start();

            _local.Navigate("");
            _remote.Navigate("");
        }

        protected override void OnClosed(EventArgs e)
        {
            _client.FileListReceived -= OnRemoteList;
            _client.TransferProgress -= OnTransferChanged;
            _client.TransferFinished -= OnTransferChanged;
            _progressTimer.Stop();
            base.OnClosed(e);
        }

        #region 浏览

        private void LoadLocal(string path)
        {
            var items = new List<Common.FileInfo>();
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        items.Add(new Common.FileInfo { Name = d.Name, IsDirectory = true, Size = d.TotalSize });
                    }
                }
                else
                {
                    var dir = new DirectoryInfo(path);
                    items.Add(new Common.FileInfo { Name = "..", IsDirectory = true });
                    items.AddRange(dir.GetDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                        .Select(d => new Common.FileInfo { Name = d.Name, IsDirectory = true, LastModified = d.LastWriteTime }));
                    items.AddRange(dir.GetFiles().Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                        .Select(f => new Common.FileInfo { Name = f.Name, Size = f.Length, LastModified = f.LastWriteTime }));
                }
                _local.Show(path, items, null);
            }
            catch (Exception ex)
            {
                _local.Show(path, items, $"无法打开：{ex.Message}");
            }
        }

        private void OnRemoteList(FileListResponseMessage msg)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 服务端解析 ~desktop 后返回真实路径；空列表且不是根目录说明打不开
                var files = msg.Files.ToList();
                if (!string.IsNullOrEmpty(msg.Path) && files.All(f => f.Name != ".."))
                {
                    files.Insert(0, new Common.FileInfo { Name = "..", IsDirectory = true });
                }
                string? error = files.Count == 0 && !string.IsNullOrEmpty(msg.Path) ? "无法打开（不存在或没有权限）" : null;
                _remote.Show(msg.Path, files, error);
            });
        }

        #endregion

        #region 传输

        private void UploadSelected()
        {
            var selected = _local.SelectedFiles().ToList();
            if (selected.Count == 0)
            {
                _local.Hint("先在左边选中要上传的文件（可按住 Ctrl 多选）");
                return;
            }
            if (string.IsNullOrEmpty(_remote.CurrentPath))
            {
                _remote.Hint("先在右边打开一个目标文件夹");
                return;
            }
            foreach (var f in selected)
            {
                StartUpload(System.IO.Path.Combine(_local.CurrentPath, f.Name));
            }
        }

        private void StartUpload(string localPath)
        {
            string dir = string.IsNullOrEmpty(_remote.CurrentPath) ? RemoteDesktopToken : _remote.CurrentPath;
            var t = _client.UploadFile(localPath, CombineRemote(dir, System.IO.Path.GetFileName(localPath)));
            if (t != null) AddRow(t);
        }

        private async void DownloadSelected()
        {
            var selected = _remote.SelectedFiles().ToList();
            if (selected.Count == 0)
            {
                _remote.Hint("先在右边选中要下载的文件（可按住 Ctrl 多选）");
                return;
            }
            if (string.IsNullOrEmpty(_local.CurrentPath))
            {
                _local.Hint("先在左边打开一个保存位置");
                return;
            }

            foreach (var f in selected)
            {
                string local = UniquePath(System.IO.Path.Combine(_local.CurrentPath, f.Name));
                var t = await _client.DownloadFileAsync(CombineRemote(_remote.CurrentPath, f.Name), f.Size, local);
                if (t != null) AddRow(t);
            }
        }

        private static string CombineRemote(string dir, string name) =>
            dir.EndsWith("\\") ? dir + name : dir + "\\" + name;

        /// <summary>本地已有同名文件时加 (1)、(2)，不覆盖</summary>
        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = System.IO.Path.GetDirectoryName(path)!;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private void OnTransferChanged(RemoteClientWithRelay.FileTransfer t)
        {
            // 进度由定时器统一刷新；结束时立即刷新，并重新列出目标目录
            if (!t.Finished) return;
            Dispatcher.BeginInvoke(() =>
            {
                RefreshRows();
                if (t.Error != null) return;
                if (t.IsUpload) _remote.Reload();
                else _local.Reload();
            });
        }

        private void AddRow(RemoteClientWithRelay.FileTransfer t)
        {
            var row = new TransferRow(t, () => _client.CancelTransfer(t.Id));
            _rows[t.Id] = row;
            _transferList.Children.Insert(0, row.Root);
            row.Refresh();
        }

        private void RefreshRows()
        {
            foreach (var row in _rows.Values) row.Refresh();
        }

        #endregion

        public static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
        };

        /// <summary>一侧的文件列表：标题 + 路径栏 + 列表</summary>
        private sealed class Pane
        {
            private readonly Action<string> _load;
            private readonly TextBox _pathBox = new();
            private readonly ListBox _list = new();
            private readonly TextBlock _hint = new();
            private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };

            public Border Root { get; }
            public string CurrentPath { get; private set; } = "";

            public Pane(string title, Action<string> load)
            {
                _load = load;

                var grid = new Grid { Margin = new Thickness(12) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(_buttons, Dock.Right);
                header.Children.Add(_buttons);
                header.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = UiKit.Br("Sub"), VerticalAlignment = VerticalAlignment.Center });
                grid.Children.Add(header);

                ExtraButton(Glyph.Up, "上一级", GoUp);
                ExtraButton(Glyph.Refresh, "刷新", Reload);

                _pathBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) Navigate(_pathBox.Text.Trim());
                };
                var pathField = UiKit.Field(_pathBox, "此电脑", fontSize: 12);
                pathField.Margin = new Thickness(0, 0, 0, 8);
                Grid.SetRow(pathField, 1);
                grid.Children.Add(pathField);

                _list.Background = Brushes.Transparent;
                _list.BorderThickness = new Thickness(0);
                _list.Foreground = UiKit.Br("Txt");
                _list.SelectionMode = SelectionMode.Extended;
                _list.ItemContainerStyle = FileItemStyle();
                ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
                _list.MouseDoubleClick += (_, e) =>
                {
                    if ((e.OriginalSource as DependencyObject) is { } src && ItemsControl.ContainerFromElement(_list, src) is ListBoxItem item &&
                        item.Content is FrameworkElement fe && fe.Tag is Common.FileInfo f && f.IsDirectory)
                    {
                        Open(f);
                    }
                };
                _list.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && _list.SelectedItem is FrameworkElement fe && fe.Tag is Common.FileInfo f && f.IsDirectory) Open(f);
                    else if (e.Key == Key.Back) GoUp();
                };
                Grid.SetRow(_list, 2);
                grid.Children.Add(_list);

                _hint.FontSize = 12;
                _hint.Foreground = UiKit.Br("Warn");
                _hint.TextWrapping = TextWrapping.Wrap;
                _hint.Margin = new Thickness(0, 6, 0, 0);
                _hint.Visibility = Visibility.Collapsed;
                Grid.SetRow(_hint, 3);
                grid.Children.Add(_hint);

                Root = new Border
                {
                    Background = UiKit.Br("Panel2"),
                    BorderBrush = UiKit.Br("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Child = grid
                };
            }

            public void ExtraButton(string glyph, string tip, Action click)
            {
                var b = UiKit.Button("IconButton", UiKit.Icon(glyph, 11), (_, _) => click(), tip);
                b.Margin = new Thickness(6, 0, 0, 0);
                _buttons.Children.Add(b);
            }

            public void Navigate(string path)
            {
                Hint(null);
                _load(path);
            }

            public void Reload() => Navigate(CurrentPath);

            private void GoUp()
            {
                if (string.IsNullOrEmpty(CurrentPath)) return;
                string trimmed = CurrentPath.TrimEnd('\\');
                int i = trimmed.LastIndexOf('\\');
                // "C:\" 的上一级是驱动器列表
                Navigate(i < 0 || trimmed.Length <= 2 ? "" : trimmed[..(i + 1)]);
            }

            private void Open(Common.FileInfo f)
            {
                if (f.Name == "..") GoUp();
                else if (string.IsNullOrEmpty(CurrentPath)) Navigate(f.Name);
                else Navigate(CombineRemote(CurrentPath, f.Name));
            }

            public void Hint(string? text)
            {
                _hint.Text = text ?? "";
                _hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            public IEnumerable<Common.FileInfo> SelectedFiles() =>
                _list.SelectedItems.OfType<FrameworkElement>().Select(x => x.Tag).OfType<Common.FileInfo>().Where(f => !f.IsDirectory);

            public void Show(string path, IEnumerable<Common.FileInfo> files, string? error)
            {
                CurrentPath = path;
                _pathBox.Text = path;
                _list.Items.Clear();
                foreach (var f in files.OrderByDescending(f => f.Name == "..").ThenByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    _list.Items.Add(Row(f, string.IsNullOrEmpty(path)));
                }
                Hint(error);
            }

            private static FrameworkElement Row(Common.FileInfo f, bool isDrive)
            {
                var g = new Grid { Tag = f, Margin = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = UiKit.Icon(isDrive ? Glyph.Monitor : f.IsDirectory ? Glyph.Folder : Glyph.Document, 13);
                icon.Foreground = f.IsDirectory ? UiKit.Br("Acc") : UiKit.Br("Sub");
                icon.Margin = new Thickness(0, 0, 10, 0);
                g.Children.Add(icon);

                var name = UiKit.Text(f.Name, 13);
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                if (!f.IsDirectory || isDrive)
                {
                    var size = UiKit.Text(FormatSize(f.Size), 11, "Sub");
                    size.Margin = new Thickness(10, 0, 0, 0);
                    size.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(size, 2);
                    g.Children.Add(size);
                }
                return g;
            }

            private static Style FileItemStyle()
            {
                var style = new Style(typeof(ListBoxItem));
                style.Setters.Add(new Setter(Control.TemplateProperty, BuildItemTemplate()));
                return style;
            }

            private static ControlTemplate BuildItemTemplate()
            {
                var template = new ControlTemplate(typeof(ListBoxItem));
                var bd = new FrameworkElementFactory(typeof(Border), "Bd");
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
                bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                template.VisualTree = bd;

                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), "Bd"));
                template.Triggers.Add(hover);
                var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
                selected.Setters.Add(new Setter(Border.BackgroundProperty, UiKit.Br("AccSoft"), "Bd"));
                template.Triggers.Add(selected);
                return template;
            }
        }

        /// <summary>传输列表里的一行：文件名、方向、进度条、取消按钮</summary>
        private sealed class TransferRow
        {
            private readonly RemoteClientWithRelay.FileTransfer _t;
            private readonly ProgressBar _bar = new() { Height = 4, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0) };
            private readonly TextBlock _status = new() { FontSize = 11, Foreground = UiKit.Br("Sub") };
            private readonly Button _cancel;
            private long _lastBytes;
            private long _lastTick = Environment.TickCount64;
            private double _speed;

            public Grid Root { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };

            public TransferRow(RemoteClientWithRelay.FileTransfer t, Action cancel)
            {
                _t = t;
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = UiKit.Icon(t.IsUpload ? Glyph.Upload : Glyph.Download, 13);
                icon.Foreground = UiKit.Br("Acc");
                icon.Margin = new Thickness(0, 0, 10, 0);
                Root.Children.Add(icon);

                var info = new StackPanel();
                info.Children.Add(UiKit.Text(System.IO.Path.GetFileName(t.IsUpload ? t.LocalPath : t.RemotePath), 12));
                _bar.Margin = new Thickness(0, 4, 0, 3);
                _bar.Background = UiKit.Br("Line");
                _bar.Foreground = UiKit.Br("Acc");
                info.Children.Add(_bar);
                info.Children.Add(_status);
                Grid.SetColumn(info, 1);
                Root.Children.Add(info);

                _cancel = UiKit.Button("IconButton", UiKit.Icon(Glyph.Close, 10), (_, _) => cancel(), "取消");
                _cancel.Margin = new Thickness(10, 0, 0, 0);
                _cancel.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(_cancel, 2);
                Root.Children.Add(_cancel);
            }

            public void Refresh()
            {
                long done = _t.DoneBytes, total = Math.Max(1, _t.TotalBytes);
                _bar.Value = _t.TotalBytes == 0 ? (_t.Finished ? 1 : 0) : Math.Min(1, done / (double)total);

                if (_t.Finished)
                {
                    _cancel.Visibility = Visibility.Collapsed;
                    if (_t.Error == null)
                    {
                        _status.Text = $"已完成 · {FormatSize(_t.TotalBytes)} · {(_t.IsUpload ? _t.RemotePath : _t.LocalPath)}";
                        _status.Foreground = UiKit.Br("Ok");
                        _bar.Value = 1;
                    }
                    else
                    {
                        _status.Text = $"失败：{_t.Error}";
                        _status.Foreground = UiKit.Br("Danger");
                    }
                    return;
                }

                long now = Environment.TickCount64;
                double seconds = (now - _lastTick) / 1000.0;
                if (seconds >= 0.5)
                {
                    // 简单平滑，速度数字不会跳得太厉害
                    double current = (done - _lastBytes) / seconds;
                    _speed = _speed <= 0 ? current : _speed * 0.6 + current * 0.4;
                    _lastBytes = done;
                    _lastTick = now;
                }
                _status.Text = $"{FormatSize(done)} / {FormatSize(_t.TotalBytes)} · {FormatSize((long)_speed)}/s";
            }
        }
    }
}
