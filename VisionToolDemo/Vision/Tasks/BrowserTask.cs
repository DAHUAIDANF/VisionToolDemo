using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 浏览器操作：识别/激活浏览器窗口，刷新、后退、前进、滚动，或直接打开一个网址。
    ///
    /// 为什么需要它：通用"窗口操作"按**标题**找窗口，而浏览器窗口标题随页面变化
    /// （"订单列表 - Google Chrome"），找不准。浏览器节点按**进程名**识别
    /// （chrome/msedge/firefox），窗口叫什么标题都能找到并激活；
    /// 刷新/后退/前进/滚动是浏览器高频动作，不用再拼组合键节点。
    ///
    /// 字符串槽：0=进程关键字（留空 = 按参数里的浏览器类型自动取）
    ///           1=网址（动作=打开网址时用；其余动作忽略）
    ///           2=结果变量名（动作=激活/识别时把 1/0 写进去，留空则不写）
    /// 干跑（默认）只记录不真操作；打开网址属外部副作用，干跑同样只记录不真开。
    /// </summary>
    public class BrowserTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "浏览器操作";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "浏览器 0自动1Chrome2Edge3Firefox", Min = 0, Max = 3, DefaultValue = 0,
                DisplayFormat = "浏览器:{0}", Group = "浏览器",
                Tip = "0 自动 = 用下面「进程关键字」匹配，留空则按 chrome/msedge/firefox 顺序找第一个；\n" +
                      "1 Chrome / 2 Edge / 3 Firefox = 按该浏览器进程名识别。" },
            new TaskParamDesc { ParamName = "动作 0激活识别1刷新2后退3前进4滚动到顶5滚动到底6打开网址", Min = 0, Max = 6, DefaultValue = 0,
                DisplayFormat = "动作:{0}", Group = "浏览器",
                Tip = "0 激活/识别：按进程名（或标题）找到浏览器主窗口并置前，把 1/0 写进结果变量；\n" +
                      "1 刷新 Ctrl+R；2 后退 Alt+←；3 前进 Alt+→；4 滚动到顶 Home；5 滚动到底 End；\n" +
                      "6 打开网址：用系统默认浏览器打开「网址」框里的地址（干跑只记录不真开）。" },
            new TaskParamDesc { ParamName = "找不到时 0跳过1中止本轮", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "找不到:{0}", Group = "浏览器",
                Tip = "0 = 跳过这一步继续（默认）；1 = 中止整轮（浏览器没开就没必要往下点了）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Skipped = false;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int browser = Math.Clamp(paramValues[0], 0, 3);
            int action = Math.Clamp(paramValues[1], 0, 6);
            bool abortIfMissing = paramValues.Length > 2 && paramValues[2] == 1;

            string keyword = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(0) ?? "").Trim());
            string url = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? "").Trim());
            string varName = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(2) ?? "").Trim());

            // 浏览器类型 → 默认进程关键字
            string[] procs = { "", "chrome", "msedge", "firefox" };
            string procKey = keyword;
            if (procKey.Length == 0 && browser > 0) procKey = procs[browser];

            string browserName = browser switch { 1 => "Chrome", 2 => "Edge", 3 => "Firefox", _ => "自动" };
            string actName = action switch
            {
                1 => "刷新", 2 => "后退", 3 => "前进", 4 => "滚动到顶", 5 => "滚动到底", 6 => "打开网址", _ => "激活/识别",
            };

            // 打开网址：外部副作用，干跑只记录
            if (action == 6)
            {
                if (url.Length == 0)
                {
                    Skipped = true;
                    LastSummary = "浏览器操作: 跳过 —— 动作=打开网址但没填网址（节点属性 → 输入内容 → 网址）";
                    return dst;
                }
                if (!Automation.AutomationContext.DryRun)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        Skipped = true;
                        LastSummary = "浏览器操作: 打开网址失败 —— " + ex.Message;
                        if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                        return dst;
                    }
                }
                LastSummary = string.Format("浏览器操作: {0} \"{1}\"{2}", actName, url,
                    Automation.AutomationContext.DryRun ? "  ★干跑，未真实打开" : "");
                return dst;
            }

            // 窗口类动作（激活/刷新/后退/前进/滚动）干跑也只记录：
            // 找窗口要走 user32，干跑不触碰系统窗口，避免无界面环境炸掉
            if (Automation.AutomationContext.DryRun)
            {
                LastSummary = string.Format("浏览器操作: {0}「{1}」({2})  ★干跑，未真实操作", actName, procKey.Length > 0 ? procKey : "自动", browserName);
                return dst;
            }

            // 其余动作：先找浏览器窗口（进程名优先，回退标题匹配）
            var win = procKey.Length > 0
                ? Automation.WindowHelper.FindByProcess(procKey)
                : null;
            if (win == null)
            {
                string titlePattern = keyword;   // 用户填的既是进程关键字也是标题关键字
                win = Automation.WindowHelper.Find(titlePattern);
            }

            if (win == null)
            {
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "0");
                Skipped = true;
                LastSummary = string.Format("浏览器操作: {0} —— 没找到{1}浏览器窗口", actName,
                    procKey.Length > 0 ? "进程 " + procKey + " 的" : "");
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            // 激活窗口：刷新/后退/前进/滚动都要求浏览器在前台，快捷键才发得进去
            bool activated = Automation.WindowHelper.Activate(win.Handle);
            if (!activated)
            {
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "0");
                Skipped = true;
                LastSummary = string.Format("浏览器操作: 激活失败 —— \"{0}\"（进程 {1}）", win.Title, win.ProcessId);
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "1");

            // 动作 → 快捷键（InputSimulator 干跑只记录，真实才发送）
            var sim = Automation.AutomationContext.Input;
            switch (action)
            {
                case 1: sim.SendCombo("Ctrl+R"); break;
                case 2: sim.SendCombo("Alt+Left"); break;
                case 3: sim.SendCombo("Alt+Right"); break;
                case 4: sim.PressKey("Home"); break;
                case 5: sim.PressKey("End"); break;
                default: break;   // 动作 0：只激活/识别
            }

            string extra = varName.Length > 0 ? string.Format("（{0} = 1）", varName) : "";
            LastSummary = string.Format("浏览器操作: {0} \"{1}\"（{2}，进程 {3}）{4}", actName, win.Title, browserName, win.ProcessId, extra);
            return dst;
        }
    }
}
