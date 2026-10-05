using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.VisualTree;
using VisionToolDemo.Wpf.ViewModels;
using VisionToolDemo.Wpf.Views;   // 已迁移页面（当前：SettingsPage）

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 主窗口（Avalonia 版，由 WPF MainWindow 迁移）：MVVM 结构与 WPF 版一致，
    /// 状态与命令在 MainWindowViewModel，本文件只保留纯视图职责：
    ///   · 自绘窗口边框（ExtendClientArea 无边框，标题栏由应用绘制并跟随主题）；
    ///   · 按工作区开窗 / 更新状态栏 DPI 数字；
    ///   · 页面实例懒加载缓存与 PageHost 装配；
    ///   · 标题栏拖动 / 双击最大化。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm = new();

        // 最大化前窗口矩形（还原时恢复用；Avalonia 的 RestoreBounds 在还原后失效，自行记录）
        private System.Drawing.Rectangle? _restore = null;

        // Win32 窗口状态：无边框 + ExtendClientArea 下 Avalonia 的 WindowState 从 Maximized 切回
        // Normal 会被忽略（最小化/最大化正常），改用 OS 级 ShowWindow 保证「最大化 → 还原」稳定。
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // 页面实例缓存：切到的页面才创建（懒加载）。
        // 【Avalonia 迁移阶段】已迁移页面用真实类型（settings）；未迁移的页面类尚不存在，
        // 缓存字段先声明为 object，迁移完成后再改成具体类型。
        private object _pipelinePage;
        private object _automationPage;
        private object _catalogPage;
        private object _historyPage;
        private SettingsPage _settingsPage;
        private object _trainPage;
        private object _helpPage;
        private object _capturePage;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            // 最大化前窗口矩形（还原时恢复用；Avalonia 的 RestoreBounds 在还原后失效，自行记录）
            _restore = null;

            // ViewModel 事件 → 视图装配/窗口动作
            _vm.PageRequested += key => ShowPage(key);
            _vm.WindowAction += action => DoWindowAction(action);

            Loaded += (_, _) =>
            {
                UpdateScreenInfo();
                ShowPage(_vm.CurrentPageKey);
                // 高亮跟随实际页面：避免 XAML 静态 IsChecked 与 ViewModel 默认页不一致
                // （打开软件时"高亮工作流、实际显示视觉页"这类错位）
                SyncNavHighlight(_vm.CurrentPageKey);
            };
            Opened += (_, _) => UpdateScreenInfo();
            // Avalonia 没有 SizeChanged 之外的尺寸事件：用 SizeChanged 刷新状态栏
            // （DPI 变化在 Avalonia 里会自动重设 RenderScaling，这里一并刷新）
            PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty ||
                    e.Property == Window.WidthProperty ||
                    e.Property == Window.HeightProperty)
                {
                    UpdateScreenInfo();
                    // 只同步状态给 ViewModel；_restore 的恢复/记录全部由 ToggleMaximize 独占，
                    // 避免 ExtendClientArea 下 Avalonia WindowState 同步时机差异覆盖还原矩形。
                    if (e.Property == Window.WindowStateProperty)
                        _vm.IsMaximized = WindowState == WindowState.Maximized;
                }
            };

            // 标题栏拖动/双击（PointerPressed 在标题栏 Border 上触发）
            PointerPressed += OnTitleBarPointerPressed;
        }

        /// <summary>标题栏按下：单击拖动窗口，双击最大化/还原；点按钮时不触发拖动</summary>
        private void OnTitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            // 点标题栏上的系统按钮时不触发窗口拖动（按钮点击会冒泡到这里）
            if (e.Source is IInputElement src && FindAncestor<Button>(src) != null) return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                try { BeginMoveDrag(e); }
                catch { /* 拖拽状态异常忽略 */ }
            }
        }

        /// <summary>沿可视化树向上找指定类型的祖先（判断点击源是否在按钮上）</summary>
        private static T FindAncestor<T>(IInputElement element) where T : class
        {
            var current = element as Avalonia.Visual;
            while (current != null)
            {
                if (current is T t) return t;
                current = current.GetVisualParent();
            }
            return null;
        }

        /// <summary>左侧导航高亮与当前页面同步（RadioButton 互斥组内只勾选一个）。
        /// 启动/切页时调用，确保高亮与实际显示的页面永远一致。</summary>
        private void SyncNavHighlight(string key)
        {
            RadioButton target = key switch
            {
                "automation" => NavAutomation,
                "pipeline" => NavPipeline,
                "capture" => NavCapture,
                "catalog" => NavCatalog,
                "history" => NavHistory,
                "help" => NavHelp,
                "train" => NavTrain,
                "settings" => NavSettings,
                _ => null,
            };
            if (target != null) target.IsChecked = true;
        }

        /// <summary>状态栏右侧：屏幕/DPI/缩放数字（现场排查"看到的不对"时先看这里）</summary>
        private void UpdateScreenInfo()
        {
            try
            {
                double dpi = 96.0;
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                    dpi = 96.0 * topLevel.RenderScaling;

                _vm.SetScreenInfo(string.Format(
                    "DPI {0:F0} ({1:P0})  窗口 {2:F0}x{3:F0}",
                    dpi, dpi / 96.0, Width, Height));
            }
            catch { _vm.SetScreenInfo(""); }
        }

        /// <summary>
        /// 页面装配：懒加载缓存实例 → 放进 PageHost → 通知页面显示 → 注册主题同步。
        /// 【Avalonia 迁移阶段】已迁移页面（当前：设置页）用真实实例，
        /// 未迁移页面显示"页面迁移中"占位；每迁移完一页在 switch 里加一个 case。
        /// </summary>
        private void ShowPage(string key)
        {
            object page;
            try
            {
                switch (key)
                {
                    case "settings": page = _settingsPage ??= new SettingsPage(); break;
                    case "help": page = _helpPage ??= new HelpPage(); break;
                    case "history": page = _historyPage ??= new HistoryPage(); break;
                    case "catalog": page = _catalogPage ??= new CatalogPage(); break;
                    case "capture": page = _capturePage ??= new CapturePage(); break;
                    case "train": page = _trainPage ??= new TrainPage(); break;
                    case "pipeline": page = _pipelinePage ??= new PipelinePage(); break;
                    case "automation": page = _automationPage ??= new AutomationPage(this); break;
                    // ── 以下页面迁移中，先占位；迁移完成后逐个接入 ──
                    default: page = new TextBlock
                    {
                        Text = "页面迁移中：" + key,
                        FontSize = 24,
                        Foreground = Ui.Brush("Fg"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                        break;
                }
                PageHost.Content = page;
                // 页面自己的工具条：切页时先清空再装（不先清空的话，不装工具条的页面会残留上一页按钮）
                ToolHost.Children.Clear();
                (page as IShellPage)?.OnShown(this);
                _vm.Status = "已切换到：" + _vm.Title;
                ThemeManager.RegisterPage(this);
                TryLog("ShowPage OK key=" + key + " page=" + page.GetType().Name + " host=" + (PageHost != null) + " content=" + (PageHost.Content != null));
            }
            catch (Exception ex)
            {
                TryLog("ShowPage FAIL key=" + key + " -> " + ex);
            }
        }

        /// <summary>诊断日志（界面空白排查用，%APPDATA%\VisionToolDemo\ui.log）</summary>
        private static void TryLog(string text)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisionToolDemo");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ui.log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>窗口动作：最小化 / 最大化还原 / 关闭（标题栏系统按钮）</summary>
        private void DoWindowAction(string action)
        {
            switch (action)
            {
                case "min":
                    var mh = TryGetPlatformHandle()?.Handle;
                    if (mh != null && mh != IntPtr.Zero) ShowWindow(mh.Value, SW_MINIMIZE);
                    else WindowState = WindowState.Minimized;
                    break;
                case "max":
                    ToggleMaximize();
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        /// <summary>最大化 ↔ 还原：无边框 + ExtendClientArea 下 Avalonia 的 WindowState 从 Maximized 切回
        /// Normal 会失效，统一走 Win32 ShowWindow，并用记录的矩形恢复窗口尺寸与位置。</summary>
        private void ToggleMaximize()
        {
            var handle = TryGetPlatformHandle()?.Handle;
            bool goMax = !_vm.IsMaximized;
            if (handle == null || handle == IntPtr.Zero)
            {
                // 无句柄的兜底（理论上不会走到）
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            if (goMax)
            {
                _restore = new System.Drawing.Rectangle(Position.X, Position.Y, (int)Width, (int)Height);
                ShowWindow(handle.Value, SW_MAXIMIZE);
            }
            else
            {
                // 还原：先让 Avalonia 状态切回 Normal（ExtendClientArea 下该设置可能被忽略，
                // 但会触发内部状态同步，避免 WindowState 残留 Maximized 干扰后续切换）；
                // 再用 SetWindowPos 按记录的矩形校正窗口位置与尺寸（ShowWindow(SW_RESTORE)
                // 在 ExtendClientArea 下不还原）。
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;
                if (_restore.HasValue)
                {
                    SetWindowPos(handle.Value, IntPtr.Zero, _restore.Value.X, _restore.Value.Y,
                                 _restore.Value.Width, _restore.Value.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                    _restore = null;
                }
            }
            _vm.IsMaximized = goMax;
            UpdateScreenInfo();
        }

        /// <summary>状态栏文字：页面经此更新（转发到 ViewModel.Status）</summary>
        public void SetStatus(string text) => _vm.Status = text ?? "";

        /// <summary>把工具按钮接到当前页面的命令上（页面自己提供按钮）</summary>
        public void SetToolbar(IEnumerable<Control> items)
        {
            ToolHost.Children.Clear();
            if (items == null) return;
            foreach (var it in items) ToolHost.Children.Add(it);
        }
    }
}
