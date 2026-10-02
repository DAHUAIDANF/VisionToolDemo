using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VisionToolDemo.Wpf.Mvvm
{
    /// <summary>
    /// MVVM 视图模型基类：提供属性变更通知（INotifyPropertyChanged）。
    ///
    /// 所有 ViewModel 继承本类，用 Set() 写属性、用 OnPropertyChanged 通知；
    /// 视图（XAML）通过 {Binding ...} 订阅变更，不需要代码里手动刷新。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        /// <summary>属性变更事件：WPF 绑定引擎订阅它，属性变化后界面自动更新</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>通知某个属性已变化（绑定引擎会刷新所有依赖它的界面元素）</summary>
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// 标准的 Set 模式：值没变就不通知（避免无谓刷新）；
        /// 值变了就写回并通知。返回是否真的发生了变更。
        /// </summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        /// <summary>一次性释放：派生类可重写（页面/窗口关闭时释放资源）</summary>
        public virtual void Dispose() { GC.SuppressFinalize(this); }
    }
}
