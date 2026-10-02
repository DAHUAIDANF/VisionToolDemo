using System.Windows;
using System.Windows.Controls;
using VisionToolDemo.Wpf.ViewModels;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 使用说明页（MVVM）：正文/清单/复制命令在 HelpViewModel；
    /// 图文卡片是静态 XAML，本文件只保留构造装配与主题注册。
    /// </summary>
    public partial class HelpPage : UserControl
    {
        private readonly HelpViewModel _vm = new();

        public HelpPage()
        {
            InitializeComponent();
            DataContext = _vm;
            ThemeManager.RegisterPage(this);
            _vm.StatusRequested += msg => (Window.GetWindow(this) as MainWindow)?.SetStatus(msg);
        }
    }
}
