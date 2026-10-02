using System;
using System.Windows.Input;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 自动化工作流页 ViewModel：承载运行抽屉统计、日志、校验与记录区命令。
    ///
    /// MVVM 边界说明（重交互页）：节点画布的拖拽/连线/缩放/参数检查器动态构建属于视图专属交互，
    /// 保留在 AutomationPage；本类把顶部工具栏与记录区命令化（View 订阅命令事件执行），
    /// 并把统计/日志/预览标题等状态绑定化。
    /// </summary>
    public sealed class AutomationViewModel : ViewModelBase
    {
        private string _statusText = "就绪";
        private string _statVerdict = "—";
        private string _statMs = "—";
        private string _statSteps = "—";
        private string _statSkip = "—";
        private string _statNote = "";
        private string _previewCaption = "选中节点后显示它的处理结果";
        private string _logText = "";

        /// <summary>状态栏/页面提示（View 经 SetStatus 写入）</summary>
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        /// <summary>统计：总判定</summary>
        public string StatVerdict { get => _statVerdict; set => Set(ref _statVerdict, value); }

        /// <summary>统计：总耗时</summary>
        public string StatMs { get => _statMs; set => Set(ref _statMs, value); }

        /// <summary>统计：步数 / 动作</summary>
        public string StatSteps { get => _statSteps; set => Set(ref _statSteps, value); }

        /// <summary>统计：跳过 / 失败</summary>
        public string StatSkip { get => _statSkip; set => Set(ref _statSkip, value); }

        /// <summary>统计补充说明</summary>
        public string StatNote { get => _statNote; set => Set(ref _statNote, value); }

        /// <summary>预览区标题（选中节点后显示它的处理结果）</summary>
        public string PreviewCaption { get => _previewCaption; set => Set(ref _previewCaption, value); }

        /// <summary>运行日志（TextBox 双向绑定，代码里 Append 后界面自动刷新）</summary>
        public string LogText { get => _logText; set => Set(ref _logText, value); }

        /// <summary>打开运行记录目录</summary>
        public ICommand OpenRecDirCommand { get; }

        /// <summary>刷新运行记录列表</summary>
        public ICommand RefreshRecCommand { get; }

        /// <summary>视图回调（View 订阅并执行实际逻辑）</summary>
        public event Action OpenRecDirRequested;
        public event Action RefreshRecRequested;

        public AutomationViewModel()
        {
            OpenRecDirCommand = new RelayCommand(() => OpenRecDirRequested?.Invoke());
            RefreshRecCommand = new RelayCommand(() => RefreshRecRequested?.Invoke());
        }

        /// <summary>追加一行日志（日志区自动滚动由 View 监听属性变化完成）</summary>
        public void AppendLog(string line)
        {
            if (LogText.Length > 60_000) LogText = LogText.Substring(LogText.Length - 40_000);   // 防无限增长
            LogText += line + Environment.NewLine;
        }
    }
}
