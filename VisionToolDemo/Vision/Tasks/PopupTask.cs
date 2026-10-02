using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 弹窗提示：运行到这一步时给操作员一个提示，或者请他确认后继续。
    ///
    /// 典型用法：
    ///   · 提示类："请确认扫码枪已就绪"、"本轮开始，请不要动鼠标"；
    ///   · 确认类：点"取消"就中止整轮自动化（例如检查结果不合格，不想继续点下去）；
    ///   · 无人值守：设"自动关闭秒"让它自己关掉，不需要人点。
    ///
    /// 内容支持 {变量} 插值：配合"全局变量"节点可以把 OCR 识别到的文字显示出来。
    /// **干跑时只写日志、不弹窗** —— 否则自动化会卡在一个没人点的窗口上。
    /// </summary>
    public class PopupTask : IVisionTask, IResultReporter, Automation.IAutomationNode, Automation.IStringParamTask
    {
        public string TaskName => "弹窗提示";

        public string LastSummary { get; private set; } = "";

        /// <summary>节点属性里的「文本」框：弹窗正文</summary>
        public string NodeText { get; set; } = "";

        /// <summary>未使用（接口要求），保留以便统一注入</summary>
        public string NodeKey { get; set; } = "";

        /// <summary>用户是否点了取消（中止整轮）</summary>
        public bool Cancelled { get; private set; }
        public bool Skipped { get; private set; }
        public bool ActuallyShown { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "类型 0信息1警告2错误3确认(可取消)", Min = 0, Max = 3, DefaultValue = 0,
                DisplayFormat = "类型:{0}", Group = "弹窗",
                Tip = "0/1/2 = 只有一个“确定”按钮；3 = “继续 / 取消”，点取消会**中止整轮自动化**。" },
            new TaskParamDesc { ParamName = "自动关闭秒 0=等待", Min = 0, Max = 600, DefaultValue = 0,
                DisplayFormat = "自动关闭:{0}s", Group = "弹窗",
                Tip = "0 = 一直等用户点（无人值守时别用 0）。>0 时倒计时结束按“继续/确定”处理。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Cancelled = false;
            Skipped = false;
            ActuallyShown = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int type = Math.Clamp(paramValues[0], 0, 3);
            int autoClose = paramValues.Length > 1 ? Math.Clamp(paramValues[1], 0, 600) : 0;
            string content = Automation.AutomationContext.ExpandVariables(NodeText ?? "");
            if (string.IsNullOrWhiteSpace(content))
            {
                content = "(弹窗内容为空：请在该节点的“节点属性 → 输入内容”里填写正文)";
            }

            string typeName = type switch { 1 => "警告", 2 => "错误", 3 => "确认", _ => "信息" };
            if (Automation.AutomationContext.DryRun)
            {
                // 干跑不弹，但**不算"被跳过"**：那会让循环里"有跳过就停"每轮误触发。
                // ActuallyShown=false + 摘要里写明，用户就知道它没真弹。
                LastSummary = string.Format("弹窗提示: {0}（干跑，未真的弹窗）\"{1}\"", typeName, OneLine(content));
                return dst;
            }

            bool proceed;
            try
            {
                proceed = PopupDialog.Show(content, typeName, type, autoClose);
                ActuallyShown = true;
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = "弹窗提示: 失败 —— " + ex.Message;
                return dst;
            }

            bool cancelled = type == 3 && !proceed;
            Cancelled = cancelled;
            LastSummary = string.Format("弹窗提示: {0} \"{1}\" → 用户{2}{3}",
                typeName, OneLine(content),
                cancelled ? "点了取消" : "继续",
                cancelled ? "，已中止整轮自动化" : "");
            if (cancelled)
            {
                // 先记日志再抛：运行器会把 OperationCanceledException 当作"用户中止"
                Automation.AutomationContext.Log(LastSummary);
                throw new OperationCanceledException("用户在弹窗里选择了取消");
            }
            return dst;
        }

        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return t.Length <= 60 ? t : t.Substring(0, 60) + "…";
        }
    }

    /// <summary>
    /// 弹窗本体（WPF 版）。不用 MessageBox 是因为需要"倒计时自动关闭"、
    /// 可改按钮文字（继续/确定），以及点"取消"要能中止整轮。
    /// 返回 true = 继续/确定；false = 取消。
    /// </summary>
    internal static class PopupDialog
    {
        public static bool Show(string content, string typeName, int type, int autoCloseSec)
        {
            bool result = true;
            var win = new Window
            {
                Title = "自动化提示 —— " + typeName,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 560,
                Height = 340,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = true,
                Topmost = true,
                WindowStyle = WindowStyle.SingleBorderWindow,
                Background = Brushes.White,
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txt = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                Background = Brushes.White,
                // 关键：本弹窗是白底，必须显式黑字。否则 TextBox 继承全局深色主题样式
                // 的亮色前景（#F6F9FD），白底白字完全看不清（用户反馈"显示了但颜色看不清"）。
                Foreground = Brushes.Black,
                CaretBrush = Brushes.Black,
                SelectionBrush = new SolidColorBrush(Color.FromRgb(76, 141, 246)),
            };
            Grid.SetRow(txt, 0);
            grid.Children.Add(txt);

            var bottom = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 14, 12),
            };
            var lblCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 60, 0)),
            };
            DockPanel.SetDock(lblCount, Dock.Left);
            bottom.Children.Add(lblCount);

            var btnOk = new Button
            {
                Content = type == 3 ? "继续" : "确定",
                Width = 110,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                FontSize = 14,
            };
            DockPanel.SetDock(btnOk, Dock.Right);
            bottom.Children.Add(btnOk);

            var btnCancel = new Button
            {
                Content = "取消",
                Width = 110,
                Height = 34,
                FontSize = 14,
                Visibility = type == 3 ? Visibility.Visible : Visibility.Collapsed,
            };
            DockPanel.SetDock(btnCancel, Dock.Right);
            bottom.Children.Add(btnCancel);

            Grid.SetRow(bottom, 1);
            grid.Children.Add(bottom);
            win.Content = grid;

            btnOk.Click += (_, _) => { result = true; win.Close(); };
            btnCancel.Click += (_, _) => { result = false; win.Close(); };
            win.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { result = true; win.Close(); }
                else if (e.Key == Key.Escape && type == 3) { result = false; win.Close(); }
            };

            DispatcherTimer timer = null;
            if (autoCloseSec > 0)
            {
                int left = autoCloseSec;
                lblCount.Text = string.Format("将自动关闭：{0} 秒", left);
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    left--;
                    if (left <= 0)
                    {
                        timer.Stop();
                        result = true;                      // 倒计时结束按"继续/确定"处理
                        win.Close();
                    }
                    else lblCount.Text = string.Format("将自动关闭：{0} 秒", left);
                };
                timer.Start();
            }

            try { win.ShowDialog(); }
            finally { timer?.Stop(); }
            return result;
        }
    }
}
