using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 运行记录页 ViewModel：承载记录列表、选中详情与打开/刷新命令。
    ///
    /// 视图行为留在 HistoryPage（View 层）：
    ///   · 详情区（判定徽标 / 结果图 / event / trace）是动态构建的 UIElement，由 View 依据 CurrentDetail 构建；
    ///   · 主题色点点击（ThemeUi.ApplyFromClick）是视图交互，保留在 View。
    /// </summary>
    public sealed class HistoryViewModel : ViewModelBase
    {
        // ===================== 可绑定状态 =====================
        private string _rootText;
        private string _detailTitle = "正在读取运行记录…";
        private string _detailSub;
        private HistoryRecord _selected;
        private HistoryDetail _currentDetail;

        /// <summary>记录根目录</summary>
        public string RootText { get => _rootText; private set => Set(ref _rootText, value); }

        /// <summary>详情区标题（目录名/提示）</summary>
        public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }

        /// <summary>详情区副标题（目录路径/提示）</summary>
        public string DetailSub { get => _detailSub; set => Set(ref _detailSub, value); }

        /// <summary>记录列表（ListBox.ItemsSource 绑定）</summary>
        public ObservableCollection<HistoryRecord> Records { get; } = new();

        /// <summary>当前选中的记录（ListBox.SelectedItem 双向绑定）</summary>
        public HistoryRecord SelectedRecord
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value)) LoadDetail(value);
            }
        }

        /// <summary>当前详情的完整数据（View 依据它重建详情区 UI）</summary>
        public HistoryDetail CurrentDetail
        {
            get => _currentDetail;
            private set
            {
                if (Set(ref _currentDetail, value)) DetailChanged?.Invoke(value);
            }
        }

        // ===================== 命令 =====================

        /// <summary>刷新记录列表</summary>
        public ICommand RefreshCommand { get; }

        /// <summary>打开记录根目录</summary>
        public ICommand OpenRootCommand { get; }

        /// <summary>打开选中这一轮的目录</summary>
        public ICommand OpenThisCommand { get; }

        /// <summary>打开选中这一轮的 report.html</summary>
        public ICommand OpenReportCommand { get; }

        // ===================== 视图回调 =====================

        /// <summary>详情数据已就绪（View 重建详情区）</summary>
        public event Action<HistoryDetail> DetailChanged;

        public HistoryViewModel()
        {
            RootText = RunRecorder.RootDir;
            RefreshCommand = new RelayCommand(() => Refresh());
            OpenRootCommand = new RelayCommand(() => Open(RunRecorder.RootDir));
            OpenThisCommand = new RelayCommand(() =>
            {
                if (SelectedRecord != null) Open(SelectedRecord.Dir);
            });
            OpenReportCommand = new RelayCommand(() =>
            {
                if (SelectedRecord == null) return;
                string report = Path.Combine(SelectedRecord.Dir, "report.html");
                if (!File.Exists(report))
                {
                    Ui.Notice("这一轮还没有报告（用新版本跑一次“运行”后才会生成 report.html）。", "没有报告");
                    return;
                }
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = report, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Ui.Notice(ex.Message, "打不开报告", true);
                }
            });
        }

        /// <summary>刷新：读记录列表（解析几十个 event.json 在后台线程，避免卡 UI）</summary>
        public async void Refresh()
        {
            Records.Clear();
            SelectedRecord = null;
            CurrentDetail = null;
            DetailTitle = "正在读取运行记录…";
            DetailSub = RunRecorder.RootDir;

            List<RunRecordInfo> records;
            try { records = await System.Threading.Tasks.Task.Run(() => RunRecorder.List(200)); }
            catch { records = new List<RunRecordInfo>(); }

            foreach (var r in records)
            {
                Records.Add(new HistoryRecord
                {
                    Title = string.Format("{0}   {1}   {2}ms   {3} 步{4}",
                        r.Time.ToString("MM-dd HH:mm:ss"),
                        r.Verdict.Length > 0 ? r.Verdict : (r.Ok ? "完成" : "失败"),
                        r.TotalMs, r.Steps, r.Skipped > 0 ? "   跳过 " + r.Skipped : ""),
                    Sub = r.Dir,
                    Dir = r.Dir,
                    Error = r.Error,
                });
            }

            if (Records.Count > 0)
            {
                DetailTitle = "运行记录（" + Records.Count + " 条）";
                return;
            }
            DetailTitle = "还没有运行记录";
            DetailSub = "在「工作流」页点一次“运行”或“干跑”，这里就会出现一轮记录（trace.json / event.json / final.png）。";
        }

        /// <summary>选中记录 → 后台读 event/trace → 组装详情数据</summary>
        private async void LoadDetail(HistoryRecord rec)
        {
            if (rec == null) return;
            DetailTitle = Path.GetFileName(rec.Dir);
            DetailSub = rec.Dir;
            CurrentDetail = null;

            HistoryDetail d;
            try
            {
                d = await System.Threading.Tasks.Task.Run(() => BuildDetail(rec.Dir));
            }
            catch { d = new HistoryDetail { Dir = rec.Dir, Verdict = "", Ok = false }; }
            CurrentDetail = d;
        }

        /// <summary>组装详情：判定徽标数据 + event 文本 + trace 行 + 结果图路径（后台线程执行）</summary>
        private static HistoryDetail BuildDetail(string dir)
        {
            var d = new HistoryDetail { Dir = dir };
            var ev = ReadJson(Path.Combine(dir, "event.json"));
            if (ev != null)
            {
                d.Ok = ev.Value<bool?>("ok") ?? false;
                d.Verdict = ev.Value<string>("verdict") ?? "";
            }

            string finalPng = Path.Combine(dir, "final.png");
            if (File.Exists(finalPng)) d.FinalPng = finalPng;
            d.EventJson = ReadFile(Path.Combine(dir, "event.json"), 4000);

            var trace = ReadJson(Path.Combine(dir, "trace.json"));
            if (trace == null) d.TraceSummary = "（没有 trace.json）";
            else
            {
                d.TotalMs = trace.Value<long?>("total_ms") ?? 0;
                var nodes = trace["nodes"] as JArray;
                if (nodes != null)
                {
                    foreach (var n in nodes)
                    {
                        long ms = n.Value<long?>("ms") ?? 0;
                        int id = n.Value<int?>("id") ?? 0;
                        string title = n.Value<string>("title") ?? "";
                        string op = n.Value<string>("op") ?? "";
                        string summary = n.Value<string>("summary") ?? "";
                        d.TraceRows.Add((string.Format("#{0} {1}{2}   {3}ms", id, title,
                                op.Length > 0 ? " · " + op : "", ms),
                            summary.Length > 0 ? summary : "(没有摘要)"));
                    }
                }
                d.TraceRows.Add(("总耗时", d.TotalMs + " ms"));
            }

            d.FinalPng2 = Path.Combine(dir, "final.png");
            return d;
        }

        private static JObject ReadJson(string path)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        private static string ReadFile(string path, int max)
        {
            try
            {
                if (!File.Exists(path)) return "（没有该文件）";
                string s = File.ReadAllText(path);
                return s.Length <= max ? s : s.Substring(0, max) + "\n…（已截断）";
            }
            catch (Exception ex) { return "（读取失败：" + ex.Message + "）"; }
        }

        private void Open(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Ui.Notice(ex.Message, "打不开目录", true);
            }
        }
    }

    /// <summary>列表里的一条运行记录（ListBox 数据项）</summary>
    public sealed class HistoryRecord
    {
        public string Title { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Dir { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>选中记录的详情数据（View 依据它构建详情区 UI）</summary>
    public sealed class HistoryDetail
    {
        public string Dir { get; set; } = "";
        public string Verdict { get; set; } = "";
        public bool Ok { get; set; }
        public string FinalPng { get; set; } = "";     // 小图（详情顶部预览）
        public string FinalPng2 { get; set; } = "";    // 大图（详情底部）
        public string EventJson { get; set; } = "";
        public string TraceSummary { get; set; } = "";
        public List<(string name, string value)> TraceRows { get; } = new();
        public long TotalMs { get; set; }
    }
}
