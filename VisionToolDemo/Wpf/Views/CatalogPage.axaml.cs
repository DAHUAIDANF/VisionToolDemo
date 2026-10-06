using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisionToolDemo.Wpf.ViewModels;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 算子与节点总览页（Avalonia 版，MVVM）：搜索/筛选/详情数据在 CatalogViewModel，
    /// 本文件只保留视图职责：
    ///   · 详情区 UI 构建（参数清单 / 字符串槽卡片）；
    ///   · 主题色点点击。
    /// </summary>
    public partial class CatalogPage : UserControl
    {
        private readonly CatalogViewModel _vm = new();

        public CatalogPage()
        {
            InitializeComponent();
            DataContext = _vm;
            ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
            ThemeManager.RegisterPage(this);
            _vm.DetailChanged += BuildDetail;
        }

        /// <summary>依据详情数据重建详情区</summary>
        private void BuildDetail(CatalogDetail d)
        {
            DetailHost.Children.Clear();
            if (d == null) return;
            foreach (var sec in d.Sections)
            {
                AddHeader(sec.Header);
                foreach (var (name, tip) in sec.Rows) AddRow(name, tip);
            }
        }

        private void AddHeader(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 6) };
            Ui.Class(tb, "sectionheader");
            DetailHost.Children.Add(tb);
        }

        private void AddRow(string name, string tip)
        {
            var card = new Border { Margin = new Thickness(0, 0, 0, 6) };
            Ui.Class(card, "card");
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            if (!string.IsNullOrWhiteSpace(tip))
            {
                var val = new TextBlock { Text = tip, Margin = new Thickness(0, 3, 0, 0) };
                Ui.Class(val, "fainttext");
                sp.Children.Add(val);
            }
            card.Child = sp;
            DetailHost.Children.Add(card);
        }

        /// <summary>顶栏主题色点点击：应用主题并刷新本页色点（全软件风格统一）</summary>
        private void ThemeDot_Click(object sender, PointerPressedEventArgs e)
        {
            ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
        }
    }
}
