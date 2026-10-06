using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteControl.Common;
using RemoteControl.Ui;

namespace RemoteControl.Client
{
    /// <summary>
    /// 会话里的附加功能：可拖动 / 收起的工具栏、Ctrl+Alt+Del、多显示器、白板标注、录制、文件传输入口、最近设备
    /// </summary>
    public partial class MainWindow
    {
        #region 工具栏拖动 / 收起

        private const double FloatBarTop = 14;

        private readonly StackPanel _floatBarContent = new() { Orientation = Orientation.Horizontal };
        private readonly Button _collapseButton = new();
        private bool _draggingBar;
        private Point _dragStart;
        private Thickness _dragStartMargin;

        /// <summary>工具栏外壳：左边拖动手柄，右边收起按钮，中间是原来的按钮</summary>
        private UIElement BuildFloatBarShell(StackPanel bar)
        {
            var shell = new StackPanel { Orientation = Orientation.Horizontal };

            // 2x3 小圆点做拖动手柄
            var grip = new Grid { Width = 14, Height = 34, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "拖动可移动工具栏，双击回到顶部" };
            var dots = new UniformGrid { Rows = 3, Columns = 2, Width = 7, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 6; i++)
            {
                dots.Children.Add(new Ellipse { Width = 2.5, Height = 2.5, Fill = UiKit.Br("FloatFg"), Opacity = 0.7 });
            }
            grip.Children.Add(dots);
            grip.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    ResetFloatBarPosition();
                    e.Handled = true;
                }
            };
            shell.Children.Add(grip);

            _floatBarContent.Children.Add(bar);
            shell.Children.Add(_floatBarContent);

            _collapseButton.Style = UiKit.St("FloatButton");
            _collapseButton.Width = 26;
            _collapseButton.Click += (_, _) => SetFloatBarCollapsed(!_settings.FloatBarCollapsed);
            shell.Children.Add(_collapseButton);

            // 在工具栏空白处（包括手柄）按住即可拖动；按钮和下拉框会自己处理点击，不会触发拖动
            _floatBar.MouseLeftButtonDown += FloatBar_MouseLeftButtonDown;
            _floatBar.MouseMove += FloatBar_MouseMove;
            _floatBar.MouseLeftButtonUp += FloatBar_MouseLeftButtonUp;
            _floatBar.LostMouseCapture += (_, _) => EndFloatBarDrag();

            return shell;
        }

        private void InitFloatBarPlacement()
        {
            _screenHost.SizeChanged += (_, _) => PlaceFloatBar();
            _floatBar.SizeChanged += (_, _) => PlaceFloatBar();
            SetFloatBarCollapsed(_settings.FloatBarCollapsed, save: false);
        }

        private void SetFloatBarCollapsed(bool collapsed, bool save = true)
        {
            _floatBarContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _collapseButton.Content = UiKit.Icon(collapsed ? Glyph.ChevronRight : Glyph.ChevronLeft, 12);
            _collapseButton.ToolTip = collapsed ? "展开工具栏" : "收起工具栏";
            if (collapsed && IsFullScreen)
            {
                // 收起后也要能退出全屏：提示一下快捷方式
                ShowNotice("工具栏已收起，点击右侧箭头可展开，拖动可移到别处");
            }

            if (save && _settings.FloatBarCollapsed != collapsed)
            {
                _settings.FloatBarCollapsed = collapsed;
                _settings.Save(SettingsName);
            }
            PlaceFloatBar();
        }

        private void ResetFloatBarPosition()
        {
            _settings.FloatBarX = double.NaN;
            _settings.FloatBarY = double.NaN;
            _settings.Save(SettingsName);
            PlaceFloatBar();
        }

        /// <summary>按保存的位置摆放，超出画面区域就拉回来（窗口变小、退出全屏时）</summary>
        private void PlaceFloatBar()
        {
            if (_draggingBar) return;
            double hostW = _screenHost.ActualWidth, hostH = _screenHost.ActualHeight;
            double barW = _floatBar.ActualWidth, barH = _floatBar.ActualHeight;
            if (hostW <= 0 || barW <= 0) return;

            double x = double.IsNaN(_settings.FloatBarX) ? (hostW - barW) / 2 : _settings.FloatBarX;
            double y = double.IsNaN(_settings.FloatBarY) ? FloatBarTop : _settings.FloatBarY;
            _floatBar.Margin = new Thickness(Math.Clamp(x, 0, Math.Max(0, hostW - barW)), Math.Clamp(y, 0, Math.Max(0, hostH - barH)), 0, 0);
        }

        private void FloatBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) return;
            _draggingBar = true;
            _dragStart = e.GetPosition(_screenHost);
            _dragStartMargin = _floatBar.Margin;
            _floatBar.CaptureMouse();
            e.Handled = true;
        }

        private void FloatBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingBar) return;
            var p = e.GetPosition(_screenHost);
            double maxX = Math.Max(0, _screenHost.ActualWidth - _floatBar.ActualWidth);
            double maxY = Math.Max(0, _screenHost.ActualHeight - _floatBar.ActualHeight);
            double x = Math.Clamp(_dragStartMargin.Left + p.X - _dragStart.X, 0, maxX);
            double y = Math.Clamp(_dragStartMargin.Top + p.Y - _dragStart.Y, 0, maxY);
            _floatBar.Margin = new Thickness(x, y, 0, 0);
        }

        private void FloatBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingBar) return;
            _floatBar.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void EndFloatBarDrag()
        {
            if (!_draggingBar) return;
            _draggingBar = false;

            // 拖到顶部中间附近就吸附回默认位置，以后窗口变宽也保持居中
            double centerX = (_screenHost.ActualWidth - _floatBar.ActualWidth) / 2;
            var m = _floatBar.Margin;
            if (Math.Abs(m.Left - centerX) < 24 && Math.Abs(m.Top - FloatBarTop) < 24)
            {
                _settings.FloatBarX = double.NaN;
                _settings.FloatBarY = double.NaN;
            }
            else
            {
                _settings.FloatBarX = m.Left;
                _settings.FloatBarY = m.Top;
            }
            _settings.Save(SettingsName);
            PlaceFloatBar();
            if (!_floatBar.IsMouseOver) FadeFloatBar(0.35);
            _screenHost.Focus();
        }

        #endregion

        #region 工具栏功能按钮

        private readonly ToggleButton _annotateButton = new();
        private readonly ToggleButton _recordButton = new();
        private readonly ComboBox _monitorCombo = new();
        private bool _updatingMonitors;
        private FileTransferWindow? _fileWindow;

        private void AddFeatureButtons(StackPanel bar)
        {
            bar.Children.Add(UiKit.Button("FloatButton", UiKit.Icon(Glyph.Transfer), (_, _) => OpenFileTransfer(), "传输文件"));
            bar.Children.Add(UiKit.Button("FloatButton", UiKit.Icon(Glyph.Keyboard), (_, _) => SendCtrlAltDel(), "发送 Ctrl+Alt+Del"));

            _annotateButton.Style = UiKit.St("FloatToggle");
            _annotateButton.Content = UiKit.Icon(Glyph.Pen);
            _annotateButton.ToolTip = "白板标注（右键清空）";
            _annotateButton.Click += (_, _) => SetAnnotating(_annotateButton.IsChecked == true);
            _annotateButton.MouseRightButtonUp += (_, e) =>
            {
                ClearAnnotations(broadcast: true);
                e.Handled = true;
            };
            bar.Children.Add(_annotateButton);

            _recordButton.Style = UiKit.St("FloatToggle");
            _recordButton.Content = UiKit.Icon(Glyph.Record);
            _recordButton.ToolTip = "录制会话";
            _recordButton.Click += (_, _) => ToggleRecording();
            bar.Children.Add(_recordButton);
        }

        private FrameworkElement BuildMonitorCombo()
        {
            _monitorCombo.Style = UiKit.St("FloatCombo");
            _monitorCombo.ToolTip = "切换显示器";
            _monitorCombo.Visibility = Visibility.Collapsed;
            _monitorCombo.SelectionChanged += (_, _) =>
            {
                if (_updatingMonitors || _monitorCombo.SelectedIndex < 0 || _client?.IsConnected != true) return;
                ClearAnnotations(broadcast: false);
                _ = _client.SelectMonitorAsync(_monitorCombo.SelectedIndex);
            };
            return _monitorCombo;
        }

        private void AttachFeatureEvents(RemoteClientWithRelay client)
        {
            client.MonitorListReceived += msg => Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                _updatingMonitors = true;
                int before = _monitorCombo.SelectedIndex;
                _monitorCombo.Items.Clear();
                foreach (var m in msg.Monitors)
                {
                    _monitorCombo.Items.Add($"屏幕{m.Index + 1}" + (m.IsPrimary ? "（主）" : ""));
                }
                _monitorCombo.SelectedIndex = Math.Clamp(msg.Current, -1, msg.Monitors.Length - 1);
                _monitorCombo.Visibility = msg.Monitors.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
                _updatingMonitors = false;
                if (before != _monitorCombo.SelectedIndex) ClearAnnotations(broadcast: false);
            });

            client.SasResultReceived += error => Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                ShowNotice(string.IsNullOrEmpty(error) ? "已发送 Ctrl+Alt+Del" : error);
            });

            client.AnnotationReceived += msg => Dispatcher.BeginInvoke(() =>
            {
                if (client != _client) return;
                OnAnnotation(msg);
            });

            client.TransferFinished += t => Dispatcher.BeginInvoke(() =>
            {
                if (client != _client || _fileWindow != null) return;
                // 文件窗口关着时（例如拖放上传）也要告诉用户结果
                string name = System.IO.Path.GetFileName(t.IsUpload ? t.LocalPath : t.RemotePath);
                ShowNotice(t.Error == null ? $"{name} 传输完成" : $"{name} 传输失败：{t.Error}");
            });
        }

        /// <summary>回到主屏时复位会话功能</summary>
        private void ResetSessionFeatures()
        {
            _fileWindow?.Close();
            _fileWindow = null;
            SetAnnotating(false);
            ClearAnnotations(broadcast: false);
            _recordButton.IsChecked = false;
            _updatingMonitors = true;
            _monitorCombo.Items.Clear();
            _monitorCombo.Visibility = Visibility.Collapsed;
            _updatingMonitors = false;
        }

        private void SendCtrlAltDel()
        {
            if (_client?.IsConnected != true) return;
            _ = _client.SendSasAsync();
            _screenHost.Focus();
        }

        private void OpenFileTransfer()
        {
            var client = _client;
            if (client?.IsConnected != true) return;

            if (_fileWindow != null)
            {
                _fileWindow.Activate();
                return;
            }

            _fileWindow = new FileTransferWindow(client, _target) { Owner = this };
            _fileWindow.Closed += (_, _) =>
            {
                _fileWindow = null;
                _screenHost.Focus();
            };
            _fileWindow.Show();
        }

        /// <summary>从资源管理器把文件拖进画面：上传到远程桌面</summary>
        private void ScreenHost_Drop(object sender, DragEventArgs e)
        {
            if (_client?.IsConnected != true || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(File.Exists).ToArray();
            if (files.Length == 0)
            {
                ShowNotice("只支持拖入文件（不支持文件夹）");
                return;
            }

            // 远程端用 "~desktop" 表示当前登录用户的桌面
            foreach (var f in files)
            {
                _client.UploadFile(f, FileTransferWindow.RemoteDesktopToken + "\\" + System.IO.Path.GetFileName(f));
            }
            ShowNotice($"正在上传 {files.Length} 个文件到对方桌面…");
        }

        #endregion

        #region 录制

        private void ToggleRecording()
        {
            var client = _client;
            if (client?.IsConnected != true)
            {
                _recordButton.IsChecked = false;
                return;
            }

            if (_recordButton.IsChecked == true)
            {
                try
                {
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LinkToMyDesk");
                    Directory.CreateDirectory(dir);
                    string safeTarget = string.Concat(_target.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c))).Replace(" ", "");
                    string path = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeTarget}.mp4");
                    client.StartRecording(path);
                    _recordButton.ToolTip = "停止录制";
                    ShowNotice("开始录制");
                }
                catch (Exception ex)
                {
                    _recordButton.IsChecked = false;
                    ShowNotice($"无法录制：{ex.Message}");
                }
            }
            else
            {
                string? path = client.StopRecording();
                _recordButton.ToolTip = "录制会话";
                if (path != null)
                {
                    ShowNotice(File.Exists(path) ? $"录制已保存：{path}" : "录制已停止（没有录到画面）");
                }
            }
            UpdateRemoteLabel(_viewerCount);
            _screenHost.Focus();
        }

        #endregion

        #region 白板标注

        private readonly Canvas _annotationCanvas = new() { IsHitTestVisible = false, ClipToBounds = true };
        private readonly List<AnnotationMessage> _annotations = new();
        private Polyline? _currentStroke;
        private readonly List<float> _currentPoints = new();

        private UIElement BuildAnnotationCanvas()
        {
            _annotationCanvas.Background = Brushes.Transparent; // 透明背景才能接收鼠标
            _annotationCanvas.Cursor = Cursors.Pen;
            _annotationCanvas.MouseLeftButtonDown += Annotation_MouseDown;
            _annotationCanvas.MouseMove += Annotation_MouseMove;
            _annotationCanvas.MouseLeftButtonUp += Annotation_MouseUp;
            _annotationCanvas.MouseRightButtonUp += (_, e) =>
            {
                ClearAnnotations(broadcast: true);
                e.Handled = true;
            };
            _screenImage.SizeChanged += (_, _) => SyncAnnotationLayer();
            return _annotationCanvas;
        }

        /// <summary>标注层和画面同样大小、同样对齐，坐标按画面归一化</summary>
        private void SyncAnnotationLayer()
        {
            _annotationCanvas.HorizontalAlignment = _screenImage.HorizontalAlignment;
            _annotationCanvas.VerticalAlignment = _screenImage.VerticalAlignment;
            _annotationCanvas.Width = _screenImage.ActualWidth;
            _annotationCanvas.Height = _screenImage.ActualHeight;
            RedrawAnnotations();
        }

        private void SetAnnotating(bool on)
        {
            _annotateButton.IsChecked = on;
            // 标注时画布接住鼠标，不再操作远程电脑
            _annotationCanvas.IsHitTestVisible = on;
            _annotateButton.ToolTip = on ? "退出标注（右键清空）" : "白板标注（右键清空）";
            if (on) ShowNotice("标注模式：在画面上拖动画线，右键清空，所有人都能看到");
            _screenHost.Focus();
        }

        private Point NormalizedPosition(MouseEventArgs e)
        {
            var p = e.GetPosition(_annotationCanvas);
            double w = Math.Max(1, _annotationCanvas.ActualWidth), h = Math.Max(1, _annotationCanvas.ActualHeight);
            return new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        }

        private uint MyAnnotationColor()
        {
            var c = ((SolidColorBrush)UiKit.ViewerBrush(_myViewerId)).Color;
            return (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        }

        private void Annotation_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _currentPoints.Clear();
            var p = NormalizedPosition(e);
            _currentPoints.Add((float)p.X);
            _currentPoints.Add((float)p.Y);

            _currentStroke = NewPolyline(MyAnnotationColor(), 3);
            _currentStroke.Points.Add(ToCanvas(p));
            _annotationCanvas.Children.Add(_currentStroke);
            _annotationCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Annotation_MouseMove(object sender, MouseEventArgs e)
        {
            if (_currentStroke == null) return;
            var p = NormalizedPosition(e);

            // 距离上一个点太近就跳过，减少数据量
            int n = _currentPoints.Count;
            double dx = (p.X - _currentPoints[n - 2]) * _annotationCanvas.ActualWidth;
            double dy = (p.Y - _currentPoints[n - 1]) * _annotationCanvas.ActualHeight;
            if (dx * dx + dy * dy < 4) return;

            _currentPoints.Add((float)p.X);
            _currentPoints.Add((float)p.Y);
            _currentStroke.Points.Add(ToCanvas(p));
        }

        private void Annotation_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_currentStroke == null) return;
            _annotationCanvas.ReleaseMouseCapture();
            _currentStroke = null;

            if (_currentPoints.Count == 2)
            {
                // 只点了一下：画一个点
                _currentPoints.Add(_currentPoints[0] + 0.001f);
                _currentPoints.Add(_currentPoints[1]);
            }

            var msg = new AnnotationMessage
            {
                Kind = AnnotationKind.Stroke,
                ViewerId = _myViewerId,
                Color = MyAnnotationColor(),
                Thickness = 3,
                Points = _currentPoints.ToArray()
            };
            _annotations.Add(msg);
            RedrawAnnotations();
            if (_client?.IsConnected == true) _ = _client.SendAnnotationAsync(msg);
            e.Handled = true;
        }

        private void OnAnnotation(AnnotationMessage msg)
        {
            if (msg.Kind == AnnotationKind.Clear)
            {
                ClearAnnotations(broadcast: false);
                if (msg.ViewerId != _myViewerId && !string.IsNullOrEmpty(msg.Name)) ShowNotice($"{msg.Name} 清空了标注");
                return;
            }

            // 自己画的已经显示过了，服务端回传的不再重复画
            if (msg.ViewerId == _myViewerId) return;
            _annotations.Add(msg);
            RedrawAnnotations();
        }

        private void ClearAnnotations(bool broadcast)
        {
            _annotations.Clear();
            _currentStroke = null;
            _annotationCanvas.Children.Clear();
            if (broadcast && _client?.IsConnected == true)
            {
                _ = _client.SendAnnotationAsync(new AnnotationMessage { Kind = AnnotationKind.Clear });
            }
        }

        private void RedrawAnnotations()
        {
            _annotationCanvas.Children.Clear();
            foreach (var a in _annotations)
            {
                var line = NewPolyline(a.Color, a.Thickness);
                for (int i = 0; i + 1 < a.Points.Length; i += 2)
                {
                    line.Points.Add(ToCanvas(new Point(a.Points[i], a.Points[i + 1])));
                }
                _annotationCanvas.Children.Add(line);
            }
            if (_currentStroke != null) _annotationCanvas.Children.Add(_currentStroke);
        }

        private Point ToCanvas(Point normalized) =>
            new(normalized.X * _annotationCanvas.ActualWidth, normalized.Y * _annotationCanvas.ActualHeight);

        private static Polyline NewPolyline(uint argb, double thickness)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return new Polyline
            {
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
        }

        #endregion

        #region 提示

        /// <summary>在画面左上角的浮动条里显示一条提示（和协作动态共用）</summary>
        private void ShowNotice(string text)
        {
            if (!_sessionActive) return;
            var toastText = (TextBlock)_activityToast.Child;
            toastText.Inlines.Clear();
            toastText.Inlines.Add(new System.Windows.Documents.Run(text));
            _activityToast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        #endregion

        #region 最近设备

        private const int MaxRecentDevices = 8;
        private readonly StackPanel _recentPanel = new() { Margin = new Thickness(0, 18, 0, 0) };
        private readonly StackPanel _recentList = new();

        private UIElement BuildRecentPanel()
        {
            var header = UiKit.Caption(UiKit.Spaced("最近连接"));
            header.Margin = new Thickness(0, 0, 0, 8);
            _recentPanel.Children.Add(header);
            _recentPanel.Children.Add(_recentList);
            RefreshRecentDevices();
            return _recentPanel;
        }

        private void RefreshRecentDevices()
        {
            _recentList.Children.Clear();
            var items = _settings.RecentDevices.OrderByDescending(d => d.LastConnected).Take(MaxRecentDevices).ToList();
            _recentPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var d in items)
            {
                _recentList.Children.Add(RecentRow(d));
            }
        }

        private UIElement RecentRow(RecentDevice d)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = UiKit.Icon(d.UseRelay ? Glyph.Monitor : Glyph.Network, 13);
            icon.Foreground = UiKit.Br("Acc");
            icon.Margin = new Thickness(0, 0, 10, 0);
            text.Children.Add(icon);
            var id = UiKit.Text(d.UseRelay ? UiKit.FormatId(d.DeviceId) : d.DeviceId, 13, "Txt", bold: true);
            text.Children.Add(id);
            if (!string.IsNullOrEmpty(d.Name))
            {
                var name = UiKit.Text("  " + d.Name, 12, "Sub");
                name.MaxWidth = 170;
                text.Children.Add(name);
            }

            var pick = new Button
            {
                Style = UiKit.St("GhostButton"),
                Content = text,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 7, 12, 7),
                ToolTip = $"上次连接：{d.LastConnected:yyyy-MM-dd HH:mm}"
            };
            pick.Click += (_, _) =>
            {
                if (d.UseRelay)
                {
                    _relayRadio.IsChecked = true;
                    _deviceIdBox.Text = UiKit.FormatId(d.DeviceId);
                }
                else
                {
                    _directRadio.IsChecked = true;
                    _directHostBox.Text = d.DeviceId;
                }
                _passwordBox.Clear();
                _passwordBox.Focus();
            };
            row.Children.Add(pick);

            var remove = UiKit.Button("IconButton", UiKit.Icon(Glyph.Close, 10), (_, _) =>
            {
                _settings.RecentDevices.Remove(d);
                _settings.Save(SettingsName);
                RefreshRecentDevices();
            }, "从列表移除");
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            return row;
        }

        private void RememberDevice(RemoteClientWithRelay client)
        {
            string id = _target.Replace(" ", "");
            bool relay = client.UseRelay;
            _settings.RecentDevices.RemoveAll(x => x.UseRelay == relay && string.Equals(x.DeviceId, id, StringComparison.OrdinalIgnoreCase));
            _settings.RecentDevices.Add(new RecentDevice
            {
                DeviceId = id,
                UseRelay = relay,
                Name = client.RemoteDeviceName,
                LastConnected = DateTime.Now
            });
            if (_settings.RecentDevices.Count > MaxRecentDevices)
            {
                _settings.RecentDevices = _settings.RecentDevices.OrderByDescending(x => x.LastConnected).Take(MaxRecentDevices).ToList();
            }
            _settings.Save(SettingsName);
            RefreshRecentDevices();
        }

        #endregion
    }
}
