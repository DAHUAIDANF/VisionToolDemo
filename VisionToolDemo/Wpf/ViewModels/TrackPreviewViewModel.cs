using System;
using System.Windows.Input;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 实时目标跟踪预览窗口 ViewModel：承载状态栏文字、播放/重置按钮状态与命令。
    ///
    /// 帧循环 / Canvas 框选 / 绘制 / 摄像头打开（重交互）属于视图职责，留在 TrackPreviewWindow；
    /// 本类命令通过事件回调交给 View 执行，View 再把结果状态写回本类属性（绑定自动刷新界面）。
    /// </summary>
    public sealed class TrackPreviewViewModel : ViewModelBase
    {
        private string _statusText = "";
        private string _playPauseText = "暂停";
        private bool _isSourceOpen;
        private bool _isMirrored = true;

        /// <summary>状态栏文字</summary>
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        /// <summary>播放/暂停按钮文字（"暂停"/"继续"）</summary>
        public string PlayPauseText { get => _playPauseText; set => Set(ref _playPauseText, value); }

        /// <summary>是否已打开视频/摄像头（控制播放/重置按钮可用）</summary>
        public bool IsSourceOpen { get => _isSourceOpen; set => Set(ref _isSourceOpen, value); }

        /// <summary>镜像画面开关（TwoWay 绑定；变化时 View 立即刷新一帧保持画面一致）</summary>
        public bool IsMirrored { get => _isMirrored; set => Set(ref _isMirrored, value); }

        /// <summary>打开视频文件</summary>
        public ICommand OpenVideoCommand { get; }

        /// <summary>打开摄像头</summary>
        public ICommand OpenCamCommand { get; }

        /// <summary>播放/暂停</summary>
        public ICommand PlayPauseCommand { get; }

        /// <summary>重置跟踪</summary>
        public ICommand ResetCommand { get; }

        /// <summary>视图回调（View 订阅并执行实际逻辑）</summary>
        public event Action OpenVideoRequested;
        public event Action OpenCamRequested;
        public event Action PlayPauseRequested;
        public event Action ResetRequested;

        public TrackPreviewViewModel()
        {
            OpenVideoCommand = new RelayCommand(() => OpenVideoRequested?.Invoke());
            OpenCamCommand = new RelayCommand(() => OpenCamRequested?.Invoke());
            PlayPauseCommand = new RelayCommand(() => PlayPauseRequested?.Invoke(), () => IsSourceOpen);
            ResetCommand = new RelayCommand(() => ResetRequested?.Invoke(), () => IsSourceOpen);
        }
    }
}
