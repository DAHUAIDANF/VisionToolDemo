using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 表达式：算一个值并写进全局变量（也写进节点输出，供后面的节点引用）。
    ///
    /// 字符串槽：0=表达式（多行）  1=结果变量名（留空则只记日志，便于调试表达式）
    ///
    /// 例子：
    ///   {目标中心X} + 20                     —— 点击位置往右挪 20 像素
    ///   {中心X} + "," + {中心Y}               —— 拼成 "420,285" 喂给点击节点
    ///   拼接("订单 ", {订单号}, " 已登记")     —— 拼要输入的文本
    ///   如果({得分} &gt; 0.9, "OK", "NG")           —— 三元判断
    ///   正则({识别结果}, "\d{4,6}")            —— 从识别结果里抠出数字
    ///
    /// 语法/函数清单见 ExpressionEvaluator；出错时跳过并说明原因（不会静默）。 </summary>
    public class ExpressionTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "表达式";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        /// <summary>本次算出来的值</summary>
        public string Result { get; private set; } = "";
        public string VarName { get; private set; } = "";
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "出错时 0跳过1中止本轮", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "出错:{0}", Group = "表达式",
                Tip = "表达式写错（变量不存在、除数为 0、括号不配对…）时怎么办：\n" +
                      "0 = 跳过这一步继续跑（默认，配合日志查错）；1 = 直接中止整轮。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Result = "";
            VarName = "";
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            bool abortOnError = paramValues.Length > 0 && paramValues[0] == 1;
            string expr = (NodeStringProvider?.Invoke(0) ?? "").Trim();
            string varName = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? "").Trim());

            if (expr.Length == 0)
            {
                Skipped = true;
                LastSummary = "表达式: 跳过 —— 表达式为空（节点属性 → 输入内容 → 表达式）";
                return dst;
            }

            try
            {
                Result = Automation.ExpressionEvaluator.EvaluateToString(expr);
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = string.Format("表达式: 失败 —— {0}（表达式: {1}）", ex.Message, OneLine(expr));
                if (abortOnError) throw new InvalidOperationException(LastSummary, ex);
                return dst;
            }

            if (varName.Length > 0)
            {
                Automation.AutomationContext.SetVariable(varName, Result);
                VarName = varName;
                LastSummary = string.Format("表达式: {0} = \"{1}\"   （= {2}）{3}",
                    varName, Result, OneLine(expr),
                    Automation.AutomationContext.DryRun ? "   （干跑，变量照记）" : "");
            }
            else
            {
                LastSummary = string.Format("表达式: {0} = \"{1}\"（未指定变量名，只记日志）", OneLine(expr), Result);
            }
            return dst;
        }

        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\n", " ").Replace("\r", " ");
            return t.Length <= 100 ? t : t.Substring(0, 100) + "…";
        }
    }
}
