using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{    /// <summary>
    /// 设置页 ViewModel：承载"显示与坐标/主题/自检/目录"的数据与命令。
    ///
    /// 视图行为留在 SettingsPage（View 层）：
    ///   · 主题卡片是动态构建的 Border（依赖 Card 样式），由 View 构建、订阅本类的主题命令；
    ///   · 自检报告滚动到末尾、剪贴板复制成功提示，经 StatusRequested 事件交给主窗口状态栏。
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase
    {
        // ===================== 可绑定状态 =====================
        private string _infoText;
        private string _dirInfoText;
        private string _reportText = "";
        private int _currentTheme;

        /// <summary>坐标空间描述（构造时生成一次）</summary>
        public string InfoText { get => _infoText; private set => Set(ref _infoText, value); }

        /// <summary>目录与资源统计（构造时生成一次）</summary>
        public string DirInfoText { get => _dirInfoText; private set => Set(ref _dirInfoText, value); }

        /// <summary>自检报告（TextBox 双向绑定，代码里 Append 后界面自动滚动/刷新）</summary>
        public string ReportText { get => _reportText; set => Set(ref _reportText, value); }

        /// <summary>当前主题索引（主题卡片高亮用）</summary>
        public int CurrentTheme { get => _currentTheme; private set => Set(ref _currentTheme, value); }

        /// <summary>主题集合（View 构建卡片时遍历）</summary>
        public ThemePalette[] Themes => ThemeManager.Themes;

        // ===================== 命令 =====================

        /// <summary>点击主题卡片应用主题（CommandParameter = 主题索引）</summary>
        public ICommand ApplyThemeCommand { get; }

        /// <summary>坐标系自检（截图 vs 屏幕）</summary>
        public ICommand CoordTestCommand { get; }

        /// <summary>鼠标定位自检（真实移动）</summary>
        public ICommand MouseTestCommand { get; }

        /// <summary>复制报告到剪贴板</summary>
        public ICommand ClipboardCommand { get; }

        /// <summary>打开截图目录</summary>
        public ICommand OpenShotDirCommand { get; }

        /// <summary>打开运行记录目录</summary>
        public ICommand OpenRecDirCommand { get; }

        // ===================== 视图回调（View 订阅） =====================

        /// <summary>请求把一句话写到主窗口状态栏</summary>
        public event Action<string> StatusRequested;

        /// <summary>请求刷新主题卡片高亮（当前主题索引已变化）</summary>
        public event Action ThemeSelectionChanged;

        public SettingsViewModel()
        {
            InfoText = CoordinateSpace.Describe();
            DirInfoText = "截图目录：" + AutomationContext.SaveImageDir
                + "\n运行记录目录：" + RunRecorder.RootDir
                + "\n算子：" + VisionTaskRegistry.GetVisionToolNames().Count() + " 个视觉算子 + 自动化节点"
                + "\n程序目录：" + AppContext.BaseDirectory;
            CurrentTheme = ThemeManager.Current;

            ApplyThemeCommand = new RelayCommand(p =>
            {
                // CommandParameter 可能是 int（代码动态绑定）或 string（XAML 绑定），统一解析
                int idx = p is int i ? i : (int.TryParse(p as string, out int j) ? j : -1);
                if (idx < 0 || idx >= ThemeManager.Themes.Length) return;
                ThemeManager.Apply(idx);
                CurrentTheme = ThemeManager.Current;
                ThemeSelectionChanged?.Invoke();
                StatusRequested?.Invoke("已切换主题：「" + ThemeManager.Themes[idx].Name + "」");
            });

            CoordTestCommand = new RelayCommand(() => Run("坐标系自检", () => CoordinateSpace.SelfTest()));

            MouseTestCommand = new RelayCommand(() => Run("鼠标定位自检", () =>
            {
                string report = AutomationContext.Input.SelfTestMouse(2);
                report += Environment.NewLine + CoordinateSpace.Describe() + Environment.NewLine;
                report += "（真实执行前建议：目标窗口先激活；本程序窗口先隐藏/移开）" + Environment.NewLine;
                return report;
            }));

            ClipboardCommand = new RelayCommand(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(ReportText ?? "");
                    StatusRequested?.Invoke("报告已复制到剪贴板");
                }
                catch { /* 剪贴板被占用等异常忽略 */ }
            });

            OpenShotDirCommand = new RelayCommand(() => Open(AutomationContext.SaveImageDir));
            OpenRecDirCommand = new RelayCommand(() => Open(RunRecorder.RootDir));
        }

        /// <summary>自检统一入口：追加标题/时间/结果/空行到报告并通知状态栏</summary>
        private void Run(string title, Func<string> work)
        {
            ReportText += "──── " + title + " ────" + Environment.NewLine;
            ReportText += DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine;
            try
            {
                ReportText += work() + Environment.NewLine;
            }
            catch (Exception ex)
            {
                ReportText += "自检异常：" + ex.Message + Environment.NewLine;
            }
            ReportText += Environment.NewLine;
            StatusRequested?.Invoke(title + " 完成（结果见下方报告）");
        }

        /// <summary>打开目录（不存在则创建）；失败时经 Ui.Notice 提示（View 层弹窗）</summary>
        private void Open(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Ui.Notice(ex.Message, "打不开目录", true);
            }
        }
    }
}
