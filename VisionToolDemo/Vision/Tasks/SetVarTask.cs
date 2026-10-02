using System;
using System.Globalization;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 全局变量：把"这一轮跑出来的信息"记下来，供后面的节点引用。
    ///
    /// 能记什么：
    ///   · 上一个文字算子（OCR/条码）识别到的文字；
    ///   · 上次匹配到的目标中心坐标 / 得分 / 命中倍率（来自 DetectionStore）；
    ///   · 固定文本；
    ///   · 计数器（在自身基础上 +1，用来给文件命名、计数循环次数）。
    ///
    /// 变量名 = 节点属性里的「文本」框；固定文本 = 「按键 / 组合键」框。
    /// 记录下来的值可以在任何文本里用 {变量名} 引用（键盘输入、弹窗、截图目录…）。
    /// </summary>
    public class SetVarTask : IVisionTask, IResultReporter, Automation.IAutomationNode, Automation.IStringParamTask
    {
        public string TaskName => "全局变量";

        public string LastSummary { get; private set; } = "";

        /// <summary>节点属性里的「文本」框 = 变量名</summary>
        public string NodeText { get; set; } = "";

        /// <summary>节点属性里的「按键 / 组合键」框 = 值来源为"固定文本"时的值</summary>
        public string NodeKey { get; set; } = "";

        public bool Skipped { get; private set; }

        /// <summary>本次写入的变量名与值（界面/测试用）</summary>
        public string VarName { get; private set; } = "";
        public string VarValue { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "值来源 0上节点文字1中心X2中心Y3中心XY4得分5倍率6固定文本7计数器+1 8表达式9初始值",
                Min = 0, Max = 9, DefaultValue = 0,
                DisplayFormat = "来源:{0}", Group = "变量",
                Tip = "**下面的“取值节点”=0（不用）时才看这里**：\n" +
                      "0 上节点文字：OCR/条码算子识别到的文字；\n" +
                      "1/2/3 上次匹配到的目标中心 X / Y / “X,Y”；\n" +
                      "4 上次匹配得分（0~1）；5 上次命中的模板倍率%；\n" +
                      "6 固定文本：值写在“按键 / 组合键”框里（支持 {变量} 插值）；\n" +
                      "7 计数器+1：在该变量原有数值上加一（缺省按 0 算）；\n" +
                      "8 表达式：按表达式求值（支持变量赋值，如 “int n=1; n=n+1;” 或 “{得分}*100”），\n" +
                      "   表达式写在“按键 / 组合键”框里，最后的值就是变量值；\n" +
                      "9 初始值：**只在变量还没有值时写入一次**（配合 7 计数器 / 8 自增表达式做循环计数），\n" +
                      "   值写在“按键 / 组合键”框里。",
            },
            // 指定引用某个节点的输出。参数只能是整数，节点 id 正好是整数，
            // 界面里用下拉框选（见节点页面"取值节点"一栏），不用手打 id。
            new TaskParamDesc
            {
                ParamName = "取值节点id 0=不用", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "取值节点:#{0}", Group = "变量",
                Tip = "不为 0 时：取**这个节点**（按 id）跑完后的结果，忽略上面的“值来源”。\n" +
                      "在节点页面的“取值节点”下拉里选就行，不用自己记 id。\n" +
                      "注意只能引用**排在这之前、已经跑过**的节点。",
            },
            new TaskParamDesc
            {
                ParamName = "取哪一项 见下拉框",
                Min = 0, Max = 15, DefaultValue = 0,
                DisplayFormat = "取第{0}项", Group = "变量",
                Tip = "从被引用节点的输出里取哪一项（节点页面的“取哪一项”下拉里列了名字）。\n" +
                      "常用：1 文字结果（OCR/条码/命令行输出/窗口是否存在）2/3/4 目标中心\n" +
                      "12 命令退出码  13/14 命令标准输出/错误输出  15 是否存在(1/0)\n" +
                      "该节点没有这一项时会跳过并说明原因。",
            },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Skipped = false;
            VarName = "";
            VarValue = "";

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int source = Math.Clamp(paramValues[0], 0, 9);
            // 新增参数：旧图（保存时只有 1 个参数）数组更短，越界读会抛
            int refNodeId = paramValues.Length > 1 ? Math.Max(0, paramValues[1]) : 0;
            int refItem = paramValues.Length > 2 ? Math.Clamp(paramValues[2], 0, 15) : 0;
            string name = Automation.AutomationContext.ExpandVariables(NodeText ?? "").Trim();
            if (name.Length == 0)
            {
                Skipped = true;
                LastSummary = "全局变量: 跳过 —— 没有变量名（在该节点的“节点属性 → 输入内容”的「文本」框里填名字）";
                return dst;
            }
            if (name.IndexOfAny(['{', '}']) >= 0)
            {
                Skipped = true;
                LastSummary = string.Format("全局变量: 跳过 —— 变量名里不能有花括号: {0}", name);
                return dst;
            }

            string value;
            if (refNodeId != 0)
            {
                // 取指定节点的输出（比"上节点文字"更明确：图里可以有多个文字/坐标来源）
                if (!Automation.AutomationContext.TryGetNodeOutput(refNodeId, out var output))
                {
                    Skipped = true;
                    LastSummary = string.Format(
                        "全局变量: 跳过 —— 节点 #{0} 还没有跑过（只能引用排在这之前、已经执行完的节点）", refNodeId);
                    return dst;
                }
                value = output.Get(refItem) ?? "";
                if (value.Length == 0)
                {
                    Skipped = true;
                    LastSummary = string.Format("全局变量: 跳过 —— 节点 #{0} 没有“{1}”这项结果",
                        refNodeId, Automation.NodeOutput.ItemName(refItem));
                    return dst;
                }
                VarName = name;
                VarValue = value;
                Automation.AutomationContext.SetVariable(name, value);
                LastSummary = string.Format("全局变量: {0} = \"{1}\"（取自节点 #{2} 的 {3}）{4}",
                    name, value, refNodeId, Automation.NodeOutput.ItemName(refItem),
                    Automation.AutomationContext.DryRun ? "   （干跑，变量照记）" : "");
                return dst;
            }

            switch (source)
            {
                case 0:
                    value = Automation.AutomationContext.LastText ?? "";
                    if (value.Length == 0)
                    {
                        Skipped = true;
                        LastSummary = "全局变量: 跳过 —— 上一个文字算子（OCR/条码）没有识别到文字";
                        return dst;
                    }
                    break;
                case 1: value = Num(Automation.DetectionStore.Center.X); break;
                case 2: value = Num(Automation.DetectionStore.Center.Y); break;
                case 3: value = Num(Automation.DetectionStore.Center.X) + "," + Num(Automation.DetectionStore.Center.Y); break;
                case 4: value = Automation.DetectionStore.Score.ToString("F3", CultureInfo.InvariantCulture); break;
                case 5: value = Automation.DetectionStore.ScalePercent.ToString(CultureInfo.InvariantCulture); break;
                case 6: value = Automation.AutomationContext.ExpandVariables(NodeKey ?? ""); break;
                case 8:
                    {
                        // 表达式赋值：支持变量赋值语句（"int n=1; n=n+1;"）与 {变量} 引用，
                        // 直接交给求值器（求值器原生支持 {变量}，不用先 ExpandVariables）
                        try { value = Automation.ExpressionEvaluator.EvaluateToString(NodeKey ?? ""); }
                        catch (Exception ex)
                        {
                            Skipped = true;
                            LastSummary = string.Format("全局变量: 跳过 —— 表达式出错: {0}（表达式: {1}）",
                                ex.Message, OneLine(NodeKey ?? ""));
                            return dst;
                        }
                        break;
                    }
                case 9:
                    {
                        // 初始值：只在变量还没有值时写入一次（配合 7 计数器 / 8 自增做循环计数）
                        if (Automation.AutomationContext.TryGetVariable(name, out _))
                        {
                            Skipped = true;
                            LastSummary = string.Format(
                                "全局变量: 跳过 —— {0} 已有值，初始值只在变量没有值时写入一次", name);
                            return dst;
                        }
                        value = Automation.AutomationContext.ExpandVariables(NodeKey ?? "");
                        break;
                    }
                default:
                    {
                        Automation.AutomationContext.TryGetVariable(name, out string old);
                        int cur = 0;
                        if (!string.IsNullOrWhiteSpace(old))
                            int.TryParse(old.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cur);
                        value = (cur + 1).ToString(CultureInfo.InvariantCulture);
                        break;
                    }
            }

            VarName = name;
            VarValue = value;
            Automation.AutomationContext.SetVariable(name, value);
            LastSummary = string.Format("全局变量: {0} = \"{1}\"{2}", name, value,
                Automation.AutomationContext.DryRun ? "   （干跑，变量照记）" : "");
            return dst;
        }

        private static string Num(float v) => Math.Round(v).ToString("F0", CultureInfo.InvariantCulture);

        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\n", " ").Replace("\r", " ");
            return t.Length <= 80 ? t : t.Substring(0, 80) + "…";
        }
    }
}
