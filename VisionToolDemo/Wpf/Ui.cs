using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 纯代码构建界面用的小工具（由 WPF 版 Ui.cs 迁移到 Avalonia）。
    /// 对话框们用它保持风格一致；颜色/样式统一从 Application 级资源取（主题字典），
    /// 取不到就退回内置颜色。
    /// </summary>
    public static class Ui
    {
        /// <summary>取主题颜色画刷。key 在主题字典里（Bg/CardBg/Accent...），切换主题后重新调用即得新色。</summary>
        public static IBrush Brush(string key, string fallback = "#1F242C")
        {
            try
            {
                if (Application.Current != null &&
                    Application.Current.Resources.TryGetResource(key, Application.Current.RequestedThemeVariant, out var v) &&
                    v is IBrush b)
                    return b;
            }
            catch { }
            try { return new SolidColorBrush(Color.Parse(fallback)); }
            catch { return new SolidColorBrush(Colors.DimGray); }
        }

        /// <summary>
        /// "响应式等待"：分片睡 10ms 并泵一次 Dispatcher 工作队列。
        /// 用在自动化真实执行（鼠标/打字/重试等待）期间——界面保持事件响应，不整窗假死；
        /// 无 Avalonia Dispatcher（无头/命令行）时退化为普通 Sleep。
        /// </summary>
        public static void SleepResponsive(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                System.Threading.Thread.Sleep(10);
                try { Dispatcher.UIThread.RunJobs(); }
                catch { }
            }
        }

        /// <summary>
        /// 【Avalonia 11 迁移】样式系统改为 class 选择器（Styles.axaml 里 Selector 样式），
        /// 控件挂样式 = 加 Classes，不再有 x:Key Style 可引用。
        /// 此方法保留签名以便兼容旧调用点，但一律返回 null——调用方需改用 Class()。
        /// </summary>
        public static Style Style(string key) => null;

        /// <summary>从全局资源取字体（App.Resources 的 UiFont/MonoFont/IconFont），取不到退回默认</summary>
        public static FontFamily Font(string key)
        {
            if (Application.Current?.Resources.TryGetResource(key, Application.Current.RequestedThemeVariant, out var v) == true
                && v is FontFamily f) return f;
            return FontFamily.Default;
        }

        /// <summary>给控件挂样式 class（对应 Styles.axaml 里的 Selector，如 "flat"/"primary"/"dimtext"）</summary>
        public static void Class(Control c, string className)
        {
            if (c == null || string.IsNullOrEmpty(className)) return;
            foreach (var part in className.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!c.Classes.Contains(part)) c.Classes.Add(part);
        }

        /// <summary>复制文本到系统剪贴板（Avalonia：经主窗口 TopLevel.Clipboard 的异步 API）</summary>
        public static async void CopyToClipboard(string text)
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lf
                    && lf.MainWindow is TopLevel tl && tl.Clipboard != null)
                    await tl.Clipboard.SetTextAsync(text ?? "");
            }
            catch { /* 剪贴板被占用等异常忽略 */ }
        }

        /// <summary>给对话框窗口套主题：背景/前景/字体跟随主题。</summary>
        public static void ApplyTheme(Window w)
        {
            w.Background = Brush("Bg");
            w.Foreground = Brush("Fg", "#F6F9FD");
            w.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, Noto Sans CJK SC, sans-serif");
            w.FontSize = 13;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
        }

        public static TextBlock Text(string text, bool bold = false, string colorKey = "Fg", double size = 13)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
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
            Class(b, primary ? "flat primary" : "flat");   // Avalonia 11：样式用 class 选择器
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
            Class(c, "toggle");   // 开关按钮样式（原 FlatToggleButton）
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
        public static Grid Row(string label, Control control, double labelWidth = 108)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse(labelWidth.ToString("0.#"))));
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var lb = Dim(label);
            Grid.SetColumn(lb, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(lb);
            g.Children.Add(control);
            return g;
        }

        public static Border Card(Control child, double margin = 0)
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
        public static StackPanel Bar(params Control[] items)
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

        /// <summary>长文本提示：可换行的 TextBlock（几百字说明用 ToolTip 会被截成一条横线）。</summary>
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

        // ==================== 模态弹窗（自绘深色风格，任何系统主题下都清晰） ====================

        /// <summary>找当前活动窗口作为 owner（居中于调用方），找不到就用主窗口。</summary>
        private static Window FindOwner()
        {
            try
            {
                var app = Application.Current;
                if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    foreach (var w in desktop.Windows)
                        if (w.IsActive) return w;
                    return desktop.MainWindow;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 阻塞式模态弹窗核心：Avalonia 的 ShowDialog 是 async（UI 线程同步等待会死锁），
        /// 这里用 Show() + Dispatcher.UIThread.RunJobs() 手动泵队列，实现与 WPF ShowDialog
        /// 相同的"弹窗期间调用方阻塞、弹窗内交互照常"语义。
        /// </summary>
        private static void ShowModal(Window win, Action<Window> onClosed = null)
        {
            var owner = FindOwner();
            if (owner != null) win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            win.Closed += (_, _) => { onClosed?.Invoke(win); tcs.TrySetResult(true); };
            win.Show(owner);
            while (!tcs.Task.IsCompleted)
            {
                try { Dispatcher.UIThread.RunJobs(); }
                catch { }
                System.Threading.Thread.Sleep(5);
            }
        }

        /// <summary>弹窗结果约定：业务弹窗实现该接口暴露"确认/取消"结果，替代 WPF 的 DialogResult。</summary>
        public interface IModalResult
        {
            /// <summary>true = 用户确认（确定/采一帧/开始抽帧等）；false = 取消或关闭。</summary>
            bool ModalResult { get; }
        }

        /// <summary>阻塞式模态弹窗并返回确认结果（业务弹窗实现 IModalResult；未实现则关闭即视为确认）。</summary>
        public static bool ShowModalResult(Window win)
        {
            bool ok = false;
            ShowModal(win, w => ok = w is IModalResult mr ? mr.ModalResult : true);
            return ok;
        }

        /// <summary>提示/错误弹窗。不用系统 MessageBox：它跟着系统主题走，深色配深字看不清。</summary>
        public static void Notice(string message, string title = "提示", bool isError = false)
        {
            var app = Application.Current;
            if (app == null) return;

            var win = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Brush("Bg", "#15181E"),
                Foreground = Brush("Fg", "#F6F9FD"),
                FontSize = 13,
            };
            ApplyTheme(win);
            win.Width = 440;

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
                FontWeight = FontWeight.SemiBold,
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
                Width = 88,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            Class(ok, "flat primary");
            ok.Click += (_, _) => win.Close();
            panel.Children.Add(ok);

            win.Content = panel;
            ShowModal(win);
        }

        public static void Warn(string message) => Notice(message, "提示", false);

        public static void Error(string message) => Notice(message, "出错了", true);

        /// <summary>确认弹窗（是/否）。返回 true = 确定，false = 取消/关闭。</summary>
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
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                FontSize = 13,
            };
            ApplyTheme(win);
            win.Width = 440;

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
                FontWeight = FontWeight.SemiBold,
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
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Class(cancel, "flat");
            cancel.Click += (_, _) => win.Close();
            bar.Children.Add(cancel);
            var ok = new Button
            {
                Content = okText,
                Width = 88,
                Height = 32,
            };
            Class(ok, "flat primary");
            ok.Click += (_, _) => { result = true; win.Close(); };
            bar.Children.Add(ok);
            panel.Children.Add(bar);

            win.Content = panel;
            ShowModal(win);
            return result;
        }
    }
}
