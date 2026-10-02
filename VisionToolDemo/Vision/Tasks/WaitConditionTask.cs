using System;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 等待条件：轮询到条件满足为止（或超时）。
    ///
    /// 自动化里"等界面反应过来"比"睡固定时间"稳得多：睡 2 秒可能不够、也可能白等 2 秒。
    /// 三种条件：
    ///   · 变量比较：某个全局变量 等于/包含/大于… 期望值（配合循环或外部程序写变量）；
    ///   · 模板命中：**自己截图**并在图里找该节点的模板（"等出现成功提示"最常用）；
    ///   · 窗口出现：按标题找窗口（"等目标程序启动起来"）。
    ///
    /// 字符串槽：0=变量名（条件=变量比较）  1=比较值  2=窗口标题（条件=窗口出现）
    /// 模板命中用的是**该节点自己导入的模板图**（节点属性 → 模板图），没导入会用页面顶部的默认模板。
    ///
    /// 注意：干跑时**也会真的等**（它只是在看，不产生任何输入），所以调试时把超时设小一点。
    /// </summary>
    public class WaitConditionTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "等待条件";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        /// <summary>该节点自己的模板（运行器按节点注入；为空时用页面顶部的默认模板）</summary>
        public Mat TemplateMat { get; set; }

        public bool Skipped { get; private set; }
        public bool Satisfied { get; private set; }
        public int Polls { get; private set; }
        public long ElapsedMs { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "条件 0变量比较1模板命中2窗口出现", Min = 0, Max = 2, DefaultValue = 1,
                DisplayFormat = "条件:{0}", Group = "等待",
                Tip = "1 模板命中（默认）：自己截图并在图里找模板 —— “等界面出现成功提示”用这个；\n" +
                      "0 变量比较：等某个全局变量的值满足条件（变量得由别的节点/程序改写）；\n" +
                      "2 窗口出现：按标题等一个窗口出现（“等程序启动起来”）。" },
            new TaskParamDesc { ParamName = "比较 0等于1不等于2包含3不包含4大于5小于6非空", Min = 0, Max = 6, DefaultValue = 2,
                DisplayFormat = "比较:{0}", Group = "等待",
                Tip = "条件=变量比较时怎么比（两边都是数字时按数值比）。" },
            new TaskParamDesc { ParamName = "超时秒 0=不限", Min = 0, Max = 3600, DefaultValue = 30,
                DisplayFormat = "超时:{0}s", Group = "等待",
                Tip = "等不到就超时。0 = 一直等（慎用）。" },
            new TaskParamDesc { ParamName = "轮询间隔ms", Min = 100, Max = 10000, DefaultValue = 500,
                DisplayFormat = "间隔:{0}ms", Group = "等待",
                Tip = "每隔多久检查一次。太密会吃 CPU（模板匹配每次都要抓屏+匹配），100~1000 比较合适。" },
            new TaskParamDesc { ParamName = "截图区域 0全屏1主屏", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "区域:{0}", Group = "等待",
                Tip = "条件=模板命中时抓哪里：1 主屏（默认，快）/ 0 整个虚拟桌面。" },
            new TaskParamDesc { ParamName = "匹配阈值%", Min = 50, Max = 100, DefaultValue = 80,
                DisplayFormat = "阈值:{0}%", Group = "等待",
                Tip = "条件=模板命中时的判定阈值。等「成功提示」这类固定界面 80 够用；\n" +
                      "界面有缩放/渲染差异时调到 70 左右。" },
            new TaskParamDesc { ParamName = "超时后 0跳过1中止本轮", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "超时后:{0}", Group = "等待",
                Tip = "0 = 超时后跳过这一步继续跑；1 = 直接中止整轮（默认：等不到就别往下点了，免得在错状态下乱操作）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Skipped = false;
            Satisfied = false;
            Polls = 0;
            ElapsedMs = 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int condition = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 1, 0, 2);
            int cmp = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 2, 0, 6);
            int timeoutSec = paramValues.Length > 2 ? Math.Max(0, paramValues[2]) : 30;
            int intervalMs = paramValues.Length > 3 ? Math.Clamp(paramValues[3], 100, 10000) : 500;
            int regionMode = paramValues.Length > 4 ? Math.Clamp(paramValues[4], 0, 1) : 1;
            int threshold = paramValues.Length > 5 ? Math.Clamp(paramValues[5], 50, 100) : 80;
            bool abortOnTimeout = paramValues.Length <= 6 || paramValues[6] == 1;

            string varName = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(0) ?? "").Trim());
            string expect = Automation.AutomationContext.ExpandVariables(NodeStringProvider?.Invoke(1) ?? "");
            string titlePattern = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(2) ?? "").Trim());

            // 条件是否已经成立（每次轮询都要重新判定）
            bool Check(out string detail)
            {
                switch (condition)
                {
                    case 0:
                        {
                            bool have = Automation.AutomationContext.TryResolveVariable(varName, out string actual);
                            detail = string.Format("变量 {0}=\"{1}\" {2} \"{3}\"", varName,
                                have ? actual : "(没有)", Automation.AutomationRunner.CompareName(cmp), expect);
                            return Automation.AutomationRunner.Compare(have, actual, expect, cmp);
                        }
                    case 2:
                        {
                            var win = Automation.WindowHelper.Find(titlePattern);
                            detail = string.Format("窗口 \"{0}\" {1}", titlePattern, win == null ? "还没出现" : "已出现");
                            return win != null;
                        }
                    default:
                        {
                            if (TemplateMat == null || TemplateMat.Empty())
                            {
                                detail = "没有模板图（节点属性 → 模板图 里导入）";
                                return false;
                            }
                            var region = regionMode == 0 ? Automation.ScreenCapture.VirtualBounds
                                                         : Automation.ScreenCapture.PrimaryBounds;
                            using var scope = Automation.ScreenCapture.SuppressOwnWindows(80);
                            if (!Automation.ScreenCapture.TryCapture(region, out Mat shot, out string err))
                            {
                                detail = "抓屏失败：" + err;
                                return false;
                            }
                            using (shot)
                            {
                                // 复用"模板匹配"算子：参数体系一致，行为也能对上
                                var match = new TemplateMatchTask { TemplateMat = TemplateMat };
                                using var outImg = match.Execute(shot, new[] { threshold, 100, 0, 0, 5 });
                                detail = string.Format("模板 {0}x{1} 最高分 {2:F0}%（阈值 {3}%）",
                                    TemplateMat.Cols, TemplateMat.Rows,
                                    double.IsNaN(match.BestScore) ? 0 : match.BestScore * 100, threshold);
                                return match.Found;
                            }
                        }
                }
            }

            var sw = Stopwatch.StartNew();
            string lastDetail = "";
            while (true)
            {
                Polls++;
                if (Check(out lastDetail)) { Satisfied = true; break; }

                if (timeoutSec > 0 && sw.Elapsed.TotalSeconds >= timeoutSec) break;
                if (timeoutSec <= 0 && Polls > 100000) break;      // 极端兜底，避免真的死等
                Thread.Sleep(intervalMs);
            }
            sw.Stop();
            ElapsedMs = sw.ElapsedMilliseconds;

            if (Satisfied)
            {
                LastSummary = string.Format("等待条件: 第 {0} 次轮询满足（{1}ms）—— {2}", Polls, ElapsedMs, lastDetail);
                return dst;
            }

            Skipped = true;
            LastSummary = string.Format("等待条件: 超时 {0}s（轮询 {1} 次）—— {2}", timeoutSec, Polls, lastDetail);
            if (abortOnTimeout) throw new InvalidOperationException(LastSummary);
            return dst;
        }
    }
}
