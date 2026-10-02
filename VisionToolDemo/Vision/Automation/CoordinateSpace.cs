using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 坐标系换算：把"截图上的像素坐标"换算成"SetCursorPos 能用的屏幕坐标"。
    ///
    /// ============================ 为什么需要它 ============================
    /// 本程序是 **DPI 不感知** 的（IsProcessDPIAware = false）。在 125% / 150% 缩放的
    /// 显示器上，Windows 会给这类进程一套"虚拟化坐标"：
    ///     · 1920x1080@150% 的屏幕，本进程看到的是 1280x720；
    ///     · WinForms 控件坐标、GetSystemMetrics、SetCursorPos 都在这套空间里；
    /// 而 **GDI 的 CopyFromScreen 抓回来的却是物理像素**（1920x1080）。
    /// 两者混用就会出现"截图与实际对不上、鼠标移到的地方不对" —— 偏差正好是缩放比。
    ///
    /// 这里的做法是**实测而不是猜**：把抓到的图像尺寸与两个已知尺寸
    /// （本进程坐标空间 / 物理像素）比一下，判断这张图落在哪个空间，给出换算系数。
    /// 100% 缩放下两者相等，系数恒为 1.0，行为与以前完全一致。
    /// ======================================================================
    /// </summary>
    public static class CoordinateSpace
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType,
                dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll")] private static extern bool IsProcessDPIAware();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        /// <summary>本进程是否 DPI 感知。不感知时"逻辑坐标 ≠ 物理像素"，坐标必须换算</summary>
        public static bool IsDpiAware
        {
            get { try { return IsProcessDPIAware(); } catch { return true; } }
        }

        /// <summary>本进程看到的屏幕尺寸（DPI 不感知时是虚拟化后的尺寸）</summary>
        public static System.Drawing.Size ProcessScreenSize
        {
            get
            {
                int w = Math.Max(1, GetSystemMetrics(0));
                int h = Math.Max(1, GetSystemMetrics(1));
                return new System.Drawing.Size(w, h);
            }
        }

        /// <summary>主显示器的**物理**像素尺寸（DPI 不感知的进程也能拿到；失败时退回进程尺寸）</summary>
        public static System.Drawing.Size PhysicalScreenSize
        {
            get
            {
                try
                {
                    var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettings(null, -1, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                        return new System.Drawing.Size(dm.dmPelsWidth, dm.dmPelsHeight);
                }
                catch { /* 拿不到就退回 */ }
                return ProcessScreenSize;
            }
        }

        /// <summary>物理/逻辑 比例（1.5 = 系统缩放 150%）</summary>
        public static double SystemScale
        {
            get
            {
                var p = PhysicalScreenSize;
                var v = ProcessScreenSize;
                return p.Width <= 0 ? 1.0 : p.Width / (double)v.Width;
            }
        }

        /// <summary>
        /// 这张抓屏图要乘多少才能变成"本进程屏幕坐标"（SetCursorPos 用的那套）。
        ///
        /// · 图尺寸 ≈ 进程坐标空间（1280x720）→ 1.0（同一个空间，不用换算）
        /// · 图尺寸 ≈ 物理像素（1920x1080）→ 逻辑/物理（0.667），即"图上的 1 像素 = 0.667 逻辑像素"
        /// · 都不是（自定义区域、被裁剪）→ 按宽度比例兜底
        /// </summary>
        public static double ImageToScreenScale(Mat image)
        {
            if (image == null || image.Empty()) return 1.0;
            var v = ScreenCapture.VirtualBounds;
            int imgW = image.Cols;
            if (imgW <= 0) return 1.0;

            // 与本进程坐标空间一致（含"自定区域"这种本来就在同一空间里的情况：
            // 区域坐标也是逻辑坐标，抓回来的图自然与逻辑 1:1）
            if (Math.Abs(imgW - v.Width) <= 2) return 1.0;

            var p = PhysicalScreenSize;
            if (Math.Abs(imgW - p.Width) <= 2) return v.Width / (double)imgW;   // 物理像素 → 逻辑

            return v.Width / (double)imgW;      // 兜底：按宽度等比
        }

        /// <summary>图像坐标 → 本进程屏幕坐标（含截图原点与缩放换算）</summary>
        public static System.Drawing.Point ImageToScreen(double imageX, double imageY, double imageToScreenScale)
        {
            double s = imageToScreenScale <= 0 ? 1.0 : imageToScreenScale;
            return new System.Drawing.Point(
                (int)Math.Round(AutomationContext.CaptureOriginX + (imageX * s)),
                (int)Math.Round(AutomationContext.CaptureOriginY + (imageY * s)));
        }

        /// <summary>把坐标空间的情况写成一段人话（日志、截图摘要、自检都用它）</summary>
        public static string Describe()
        {
            var p = PhysicalScreenSize;      // var 一次只能声明一个变量（CS0819）
            var v = ProcessScreenSize;
            string tail = Math.Abs(SystemScale - 1.0) < 0.01
                ? "1:1"
                : string.Format("系统缩放约 {0:P0}（坐标已换算）", SystemScale);
            return string.Format("DPI 感知={0}  物理 {1}x{2}  本进程坐标空间 {3}x{4}  {5}",
                IsDpiAware ? "是" : "否", p.Width, p.Height, v.Width, v.Height, tail);
        }

        /// <summary>
        /// 坐标系自检：报出两套尺寸，并**实测**一次"屏幕上的标记在抓屏图里落在哪"，
        /// 直接给出图像坐标与屏幕坐标的偏差。
        ///
        /// 这一步能一眼看出"截图与实际对不上"到底是 DPI 缩放、还是别的偏移；
        /// 偏差不为 0 时，点击算子就会照着错的坐标点下去。
        /// </summary>
        public static string SelfTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Describe());

            var mark = new System.Windows.Window
            {
                Title = "",
                WindowStyle = System.Windows.WindowStyle.None,
                Topmost = true,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = 260,
                Top = 240,
                Width = 360,
                Height = 260,
                Background = System.Windows.Media.Brushes.White,
                ShowInTaskbar = false,
            };
            var canvas = new System.Windows.Controls.Canvas { Width = 360, Height = 260 };
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = 120, Height = 90,
                Fill = System.Windows.Media.Brushes.Red,
            };
            System.Windows.Controls.Canvas.SetLeft(ellipse, 90);
            System.Windows.Controls.Canvas.SetTop(ellipse, 70);
            canvas.Children.Add(ellipse);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = 90, Height = 60,
                Fill = System.Windows.Media.Brushes.Blue,
            };
            System.Windows.Controls.Canvas.SetLeft(rect, 230);
            System.Windows.Controls.Canvas.SetTop(rect, 170);
            canvas.Children.Add(rect);
            mark.Content = canvas;
            mark.Show();
            DoEvents();
            System.Threading.Thread.Sleep(250);
            DoEvents();
            try
            {
                if (!ScreenCapture.TryCapture(ScreenCapture.VirtualBounds, out Mat full, out string err))
                {
                    sb.AppendLine("抓屏失败：" + err);
                    return sb.ToString();
                }
                using (full)
                {
                    sb.AppendLine(string.Format("抓屏尺寸：{0}x{1}", full.Cols, full.Rows));

                    double scale = ImageToScreenScale(full);
                    // 我们画的椭圆是**纯红**（BGR 0,0,255），在抓屏图里按颜色找它的质心 ——
                    // 这是独立测量：不拿整屏图里的东西当模板再回找自己（那样永远"对齐"，什么也测不出来）
                    // WPF PointToScreen 在 PerMonitorV2 感知下返回物理像素，与抓屏坐标同一空间
                    var centerScreen = mark.PointToScreen(new System.Windows.Point(150, 115));
                    var vb = ScreenCapture.VirtualBounds;
                    int ex = (int)Math.Round((centerScreen.X - vb.Left) / scale);
                    int ey = (int)Math.Round((centerScreen.Y - vb.Top) / scale);

                    int rx = Math.Max(0, ex - 140), ry = Math.Max(0, ey - 110);
                    int rw = Math.Min(280, full.Cols - rx), rh = Math.Min(220, full.Rows - ry);
                    if (rw < 20 || rh < 20)
                    {
                        sb.AppendLine("标记超出抓屏范围，跳过实测");
                    }
                    else
                    {
                        using var roi = new Mat(full, new Rect(rx, ry, rw, rh));
                        using var mask = new Mat();
                        Cv2.InRange(roi, new Scalar(0, 0, 200), new Scalar(60, 60, 255), mask);
                        var moments = Cv2.Moments(mask, true);
                        if (moments.M00 < 50)
                        {
                            sb.AppendLine("抓屏图里找不到那个红色标记 —— 抓到的画面里没有本窗口？");
                            sb.AppendLine("结论：**对不齐**（标记都不在抓屏里），请把这段报告发给开发者。");
                        }
                        else
                        {
                            int ux = rx + (int)Math.Round(moments.M10 / moments.M00);
                            int uy = ry + (int)Math.Round(moments.M01 / moments.M00);
                            int dx = ux - ex, dy = uy - ey;
                            sb.AppendLine(string.Format("标记实测：图内 ({0},{1})，按换算比期望 ({2},{3})", ux, uy, ex, ey));
                            sb.AppendLine(string.Format("图像坐标 → 屏幕坐标 换算比 = {0:F4}（偏差 dx={1} dy={2} 图内像素）",
                                scale, dx, dy));
                            sb.AppendLine(Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2
                                ? "结论：抓屏与屏幕对齐（换算后点击会落在正确位置）。"
                                : "结论：**对不齐**！偏差不为 0，点击会偏 —— 请把这段报告发给开发者。");
                        }
                    }
                }
            }
            finally { mark.Close(); }
            return sb.ToString();
        }

        /// <summary>WPF 版 DoEvents：把 Dispatcher 队列里待处理的消息跑一遍（替代 WinForms 的 Application.DoEvents）</summary>
        private static void DoEvents()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}
