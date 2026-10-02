using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>一条运行记录的概要（界面列表用）</summary>
    public sealed class RunRecordInfo
    {
        public string Dir = "";
        public DateTime Time;
        public string Verdict = "";
        public bool Ok;
        public int Steps;
        public int Skipped;
        public long TotalMs;
        public string Error = "";
    }

    /// <summary>
    /// 运行记录：每跑一轮就在 程序目录\运行记录\yyyyMMdd_HHmmss\ 下留档
    ///   trace.json  每个节点用了多久、参数是什么、结果摘要（追溯"这一轮到底发生了什么"）
    ///   event.json  总判定 OK/NG、总耗时、步数/动作数/跳过数、异常（给看板/外部系统读）
    ///   final.png   最终结果图（有图像就存，便于事后复核）
    ///
    /// 为什么要有它：工业现场出问题时，"当时那张图长什么样、每个节点花了多久、参数是多少"
    /// 往往比"现在再跑一遍"更有用；参考项目把 trace/event/artifact 作为一等输出，这里照做。
    /// </summary>
    public static class RunRecorder
    {
        /// <summary>记录根目录（与"截图"目录同级，放在程序目录下，避免污染用户桌面）</summary>
        public static string RootDir
            => string.IsNullOrEmpty(RootDirOverride) ? Path.Combine(AppContext.BaseDirectory, "运行记录") : RootDirOverride;

        /// <summary>测试/临时用：把记录目录指到别处（空 = 用默认）</summary>
        public static string RootDirOverride { get; set; }

        /// <summary>本轮记录的目录（没开始记录时为 null）</summary>
        public static string CurrentDir { get; private set; }

        /// <summary>创建本轮目录并返回路径</summary>
        public static string Begin(DateTime? when = null)
        {
            var t = when ?? DateTime.Now;
            string dir;
            int n = 0;
            do
            {
                string suffix = n == 0 ? "" : "_" + n.ToString(CultureInfo.InvariantCulture);
                dir = Path.Combine(RootDir, t.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + suffix);
                n++;
            } while (Directory.Exists(dir) && n < 1000);

            Directory.CreateDirectory(dir);
            CurrentDir = dir;
            return dir;
        }

        /// <summary>写入 trace/event/final 图；返回目录（失败不抛，留档失败不该影响生产）</summary>
        public static string Save(AutomationGraph graph, AutomationRunResult result,
            IEnumerable<string> log, double mmPerPixel = 0)
        {
            try
            {
                string dir = CurrentDir ?? Begin();

                // 本轮运行时的图快照：改坏了图也能从任一历史轮次找回
                File.WriteAllText(Path.Combine(dir, "graph.json"),
                    AutomationGraphIO.Export(graph), new UTF8Encoding(false));

                var nodeRows = new List<object>();
                foreach (var n in graph.Nodes)
                {
                    result.NodeTimes.TryGetValue(n.Id, out long ms);
                    result.NodeSummaries.TryGetValue(n.Id, out string summary);
                    nodeRows.Add(new
                    {
                        id = n.Id,
                        kind = n.Kind.ToString(),
                        title = AutoNodeInfo.Title(n.Kind),
                        op = n.OpName ?? "",
                        enabled = n.Enabled,
                        ms,
                        next = n.NextId,
                        alt_next = n.AltNextId,
                        summary = summary ?? "",
                        text = graph.GetNodeText(n.Id),
                        key = graph.GetNodeKey(n.Id),
                        extra = Enumerable.Range(2, 4)
                            .Select(s => graph.GetNodeString(n.Id, s))
                            .Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                        @params = n.Params,
                    });
                }

                var trace = new
                {
                    started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    dry_run = AutomationContext.DryRun,
                    mm_per_pixel = mmPerPixel,
                    total_ms = result.TotalMs,
                    nodes = nodeRows,
                    log = log == null ? Array.Empty<string>() : log.ToArray(),
                };
                File.WriteAllText(Path.Combine(dir, "trace.json"),
                    JsonConvert.SerializeObject(trace, Formatting.Indented));

                var ev = new
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ok = result.Ok,
                    verdict = AutomationContext.FinalVerdict ?? "",
                    error = result.Error ?? "",
                    steps = result.Steps,
                    actions = result.Actions,
                    skipped = result.SkippedActions,
                    total_ms = result.TotalMs,
                    actually_executed = result.ActuallyExecuted,
                    dry_run = AutomationContext.DryRun,
                    variables = AutomationContext.Variables.ToDictionary(kv => kv.Key, kv => kv.Value),
                    rules = AutomationContext.RuleResults
                        .Select(r => new { name = r.Name, ok = r.Ok, reason = r.Reason }).ToArray(),
                };
                File.WriteAllText(Path.Combine(dir, "event.json"),
                    JsonConvert.SerializeObject(ev, Formatting.Indented));

                var final = result.FinalImage;
                if (final != null && !final.Empty())
                    Cv2.ImWrite(Path.Combine(dir, "final.png"), final);

                // 节点级归档（数据 + 图片）：
                //   captures\node{id}_{节点名}.png   每个节点的结果图（result.NodeImages）
                //   result.csv                       各节点结果一行，Excel/记事本可直接打开
                //   report.html                      自包含可读报告，浏览器打开（引用 captures/ 与 final.png）
                // 全部单独 try/catch：留档失败不影响本轮运行结果。
                var captures = SaveNodeCaptures(dir, graph, result);
                try { WriteResultCsv(dir, graph, result); } catch { }
                try { WriteReportHtml(dir, graph, result, captures); } catch { }

                return dir;
            }
            catch
            {
                return CurrentDir ?? "";
            }
        }

        // ================================================================ 节点级归档

        /// <summary>把每个动作节点的结果图存到 captures\，返回 节点id → 文件名（失败返回空表）</summary>
        private static Dictionary<int, string> SaveNodeCaptures(string dir, AutomationGraph graph, AutomationRunResult result)
        {
            var map = new Dictionary<int, string>();
            try
            {
                string capDir = Path.Combine(dir, "captures");
                Directory.CreateDirectory(capDir);
                foreach (var kv in result.NodeImages)
                {
                    if (kv.Value == null || kv.Value.Empty()) continue;
                    var n = graph.Get(kv.Key);
                    string title = n == null ? "node" + kv.Key : AutoNodeInfo.Title(n.Kind);
                    string name = "node" + kv.Key + "_" + SanitizeFile(title) + ".png";
                    if (Cv2.ImWrite(Path.Combine(capDir, name), kv.Value))
                        map[kv.Key] = name;
                }
            }
            catch { }
            return map;
        }

        /// <summary>节点结果汇总表（result.csv）：Excel/记事本可直接打开；每节点一行</summary>
        private static void WriteResultCsv(string dir, AutomationGraph graph, AutomationRunResult result)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("id,节点,耗时ms,结果摘要,文字结果,目标中心,得分,命中倍率,存图路径,命令退出码,命令输出,是否存在");
            foreach (var n in graph.Nodes)
            {
                result.NodeTimes.TryGetValue(n.Id, out long ms);
                result.NodeSummaries.TryGetValue(n.Id, out string summary);
                AutomationContext.NodeOutputs.TryGetValue(n.Id, out var o);
                string center = o != null && o.HasTarget
                    ? o.CenterX.ToString("F0", inv) + "," + o.CenterY.ToString("F0", inv) : "";
                string score = o != null && o.HasTarget ? o.Score.ToString("F3", inv) : "";
                string scale = o != null && o.Scale > 0 ? o.Scale.ToString(inv) : "";
                string exit = o != null && o.ExitCode >= 0 ? o.ExitCode.ToString(inv) : "";
                sb.AppendLine(string.Join(",",
                    n.Id.ToString(inv),
                    Csv(AutoNodeInfo.Title(n.Kind)),
                    ms.ToString(inv),
                    Csv(summary ?? ""),
                    Csv(o?.Text ?? ""),
                    Csv(center),
                    score,
                    scale,
                    Csv(o?.SavedPath ?? ""),
                    exit,
                    Csv(o?.Stdout ?? ""),
                    Csv(o?.BoolResult ?? "")));
            }
            File.WriteAllText(Path.Combine(dir, "result.csv"), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>自包含可读报告（report.html）：浏览器直接打开，图片用相对路径引用本轮 captures\ 与 final.png</summary>
        private static void WriteReportHtml(string dir, AutomationGraph graph, AutomationRunResult result,
            Dictionary<int, string> captures)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>运行报告</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:'Microsoft YaHei UI',Segoe UI,sans-serif;background:#1A1E24;color:#F6F9FD;margin:0;padding:24px;}");
            sb.AppendLine("h1{font-size:20px;margin:0 0 4px;}h2{font-size:15px;margin:22px 0 8px;color:#CBD4E0;}");
            sb.AppendLine(".sub{color:#B4BFCE;margin-bottom:16px;font-size:13px;}");
            sb.AppendLine(".cards{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:8px;}");
            sb.AppendLine(".card{background:#1F242C;border:1px solid #4A5666;border-radius:8px;padding:8px 16px;min-width:110px;}");
            sb.AppendLine(".card b{display:block;font-size:20px;}.card span{color:#B4BFCE;font-size:12px;}");
            sb.AppendLine(".ok{color:#45DEA8;}.ng{color:#FF8A8A;}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;background:#1F242C;font-size:13px;}");
            sb.AppendLine("th,td{border:1px solid #333C4B;padding:6px 10px;text-align:left;vertical-align:top;}");
            sb.AppendLine("th{background:#333C4B;color:#F6F9FD;}td img{max-height:150px;border-radius:4px;border:1px solid #4A5666;}");
            sb.AppendLine(".err{background:#2A1F1F;border:1px solid #FF8A8A;color:#FFB4B4;border-radius:8px;padding:10px 14px;margin:14px 0;white-space:pre-wrap;}");
            sb.AppendLine("</style></head><body>");

            string verdict = AutomationContext.FinalVerdict ?? (result.Ok ? "OK" : "NG");
            sb.Append("<h1>运行报告</h1>");
            sb.Append("<div class=\"sub\">" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv)
                + "  ·  干跑=" + (AutomationContext.DryRun ? "是" : "否") + "</div>");

            sb.Append("<div class=\"cards\">");
            sb.Append("<div class=\"card\"><b class=\"" + (result.Ok ? "ok" : "ng") + "\">" + Html(verdict) + "</b><span>判定</span></div>");
            sb.Append("<div class=\"card\"><b>" + result.TotalMs + " ms</b><span>总耗时</span></div>");
            sb.Append("<div class=\"card\"><b>" + result.Steps + "</b><span>执行步数</span></div>");
            sb.Append("<div class=\"card\"><b>" + result.Actions + "</b><span>动作数</span></div>");
            sb.Append("<div class=\"card\"><b>" + result.SkippedActions + "</b><span>跳过动作</span></div>");
            sb.Append("</div>");

            if (!string.IsNullOrEmpty(result.Error))
                sb.Append("<div class=\"err\">失败：" + Html(result.Error) + "</div>");

            sb.Append("<h2>最终结果图</h2>");
            if (result.FinalImage != null && !result.FinalImage.Empty())
                sb.Append("<img src=\"final.png\" alt=\"final.png\" style=\"max-height:220px;border-radius:4px;border:1px solid #4A5666;\">");
            else
                sb.Append("<p style=\"color:#B4BFCE;\">（本轮没有图像结果）</p>");

            sb.Append("<h2>逐节点明细</h2><table><tr>");
            sb.Append("<th>#</th><th>节点</th><th>耗时</th><th>结果摘要</th><th>文字结果</th><th>目标中心 / 得分</th><th>存图</th><th>节点图</th></tr>");
            foreach (var n in graph.Nodes)
            {
                result.NodeTimes.TryGetValue(n.Id, out long ms);
                result.NodeSummaries.TryGetValue(n.Id, out string summary);
                AutomationContext.NodeOutputs.TryGetValue(n.Id, out var o);
                string center = "";
                if (o != null && o.HasTarget)
                    center = o.CenterX.ToString("F0", inv) + "," + o.CenterY.ToString("F0", inv)
                        + (o.Score > 0 ? "  （得分 " + o.Score.ToString("F3", inv) + "）" : "");
                string saved = o != null && o.SavedPath.Length > 0 ? Html(Path.GetFileName(o.SavedPath)) : "";
                string img = captures.TryGetValue(n.Id, out var cap) ? "<img src=\"captures/" + Html(cap) + "\" alt=\"\">" : "";
                sb.Append("<tr><td>" + n.Id + "</td><td>" + Html(AutoNodeInfo.Title(n.Kind))
                    + (n.Enabled ? "" : " <span style=\"color:#FFC94A;\">（停用）</span>")
                    + "</td><td>" + ms + " ms</td><td>" + Html(summary ?? "")
                    + "</td><td>" + Html(o?.Text ?? "") + "</td><td>" + Html(center)
                    + "</td><td>" + saved + "</td><td>" + img + "</td></tr>");
            }
            sb.Append("</table></body></html>");
            File.WriteAllText(Path.Combine(dir, "report.html"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string SanitizeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var bad = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        /// <summary>
        /// 快速列出运行记录：**只读目录名与时间，不解析 event.json**。
        /// 界面"每跑完一轮就刷新列表"用它 —— 解析几十个 JSON 放在 UI 线程上是会卡的。
        /// </summary>
        public static List<RunRecordInfo> ListFast(int max = 200)
        {
            var list = new List<RunRecordInfo>();
            try
            {
                if (!Directory.Exists(RootDir)) return list;
                foreach (string dir in Directory.GetDirectories(RootDir)
                             .OrderByDescending(d => d, StringComparer.Ordinal).Take(max))
                {
                    list.Add(new RunRecordInfo
                    {
                        Dir = dir,
                        Time = Directory.GetCreationTime(dir),
                        Ok = true,
                        Verdict = "",
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>列出所有运行记录并解析 event.json（新的在前）—— 较慢，放后台线程用</summary>
        public static List<RunRecordInfo> List(int max = 200)
        {
            var list = new List<RunRecordInfo>();
            try
            {
                if (!Directory.Exists(RootDir)) return list;
                foreach (string dir in Directory.GetDirectories(RootDir)
                             .OrderByDescending(d => d, StringComparer.Ordinal).Take(max))
                {
                    var info = new RunRecordInfo { Dir = dir };
                    info.Time = Directory.GetCreationTime(dir);
                    string ev = Path.Combine(dir, "event.json");
                    if (File.Exists(ev))
                    {
                        try
                        {
                            var o = JObject.Parse(File.ReadAllText(ev));
                            info.Ok = o.Value<bool?>("ok") ?? false;
                            info.Verdict = o.Value<string>("verdict") ?? "";
                            info.Steps = o.Value<int?>("steps") ?? 0;
                            info.Skipped = o.Value<int?>("skipped") ?? 0;
                            info.TotalMs = o.Value<long?>("total_ms") ?? 0;
                            info.Error = o.Value<string>("error") ?? "";
                        }
                        catch { /* 记录坏了也要能列出来 */ }
                    }
                    list.Add(info);
                }
            }
            catch { /* 列不出来就返回已有的 */ }
            return list;
        }
    }
}
