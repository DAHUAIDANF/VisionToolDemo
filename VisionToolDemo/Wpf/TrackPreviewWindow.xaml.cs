using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 实时目标跟踪预览窗口：打开视频文件或摄像头，逐帧读取画面，
    /// 在画面上按住左键拖框框住目标（松开即初始化追踪器），之后逐帧自动跟踪并画框。
    ///
    /// 追踪器与「目标跟踪」算子同源（OpenCV KCF/CSRT/MIL）。
    /// 想让跟踪结果进算子链/批量/记录，用视觉页「目标跟踪」算子的「框选目标」。
    /// </summary>
    public partial class TrackPreviewWindow : System.Windows.Window
    {
        /// <summary>视频/摄像头捕获器（null=未打开）</summary>
        private VideoCapture _cap;

        /// <summary>当前追踪器（null=未框选）</summary>
        private Tracker _tracker;

        /// <summary>目标框（图像像素坐标）</summary>
        private OpenCvSharp.Rect _target;

        /// <summary>帧定时器：约 33ms（~30fps 视觉节奏，慢视频自动跟帧）</summary>
        private readonly DispatcherTimer _timer;

        /// <summary>显示位图（复用缓冲）</summary>
        private WriteableBitmap _wb;

        /// <summary>显示区域尺寸（缩放用）</summary>
        private int _viewW, _viewH;

        /// <summary>鼠标拖框状态</summary>
        private System.Windows.Point _dragStart;
        private System.Windows.Shapes.Rectangle _dragRect;
        private bool _dragging;

        /// <summary>当前正在显示的帧（克隆保存；框选初始化追踪器用同一帧，避免视频播放中
        /// 重读帧导致"框选画面"与"绿框画面"不一致——摄像头静止看不出，视频一放就错位）</summary>
        private Mat _lastFrame;

        /// <summary>帧统计</summary>
        private int _frameCount;
        private DateTime _t0 = DateTime.MinValue;

        /// <summary>是否暂停</summary>
        private bool _paused;

        /// <summary>当前源说明（状态栏）</summary>
        private string _sourceLabel = "";

        /// <summary>ViewModel：状态与命令（帧循环/框选等视图交互留在本类）</summary>
        private readonly ViewModels.TrackPreviewViewModel _vm = new();

        public TrackPreviewWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            _vm.OpenVideoRequested += BtnOpenVideo_Click;
            _vm.OpenCamRequested += BtnOpenCam_Click;
            _vm.PlayPauseRequested += BtnPlayPause_Click;
            _vm.ResetRequested += BtnReset_Click;
            _vm.PropertyChanged += (_, e) =>
            {
                // 镜像开关变化：立即按新状态取一帧刷新，保持"显示帧 = _lastFrame"，框选不错位
                if (e.PropertyName == nameof(ViewModels.TrackPreviewViewModel.IsMirrored)) RefreshNow();
            };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => GrabTick();
            Closed += (_, _) => { _timer.Stop(); _cap?.Dispose(); _tracker?.Dispose(); _lastFrame?.Dispose(); };
            ThemeManager.RegisterPage(this);
        }

        // ============================== 打开源 ==============================

        private void BtnOpenVideo_Click()
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开视频",
                Filter = "视频|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            OpenSource(new VideoCapture(dlg.FileName), Path.GetFileName(dlg.FileName));
        }

        private void BtnOpenCam_Click()
        {
            // 摄像头设备号输入（0 通常是默认摄像头）
            var dlg = new CameraPickWindow { Owner = this };
            if (dlg.ShowDialog() != true) return;
            // 与「相机取帧」一致：显式指定后端（DSHOW → MSMF → ANY 依次尝试）。
            // 用默认后端（ANY）在 Windows 上经常打不开摄像头，这是之前"相机取帧能开、实时跟踪打不开"的根因。
            VideoCapture cap = null;
            string tried = "";
            foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY })
            {
                try
                {
                    var c = new VideoCapture(dlg.DeviceIndex, api);
                    if (c.IsOpened()) { cap = c; break; }
                    c.Dispose();
                }
                catch (Exception ex) { tried += api + ":" + ex.Message + " "; }
                tried += api + " ";
            }
            if (cap == null)
            {
                MessageBox.Show(this, "打不开摄像头 " + dlg.DeviceIndex + "。\n\n" +
                    "已尝试后端：" + tried + "\n可试：换设备号（0~5）、确认相机没被其它程序占用。",
                    "摄像头", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OpenSource(cap, "摄像头 " + dlg.DeviceIndex);
        }

        /// <summary>镜像开关变化后立即按新状态取一帧刷新画面，保持"显示帧 = _lastFrame"，框选不错位</summary>
        private void RefreshNow()
        {
            if (_cap == null) return;
            using var frame = new Mat();
            if (!ReadFrame(frame) || frame.Empty()) return;
            ShowFrame(frame);
            _lastFrame?.Dispose();
            _lastFrame = frame.Clone();
            if (_tracker != null) ResetTracking();
        }

        /// <summary>切换视频/摄像头源：释放旧的，重置跟踪与画面</summary>
        private void OpenSource(VideoCapture cap, string label)
        {
            _cap?.Dispose();
            _lastFrame?.Dispose();
            _lastFrame = null;
            _cap = cap;
            _sourceLabel = label;
            // 摄像头画面多为镜像输出：摄像头源默认开镜像，视频文件默认关，可随时切换
            _vm.IsMirrored = label.StartsWith("摄像头");
            _frameCount = 0;
            _t0 = DateTime.MinValue;
            _paused = false;
            _vm.PlayPauseText = "暂停";
            _vm.IsSourceOpen = true;
            _vm.StatusText = "已打开 " + label + "：按住左键拖框框住目标";
            _timer.Start();
        }

        // ============================== 帧循环 ==============================

        /// <summary>每帧：读帧 → 显示 → 跟踪（有目标框时）→ 画框</summary>
        private void GrabTick()
        {
            if (_cap == null || _paused) return;
            using var frame = new Mat();
            if (!ReadFrame(frame)) { _vm.StatusText = "读取结束（或摄像头断开）"; _timer.Stop(); return; }

            // 显示帧，并克隆保存"当前正在显示的画面"（框选初始化必须用它，保证所见即所得）
            ShowFrame(frame);
            _lastFrame?.Dispose();
            _lastFrame = frame.Clone();

            // 跟踪
            if (_tracker != null)
            {
                var r = _target;
                bool ok = _tracker.Update(frame, ref r);
                if (ok) _target = r;
                DrawOverlay(r, ok);
                if (!ok) _vm.StatusText = "目标丢失！已保留上一位置（红框）";
                else
                {
                    _frameCount++;
                    double fps = 0;
                    if (_t0 == DateTime.MinValue) _t0 = DateTime.Now;
                    else fps = _frameCount / (DateTime.Now - _t0).TotalSeconds;
                    _vm.StatusText = string.Format("{0} 帧 {1} | 目标 ({2},{3}) {4}x{5} | {6:F0}fps",
                        _sourceLabel, _frameCount, r.X, r.Y, r.Width, r.Height, fps);
                }
            }
        }

        /// <summary>
        /// 读一帧：读成功返回 true；按「镜像画面」开关对帧做水平翻转（Cv2.Flip in-place）。
        /// 显示与框选/跟踪共用同一翻转结果，避免坐标系错位。
        /// </summary>
        private bool ReadFrame(Mat frame)
        {
            if (!_cap.Read(frame) || frame.Empty()) return false;
            if (_vm.IsMirrored)
                Cv2.Flip(frame, frame, FlipMode.Y);   // FlipMode.Y = 绕Y轴 = 左右镜像（修正：之前用X成了上下镜像）
            return true;
        }

        /// <summary>把 Mat(BGR) 转成 BGRA 显示到 FrameImage（复用 WriteableBitmap）</summary>
        private unsafe void ShowFrame(Mat frame)
        {
            _viewW = frame.Cols; _viewH = frame.Rows;
            int w = frame.Cols, h = frame.Rows;
            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            using var rgba = new Mat();
            Cv2.CvtColor(frame, rgba, ColorConversionCodes.BGR2BGRA);
            _wb.Lock();
            try
            {
                // 每行拷入（处理步长差异）
                int stride = _wb.BackBufferStride;
                byte* src = (byte*)rgba.Data;
                int srcStep = (int)rgba.Step();
                byte* dst = (byte*)_wb.BackBuffer;
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * srcStep, dst + (long)y * stride, (ulong)(w * 4), (ulong)(w * 4));
            }
            finally { _wb.AddDirtyRect(new Int32Rect(0, 0, w, h)); _wb.Unlock(); }
            FrameImage.Source = _wb;
        }

        // ============================== 框选交互 ==============================

        /// <summary>按下：记录起点，开始拖框</summary>
        private void Frame_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_cap == null) return;
            _dragStart = e.GetPosition(FrameImage);
            _dragging = true;
            _dragRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Colors.Lime),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
            };
            Canvas.SetLeft(_dragRect, _dragStart.X);
            Canvas.SetTop(_dragRect, _dragStart.Y);
            OverlayCanvas.Children.Add(_dragRect);
        }

        /// <summary>移动：更新拖框大小</summary>
        private void Frame_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _dragRect == null) return;
            var p = e.GetPosition(FrameImage);
            double x = Math.Min(_dragStart.X, p.X), y = Math.Min(_dragStart.Y, p.Y);
            Canvas.SetLeft(_dragRect, x);
            Canvas.SetTop(_dragRect, y);
            _dragRect.Width = Math.Abs(p.X - _dragStart.X);
            _dragRect.Height = Math.Abs(p.Y - _dragStart.Y);
        }

        /// <summary>松开：定框 → 转成图像坐标 → 初始化追踪器</summary>
        private void Frame_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            OverlayCanvas.Children.Remove(_dragRect);
            _dragRect = null;
            if (_cap == null || _tracker != null) return;   // 已有目标需先重置再重框

            // 视图坐标 → 图像坐标（Stretch=Uniform 等比缩放 + 居中，扣留白）
            var p = e.GetPosition(FrameImage);
            if (!ViewToFrame(_dragStart, out int x0, out int y0)) return;
            if (!ViewToFrame(p, out int x1, out int y1)) return;
            int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
            int w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
            if (w < 2) w = 2; if (h < 2) h = 2;
            if (x + w > _viewW) w = Math.Max(2, _viewW - x);
            if (y + h > _viewH) h = Math.Max(2, _viewH - y);

            // 用"当前正在显示的帧"初始化追踪器（同一帧 = 同一坐标系 = 同一画面，
            // 绿框必然落在鼠标框选处；不再重读帧，避免视频播放中画面跳走造成错位）
            if (_lastFrame == null || _lastFrame.Empty())
            {
                _vm.StatusText = "还没有画面，请先打开视频/摄像头";
                return;
            }
            int type = TrackerTypeBox.SelectedIndex;
            try
            {
                _tracker = CreateTracker(type);
                _target = new OpenCvSharp.Rect(x, y, w, h);
                _tracker.Init(_lastFrame, _target);
                _frameCount = 0;
                _t0 = DateTime.MinValue;
                _vm.StatusText = string.Format("已框选目标 ({0},{1}) {2}x{3}，开始跟踪（绿框）", x, y, w, h);
                DrawOverlay(_target, true);
                if (_paused) { _paused = false; _vm.PlayPauseText = "暂停"; }
            }
            catch (Exception ex)
            {
                _tracker?.Dispose(); _tracker = null;
                _vm.StatusText = "初始化失败：" + ex.Message;
            }
        }

        // ============================== 控制 ==============================

        private void BtnPlayPause_Click()
        {
            _paused = !_paused;
            _vm.PlayPauseText = _paused ? "继续" : "暂停";
            if (!_paused) _timer.Start(); else _timer.Stop();
        }

        private void BtnReset_Click()
        {
            ResetTracking();
            _vm.StatusText = "已重置，重新拖框框住目标";
        }

        /// <summary>重置追踪器与目标框（保留视频源）</summary>
        private void ResetTracking()
        {
            _tracker?.Dispose();
            _tracker = null;
            OverlayCanvas.Children.Clear();
            _frameCount = 0;
            _t0 = DateTime.MinValue;
        }

        /// <summary>在叠加层画目标框（绿=跟踪成功 / 红=丢失）</summary>
        private void DrawOverlay(OpenCvSharp.Rect r, bool ok)
        {
            OverlayCanvas.Children.Clear();
            if (!FrameToView(r.X, r.Y, out double vx, out double vy)) return;
            if (!FrameToView(r.X + r.Width, r.Y + r.Height, out double vx2, out double vy2)) return;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, vx2 - vx),
                Height = Math.Max(1, vy2 - vy),
                Stroke = new SolidColorBrush(ok ? Colors.Lime : Colors.Red),
                StrokeThickness = 3,
            };
            Canvas.SetLeft(rect, vx);
            Canvas.SetTop(rect, vy);
            OverlayCanvas.Children.Add(rect);
        }

        /// <summary>
        /// 视图控件坐标 → 视频图像坐标。
        /// FrameImage 是 Stretch=Uniform（等比缩放 + 居中留白），换算必须扣掉上下/左右留白，
        /// 否则框选位置与画面显示不符（和视觉页早期 ROI 偏移是同一个坑）。
        /// </summary>
        private bool ViewToFrame(System.Windows.Point p, out int x, out int y)
        {
            x = y = 0;
            double vw = FrameImage.ActualWidth, vh = FrameImage.ActualHeight;
            if (vw < 2 || vh < 2 || _viewW < 2 || _viewH < 2) return false;
            double scale = Math.Min(vw / _viewW, vh / _viewH);   // 显示倍率（等比）
            double dw = _viewW * scale, dh = _viewH * scale;
            double ox = (vw - dw) / 2, oy = (vh - dh) / 2;       // 居中留白
            x = (int)Math.Clamp((p.X - ox) / scale, 0, _viewW - 2);
            y = (int)Math.Clamp((p.Y - oy) / scale, 0, _viewH - 2);
            return true;
        }

        /// <summary>视频图像坐标 → 视图控件坐标（画目标框用，与框选换算互为逆运算）</summary>
        private bool FrameToView(int fx, int fy, out double vx, out double vy)
        {
            vx = vy = 0;
            double vw = FrameImage.ActualWidth, vh = FrameImage.ActualHeight;
            if (vw < 2 || vh < 2 || _viewW < 2 || _viewH < 2) return false;
            double scale = Math.Min(vw / _viewW, vh / _viewH);
            double dw = _viewW * scale, dh = _viewH * scale;
            double ox = (vw - dw) / 2, oy = (vh - dh) / 2;
            vx = fx * scale + ox;
            vy = fy * scale + oy;
            return true;
        }

        /// <summary>按类型创建追踪器</summary>
        private static Tracker CreateTracker(int type) => type switch
        {
            1 => TrackerCSRT.Create(),
            2 => TrackerMIL.Create(),
            _ => TrackerKCF.Create(),
        };
    }

    /// <summary>摄像头设备号选择小窗（0~5）</summary>
    public class CameraPickWindow : System.Windows.Window
    {
        public int DeviceIndex { get; private set; } = 0;

        private readonly TextBox _box;

        public CameraPickWindow()
        {
            Title = "选择摄像头";
            Width = 320;
            // 高度自适应内容（SizeToContent）：固定 Height 在 Windows 缩放（125%/150%）下会把
            // 底部按钮挤出客户区，出现"界面没显示完全"。WPF 高度含标题栏，固定值最易踩这个坑。
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = "摄像头设备号（0 通常是默认摄像头）：", Foreground = Brushes.White });
            _box = new TextBox { Text = "0", Margin = new Thickness(0, 8, 0, 12) };
            sp.Children.Add(_box);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = new Button
            {
                Content = "打开",
                Width = 80,
                Padding = new Thickness(0, 4, 0, 4),
                Style = (Style)Application.Current.Resources["PrimaryButton"],
            };
            ok.Click += (_, _) =>
            {
                if (int.TryParse(_box.Text.Trim(), out int idx) && idx >= 0 && idx <= 5) { DeviceIndex = idx; DialogResult = true; }
                else MessageBox.Show(this, "设备号需为 0~5 的整数", "摄像头", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            var cancel = new Button
            {
                Content = "取消",
                Width = 80,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(0, 4, 0, 4),
                Style = (Style)Application.Current.Resources["FlatButton"],
            };
            cancel.Click += (_, _) => DialogResult = false;
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            Content = sp;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A));
        }
    }
}
