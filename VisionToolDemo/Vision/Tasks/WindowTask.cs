using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 窗口操作：按标题找窗口，然后激活（前置）/关闭/最小化/最大化，或只判断在不在。
    ///
    /// 为什么把它排在高优先级：盲点击最容易点错窗口，而"先激活目标窗口再操作"
    /// 能显著提高整条自动化的稳定性；"等某窗口出现"更是启动类流程的刚需。
    ///
    /// 字符串槽：0=窗口标题  1=结果变量名（动作=是否存在时，把 1/0 写进去，留空则不写）
    /// 标题写法：普通文本 = 包含（不区分大小写）；/正则/ = 用正则匹配，例如 /^记事本/。
    /// </summary>
    public class WindowTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "窗口操作";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        /// <summary>本次是否找到了窗口</summary>
        public bool Exists { get; private set; }
        public string FoundTitle { get; private set; } = "";
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "动作 0激活1关闭2是否存在3最小化4最大化", Min = 0, Max = 4, DefaultValue = 0,
                DisplayFormat = "动作:{0}", Group = "窗口",
                Tip = "0 激活/前置：把窗口调到最前（最小化的会先还原）—— 操作前先做这一步最稳；\n" +
                      "1 关闭：给它发关闭消息（相当于点右上角 ×，程序可以拦截/弹保存框）；\n" +
                      "2 是否存在：只判断，把 1/0 写进变量，不改变窗口；\n" +
                      "3 最小化 / 4 最大化。" },
            new TaskParamDesc { ParamName = "找不到时 0跳过1中止本轮", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "找不到:{0}", Group = "窗口",
                Tip = "0 = 跳过这一步继续（默认）；1 = 中止整轮（窗口没了就没必要往下点了）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Exists = false;
            FoundTitle = "";
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int action = Math.Clamp(paramValues[0], 0, 4);
            bool abortIfMissing = paramValues.Length > 1 && paramValues[1] == 1;
            string pattern = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(0) ?? "").Trim());
            string varName = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? "").Trim());

            if (pattern.Length == 0)
            {
                Skipped = true;
                LastSummary = "窗口操作: 跳过 —— 没填窗口标题（节点属性 → 输入内容 → 窗口标题）";
                return dst;
            }

            var win = Automation.WindowHelper.Find(pattern);
            Exists = win != null;
            if (win != null) FoundTitle = win.Title;

            string actName = action switch { 1 => "关闭", 2 => "是否存在", 3 => "最小化", 4 => "最大化", _ => "激活" };

            if (!Exists)
            {
                if (action == 2 && varName.Length > 0)
                    Automation.AutomationContext.SetVariable(varName, "0");
                Skipped = true;
                LastSummary = string.Format("窗口操作: {0} —— 没找到标题匹配 \"{1}\" 的窗口", actName, pattern);
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            switch (action)
            {
                case 1: Automation.WindowHelper.Close(win.Handle); break;
                case 3: Automation.WindowHelper.Minimize(win.Handle); break;
                case 4: Automation.WindowHelper.Maximize(win.Handle); break;
                case 2: break;
                default:
                    if (!Automation.WindowHelper.Activate(win.Handle))
                    {
                        Skipped = true;
                        LastSummary = string.Format("窗口操作: 激活失败 —— \"{0}\"（句柄 {1}）", win.Title, win.Handle);
                        if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                        return dst;
                    }
                    break;
            }

            if (action == 2 && varName.Length > 0)
                Automation.AutomationContext.SetVariable(varName, "1");

            string extra = varName.Length > 0 && action == 2 ? string.Format("（{0} = {1}）", varName, Exists ? "1" : "0") : "";
            LastSummary = string.Format("窗口操作: {0} \"{1}\"（进程 {2}）{3}", actName, win.Title, win.ProcessId, extra);
            return dst;
        }
    }
}
