using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
// 不能 using OpenCvSharp; —— 它有 Window/Point/Size/Rect，会和 System.Windows 的同名类型撞（CS0104）
using Mat = OpenCvSharp.Mat;
using VideoCapture = OpenCvSharp.VideoCapture;
using VideoCaptureAPIs = OpenCvSharp.VideoCaptureAPIs;
using VideoCaptureProperties = OpenCvSharp.VideoCaptureProperties;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 相机取帧（WPF 版，**新功能**：旧界面只有"相机 RAW 文件解码"，没有实时采集）。
    ///
    /// 用法：选设备号与后端 → 「打开预览」→ 对准目标 → 「采一帧」把它送进视觉页当输入图。
    /// 后端说明：DSHOW 兼容性最好；MSMF 在部分 USB3 工业相机上更快，但有的驱动会打不开。
    /// </summary>
    public sealed class CameraWindow : Window, Ui.IModalResult
    {
        /// <summary>采到的那一帧（调用方持有；取消则为 null）</summary>
        public Mat CapturedMat { get; private set; }
        public bool ModalResult { get; private set; }

        private readonly ComboBox _device, _backend;
        private readonly Image _view;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _timer;
        private VideoCapture _cap;
        private Mat _last;                 // 复用的帧缓冲：每帧 CopyTo 进来，不再每帧新建 Mat
        private WriteableBitmap _wb;       // 复用的显示位图：不再每帧新建 BitmapSource
        private byte[] _scratch;           // 复用的像素缓冲

        public CameraWindow()
        {
            Ui.ApplyTheme(this);
            Title = "相机取帧";
            Width = 760;
            Height = 620;

            _device = Ui.Combo(new[] { "0", "1", "2", "3", "4", "5" }, 0);
            _backend = Ui.Combo(new[] { "DSHOW（兼容性最好）", "MSMF（部分相机更快）", "自动" }, 0);
            _view = new Image { Stretch = Stretch.Uniform };
            _status = Ui.Dim("还没有打开相机。");

            var panel = new DockPanel { Margin = new Thickness(12) };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(Ui.Dim("设备号"));
            top.Children.Add(_device);
            top.Children.Add(new Border { Width = 12 });
            top.Children.Add(Ui.Dim("后端"));
            top.Children.Add(_backend);
            top.Children.Add(new Border { Width = 12 });
            top.Children.Add(Ui.Btn("打开预览", Open));
            top.Children.Add(Ui.Btn("关闭预览", CloseCamera));
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);

            var bottom = new StackPanel();
            var bar = Ui.Bar(Ui.Btn("采一帧并返回", Capture, true), Ui.Btn("取消", () => { ModalResult = false; Close(); }));
            bar.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            bottom.Children.Add(bar);
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            panel.Children.Add(bottom);

            panel.Children.Add(new Border
            {
                Background = Ui.Brush("InputBg", "#10151B"),
                CornerRadius = new CornerRadius(6),
                Child = _view,
            });
            Content = panel;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += (_, _) => GrabPreview();
            Closed += (_, _) => CloseCamera();
        }

        private VideoCaptureAPIs Api => _backend.SelectedIndex switch
        {
            1 => VideoCaptureAPIs.MSMF,
            2 => VideoCaptureAPIs.ANY,
            _ => VideoCaptureAPIs.DSHOW,
        };

        private void Open()
        {
            CloseCamera();
            int index = _device.SelectedIndex < 0 ? 0 : _device.SelectedIndex;
            try
            {
                _cap = new VideoCapture(index, Api);
                if (!_cap.IsOpened())
                {
                    _cap.Dispose();
                    _cap = null;
                    Ui.Warn(string.Format(CultureInfo.InvariantCulture,
                        "打不开设备 {0}（后端 {1}）。\n\n" +
                        "可试：换设备号、换后端（DSHOW/MSMF）、确认相机没被其它程序（相机软件/微信/浏览器）占用。",
                        index, _backend.SelectedItem));
                    return;
                }
                _timer.Start();
                _status.Text = string.Format(CultureInfo.InvariantCulture,
                    "已打开设备 {0}（{1}）：画面 {2}x{3}",
                    index, _backend.SelectedItem,
                    (int)_cap.Get(VideoCaptureProperties.FrameWidth),
                    (int)_cap.Get(VideoCaptureProperties.FrameHeight));
            }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }

        private void GrabPreview()
        {
            if (_cap == null) return;
            try
            {
                using var frame = new Mat();
                if (!_cap.Read(frame) || frame.Empty()) return;

                // 复用同一块 Mat 内存（尺寸变化时 CopyTo 会自行重分配）
                _last ??= new Mat();
                frame.CopyTo(_last);

                // 复用同一个 WriteableBitmap：尺寸变了才重建，之后每帧只 WritePixels
                if (_wb == null || _wb.PixelSize.Width != _last.Cols || _wb.PixelSize.Height != _last.Rows)
                {
                    _wb = MatImage.CreateWriteableBitmap(_last.Cols, _last.Rows);
                    _scratch = null;
                    _view.Source = _wb;
                }
                if (!MatImage.WriteIntoBitmap(_wb, _last, ref _scratch))
                    _view.Source = MatImage.ToBitmapSource(_last);
            }
            catch { /* 单帧失败就跳过，下个 tick 再试 */ }
        }

        private void CloseCamera()
        {
            try { _timer.Stop(); } catch { }
            try { _cap?.Release(); _cap?.Dispose(); } catch { }
            _cap = null;
        }

        private void Capture()
        {
            if (_last == null || _last.Empty())
            {
                // 还没预览时也允许直接抓一帧：临时开一下相机
                if (_cap == null)
                {
                    Open();
                    System.Threading.Thread.Sleep(250);
                }
                GrabPreview();
            }
            if (_last == null || _last.Empty()) { Ui.Warn("还没有取到画面：先「打开预览」确认能看到相机画面"); return; }

            // 交出去的是**副本**：_last 还要留给预览循环复用
            CapturedMat = _last.Clone();
            CloseCamera();
            ModalResult = true; Close();
        }
    }
}
