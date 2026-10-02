using System;
using System.Windows.Input;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：承载导航、窗口控制与状态栏数据。
    ///
    /// MVVM 分层：
    ///   · 本类只暴露"状态 + 命令"，不引用任何 View 类型；
    ///   · 页面实例的懒加载缓存、PageHost 装配、标题栏拖拽等纯视图行为留在 MainWindow（View 层）；
    ///   · View 订阅 PageRequested / WindowAction 事件完成装配与窗口动作。
    /// </summary>
    public sealed class MainWindowViewModel : ViewModelBase
    {
        // ===================== 可绑定状态 =====================
        private string _title = "视觉流水线";
        private string _subTitle = "";
        private string _status = "就绪";
        private string _screenInfo = "";
        private string _pageKey = "pipeline";

        /// <summary>页面主标题（顶部大字）</summary>
        public string Title { get => _title; set => Set(ref _title, value); }

        /// <summary>页面副标题（顶部小字说明）</summary>
        public string SubTitle { get => _subTitle; set => Set(ref _subTitle, value); }

        /// <summary>状态栏左侧文字（切换页面、执行结果等）</summary>
        public string Status { get => _status; set => Set(ref _status, value); }

        /// <summary>状态栏右侧：屏幕/DPI/缩放/工作区客观数字</summary>
        public string ScreenInfo { get => _screenInfo; set => Set(ref _screenInfo, value); }

        /// <summary>当前页面标识（pipeline/automation/catalog/history/settings/train/help）</summary>
        public string CurrentPageKey { get => _pageKey; set => Set(ref _pageKey, value); }

        /// <summary>是否最大化（标题栏按钮/双击切换用）</summary>
        public bool IsMaximized { get; set; }

        // ===================== 命令 =====================

        /// <summary>左侧导航：CommandParameter = 页面标识</summary>
        public ICommand NavigateCommand { get; }

        /// <summary>标题栏系统按钮：CommandParameter = min / max / close</summary>
        public ICommand WindowCommand { get; }

        // ===================== 视图装配回调（View 订阅） =====================

        /// <summary>请求切换到某页面（View 侧懒加载实例并装配进 PageHost）</summary>
        public event Action<string> PageRequested;

        /// <summary>请求窗口动作（View 侧执行最小化/最大化/关闭）</summary>
        public event Action<string> WindowAction;

        public MainWindowViewModel()
        {
            NavigateCommand = new RelayCommand(p =>
            {
                string key = p as string;
                if (string.IsNullOrEmpty(key)) return;
                CurrentPageKey = key;
                ApplyPageTitles(key);
                PageRequested?.Invoke(key);
            });

            WindowCommand = new RelayCommand(p => WindowAction?.Invoke(p as string));
        }

        /// <summary>页面标题表：与 ShowPage 的页面实例一一对应（数据与视图分离）</summary>
        private static (string title, string sub) PageTitles(string key) => key switch
        {
            "pipeline" => ("视觉流水线", "挑算子 → 调参数 → 看结果（图像处理链）"),
            "automation" => ("自动化工作流（节点）", "节点库 → 画布 → 实例属性 → 运行抽屉"),
            "catalog" => ("算子与节点总览", "全部算子按分类列出，可搜索、可看参数"),
            "history" => ("运行记录", "每轮的 trace / 事件 / 结果图，可回看追溯"),
            "settings" => ("设置", "坐标空间自检、鼠标定位自检、路径与默认值"),
            "train" => ("训练", "小样本图像分类训练：选目录 → 调参数 → 训练 → 导出 ONNX"),
            "help" => ("使用说明", "节点、算子、坐标与自检的完整说明（与旧界面同一份正文）"),
            _ => ("视觉检测台", ""),
        };

        private void ApplyPageTitles(string key)
        {
            var (title, sub) = PageTitles(key);
            Title = title;
            SubTitle = sub;
            Status = "已切换到：" + title;
        }

        /// <summary>状态栏右侧数值（View 侧 UpdateScreenInfo 调用）</summary>
        public void SetScreenInfo(string text) => ScreenInfo = text ?? "";
    }
}
