using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 桌面截图：把屏幕（整屏 / 指定区域）抓成 Mat，供流水线处理。
    ///
    /// —— 关于 DPI（重要）——
    /// 本进程目前是 **DPI 不感知**的（IsProcessDPIAware = false）。在 125%/150% 缩放的
    /// 显示器上，Windows 会给这类进程一套"虚拟化坐标"：
    ///   · 截图抓到的尺寸是**虚拟化后的**（例如 1920x1080@150% -> 1280x720）
    ///   · SetCursorPos 接受的坐标也在同一套虚拟化空间里
    /// 两者**彼此一致**，所以"在截图上匹配到目标、再点那个坐标"是成立的 ——
    /// 前提是**始终用同一套坐标系**（下面所有方法都用虚拟桌面边界 VirtualBounds）。
    ///
    /// 千万不要把"截图上的像素坐标"直接喂给别的进程或外部工具（那些可能在真实像素空间），
    /// 否则在非 100% 缩放下会整体偏移。
    /// </summary>
    public static class ScreenCapture
    {
        /// <summary>虚拟桌面（所有显示器合并）的边界，当前进程坐标系下的值</summary>
        public static Rectangle VirtualBounds
        {
            get
            {
                int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                return new Rectangle(x, y, w, h);
            }
        }

        /// <summary>主显示器边界</summary>
        public static Rectangle PrimaryBounds
        {
            get
            {
                try
                {
                    // MonitorFromPoint + MONITOR_DEFAULTTOPRIMARY：点无效时返回主监视器
                    var h = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
                    var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                    if (h != IntPtr.Zero && GetMonitorInfo(h, ref mi))
                    {
                        var m = mi.rcMonitor;
                        return new Rectangle(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
                    }
                }
                catch { }
                return VirtualBounds;
            }
        }

        /// <summary>按索引取显示器边界；越界时退回主显示器</summary>
        public static Rectangle MonitorBounds(int index)
        {
            if (index < 0) index = 0;
            var list = new List<Rectangle>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(h, ref mi))
                {
                    var m = mi.rcMonitor;
                    list.Add(new Rectangle(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top));
                }
                return true;
            }, IntPtr.Zero);
            if (list.Count == 0) return VirtualBounds;
            if (index >= list.Count) index = 0;
            return list[index];
        }

        public static int MonitorCount
        {
            get
            {
                int count = 0;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (_, _, _, _) => { count++; return true; }, IntPtr.Zero);
                return count < 1 ? 1 : count;
            }
        }

        /// <summary>
        /// 抓取指定屏幕区域。失败时不抛异常，返回 false + 原因
        /// （自动化里一次失败不应该让整个任务崩掉，而应当能被上层记录并继续/停止）。
        /// </summary>
        public static bool TryCapture(Rectangle region, out Mat mat, out string error)
        {
            mat = null;
            error = null;

            var vb = VirtualBounds;
            // 允许部分越界：裁到虚拟桌面范围内，而不是直接失败
            // （用户手填的区域经常压到屏幕边界外一点点）
            region = Rectangle.Intersect(region, vb);
            if (region.Width < 1 || region.Height < 1)
            {
                error = string.Format("截图区域无效（{0}x{1}），已完全落在屏幕外", region.Width, region.Height);
                return false;
            }

            try
            {
                using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(region.Left, region.Top, 0, 0,
                        new System.Drawing.Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
                }
                mat = ToMat(bmp);
                return true;
            }
            catch (Exception ex)
            {
                error = "截图失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>抓取整个虚拟桌面</summary>
        public static bool TryCaptureVirtual(out Mat mat, out string error)
            => TryCapture(VirtualBounds, out mat, out error);

        // ================================================================== 隐藏本程序自己的窗口

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_SHOWMINNOACTIVE = 7;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMinPosition, ptMaxPosition;
            public RECT rcNormalPosition;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        // ---- 屏幕/监视器信息（Win32 API 替代 System.Windows.Forms.Screen）----
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                          SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static int _hideDepth;
        private static List<(IntPtr H, bool Minimized)> _hiddenWindows;
        private static int _awayDepth;
        private static List<(IntPtr H, WINDOWPLACEMENT WP)> _movedWindows;

        /// <summary>当前是否处于"隐藏自己窗口"的作用域内</summary>
        public static bool OwnWindowsHidden => _hideDepth > 0;

        /// <summary>当前隐藏了几个窗口（用于日志里如实说明，而不是无条件说"已隐藏"）</summary>
        public static int OwnHiddenWindowCount => _hiddenWindows?.Count ?? 0;

        /// <summary>当前是否处于"把窗口移出屏幕"的作用域内</summary>
        public static bool OwnWindowsMovedAway => _awayDepth > 0;

        /// <summary>当前移出屏幕了几个窗口</summary>
        public static int OwnMovedWindowCount => _movedWindows?.Count ?? 0;

        /// <summary>本进程所有可见的顶层窗口（不含最小化的？含；调用方自己判断）</summary>
        private static List<IntPtr> VisibleTopLevelWindows()
        {
            var list = new List<IntPtr>();
            uint self = (uint)Environment.ProcessId;

            bool Collect(IntPtr h, IntPtr _)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != self) return true;
                if (!IsWindowVisible(h)) return true;
                // 注意：**不能**按"有属主就跳过"过滤。WinForms 里 ShowInTaskbar=false 的
                // 窗口、以及 ShowDialog 弹出的对话框都带属主，跳过它们的话这些窗口会
                // 留在屏幕上（正好被截图抓到），日志却还说"已隐藏"。
                list.Add(h);
                return true;
            }

            EnumWindows(Collect, IntPtr.Zero);
            return list;
        }

        /// <summary>
        /// 抓屏期间把自己的窗口（本进程所有可见的顶层窗口）藏起来，Dispose 时恢复。
        ///
        /// 为什么需要：本程序是最大化运行的，不藏起来抓到的"桌面"几乎全是自己的界面，
        /// 想自动化的目标窗口根本看不见。
        ///
        /// 支持嵌套（按深度计数）：节点图里"隐藏"要覆盖整张图 —— 只藏这一下截图的话，
        /// 后面"鼠标点击"的坐标是照着没有本窗口的桌面算出来的，窗口一恢复就把目标挡住了，
        /// 点下去就点在自己身上。所以运行器会在整轮开始时进入作用域、结束时才退出。
        ///
        /// 恢复用的是 SW_SHOWNOACTIVATE：不抢焦点，也不会把最小化的窗口还原成普通大小。
        /// </summary>
        public static IDisposable HideOwnWindows(int settleMs = 180)
        {
            if (_hideDepth++ == 0)
            {
                var hidden = new List<(IntPtr H, bool Minimized)>();
                uint self = (uint)Environment.ProcessId;

                bool Collect(IntPtr h, IntPtr _)
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != self) return true;
                    if (!IsWindowVisible(h)) return true;
                    // 注意：**不能**按"有属主就跳过"过滤。WinForms 里 ShowInTaskbar=false 的
                    // 窗口、以及 ShowDialog 弹出的对话框都带属主，跳过它们的话这些窗口会
                    // 留在屏幕上（正好被截图抓到），日志却还说"已隐藏"。
                    bool minimized = IsIconic(h);
                    ShowWindow(h, SW_HIDE);
                    hidden.Add((h, minimized));
                    return true;
                }

                EnumWindows(Collect, IntPtr.Zero);
                _hiddenWindows = hidden;

                // ShowWindow 只是把窗口标成不可见，DWM 合成下一帧需要一点时间。
                // 不等待这一下经常抓到"窗口还在"的那一帧，用户会以为选项没生效。
                if (hidden.Count > 0) Thread.Sleep(settleMs);
            }
            return new WindowRestoreScope();
        }

        private sealed class WindowRestoreScope : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (--_hideDepth > 0) return;      // 还有外层作用域，先不恢复

                var hidden = _hiddenWindows;
                _hiddenWindows = null;
                if (hidden == null) return;
                foreach (var (h, minimized) in hidden)
                    ShowWindow(h, minimized ? SW_SHOWMINNOACTIVE : SW_SHOWNOACTIVATE);
                // 让桌面先回到"窗口已显示"的稳定状态，再做后续动作
                if (hidden.Count > 0) Thread.Sleep(60);
            }
        }

        /// <summary>
        /// 把本程序的窗口整体**移出虚拟桌面**（挪到屏幕右侧之外），Dispose 时还原。
        ///
        /// 为什么要有这条退路：有些环境下"隐藏窗口"会让前台窗口落到别的程序上，
        /// 而那个程序若是管理员权限，Windows 的 UIPI 会禁止我们注入鼠标/键盘
        /// （表现为 SetCursorPos 失败且不给错误码）。把窗口移出屏幕则：
        ///   · 前台窗口仍然是我们自己 → 注入不会被拦；
        ///   · 窗口不在任何显示器上 → 抓屏里没有我们，点击目标也不会被挡。
        /// 实测这条路径在"隐藏后无法控制光标"的环境里可以正常注入。
        /// </summary>
        public static IDisposable MoveOwnWindowsAway(int settleMs = 180)
        {
            if (_awayDepth++ == 0)
            {
                var moved = new List<(IntPtr H, WINDOWPLACEMENT WP)>();
                var vb = VirtualBounds;
                int offX = vb.Right + 40;     // 完全挪到虚拟桌面右侧之外

                foreach (var h in VisibleTopLevelWindows())
                {
                    var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (!GetWindowPlacement(h, ref wp)) continue;

                    // 最大化的窗口挪不动（SetWindowPos 改不了它的显示位置），先还原成普通窗口
                    if (wp.showCmd == 3 /*SW_SHOWMAXIMIZED*/ || IsZoomed(h))
                        ShowWindow(h, SW_RESTORE);

                    var r = wp.rcNormalPosition;
                    int w = Math.Max(200, r.Right - r.Left);
                    int ht = Math.Max(120, r.Bottom - r.Top);
                    if (SetWindowPos(h, IntPtr.Zero, offX, r.Top, w, ht, SWP_NOACTIVATE | SWP_NOZORDER))
                        moved.Add((h, wp));
                }

                _movedWindows = moved;
                if (moved.Count > 0) Thread.Sleep(settleMs);
            }
            return new WindowAwayScope();
        }

        private sealed class WindowAwayScope : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (--_awayDepth > 0) return;      // 还有外层作用域

                var moved = _movedWindows;
                _movedWindows = null;
                if (moved == null) return;
                foreach (var (h, wp) in moved)
                {
                    var restore = wp;              // SetWindowPlacement 要 ref，传副本
                    SetWindowPlacement(h, ref restore);
                }
                if (moved.Count > 0) Thread.Sleep(60);
            }
        }

        /// <summary>
        /// 按当前策略让本程序窗口"不挡路"：优先隐藏，或者（AutomationContext 里选了）移出屏幕。
        /// 运行器与截图算子统一走这个入口，保证整轮用的是同一种方式。
        /// </summary>
        public static IDisposable SuppressOwnWindows(int settleMs = 180)
            => AutomationContext.MoveWindowsAwayInsteadOfHiding
                ? MoveOwnWindowsAway(settleMs)
                : HideOwnWindows(settleMs);

        /// <summary>当前策略的名字（日志里如实说明用了哪种）</summary>
        public static string SuppressStrategyName
            => AutomationContext.MoveWindowsAwayInsteadOfHiding ? "移出屏幕" : "隐藏";

        /// <summary>
        /// Bitmap -> Mat（BGR 8U）。直接像素拷贝，不走编码中转。
        ///
        /// 注意 GDI+ 的扫描线方向：LockBits 的 Scan0 指向**第一行**，Stride 为负表示
        /// 自下而上。这里对负 Stride 做了处理，否则图像会上下颠倒
        /// （内容能看但点上去位置全反，而且不容易一眼看出来）。
        /// </summary>
        public static Mat ToMat(Bitmap bmp)
        {
            var mat = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC3);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* src0 = (byte*)data.Scan0;
                    int stride = data.Stride;
                    if (stride < 0)
                    {
                        // 自下而上：从最后一行开始读
                        src0 += (long)stride * (bmp.Height - 1);
                        stride = -stride;
                    }
                    byte* dst0 = (byte*)mat.Data;
                    long dstStep = mat.Step();
                    long rowBytes = bmp.Width * 3L;
                    for (int y = 0; y < bmp.Height; y++)
                        Buffer.MemoryCopy(src0 + ((long)y * stride), dst0 + (y * dstStep), dstStep, rowBytes);
                }
            }
            finally { bmp.UnlockBits(data); }
            return mat;
        }
    }
}
