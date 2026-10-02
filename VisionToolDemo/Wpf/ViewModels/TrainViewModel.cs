using System;
using System.Windows.Input;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 训练页 ViewModel：承载训练/标注命令入口与关键状态。
    ///
    /// MVVM 边界说明（重交互页）：标注画布的框选/拖拽、混淆矩阵绘制、训练循环（后台线程）
    /// 属于视图/服务层交互，保留在 TrainPage 与 DeepTrainer；本类把全部按钮命令化，
    /// 并把训练运行状态、模型就绪、日志、状态栏等绑定化。
    /// </summary>
    public sealed class TrainViewModel : ViewModelBase
    {
        private bool _isTrainRunning;
        private bool _isModelReady;
        private bool _faceAuto;
        private string _statusText = "就绪";
        private string _logText = "";

        /// <summary>训练是否进行中（控制 停止/验证/导出/保存 按钮可用与训练按钮文字）</summary>
        public bool IsTrainRunning
        {
            get => _isTrainRunning;
            set
            {
                if (Set(ref _isTrainRunning, value))
                {
                    TrainCommand.RaiseCanExecuteChanged();
                    StopCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>模型是否已训练就绪（验证/导出/保存可用）</summary>
        public bool IsModelReady
        {
            get => _isModelReady;
            set
            {
                if (Set(ref _isModelReady, value))
                {
                    ValidateCommand.RaiseCanExecuteChanged();
                    ExportCommand.RaiseCanExecuteChanged();
                    SaveModelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>人脸自动辅助标注开关（TwoWay 绑定）</summary>
        public bool FaceAuto { get => _faceAuto; set => Set(ref _faceAuto, value); }

        /// <summary>状态栏/页面提示</summary>
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        /// <summary>训练日志（TextBox 双向绑定）</summary>
        public string LogText { get => _logText; set => Set(ref _logText, value); }

        /// <summary>开始训练</summary>
        public RelayCommand TrainCommand { get; }

        /// <summary>停止训练</summary>
        public RelayCommand StopCommand { get; }

        /// <summary>验证一张图</summary>
        public RelayCommand ValidateCommand { get; }

        /// <summary>导出 ONNX</summary>
        public RelayCommand ExportCommand { get; }

        /// <summary>保存模型(.vtmodel)</summary>
        public RelayCommand SaveModelCommand { get; }

        /// <summary>加载模型继续训练</summary>
        public ICommand LoadModelCommand { get; }

        /// <summary>浏览训练集目录</summary>
        public ICommand BrowseDirCommand { get; }

        /// <summary>扫描统计训练集</summary>
        public ICommand ScanDirCommand { get; }

        /// <summary>选择标注图片</summary>
        public ICommand PickAnnotCommand { get; }

        /// <summary>刷新标注列表</summary>
        public ICommand RefreshAnnotCommand { get; }

        /// <summary>保存标注</summary>
        public ICommand SaveAnnotCommand { get; }

        /// <summary>清除全部标注</summary>
        public ICommand ClearAnnotCommand { get; }

        /// <summary>添加标注</summary>
        public ICommand AddAnnotCommand { get; }

        /// <summary>删除选中标注</summary>
        public ICommand DelAnnotCommand { get; }

        /// <summary>人脸检测预填</summary>
        public ICommand DetectFacesCommand { get; }

        /// <summary>预填入库</summary>
        public ICommand AddFaceDraftsCommand { get; }

        /// <summary>视图回调（View 订阅并执行实际逻辑）</summary>
        public event Action TrainRequested;
        public event Action StopRequested;
        public event Action ValidateRequested;
        public event Action ExportRequested;
        public event Action SaveModelRequested;
        public event Action LoadModelRequested;
        public event Action BrowseDirRequested;
        public event Action ScanDirRequested;
        public event Action PickAnnotRequested;
        public event Action RefreshAnnotRequested;
        public event Action SaveAnnotRequested;
        public event Action ClearAnnotRequested;
        public event Action AddAnnotRequested;
        public event Action DelAnnotRequested;
        public event Action DetectFacesRequested;
        public event Action AddFaceDraftsRequested;

        public TrainViewModel()
        {
            TrainCommand = new RelayCommand(() => TrainRequested?.Invoke(), () => !IsTrainRunning);
            StopCommand = new RelayCommand(() => StopRequested?.Invoke(), () => IsTrainRunning);
            ValidateCommand = new RelayCommand(() => ValidateRequested?.Invoke(), () => IsModelReady);
            ExportCommand = new RelayCommand(() => ExportRequested?.Invoke(), () => IsModelReady);
            SaveModelCommand = new RelayCommand(() => SaveModelRequested?.Invoke(), () => IsModelReady);
            LoadModelCommand = new RelayCommand(() => LoadModelRequested?.Invoke());
            BrowseDirCommand = new RelayCommand(() => BrowseDirRequested?.Invoke());
            ScanDirCommand = new RelayCommand(() => ScanDirRequested?.Invoke());
            PickAnnotCommand = new RelayCommand(() => PickAnnotRequested?.Invoke());
            RefreshAnnotCommand = new RelayCommand(() => RefreshAnnotRequested?.Invoke());
            SaveAnnotCommand = new RelayCommand(() => SaveAnnotRequested?.Invoke());
            ClearAnnotCommand = new RelayCommand(() => ClearAnnotRequested?.Invoke());
            AddAnnotCommand = new RelayCommand(() => AddAnnotRequested?.Invoke());
            DelAnnotCommand = new RelayCommand(() => DelAnnotRequested?.Invoke());
            DetectFacesCommand = new RelayCommand(() => DetectFacesRequested?.Invoke());
            AddFaceDraftsCommand = new RelayCommand(() => AddFaceDraftsRequested?.Invoke());
        }

        /// <summary>追加一行日志（防无限增长）</summary>
        public void AppendLog(string line)
        {
            if (LogText.Length > 60_000) LogText = LogText.Substring(LogText.Length - 40_000);
            LogText += line + Environment.NewLine;
        }
    }
}
