using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VisionToolDemo.Wpf.ViewModels;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 运行记录页（Avalonia 版，MVVM）：列表数据/选中详情/命令在 HistoryViewModel，
    /// 本文件只保留视图职责：
    ///   · 详情区 UI 构建（判定徽标 / 结果图 / event / trace 卡片）；
    ///   · 主题色点点击（视图交互）；
    ///   · 图片加载（Image 控件是 View 元素）。
    /// </summary>
    public partial class HistoryPage : UserControl
    {
        private readonly HistoryViewModel _vm = new();

        public HistoryPage()
        {
            InitializeComponent();
            DataContext = _vm;
            ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
            ThemeManager.RegisterPage(this);
            _vm.DetailChanged += d => BuildDetail(d);
            _vm.RefreshCommand.Execute(null);
        }

        /// <summary>依据详情数据重建详情区（判定徽标 → 结果图 → event → trace）</summary>
        private void BuildDetail(HistoryDetail d)
        {
            DetailHost.Children.Clear();
            if (d == null) return;

            // 判定徽标 + 最终结果图预览（不打开目录也能看到"那轮长什么样"）
            DetailHost.Children.Add(new TextBlock
            {
                Text = "判定：" + (d.Verdict.Length > 0 ? d.Verdict : (d.Ok ? "OK" : "NG")),
                Foreground = Ui.Brush(d.Ok ? "Ok" : "Ng"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            if (!string.IsNullOrEmpty(d.FinalPng) && File.Exists(d.FinalPng))
            {
                AddHeader("最终结果图");
                try
                {
                    using var fs = File.OpenRead(d.FinalPng);
                    DetailHost.Children.Add(new Image
                    {
                        Source = new Bitmap(fs),
                        MaxHeight = 240,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 10),
                    });
                }
                catch { /* 图片读取失败忽略 */ }
            }

            AddHeader("本轮事件（event.json）");
            AddCode(d.EventJson);

            AddHeader("逐节点耗时（trace.json）");
            if (d.TraceRows.Count == 0) AddCode(d.TraceSummary);
            else foreach (var (name, value) in d.TraceRows) AddRow(name, value);

            AddHeader("最终结果图");
            if (string.IsNullOrEmpty(d.FinalPng2) || !File.Exists(d.FinalPng2)) AddCode("（这一轮没有存结果图）");
            else
            {
                var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 320, Margin = new Thickness(0, 0, 0, 8) };
                try
                {
                    using var fs = File.OpenRead(d.FinalPng2);
                    img.Source = new Bitmap(fs);
                }
                catch { /* 图片读取失败忽略 */ }
                DetailHost.Children.Add(img);
            }
        }

        private void AddHeader(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 10, 0, 6),
            };
            Ui.Class(tb, "sectionheader");
            DetailHost.Children.Add(tb);
        }

        private void AddCode(string text)
        {
            var box = new TextBox
            {
                Text = text ?? "",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MaxHeight = 240,
                FontFamily = Ui.Font("MonoFont"),
                Margin = new Thickness(0, 0, 0, 6),
            };
            DetailHost.Children.Add(box);
        }

        private void AddRow(string name, string value)
        {
            var card = new Border { Margin = new Thickness(0, 0, 0, 6) };
            Ui.Class(card, "card");
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            var val = new TextBlock
            {
                Text = value,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Ui.Class(val, "fainttext");
            sp.Children.Add(val);
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
