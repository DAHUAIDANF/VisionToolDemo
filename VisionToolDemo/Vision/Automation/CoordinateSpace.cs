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

        /// <summary>本进程是否 DPI 感知。不感知时"逻辑坐标 ≠ 物理像素"，坐标必须换算。
        /// 【跨平台】user32 只存在于 Windows：Linux 由 Avalonia/Wayland 负责 DPI，恒视为感知。</summary>
        public static bool IsDpiAware
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return true;
                try { return IsProcessDPIAware(); } catch { return true; }
            }
        }

        /// <summary>本进程看到的屏幕尺寸（DPI 不感知时是虚拟化后的尺寸）。
        /// 【跨平台】Linux 无 user32：退回默认全高清，缩放比恒 1:1（Avalonia 自带 DPI 适配）。</summary>
        public static System.Drawing.Size ProcessScreenSize
        {
            get
            {
                if (!OperatingSystem.IsWindows())
                    return new System.Drawing.Size(1920, 1080);
                int w = Math.Max(1, GetSystemMetrics(0));
                int h = Math.Max(1, GetSystemMetrics(1));
                return new System.Drawing.Size(w, h);
            }
        }

        /// <summary>主显示器的**物理**像素尺寸（DPI 不感知的进程也能拿到；失败时退回进程尺寸）。
        /// 【跨平台】Linux 无 EnumDisplaySettings：退回进程尺寸（1:1）。</summary>
        public static System.Drawing.Size PhysicalScreenSize
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return ProcessScreenSize;
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
        /// 【Avalonia 迁移阶段】原 WPF 标定窗口（System.Windows.Window + DispatcherFrame）
        /// 已随 WPF 移除，暂返回坐标空间报告；跨平台标定自检在 Task 18 用 Avalonia 窗口重写。
        /// </summary>
        public static string SelfTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Describe());
            sb.AppendLine("【迁移阶段】标定自检窗口尚未迁移（Task 18），坐标换算逻辑已保留，可先用 'Describe' 报告核对尺寸。");
            return sb.ToString();
        }
    }
}
