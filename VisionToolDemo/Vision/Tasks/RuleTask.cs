using System;
using System.Globalization;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 规则判定（一条规则的独立节点）。
    ///
    /// 为什么把"判定"单独做成节点，而不是让它散在各算法节点里：
    /// 质检规则会随产品/工艺变，而算法流程不变。规则独立出来以后，
    /// 换产品只要改规则参数，不用动流程；而且每条规则的通过/不通过连同**原因**
    /// 会被记进本轮明细，NG 时能直接说清是哪一条不满足。
    ///
    /// 字符串槽：0 变量名   1 期望值/下限   2 上限（数值范围用）   3 结果变量名（可选）
    /// 结果：节点输出「是否存在(1/0)」= 通过与否，文字结果 = 判定说明；同时记入本轮明细。
    /// </summary>
    public class RuleTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "规则判定";

        public string LastSummary { get; private set; } = "";
        public Func<int, string> NodeStringProvider { get; set; }

        public bool Passed { get; private set; }

        /// <summary>本次产生的规则明细（运行器补上节点 id，便于"是哪条节点判的"追溯）</summary>
        public Automation.RuleResult LastRule { get; private set; }
        public string Reason { get; private set; } = "";
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "规则 0数值范围1等于2包含3非空4下限", Min = 0, Max = 4, DefaultValue = 0,
                DisplayFormat = "规则:{0}", Group = "判定",
                Tip = "0 数值范围：下限 ≤ 变量值 ≤ 上限（测量尺寸的都用它）；\n" +
                      "1 等于：变量值 == 期望值（文本或数字都行）；\n" +
                      "2 包含：变量值里包含期望文本（例如识别结果里有\"OK\"）；\n" +
                      "3 非空：变量有值即通过（防呆：OCR 什么都没识别出来算 NG）；\n" +
                      "4 下限：变量值 ≥ 期望值（置信度/得分/面积这类\"至少多少\"用）。" },
            new TaskParamDesc { ParamName = "不通过时 0继续1中止本轮", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "不通过:{0}", Group = "判定",
                Tip = "0 = 继续跑完其余规则（这样才能一次拿到完整明细）；\n" +
                      "1 = 直接中止本轮（严重不合格时不浪费时间）。" },
            new TaskParamDesc { ParamName = "数值容差(×0.001)", Min = 0, Max = 1000, DefaultValue = 0,
                DisplayFormat = "容差:{0}", Group = "判定",
                Tip = "比较数值时允许的误差（单位 0.001）。例如填 2 表示 ±0.002。\n" +
                      "浮点测量值建议给一点容差，避免\"明明合格却判 NG\"。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Passed = false;
            Reason = "";
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int rule = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 0, 0, 4);
            bool abortIfFail = paramValues.Length > 1 && paramValues[1] == 1;
            double tolerance = (paramValues.Length > 2 ? paramValues[2] : 0) * 0.001;

            string varName = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(0) ?? "").Trim());
            string expect = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? "").Trim());
            string upper = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(2) ?? "").Trim());
            string outVar = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(3) ?? "").Trim());

            string[] ruleNames = ["数值范围", "等于", "包含", "非空", "下限"];
            string name = ruleNames[rule];

            if (varName.Length == 0)
            {
                Skipped = true;
                LastSummary = "规则判定: 跳过 —— 没填变量名（节点属性 → 变量名）";
                return dst;
            }

            bool have = Automation.AutomationContext.TryResolveVariable(varName, out string actual);
            actual ??= "";

            bool ok;
            switch (rule)
            {
                case 3:
                    ok = have && actual.Trim().Length > 0;
                    Reason = string.Format("{0} {1} \"{2}\"", varName, ok ? "有值" : "为空"
                        , Trim(actual));
                    break;
                case 1:
                    ok = have && CompareText(actual, expect, tolerance);
                    Reason = string.Format("{0}=\"{1}\" {2} \"{3}\"", varName, Trim(actual),
                        ok ? "等于" : "不等于", expect);
                    break;
                case 2:
                    ok = have && actual.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0;
                    Reason = string.Format("{0}=\"{1}\" {2} 包含 \"{3}\"", varName, Trim(actual),
                        ok ? "" : "不", expect);
                    break;
                case 4:
                    {
                        // 注意：out 变量必须先赋值 —— 上面的 have && 会短路，v 可能压根没被赋值（CS0165）
                        double v = 0;
                        bool haveNum = have && double.TryParse(actual.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v);
                        bool haveExp = double.TryParse(expect.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double e);
                        ok = haveNum && haveExp && v >= e - tolerance;
                        Reason = string.Format("{0}={1} 下限 {2} → {3}", varName, Trim(actual), expect,
                            ok ? "通过" : "不通过");
                        break;
                    }
                default:
                    {
                        double v = 0;      // 同 case 4：短路时 out 不会被赋值
                        bool haveNum = have && double.TryParse(actual.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v);
                        bool haveLow = double.TryParse(expect.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double low);
                        bool hasUpper = upper.Trim().Length > 0;
                        double high = double.NaN;
                        bool haveHigh = !hasUpper || double.TryParse(upper.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out high);
                        bool lowOk = !haveLow || (haveNum && v >= low - tolerance);
                        bool highOk = !hasUpper || (haveNum && v <= high + tolerance);
                        ok = haveNum && lowOk && highOk;
                        Reason = string.Format("{0}={1} 范围 [{2}, {3}] → {4}", varName, Trim(actual),
                            expect.Length == 0 ? "-∞" : expect, hasUpper ? upper : "+∞",
                            ok ? "通过" : "不通过");
                        break;
                    }
            }

            Passed = ok;
            LastRule = new Automation.RuleResult
            {
                Name = varName + "(" + name + ")",
                Ok = ok,
                Reason = Reason,
            };
            Automation.AutomationContext.RuleResults.Add(LastRule);

            if (outVar.Length > 0)
                Automation.AutomationContext.SetVariable(outVar, ok ? "OK" : "NG");

            LastSummary = string.Format("规则判定: {0}{1} —— {2}", ok ? "通过" : "不通过", "「" + name + "」", Reason);
            if (!ok && abortIfFail)
                throw new InvalidOperationException("规则判定不通过并设置为中止本轮：" + Reason);
            return dst;
        }

        private static bool CompareText(string actual, string expect, double tolerance)
        {
            if (double.TryParse(actual.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && double.TryParse(expect.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double e))
                return Math.Abs(a - e) <= tolerance;
            return string.Equals(actual.Trim(), expect.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Trim(string s)
        {
            s ??= "";
            s = s.Replace("\n", " ").Trim();
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }
    }
}
