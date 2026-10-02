using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 键盘输入：输入文本、发送组合键或单键。
    ///
    /// 内容来源有两条，节点图运行时**节点自带的优先**：
    ///   1. 节点自带（节点图在"节点属性"里填，见 AutomationGraph.NodeTexts / NodeKeys）——
    ///      每个"键盘输入"节点各存一份，彼此独立；
    ///   2. 全局槽（AutomationContext.TextSlots / KeySlots，自动化面板里填）——
    ///      节点没填内容时退回这里，兼容旧图与旧面板。
    ///
    /// 为什么字符串要绕这么一圈：算子参数体系只支持 int[]，
    /// 而 Execute 的签名是 Execute(Mat, int[])，改签名会牵动全部算子。
    /// </summary>
    public class KeyInputTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.IStringParamTask, Automation.IActionStateTask
    {
        public string TaskName => "键盘输入";

        public string LastSummary { get; private set; } = "";

        /// <summary>本轮已发出按键（干跑时表示"本应发出"，实际未操作系统）</summary>
        public bool HasAction { get; private set; }

        /// <summary>是否真的发送了按键。**干跑恒为 false**。</summary>
        public bool ActuallyExecuted => HasAction && !Automation.AutomationContext.DryRun;
        public bool Skipped { get; private set; }

        /// <summary>本次实际发送的内容</summary>
        public string Payload { get; private set; } = "";

        /// <summary>节点属性里的「文本」框（类型 0 用它）</summary>
        public string NodeText { get; set; }

        /// <summary>节点属性里的「按键 / 组合键」框（类型 1/2 用它）</summary>
        public string NodeKey { get; set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "类型 0文本1组合键2单键/序列", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "类型:{0}", Group = "键盘",
                Tip = "0文本：把整段文字打出来（支持中文，不走剪贴板、不依赖输入法）。\n" +
                "1组合键：发送一个组合，写法如 Ctrl+C、Ctrl+Shift+S、Alt+F4。\n" +
                "2单键/序列：按键名依次发送，用空格或逗号分隔，如 Enter、Tab、F5、Left；\n" +
                "   也可以写成序列：Tab Tab Enter、Ctrl+S, Alt+F4（里面的组合仍用 + 写）。" },
            new TaskParamDesc { ParamName = "槽位", Min = 0, Max = 3, DefaultValue = 0,
                DisplayFormat = "槽:{0}", Group = "键盘",
                Tip = "本节点没填内容时取第几个全局槽（自动化面板里填，共 4 个文本槽 + 4 个按键槽）。\n" +
                "在“自动化（节点）”页面里，直接在该节点的“输入内容”里填就能覆盖它。" },
            new TaskParamDesc { ParamName = "动作后等待ms", Min = 0, Max = 10000, DefaultValue = 200,
                DisplayFormat = "等待:{0}ms", Group = "键盘",
                Tip = "发送完成后等待多久。输入较长文本或触发耗时操作时调大。" },
            // 以下是"完善键盘输入"新增的常用能力，一律追加在末尾（参数按索引保存）
            new TaskParamDesc { ParamName = "输入后回车 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "回车:{0}", Group = "键盘",
                Tip = "1 = 发完内容后再按一次 Enter。填完搜索框/命令行直接提交时用。" },
            new TaskParamDesc { ParamName = "先清空字段 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "先清空:{0}", Group = "键盘",
                Tip = "1 = 先 Ctrl+A 再 Delete，把输入框里原有内容清掉再输入（覆盖式填写）。" },
            new TaskParamDesc { ParamName = "每键间隔ms", Min = 0, Max = 2000, DefaultValue = 0,
                DisplayFormat = "键间隔:{0}ms", Group = "键盘",
                Tip = "每个字符/每次按键之间额外等待多久。0 = 尽快发。\n" +
                "有些程序会丢太快的合成输入（尤其远程桌面），填 20~50 通常就稳了。" },
            new TaskParamDesc { ParamName = "重复次数", Min = 1, Max = 100, DefaultValue = 1,
                DisplayFormat = "重复:{0}", Group = "键盘",
                Tip = "整套动作（清空+内容+回车）重复几遍。下拉列表里翻页/连按时用。" },
            new TaskParamDesc { ParamName = "取全局变量 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "取变量:{0}", Group = "键盘",
                Tip = "1 = 把「文本」框（类型0）或「按键 / 组合键」框（类型1/2）里填的**当作变量名**，\n" +
                "发送这个变量的值。适合把 OCR 识别到的、或前一个节点算出来的内容原样打出去。\n" +
                "变量不存在时跳过并说明（不会打出一串花括号）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            HasAction = false;
            Skipped = false;
            Payload = "";

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int type = Math.Clamp(paramValues[0], 0, 2);
            int slot = Math.Clamp(paramValues[1], 0, 3);
            int waitMs = Math.Max(0, paramValues[2]);
            // 新增参数：旧图（保存时只有 3 个参数）数组更短，越界读会抛
            int enterAfter = paramValues.Length > 3 ? Math.Clamp(paramValues[3], 0, 1) : 0;
            int clearFirst = paramValues.Length > 4 ? Math.Clamp(paramValues[4], 0, 1) : 0;
            int keyDelay = paramValues.Length > 5 ? Math.Clamp(paramValues[5], 0, 2000) : 0;
            int repeat = paramValues.Length > 6 ? Math.Clamp(paramValues[6], 1, 100) : 1;
            int useVar = paramValues.Length > 7 ? Math.Clamp(paramValues[7], 0, 1) : 0;

            // 节点自带的优先，没填才用全局槽；内容里的 {变量} 会先展开
            string own = type == 0 ? NodeText : NodeKey;
            bool fromNode = !string.IsNullOrEmpty(own);
            string content;
            if (useVar == 1)
            {
                // 整个内容取自全局变量（框里填的是变量名）
                if (!Automation.AutomationContext.TryResolveVariable(own, out content))
                {
                    Skipped = true;
                    LastSummary = string.IsNullOrWhiteSpace(own)
                        ? "键盘输入: 跳过 —— 开了“取全局变量”，但没填变量名"
                        : string.Format("键盘输入: 跳过 —— 找不到变量 {0}（这一轮还没有节点写过它?）", own.Trim());
                    return dst;
                }
            }
            else
            {
                content = Automation.AutomationContext.ExpandVariables(fromNode
                    ? own
                    : (type == 0 ? Automation.AutomationContext.TextSlots[slot]
                                 : Automation.AutomationContext.KeySlots[slot]));
            }

            if (string.IsNullOrEmpty(content))
            {
                Skipped = true;
                LastSummary = string.Format(
                    "键盘输入: 跳过 —— 没有内容。请在“节点属性 → 输入内容（此节点专用）”里填写；" +
                    "或者退回全局：在自动化面板里填{0}槽{1}",
                    type == 0 ? "文本" : "按键", slot);
                return dst;
            }

            Payload = content;
            var sim = Automation.AutomationContext.Input;
            int savedDelay = sim.TypeDelayMs;
            try
            {
                sim.TypeDelayMs = keyDelay;
                for (int round = 0; round < repeat; round++)
                {
                    // 覆盖式填写：先把输入框里原有的内容全选删掉
                    if (clearFirst == 1) { sim.SendCombo("Ctrl+A"); sim.PressKey("Delete"); }

                    if (type == 0) sim.TypeText(content);
                    else if (type == 1) sim.SendCombo(content);
                    else SendKeySequence(sim, content, keyDelay);

                    if (enterAfter == 1) sim.PressKey("Enter");

                    // 轮与轮之间也留一点间隔，否则连按会被某些程序合并成一次
                    if (round < repeat - 1 && keyDelay > 0) System.Threading.Thread.Sleep(keyDelay);
                }
                HasAction = true;
            }
            catch (OperationCanceledException ex)
            {
                Skipped = true;
                LastSummary = "键盘输入: 已中止 —— " + ex.Message;
                return dst;
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = "键盘输入: 失败 —— " + ex.Message;
                return dst;
            }
            finally { sim.TypeDelayMs = savedDelay; }

            if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);

            string shown = content.Length <= 30 ? content : content.Substring(0, 30) + "…";
            var flags = new System.Collections.Generic.List<string>();
            if (clearFirst == 1) flags.Add("先清空");
            if (enterAfter == 1) flags.Add("回车");
            if (keyDelay > 0) flags.Add(keyDelay + "ms/键");
            if (repeat > 1) flags.Add("×" + repeat);
            LastSummary = string.Format("键盘输入: {0} \"{1}\"{2}{3}{4}",
                type == 0 ? "文本" : type == 1 ? "组合键" : "按键序列", shown,
                flags.Count > 0 ? "  [" + string.Join(" ", flags) + "]" : "",
                useVar == 1 ? string.Format("  (取自变量 {0})", own.Trim())
                            : (fromNode ? "" : string.Format("  (取自{0}槽{1})", type == 0 ? "文本" : "按键", slot)),
                Automation.AutomationContext.DryRun ? "   ★干跑，未真实操作" : "");
            return dst;
        }

        /// <summary>
        /// 按顺序发送一串按键：用空格/逗号/分号/竖线分隔，每项可以是单键（Enter、F5、Left）
        /// 或组合键（Ctrl+S）。写成 {Enter} 这种带花括号的写法也认（从 AutoHotkey 抄过来的习惯）。
        /// </summary>
        internal static void SendKeySequence(Automation.InputSimulator sim, string content, int keyDelay)
        {
            string[] parts = content.Split([' ', ',', ';', '|', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim().Trim('{', '}');
                if (token.Length == 0) continue;
                if (token.Contains('+')) sim.SendCombo(token);
                else sim.PressKey(token);
                if (keyDelay > 0 && i < parts.Length - 1) System.Threading.Thread.Sleep(keyDelay);
            }
        }
    }
}
