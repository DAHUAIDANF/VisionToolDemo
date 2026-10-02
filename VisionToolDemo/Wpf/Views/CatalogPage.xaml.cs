using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionToolDemo.Wpf.ViewModels;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 算子与节点总览页（MVVM）：搜索/筛选/详情数据在 CatalogViewModel，
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
            => DetailHost.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("SectionHeader"),
                Margin = new Thickness(0, 10, 0, 6),
            });

        private void AddRow(string name, string tip)
        {
            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(tip))
                sp.Children.Add(new TextBlock
                {
                    Text = tip,
                    Style = (Style)FindResource("FaintText"),
                    Margin = new Thickness(0, 3, 0, 0),
                });
            card.Child = sp;
            DetailHost.Children.Add(card);
        }

        /// <summary>顶栏主题色点点击：应用主题并刷新本页色点（全软件风格统一）</summary>
        private void ThemeDot_Click(object sender, MouseButtonEventArgs e)
        {
            ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
        }
    }
}
