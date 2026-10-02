using System;
using System.Diagnostics;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 命令行：跑一个外部程序，把退出码与输出交给后面的节点用。
    ///
    /// 这是"脚本能力"的零依赖版本：真正复杂的逻辑（Python/批处理/厂商 SDK 的命令行）
    /// 交给现成的程序去做，我们只负责调用、等它、把结果记下来。
    ///
    /// 字符串槽（节点属性里的框）：0=命令/程序  1=参数  2=工作目录（都支持 {变量} 插值）
    /// 结果：退出码 / 标准输出 / 错误输出 → 由"全局变量"节点按节点 id 取走。
    ///
    /// **干跑时只打印命令、不执行** —— 与鼠标键盘一样，"真实执行"必须先核对过。
    /// </summary>
    public class CommandTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "命令行";

        public string LastSummary { get; private set; } = "";

        /// <summary>由运行器注入：按槽位取本节点的字符串</summary>
        public Func<int, string> NodeStringProvider { get; set; }

        public int ExitCode { get; private set; } = -1;
        public string Stdout { get; private set; } = "";
        public string Stderr { get; private set; } = "";
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "等待完成 0否1是", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "等待完成:{0}", Group = "命令行",
                Tip = "1 = 等程序结束并收集退出码与输出（默认）。\n" +
                      "0 = 启动后立刻继续（适合拉起一个程序就不管了），此时拿不到退出码与输出。" },
            new TaskParamDesc { ParamName = "超时秒 0=不限", Min = 0, Max = 3600, DefaultValue = 30,
                DisplayFormat = "超时:{0}s", Group = "命令行",
                Tip = "等多久还没结束就强杀并跳过。0 = 一直等（慎用，可能把整轮卡死）。" },
            new TaskParamDesc { ParamName = "显示窗口 0隐藏1显示", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "窗口:{0}", Group = "命令行",
                Tip = "1 = 让被调用的程序显示自己的控制台窗口（要看它的提示时用）。" },
            new TaskParamDesc { ParamName = "输出截断字符数", Min = 0, Max = 100000, DefaultValue = 2000,
                DisplayFormat = "截断:{0}", Group = "命令行",
                Tip = "标准输出/错误输出最多留多少字符（防止一个刷屏的程序把日志撑爆）。0 = 不截断。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ExitCode = -1;
            Stdout = "";
            Stderr = "";
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            string Slot(int i) => Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(i) ?? "").Trim());

            string command = Slot(0);
            string args = NodeStringProvider?.Invoke(1) ?? "";
            args = Automation.AutomationContext.ExpandVariables(args);      // 参数里的空格不要被 Trim 掉
            string workDir = Slot(2);

            bool wait = paramValues.Length < 1 || paramValues[0] == 1;
            int timeoutSec = paramValues.Length > 1 ? Math.Max(0, paramValues[1]) : 30;
            bool showWindow = paramValues.Length > 2 && paramValues[2] == 1;
            int maxChars = paramValues.Length > 3 ? Math.Max(0, paramValues[3]) : 2000;

            if (string.IsNullOrWhiteSpace(command))
            {
                Skipped = true;
                LastSummary = "命令行: 跳过 —— 没填命令（节点属性 → 输入内容 → 命令 / 程序）";
                return dst;
            }

            string shown = string.IsNullOrWhiteSpace(args) ? command : command + " " + args;

            if (Automation.AutomationContext.DryRun)
            {
                // 干跑：绝不起进程。命令照写日志，方便核对参数对不对
                LastSummary = string.Format("命令行:（干跑，未执行）\"{0}\"{1}", shown,
                    string.IsNullOrWhiteSpace(workDir) ? "" : "  目录 " + workDir);
                return dst;
            }

            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = wait,
                RedirectStandardError = wait,
                CreateNoWindow = !showWindow,
            };
            if (!string.IsNullOrWhiteSpace(workDir))
            {
                try { psi.WorkingDirectory = workDir; }
                catch (Exception ex)
                {
                    Skipped = true;
                    LastSummary = string.Format("命令行: 跳过 —— 工作目录无效 \"{0}\"：{1}", workDir, ex.Message);
                    return dst;
                }
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using var p = new Process { StartInfo = psi };
                if (!p.Start())
                {
                    Skipped = true;
                    LastSummary = "命令行: 失败 —— 进程没能启动";
                    return dst;
                }

                if (!wait)
                {
                    LastSummary = string.Format("命令行: 已启动（不等待）\"{0}\"", shown);
                    return dst;
                }

                // 先挂上异步读，否则子进程输出一多就会把管道写满、双方一起卡住
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                bool exited;
                if (timeoutSec <= 0)
                {
                    p.WaitForExit();          // 无参重载 = 一直等
                    exited = true;
                }
                else exited = p.WaitForExit(timeoutSec * 1000);

                if (!exited)
                {
                    try { p.Kill(true); } catch { /* 已经退了就忽略 */ }
                    Skipped = true;
                    LastSummary = string.Format("命令行: 超时 {0}s 已强杀 —— \"{1}\"", timeoutSec, shown);
                    return dst;
                }

                Stdout = Crop(outTask.GetAwaiter().GetResult(), maxChars);
                Stderr = Crop(errTask.GetAwaiter().GetResult(), maxChars);
                ExitCode = p.ExitCode;
                sw.Stop();

                string extra = string.IsNullOrWhiteSpace(Stderr) ? "" : "  错误输出: " + OneLine(Stderr);
                LastSummary = string.Format("命令行: \"{0}\" 退出码 {1}，{2}ms{3}{4}",
                    shown, ExitCode, sw.ElapsedMilliseconds,
                    string.IsNullOrWhiteSpace(Stdout) ? "" : "  输出: " + OneLine(Stdout), extra);
                if (ExitCode != 0) Skipped = true;      // 非 0 退出码算"这一步没成功"，配合"跳过就停"能及时刹车
                return dst;
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = string.Format("命令行: 失败 —— \"{0}\"：{1}", shown, ex.Message.Split('\n')[0]);
                return dst;
            }
        }

        private static string Crop(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").TrimEnd();
            if (max > 0 && s.Length > max) s = s.Substring(0, max) + "…（已截断）";
            return s;
        }

        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\n", " ⏎ ");
            return t.Length <= 120 ? t : t.Substring(0, 120) + "…";
        }
    }
}
