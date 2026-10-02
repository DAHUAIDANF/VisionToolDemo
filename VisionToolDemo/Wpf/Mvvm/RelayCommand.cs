using System;
using System.Windows.Input;

namespace VisionToolDemo.Wpf.Mvvm
{
    /// <summary>
    /// MVVM 命令实现：把按钮/菜单的 Click 变成可绑定的 ICommand。
    ///
    /// 用法：
    ///   public ICommand SaveCommand => new RelayCommand(() => Save(), () => CanSave);
    ///   XAML：<Button Command="{Binding SaveCommand}" />
    ///   CommandParameter 通过 parameter 参数传入（RadioButton 传页面标识、窗口按钮传 min/max/close）。
    ///
    /// 性能说明：不使用 CommandManager.RequerySuggested（它会挂在全局输入事件上，每次鼠标/键盘
    /// 事件后对所有命令全量重查 CanExecute，几十个命令就会造成界面明显卡顿）。
    /// 改为显式 RaiseCanExecuteChanged()：依赖 CanExecute 的 VM 在状态变化时手动刷新（如训练
    /// 页 IsTrainRunning/IsModelReady 变化后刷新对应命令），状态不常变、刷新次数极少，界面不卡。
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>命令可否执行的变更事件（无全局订阅，由状态变化方显式触发）</summary>
        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        /// <summary>状态变化后手动刷新按钮可用性（调用方在 VM 状态 Set 后调用）</summary>
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
