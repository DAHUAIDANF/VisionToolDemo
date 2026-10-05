using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using VisionToolDemo.Vision.Automation;

namespace VisionToolDemo
{
    internal static class Program
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();

        /// <summary>
        /// 应用程序的主入口点。
        ///
        /// 两种运行方式：
        ///   1) 默认：Avalonia 界面（跨平台，Windows/Linux 同一套 UI）；
        ///   2) --run 节点图.autograph.json：**无界面**跑一轮（无人值守/回归用），
        ///      默认干跑（不动鼠标键盘），要真操作必须显式加 --real。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            args ??= Array.Empty<string>();
            for (int i = 0; i < args.Length; i++) args[i] = args[i].Trim();

            if (HasArg(args, "--help") || HasArg(args, "-h") || HasArg(args,"/?"))
            {
                AttachToConsole();
                PrintUsage();
                return;
            }

            if (TryGetArgValue(args, "--run", out string graphPath))
            {
                AttachToConsole();
                int code = RunHeadless(graphPath, args);
                Environment.ExitCode = code;
                return;
            }

            // 全局异常兜底：Avalonia UI 线程未捕获异常默认会终止进程（表现为"闪退"）。
            // 这里拦截后弹窗说明原因并保持程序继续运行，至少用户能看见是哪一步出的问题。
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Wpf.Ui.Error("程序遇到未处理异常：\n" + ex.Message
                        + "\n\n--- 堆栈 ---\n" + ex.StackTrace);
            };
            // 后台 Task 未观察异常默认不终止进程，吞掉避免任何后台路径把进程带崩
            TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Avalonia 应用构建器（跨平台桌面）</summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();

        // ================================================================ 无界面运行

        /// <summary>
        /// 无界面跑节点图。退出码：0 = 正常（且判定不是 NG）、1 = 运行失败、2 = 判定 NG。
        /// 为什么做这个：现场常有"定时跑一遍看看"的需求，CI/回归也需要不弹窗的入口。
        /// </summary>
        private static int RunHeadless(string graphPath, string[] args)
        {
            bool real = HasArg(args, "--real") || HasArg(args, "--真实执行");
            int repeat = 1;
            if (TryGetArgValue(args, "--repeat", out string repText) && int.TryParse(repText, out int rp))
                repeat = Math.Max(1, Math.Min(1000, rp));
            bool quiet = HasArg(args, "--quiet");

            if (!File.Exists(graphPath))
            {
                Console.WriteLine("找不到节点图文件：" + graphPath);
                return 1;
            }

            Console.WriteLine("=== 无界面运行 ===");
            Console.WriteLine("节点图：" + graphPath);
            Console.WriteLine("模式：" + (real ? "★真实执行★（会操作鼠标键盘）" : "干跑（不会操作鼠标键盘；要真实执行加 --real）"));
            Console.WriteLine("轮数：" + repeat);
            Console.WriteLine(CoordinateSpace.Describe());
            Console.WriteLine();

            AutomationContext.DryRun = !real;

            // 图文件读写：界面与无界面入口共用 AutomationGraphIO（纯序列化实现）
            AutomationGraph graph = new();
            try { AutomationGraphIO.Import(graph, File.ReadAllText(graphPath)); }
            catch (Exception ex)
            {
                Console.WriteLine("节点图解析失败：" + ex.Message);
                return 1;
            }

            string invalid = graph.Validate();
            if (invalid != null)
            {
                Console.WriteLine("校验不通过：" + invalid);
                return 1;
            }

            int failed = 0, ng = 0;
            for (int round = 1; round <= repeat; round++)
            {
                AutomationContext.BeginRound();
                RunRecorder.Begin();
                var lines = new System.Collections.Generic.List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = AutomationRunner.Run(graph, 2000,
                    quiet ? (Action<string>)null : s => { lines.Add(s); Console.WriteLine(s); });
                sw.Stop();
                if (quiet) lines.AddRange(result.Lines);

                string dir = RunRecorder.Save(graph, result, result.Lines, AutomationContext.MmPerPixel);
                string verdict = AutomationContext.FinalVerdict;

                Console.WriteLine(string.Format("[第 {0}/{1} 轮] {2}  步数 {3}  动作 {4}  跳过 {5}  {6}ms  判定 {7}  记录 {8}",
                    round, repeat, result.Ok ? "完成" : "失败", result.Steps, result.Actions,
                    result.SkippedActions, result.TotalMs,
                    verdict.Length == 0 ? "(未判定)" : verdict,
                    Path.GetFileName(dir)));

                if (!string.IsNullOrEmpty(result.Error)) Console.WriteLine("  中止原因：" + result.Error);
                foreach (var rr in AutomationContext.RuleResults)
                    if (!rr.Ok) Console.WriteLine("  NG 规则：" + rr.ToString());

                result.DisposeImages();
                if (!result.Ok) failed++;
                else if (verdict == "NG") ng++;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("=== 结束：{0} 轮，失败 {1}，判定 NG {2} ===", repeat, failed, ng));
            return failed > 0 ? 1 : (ng > 0 ? 2 : 0);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("VisionTool 视觉检测工作台");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  VisionToolDemo                        启动界面（Avalonia，跨 Windows/Linux，按分辨率/DPI 自适应）");
            Console.WriteLine("  VisionToolDemo --run 图.autograph.json [--real] [--repeat N] [--quiet]");
            Console.WriteLine("                                         无界面跑节点图（默认干跑；--real 才会动鼠标键盘）");
            Console.WriteLine();
            Console.WriteLine("退出码：0 = 正常且判定不是 NG；1 = 运行失败；2 = 判定 NG。");
        }

        private static void AttachToConsole()
        {
            try
            {
                // WinExe 默认没有控制台，把它挂到父进程（终端/CI）的控制台上，输出才看得见
                if (!AttachConsole(-1)) AllocConsole();
            }
            catch { /* 挂不上就算了，记录文件里照样有全部信息 */ }
        }

        private static bool HasArg(string[] args, string name)
            => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static bool TryGetArgValue(string[] args, string name, out string value)
        {
            value = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length) return false;
                value = args[i + 1];
                return true;
            }
            return false;
        }
    }
}
