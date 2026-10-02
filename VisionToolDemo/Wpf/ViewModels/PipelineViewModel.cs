using System;
using System.Windows.Input;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 视觉流水线页 ViewModel：承载顶部命令（打开图片/加入算子/清空链/上移下移移除/运行/重置视图/切换标签）。
    ///
    /// MVVM 边界说明（重交互页）：本页的 Canvas 绘制、鼠标框选/缩放/平移、参数面板动态构建、
    /// 图像渲染属于视图专属交互，按 WPF 最佳实践保留在 PipelinePage（View 层）；
    /// 本类把"用户动作"命令化，View 订阅命令事件执行实际逻辑，状态经 StatusText 等属性绑定回显。
    /// </summary>
    public sealed class PipelineViewModel : ViewModelBase
    {
        private string _statusText = "就绪";

        /// <summary>状态栏/页面提示文字（View 经 SetStatus 写入，绑定自动刷新）</summary>
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        /// <summary>加入选中算子到链</summary>
        public ICommand AddStepCommand { get; }

        /// <summary>打开图片（交互选文件）</summary>
        public ICommand OpenImageCommand { get; }

        /// <summary>清空算子链</summary>
        public ICommand ClearChainCommand { get; }

        /// <summary>选中步骤上移</summary>
        public ICommand MoveUpCommand { get; }

        /// <summary>选中步骤下移</summary>
        public ICommand MoveDownCommand { get; }

        /// <summary>移除选中步骤</summary>
        public ICommand RemoveStepCommand { get; }

        /// <summary>运行整条链</summary>
        public ICommand RunChainCommand { get; }

        /// <summary>重置视图缩放/平移</summary>
        public ICommand ResetViewCommand { get; }

        /// <summary>切换 输入/结果 标签（CommandParameter = "input"/"result"）</summary>
        public ICommand SelectTabCommand { get; }

        /// <summary>视图回调（View 订阅并执行实际逻辑）</summary>
        public event Action AddStepRequested;
        public event Action OpenImageRequested;
        public event Action ClearChainRequested;
        public event Action MoveUpRequested;
        public event Action MoveDownRequested;
        public event Action RemoveStepRequested;
        public event Action RunChainRequested;
        public event Action ResetViewRequested;
        public event Action<string> SelectTabRequested;

        public PipelineViewModel()
        {
            AddStepCommand = new RelayCommand(() => AddStepRequested?.Invoke());
            OpenImageCommand = new RelayCommand(() => OpenImageRequested?.Invoke());
            ClearChainCommand = new RelayCommand(() => ClearChainRequested?.Invoke());
            MoveUpCommand = new RelayCommand(() => MoveUpRequested?.Invoke());
            MoveDownCommand = new RelayCommand(() => MoveDownRequested?.Invoke());
            RemoveStepCommand = new RelayCommand(() => RemoveStepRequested?.Invoke());
            RunChainCommand = new RelayCommand(() => RunChainRequested?.Invoke());
            ResetViewCommand = new RelayCommand(() => ResetViewRequested?.Invoke());
            SelectTabCommand = new RelayCommand(p => SelectTabRequested?.Invoke(p as string));
        }
    }
}
