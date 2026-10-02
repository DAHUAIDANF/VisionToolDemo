using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 纯代码构建界面用的小工具（对话框们用它保持风格一致）。
    ///
    /// 为什么这些对话框不走 XAML：它们是"填几个参数就关掉"的小窗体，
    /// 用代码构建更紧凑，也少一层 x:Class/事件绑定的出错面。
    /// 样式统一从 Application.Resources 取（Styles.xaml 已合并进去），取不到就退回内置颜色。
    /// </summary>
    public static class Ui
    {
        public static Brush Brush(string key, string fallback = "#1F242C")
        {
            try
            {
                if (Application.Current != null && Application.Current.TryFindResource(key) is Brush b) return b;
            }
            catch { }
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
            catch { return Brushes.DimGray; }
        }

        /// <summary>
        /// "响应式等待"：分片睡 10ms 并泵一次 Dispatcher 渲染帧。
        /// 用在自动化真实执行（鼠标/打字/重试等待）期间——界面保持渲染与事件响应，
        /// 不再整窗假死；没有 WPF Dispatcher（无头/命令行）时退化为普通 Sleep。
        /// </summary>
        public static void SleepResponsive(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var disp = Application.Current?.Dispatcher;
            while (sw.ElapsedMilliseconds < ms)
            {
                System.Threading.Thread.Sleep(10);
                if (disp == null) continue;
                try
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    disp.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                catch { }
            }
        }

        public static Style Style(string key)
        {
            try { return Application.Current?.TryFindResource(key) as Style; }
            catch { return null; }
        }

        public static void ApplyTheme(Window w)
        {
            w.Background = Brush("Bg");
            w.Foreground = Brush("Fg", "#F6F9FD");
            w.FontFamily = (Application.Current?.TryFindResource("UiFont") as FontFamily)
                ?? new FontFamily("Microsoft YaHei UI, Segoe UI");
            w.FontSize = 13;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
        }

        public static TextBlock Text(string text, bool bold = false, string colorKey = "Fg", double size = 13)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(colorKey, "#F6F9FD"),
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        public static TextBlock Dim(string text) => Text(text, false, "FgDim", 12);

        public static Button Btn(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), MinWidth = 88 };
            var st = Style(primary ? "PrimaryButton" : "FlatButton");
            if (st != null) b.Style = st;
            if (click != null) b.Click += (_, _) => { try { click(); } catch (Exception ex) { Warn(ex.Message); } };
            return b;
        }

        public static TextBox Input(string text = "", double width = double.NaN)
        {
            var t = new TextBox { Text = text ?? "" };
            if (!double.IsNaN(width)) t.Width = width;
            return t;
        }

        public static CheckBox Check(string text, bool isChecked)
        {
            var c = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 4, 0, 4) };
            var st = Style("CheckBox");
            if (st != null) c.Style = st;
            c.Foreground = Brush("Fg", "#F6F9FD");
            return c;
        }

        public static ComboBox Combo(IEnumerable<string> items, int selected = 0)
        {
            var c = new ComboBox { MinWidth = 120 };
            foreach (var it in items) c.Items.Add(it);
            if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(c.Items.Count - 1, selected));
            return c;
        }

        /// <summary>一行：左侧标签 + 右侧控件</summary>
        public static Grid Row(string label, UIElement control, double labelWidth = 108)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lb = Dim(label);
            Grid.SetColumn(lb, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(lb);
            g.Children.Add(control);
            return g;
        }

        public static Border Card(UIElement child, double margin = 0)
        {
            var b = new Border
            {
                Background = Brush("CardBg", "#333C4B"),
                BorderBrush = Brush("Line", "#4A5666"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, margin),
                Child = child,
            };
            return b;
        }

        /// <summary>底部按钮条（确定/取消之类右对齐）</summary>
        public static StackPanel Bar(params UIElement[] items)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            foreach (var it in items) sp.Children.Add(it);
            return sp;
        }

        /// <summary>
        /// 长文本提示：用可换行的 TextBlock 做 ToolTip。
        /// 直接把长字符串赋给 ToolTip 不会自动换行，几百字的参数说明会被截成一条横线。
        /// </summary>
        public static TextBlock Tip(string text)
        {
            return new TextBlock
            {
                Text = text ?? "",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Foreground = Brush("Fg", "#F6F9FD"),
                FontSize = 12.5,
            };
        }

        /// <summary>
        /// 提示/错误弹窗。为什么不用系统 MessageBox：MessageBox 是系统自绘，
        /// 文字颜色跟着系统主题走，在部分 Windows 主题/缩放组合下深色背景配深色字看不清
        /// （用户反馈"弹窗提示的字看不清"）。自绘深色弹窗在任何系统主题下都是深底亮字。
        /// </summary>
        public static void Notice(string message, string title = "提示", bool isError = false)
        {
            var app = Application.Current;
            if (app == null) return;

            var win = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Brush("Bg", "#15181E"),
                Foreground = Brush("Fg", "#F6F9FD"),
                FontFamily = (app.TryFindResource("UiFont") as FontFamily)
                    ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
            };

            // owner：当前活动窗口，找不到就用主窗口（保证居中于调用方窗口）
            Window owner = null;
            foreach (Window w in app.Windows)
            {
                if (w.IsActive && !ReferenceEquals(w, win)) { owner = w; break; }
            }
            if (owner == null && app.MainWindow != null && !ReferenceEquals(app.MainWindow, win)) owner = app.MainWindow;
            if (owner != null && owner.IsVisible) win.Owner = owner;

            var panel = new StackPanel { Margin = new Thickness(18) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(new TextBlock
            {
                Text = isError ? "⚠" : "ℹ",
                FontSize = 24,
                Foreground = Brush(isError ? "Warn" : "Accent", isError ? "#FFC94A" : "#4C8DF6"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("Fg", "#F6F9FD"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(head);

            panel.Children.Add(new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("Fg", "#F6F9FD"),
                FontSize = 13.5,
                MaxWidth = 384,
                LineHeight = 21,
            });

            var ok = new Button
            {
                Content = "确定",
                Style = Style("PrimaryButton") ?? Style("FlatButton"),
                Width = 88,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            ok.Click += (_, _) => win.Close();
            panel.Children.Add(ok);

            win.Content = panel;
            win.ShowDialog();
        }

        public static void Warn(string message) => Notice(message, "提示", false);

        public static void Error(string message) => Notice(message, "出错了", true);

        /// <summary>
        /// 确认弹窗（是/否）。与 Notice 同风格（深色自绘，任何系统主题下都清晰），
        /// 返回 true = 确定，false = 取消/关闭。
        /// </summary>
        public static bool Confirm(string message, string title = "确认",
            string okText = "确定", string cancelText = "取消")
        {
            var app = Application.Current;
            if (app == null) return false;

            var win = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Brush("Bg", "#15181E"),
                Foreground = Brush("Fg", "#F6F9FD"),
                FontFamily = (app.TryFindResource("UiFont") as FontFamily)
                    ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
            };

            Window owner = null;
            foreach (Window w in app.Windows)
            {
                if (w.IsActive && !ReferenceEquals(w, win)) { owner = w; break; }
            }
            if (owner == null && app.MainWindow != null && !ReferenceEquals(app.MainWindow, win)) owner = app.MainWindow;
            if (owner != null && owner.IsVisible) win.Owner = owner;

            var panel = new StackPanel { Margin = new Thickness(18) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(new TextBlock
            {
                Text = "❓",
                FontSize = 24,
                Foreground = Brush("Accent", "#4C8DF6"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("Fg", "#F6F9FD"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(head);

            panel.Children.Add(new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("Fg", "#F6F9FD"),
                FontSize = 13.5,
                MaxWidth = 384,
                LineHeight = 21,
            });

            bool result = false;
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var cancel = new Button
            {
                Content = cancelText,
                Style = Style("FlatButton"),
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
            };
            cancel.Click += (_, _) => win.Close();
            bar.Children.Add(cancel);
            var ok = new Button
            {
                Content = okText,
                Style = Style("PrimaryButton") ?? Style("FlatButton"),
                Width = 88,
                Height = 32,
            };
            ok.Click += (_, _) => { result = true; win.Close(); };
            bar.Children.Add(ok);
            panel.Children.Add(bar);

            win.Content = panel;
            win.ShowDialog();
            return result;
        }
    }
}
