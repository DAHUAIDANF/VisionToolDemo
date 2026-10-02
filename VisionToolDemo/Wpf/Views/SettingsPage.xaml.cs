using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionToolDemo.Wpf.ViewModels;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 设置与自检页（MVVM）：数据与命令在 SettingsViewModel，
    /// 本文件只保留视图职责：
    ///   · 动态构建主题卡片（Border 依赖 Card 样式，是 View 行为）；
    ///   · 主题卡片高亮刷新（操作卡片 Border 外观）；
    ///   · 报告滚动到末尾、状态栏文字经 StatusRequested 事件转发给主窗口。
    /// </summary>
    public partial class SettingsPage : UserControl
    {
        private readonly SettingsViewModel _vm = new();

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = _vm;
            ThemeManager.RegisterPage(this);
            BuildThemeCards();

            // 状态栏文字：经 ViewModel 事件 → 主窗口 SetStatus
            _vm.StatusRequested += msg => (Window.GetWindow(this) as MainWindow)?.SetStatus(msg);
            // 主题切换后刷新卡片高亮
            _vm.ThemeSelectionChanged += RefreshThemeSelection;
            // 报告追加后滚动到底部（视图行为）
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.ReportText))
                    Report.ScrollToEnd();
            };
        }

        /// <summary>生成主题选择卡片：每张卡片显示 5 色块预览 + 主题名，点击即应用并保存</summary>
        private void BuildThemeCards()
        {
            ThemeHost.Children.Clear();
            for (int i = 0; i < _vm.Themes.Length; i++)
            {
                var t = _vm.Themes[i];
                var card = new Border
                {
                    Style = (Style)FindResource("Card"),
                    Margin = new Thickness(0, 0, 12, 12),
                    Width = 152,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = Ui.Tip("点击应用「" + t.Name + "」主题，立即生效并保存"),
                };
                var sp = new StackPanel { Margin = new Thickness(12) };
                // 色板预览：背景/卡片/强调/正文/描边 5 个色块（ToolTip 说明各自含义）
                var preview = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                string[] keys = { "Bg", "CardBg", "Accent", "Fg", "Line" };
                string[] keyTips = { "界面背景", "卡片底色", "强调色", "正文", "描边" };
                for (int j = 0; j < keys.Length; j++)
                {
                    if (!t.Colors.TryGetValue(keys[j], out var c)) continue;
                    preview.Children.Add(new Border
                    {
                        Width = 26,
                        Height = 26,
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 5, 0),
                        Background = new SolidColorBrush(c),
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                        BorderThickness = new Thickness(1),
                        ToolTip = keyTips[j] + " " + c.ToString(),
                    });
                }
                sp.Children.Add(preview);
                sp.Children.Add(new TextBlock
                {
                    Text = t.Name,
                    Style = (Style)FindResource("CardTitle"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                card.Child = sp;
                int idx = i;
                card.MouseLeftButtonDown += (_, _) => _vm.ApplyThemeCommand.Execute(idx);
                ThemeHost.Children.Add(card);
            }
            RefreshThemeSelection();
        }

        /// <summary>当前主题卡片用强调色粗边框高亮</summary>
        private void RefreshThemeSelection()
        {
            for (int i = 0; i < ThemeHost.Children.Count; i++)
            {
                if (ThemeHost.Children[i] is not Border card) continue;
                bool on = i == _vm.CurrentTheme;
                card.BorderBrush = new SolidColorBrush(on
                    ? _vm.Themes[i].Colors["Accent"]
                    : System.Windows.Media.Color.FromRgb(0x55, 0x5E, 0x6E));
                card.BorderThickness = new Thickness(on ? 3 : 1);
                card.ToolTip = on ? "当前主题（点击应用）" : "点击应用「" + _vm.Themes[i].Name + "」";
            }
        }
    }
}
