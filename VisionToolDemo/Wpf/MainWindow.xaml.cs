using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using VisionToolDemo.Wpf.ViewModels;
using VisionToolDemo.Wpf.Views;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 主窗口（MVVM）：状态与命令在 MainWindowViewModel，
    /// 本文件只保留纯视图职责：
    ///   · 自绘窗口边框（WindowChrome 代码设置，绕开 XAML 前向引用）；
    ///   · 按工作区开窗 / 更新状态栏 DPI 数字（PresentationSource 是视图操作）；
    ///   · 页面实例懒加载缓存与 PageHost 装配（页面是 View 类型，装配是视图行为）；
    ///   · 标题栏拖动 / 双击最大化（DragMove 是视图操作）。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm = new();

        // 页面实例缓存：切到的页面才创建（懒加载）
        private PipelinePage _pipelinePage;
        private AutomationPage _automationPage;
        private CatalogPage _catalogPage;
        private HistoryPage _historyPage;
        private SettingsPage _settingsPage;
        private TrainPage _trainPage;
        private HelpPage _helpPage;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            // 自绘窗口边框（代码设置）：四边可缩放，顶部标题栏由应用绘制并跟随主题。
            // 不用 XAML 附加属性——Window 元素上的属性在 Window.Resources 解析之前求值，
            // StaticResource 前向引用自身资源会抛"无法找到资源 Wc"，且设计器会误报 XDG0045。
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,                       // 系统标题栏区域 = 0（我们自己画标题栏）
                ResizeBorderThickness = new Thickness(6), // 四边 6px 边缘可拖拽缩放
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            });

            // 按工作区开窗（工作区已排除任务栏），并对小屏/大屏都给合理默认
            var wa = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(1680, wa.Width * 0.92));
            Height = Math.Max(MinHeight, Math.Min(1000, wa.Height * 0.94));
            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

            // ViewModel 事件 → 视图装配/窗口动作
            _vm.PageRequested += key => ShowPage(key);
            _vm.WindowAction += action => DoWindowAction(action);
            _vm.IsMaximized = WindowState == WindowState.Maximized;

            Loaded += (_, _) => { UpdateScreenInfo(); ShowPage(_vm.CurrentPageKey); };
            SizeChanged += (_, _) => UpdateScreenInfo();
            DpiChanged += (_, _) => UpdateScreenInfo();
            StateChanged += OnWindowStateChanged;

            // 最大化时把窗口限制在工作区内（排除任务栏）。
            // WindowStyle=None + WindowChrome 的窗口默认最大化会扩展到整个屏幕矩形（含任务栏），
            // 底部被任务栏遮住——用 WM_GETMINMAXINFO 修正最大尺寸/位置。
            SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(this) is HwndSource src)
                    src.AddHook(WndProc);
            };
        }

        // ===================== 最大化不遮任务栏（WM_GETMINMAXINFO） =====================

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>最大化/还原状态切换：保存并显式恢复窗口原始矩形。
        /// WindowChrome 自绘窗口在 WM_GETMINMAXINFO 修改最大尺寸后，最大化→还原可能不恢复
        /// 最大化前的窗口大小（表现为"还原后窗口还是很大/贴满工作区"），这里手动恢复。</summary>
        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                _restoreBounds = RestoreBounds;
                _vm.IsMaximized = true;
            }
            else if (WindowState == WindowState.Normal && _restoreBounds != Rect.Empty)
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
                _restoreBounds = Rect.Empty;
                _vm.IsMaximized = false;
            }
            else
            {
                _vm.IsMaximized = false;
            }
        }

        /// <summary>最大化前窗口矩形（还原时恢复用）</summary>
        private Rect _restoreBounds = Rect.Empty;

        /// <summary>最大化时把窗口最大尺寸/位置钳制到所在显示器的工作区（任务栏上方）</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    // 最大尺寸 = 工作区尺寸，最大位置 = 工作区原点（Windows 像素，自动匹配当前 DPI）
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    mmi.ptMaxPosition.X = mi.rcWork.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>状态栏右侧：屏幕/DPI/缩放的客观数字（现场排查"看到的不对"时先看这里）</summary>
        private void UpdateScreenInfo()
        {
            try
            {
                double dpi = 96.0;
                var src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                    dpi = 96.0 * src.CompositionTarget.TransformToDevice.M11;
                double scale = dpi / 96.0;
                var wa = SystemParameters.WorkArea;
                double physW = wa.Width * scale, physH = wa.Height * scale;

                _vm.SetScreenInfo(string.Format(
                    "屏幕 {0}x{1}  DPI {2:F0} ({3:P0})  工作区 {4:F0}x{5:F0} DIP  窗口 {6:F0}x{7:F0}",
                    Math.Round(physW), Math.Round(physH), dpi, scale, wa.Width, wa.Height, Width, Height));
            }
            catch { _vm.SetScreenInfo(""); }
        }

        /// <summary>页面装配：懒加载缓存实例 → 放进 PageHost → 通知页面显示 → 注册主题同步</summary>
        private void ShowPage(string key)
        {
            object page;
            switch (key)
            {
                case "pipeline": page = _pipelinePage ??= new PipelinePage(); break;
                case "catalog": page = _catalogPage ??= new CatalogPage(); break;
                case "history": page = _historyPage ??= new HistoryPage(); break;
                case "help": page = _helpPage ??= new HelpPage(); break;
                case "train": page = _trainPage ??= new TrainPage(); break;
                case "settings": page = _settingsPage ??= new SettingsPage(); break;
                default: page = _automationPage ??= new AutomationPage(this); break;   // automation（默认）
            }
            PageHost.Content = page;
            // 页面自己的工具条：切页时先清空再装（"视觉"页要的是"打开图片"，"工作流"页要的是"运行/校验"）。
            // 不先清空的话，算子/记录/说明等不装工具条的页面会残留上一页（视觉页）的按钮。
            ToolHost.Children.Clear();
            (page as IShellPage)?.OnShown(this);
            _vm.Status = "已切换到：" + _vm.Title;
            ThemeManager.RegisterPage(this);
        }

        /// <summary>窗口动作：最小化 / 最大化还原 / 关闭（标题栏系统按钮）</summary>
        private void DoWindowAction(string action)
        {
            switch (action)
            {
                case "min":
                    WindowState = WindowState.Minimized;
                    break;
                case "max":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        // ===================== 自绘标题栏（跟随主题）：拖动 / 双击最大化 =====================

        /// <summary>标题栏按下：单击拖动窗口，双击最大化/还原</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点标题栏上的系统按钮时不触发窗口拖动（按钮点击会冒泡到这里，直接 DragMove 会干扰按钮操作）
            if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch { /* 拖拽状态异常忽略（双击分支已处理） */ }
            }
        }

        /// <summary>沿可视化树向上找指定类型的祖先（用于判断点击源是否在按钮上）</summary>
        private static T FindVisualAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>状态栏文字：页面经此更新（转发到 ViewModel.Status，绑定自动刷新）</summary>
        public void SetStatus(string text) => _vm.Status = text ?? "";

        /// <summary>兼容旧接口调用：把工具按钮接到当前页面的命令上（页面自己提供按钮）</summary>
        public void SetToolbar(System.Collections.Generic.IEnumerable<UIElement> items)
        {
            ToolHost.Children.Clear();
            if (items == null) return;
            foreach (var it in items) ToolHost.Children.Add(it);
        }
    }
}
