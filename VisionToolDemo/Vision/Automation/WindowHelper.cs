using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>一个顶层窗口</summary>
    public sealed class TopWindow
    {
        public IntPtr Handle;
        public string Title = "";
        public string ClassName = "";
        public uint ProcessId;
        public bool IsVisible;
        public System.Drawing.Rectangle Bounds;
        public override string ToString() => string.Format("[{0}] {1}", ProcessId, Title);
    }

    /// <summary>
    /// 窗口查找与操作。
    ///
    /// 为什么自动化需要它：盲点击最容易点错窗口；"先激活目标窗口再操作"能显著提高稳定性，
    /// 而"等某个窗口出现"更是启动类流程的刚需。查找支持两种写法：
    ///   · 普通文本 = 标题**包含**（不区分大小写）
    ///   · /正则/   = 用 .NET 正则匹配标题（例如 /^记事本/ 或 /订单\s*\d+/）
    /// </summary>
    public static class WindowHelper
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2,
            SW_SHOWMAXIMIZED = 3, SW_RESTORE = 9;
        private const uint WM_CLOSE = 0x0010;

        /// <summary>所有顶层窗口（含不可见的；调用方按需过滤）</summary>
        public static List<TopWindow> TopLevelWindows(bool visibleOnly = true)
        {
            var list = new List<TopWindow>();
            bool Collect(IntPtr h, IntPtr _)
            {
                if (visibleOnly && !IsWindowVisible(h)) return true;
                int len = GetWindowTextLength(h);
                var sb = new StringBuilder(len + 2);
                GetWindowText(h, sb, sb.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                GetWindowRect(h, out RECT r);
                list.Add(new TopWindow
                {
                    Handle = h,
                    Title = sb.ToString(),
                    ClassName = cls.ToString(),
                    ProcessId = pid,
                    IsVisible = IsWindowVisible(h),
                    Bounds = new System.Drawing.Rectangle(r.Left, r.Top,
                        Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)),
                });
                return true;
            }
            EnumWindows(Collect, IntPtr.Zero);
            return list;
        }

        /// <summary>把 /.../  这种写法当正则，其余当"标题包含"</summary>
        public static bool IsRegexPattern(string pattern)
            => !string.IsNullOrEmpty(pattern) && pattern.Length >= 2
               && pattern[0] == '/' && pattern[pattern.Length - 1] == '/';

        /// <summary>标题是否匹配（包含 或 /正则/）；空模式不匹配任何窗口（避免"空=全部"的误操作）</summary>
        public static bool TitleMatches(string title, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            title ??= "";
            if (IsRegexPattern(pattern))
            {
                string expr = pattern.Substring(1, pattern.Length - 2);
                try { return Regex.IsMatch(title, expr, RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return false; }      // 正则写错就当不匹配
            }
            return title.IndexOf(pattern.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 按**进程名**找一个可见顶层窗口（浏览器这类"窗口标题随页面变"的用进程名最稳）。
        /// keyword 只要包含即可（忽略大小写）："chrome" 匹配 chrome.exe，"msedge" 匹配 msedge.exe。
        /// 多个窗口取标题最短的（通常是最外层主窗口）；进程已退出/无权读名时跳过该窗口。
        /// </summary>
        public static TopWindow FindByProcess(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return null;
            TopWindow best = null;
            foreach (var w in TopLevelWindows())
            {
                string proc = "";
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)w.ProcessId);
                    proc = p.ProcessName ?? "";
                }
                catch { continue; }   // 进程已退出或无权限：跳过
                if (proc.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || w.Title.Length < best.Title.Length) best = w;
            }
            return best;
        }

        /// <summary>找一个匹配的可见窗口；找不到返回 null。多个时取标题最短的（通常是最外层主窗口）</summary>
        public static TopWindow Find(string pattern)
        {
            TopWindow best = null;
            foreach (var w in TopLevelWindows())
            {
                if (!TitleMatches(w.Title, pattern)) continue;
                if (best == null || w.Title.Length < best.Title.Length) best = w;
            }
            return best;
        }

        /// <summary>激活并前置窗口（最小化先还原）；返回是否成功</summary>
        public static bool Activate(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                return SetForegroundWindow(h);
            }
            catch { return false; }
        }

        public static void Close(IntPtr h) { if (h != IntPtr.Zero) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
        public static void Minimize(IntPtr h) { if (h != IntPtr.Zero) ShowWindow(h, SW_SHOWMINIMIZED); }
        public static void Maximize(IntPtr h) { if (h != IntPtr.Zero) ShowWindow(h, SW_SHOWMAXIMIZED); }
        public static void Hide(IntPtr h) { if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE); }
        public static void Show(IntPtr h) { if (h != IntPtr.Zero) ShowWindow(h, SW_SHOWNORMAL); }
    }
}
