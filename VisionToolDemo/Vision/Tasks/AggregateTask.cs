using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 结果聚合：把本轮所有"规则判定"的结果与"被跳过的动作"汇总成**一个总判定**。
    ///
    /// 这是质检闭环的最后一环：单条规则节点各自判各自的，最终"这一件是 OK 还是 NG"、
    /// "NG 是因为哪几条"必须有一个权威结论，才能拿去留档 / 输出信号 / 上报。
    ///
    /// 字符串槽：0 总判定变量名（默认「总判定」）  1 明细变量名（默认「判定明细」）
    /// 结果：节点输出「是否存在(1/0)」= 是否 OK，文字结果 = 明细（每条一行）。
    /// </summary>
    public class AggregateTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "结果聚合";

        public string LastSummary { get; private set; } = "";
        public Func<int, string> NodeStringProvider { get; set; }

        public string Verdict { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public int RuleCount { get; private set; }
        public int FailedCount { get; private set; }
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "计入跳过的动作 0否1是", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "计跳过:{0}", Group = "判定",
                Tip = "1（默认）= 本轮有动作被跳过（没找到目标/超时/命令失败）也算 NG。\n" +
                      "0 = 只看规则节点的判定结果。" },
            new TaskParamDesc { ParamName = "没有规则时 0判OK1判NG", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "无规则:{0}", Group = "判定",
                Tip = "一条规则都没跑到时怎么判。默认 1（判 NG）—— 「什么都没检」比「检了没问题」危险得多。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Verdict = "";
            Detail = "";
            RuleCount = 0;
            FailedCount = 0;
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            bool countSkips = paramValues.Length == 0 || paramValues[0] == 1;
            bool ngWhenNoRule = paramValues.Length > 1 && paramValues[1] == 1;

            string verdictVar = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(0) ?? "").Trim());
            string detailVar = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? "").Trim());
            if (verdictVar.Length == 0) verdictVar = "总判定";
            if (detailVar.Length == 0) detailVar = "判定明细";

            var rules = Automation.AutomationContext.RuleResults;
            RuleCount = rules.Count;
            FailedCount = rules.Count(r => !r.Ok);

            var sb = new StringBuilder();
            foreach (var r in rules)
                sb.AppendLine((r.Ok ? "OK  " : "NG  ") + r.Name + "  " + r.Reason);

            int skipped = 0;
            bool hasSkipInfo = false;
            // 跳过数由"运行器"统计，聚合节点读不到正在进行的 result；
            // 因此这里用运行上下文里的计数快照（动作算子每次跳过都会累加）
            skipped = Automation.AutomationContext.SkippedActionCount;
            hasSkipInfo = skipped > 0;
            if (countSkips && hasSkipInfo)
                sb.AppendLine("NG  有 " + skipped + " 个动作被跳过（未执行/未命中/超时）");

            bool ok;
            if (RuleCount == 0 && !hasSkipInfo)
                ok = !ngWhenNoRule;
            else
                ok = FailedCount == 0 && !(countSkips && hasSkipInfo);

            Verdict = ok ? "OK" : "NG";
            Detail = sb.ToString().TrimEnd();

            Automation.AutomationContext.FinalVerdict = Verdict;
            Automation.AutomationContext.SetVariable(verdictVar, Verdict);
            Automation.AutomationContext.SetVariable(detailVar, Detail);

            LastSummary = string.Format("结果聚合: 总判定 {0}（规则 {1} 条，不通过 {2} 条{3}）→ {4} / {5}",
                Verdict, RuleCount, FailedCount,
                skipped > 0 ? "，跳过 " + skipped + " 个动作" : "", verdictVar, detailVar);
            return dst;
        }
    }
}
