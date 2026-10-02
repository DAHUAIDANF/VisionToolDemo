using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisionToolDemo.Vision;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 批量处理文件夹（WPF 版，等价旧界面的「批量处理文件夹」）：
    /// 对输入文件夹里的每张图跑同一条**算子里程**，结果图另存并生成报告。
    ///
    /// 与旧界面的关键差别：这里跑的是"算子里程"（可存文件），不依赖当前界面状态，
    /// 所以同一条链可以在这里批量跑，也可以在无人值守下用。
    /// </summary>
    public sealed class BatchWindow : Window
    {
        private readonly TextBox _inDir, _outDir;
        private readonly ComboBox _chainSource;
        private readonly TextBox _chainFile;
        private readonly ProgressBar _bar;
        private readonly TextBlock _status, _summary;
        private readonly ListBox _list;
        private readonly List<ChainStepData> _currentChain;
        private readonly RawImageParams _rawParams;
        private string _reportPath = "";

        /// <summary>currentChain = 视觉页当前那条链（可以直接批量跑它）</summary>
        public BatchWindow(List<ChainStepData> currentChain, RawImageParams rawParams)
        {
            _currentChain = currentChain ?? new List<ChainStepData>();
            _rawParams = rawParams;

            Ui.ApplyTheme(this);
            Title = "批量处理文件夹";
            Width = 760;
            Height = 620;

            _inDir = Ui.Input("");
            _outDir = Ui.Input("");
            _chainSource = Ui.Combo(new[] { "用视觉页当前的算子里程", "用算子里程文件…" }, 0);
            _chainFile = Ui.Input("");
            _chainFile.IsReadOnly = true;
            _bar = new ProgressBar { Height = 18, Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 6, 0, 6) };
            _status = Ui.Dim("选择输入文件夹后点「开始批量处理」。");
            _summary = Ui.Dim("");
            _list = new ListBox { MinHeight = 160, ItemTemplate = null };

            var panel = new DockPanel { Margin = new Thickness(14) };

            // 顶部：目录与算子里程
            var top = new StackPanel();
            top.Children.Add(Ui.Row("输入文件夹", MakeBrowseRow(_inDir, BrowseIn)));
            top.Children.Add(Ui.Row("输出文件夹", MakeBrowseRow(_outDir, BrowseOut)));
            top.Children.Add(Ui.Row("算子里程", _chainSource));
            top.Children.Add(Ui.Row("里程文件", MakeBrowseRow(_chainFile, BrowseChain, "选择…")));
            top.Children.Add(Ui.Dim("输出留空 = <输入文件夹>\\处理结果；报告写在输入文件夹下的「批量处理报告.txt」。"));
            _chainSource.SelectionChanged += (_, _) => UpdateChainHint();
            UpdateChainHint();
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);

            // 底部：按钮与进度
            var bottom = new StackPanel();
            bottom.Children.Add(_bar);
            bottom.Children.Add(_status);
            bottom.Children.Add(_summary);
            var openReport = Ui.Btn("打开报告所在目录", OpenReportDir);
            var close = Ui.Btn("关闭", () => Close());
            var start = Ui.Btn("开始批量处理", Start, true);
            var bar = Ui.Bar(start, openReport, close);
            DockPanel.SetDock(bottom, Dock.Bottom);
            panel.Children.Add(bottom);
            panel.Children.Add(bottom.Children.Count > 0 ? new Border { Height = 0 } : new Border());

            // 中间：结果列表
            var mid = new DockPanel();
            var head = Ui.Dim("逐文件结果（成功/失败、耗时、摘要、结果图路径）");
            DockPanel.SetDock(head, Dock.Top);
            mid.Children.Add(head);
            mid.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
            panel.Children.Add(mid);

            Content = panel;
        }

        private UIElement MakeBrowseRow(TextBox box, Action browse, string buttonText = "浏览…")
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var b = Ui.Btn(buttonText, browse);
            b.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(box, 0);
            Grid.SetColumn(b, 1);
            g.Children.Add(box);
            g.Children.Add(b);
            return g;
        }

        private void UpdateChainHint()
        {
            bool useFile = _chainSource.SelectedIndex == 1;
            _chainFile.IsEnabled = useFile;
            _status.Text = useFile
                ? "将使用所选算子里程文件（模板图也在文件里）。"
                : string.Format("将使用视觉页当前的算子里程（{0} 步）。", _currentChain.Count);
        }

        private void BrowseIn()
        {
            var dlg = new OpenFolderDialog { Title = "选择要批量处理的图片文件夹" };
            if (dlg.ShowDialog(this) == true) _inDir.Text = dlg.FolderName;
        }

        private void BrowseOut()
        {
            var dlg = new OpenFolderDialog { Title = "选择结果输出文件夹（可留空）" };
            if (dlg.ShowDialog(this) == true) _outDir.Text = dlg.FolderName;
        }

        private void BrowseChain()
        {
            var dlg = new OpenFileDialog { Title = "选择算子里程文件", Filter = "算子里程 (*.chain.json)|*.chain.json|JSON|*.json|所有文件|*.*" };
            if (dlg.ShowDialog(this) == true)
            {
                _chainFile.Text = dlg.FileName;
                _chainSource.SelectedIndex = 1;
            }
        }

        private void Start()
        {
            string inDir = _inDir.Text.Trim();
            if (inDir.Length == 0) { Ui.Warn("先选输入文件夹"); return; }

            List<ChainStepData> chain;
            if (_chainSource.SelectedIndex == 1)
            {
                string file = _chainFile.Text.Trim();
                if (file.Length == 0 || !File.Exists(file)) { Ui.Warn("先选算子里程文件"); return; }
                try { chain = OperatorChain.FromJson(File.ReadAllText(file)); }
                catch (Exception ex) { Ui.Error(ex.Message); return; }
            }
            else chain = _currentChain;

            if (chain.Count == 0) { Ui.Warn("算子里程是空的：先回视觉页搭好链"); return; }

            _list.Items.Clear();
            _bar.Maximum = 1;
            _bar.Value = 0;
            _status.Text = "处理中…";

            var rows = BatchProcessor.Run(inDir, _outDir.Text.Trim(), chain, _rawParams,
                (done, total, current) =>
                {
                    _bar.Maximum = Math.Max(1, total);
                    _bar.Value = Math.Min(done + 1, total);
                    _status.Text = string.Format(CultureInfo.InvariantCulture, "处理中… {0}/{1}  {2}", done, total, current);
                    // 让进度条真的能刷新（本方法是同步跑完的）
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                        System.Windows.Threading.DispatcherPriority.Background);
                },
                out string report, out string reportPath);

            _reportPath = reportPath;
            _bar.Value = _bar.Maximum;
            int ok = rows.Count(r => r.Ok);
            _status.Text = string.Format("完成：共 {0} 个，成功 {1}，失败 {2}{3}",
                rows.Count, ok, rows.Count - ok,
                reportPath.Length > 0 ? "   报告：" + reportPath : "   （报告写入失败）");
            _summary.Text = report.Length > 4000 ? report.Substring(0, 4000) + "…" : report;

            foreach (var r in rows)
                _list.Items.Add(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] {1}  {2}ms{3}{4}", r.Ok ? "OK" : "NG", r.File, r.Ms,
                    r.Summary.Length > 0 ? "  " + r.Summary : "",
                    r.Error.Length > 0 ? "  ⚠ " + r.Error : ""));

            if (rows.Count == 0 && report.Length > 0) Ui.Warn(report);
        }

        private void OpenReportDir()
        {
            try
            {
                string dir = _reportPath.Length > 0 ? Path.GetDirectoryName(_reportPath) : _inDir.Text.Trim();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) { Ui.Warn("还没有报告目录"); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }
    }
}
