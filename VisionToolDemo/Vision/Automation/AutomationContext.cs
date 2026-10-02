using System;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 自动化运行期的全局上下文。
    ///
    /// 把"干跑开关 / 动作上限 / 失效保护"放在全局而不是每个动作算子的参数里，
    /// 是为了让**只有一个开关**：如果开关分散在多个算子参数上，
    /// 用户很可能只改了一处，剩下几处仍在真实执行 —— 这正是最危险的配置。
    /// </summary>
    public static class AutomationContext
    {
        /// <summary>共享的输入模拟器（动作计数与日志在一轮内连续累计）</summary>
        public static InputSimulator Input { get; } = new InputSimulator();

        /// <summary>干跑开关。**默认 true**，必须由用户显式关闭才会真实操作鼠标键盘。</summary>
        public static bool DryRun
        {
            get => Input.DryRun;
            set => Input.DryRun = value;
        }

        /// <summary>日志回调（UI 订阅后实时显示动作）</summary>
        public static event Action<string> OnLog;

        /// <summary>
        /// 文本槽 / 按键槽（各 4 个），在自动化面板里填写，由"键盘输入"算子按槽位取用。
        ///
        /// 为什么要用"槽"这种间接方式：算子参数体系（TaskParamDesc）只支持整数，
        /// 而 Execute 的签名是 Execute(Mat, int[])，改签名会牵动全部 100 个算子。
        /// 用一个小的全局槽表即可在不改动算子接口的前提下支持字符串输入，
        /// 而且用户在面板里一眼能看到"槽 1 里到底是什么内容"，比藏在参数里更直观。
        /// </summary>
        public static string[] TextSlots { get; } = ["", "", "", ""];

        /// <summary>按键槽：填写形如 "Ctrl+C"、"Enter"、"Alt+F4" 的组合</summary>
        public static string[] KeySlots { get; } = ["", "", "", ""];

        /// <summary>
        /// 最近一次截图的**屏幕原点**（虚拟桌面坐标）。
        /// 截图得到的图像坐标 (u,v) 对应的屏幕坐标是 (Origin.X+u, Origin.Y+v)。
        /// 多显示器时虚拟桌面原点可能不是 (0,0)（副屏在主屏左侧时为负），
        /// 不加上这个偏移就会整体点偏。
        /// </summary>
        public static int CaptureOriginX { get; set; }
        public static int CaptureOriginY { get; set; }

        /// <summary>
        /// 运行期间让本程序窗口"不挡路"的方式：
        /// false = 隐藏窗口（默认）；true = 把窗口移出屏幕。
        ///
        /// 为什么需要后者：某些环境下隐藏窗口会让前台窗口落到别的程序上，
        /// 而那个程序若是管理员权限，UIPI 会禁止我们注入鼠标/键盘。
        /// 移出屏幕时前台仍是我们自己，注入不会被拦。
        /// 真实执行前自检会自动判断该用哪种，不需要用户手动选。
        /// </summary>
        public static bool MoveWindowsAwayInsteadOfHiding { get; set; }

        /// <summary>
        /// 本轮是否真的抓过屏。
        ///
        /// 用来区分"这是屏幕截图"和"这是一张普通图片"：匹配算子会据此决定要不要在摘要里
        /// 报出屏幕坐标。普通图片没有"屏幕坐标"这个概念，硬报一个只会让人按错坐标去点。
        /// </summary>
        public static bool HasCapture { get; set; }

        /// <summary>
        /// 本轮"总判定"（OK / NG，空 = 还没判定）。由"结果聚合"节点或判定类节点写入。
        /// 界面统计卡片、event.json、外部信号都用它 —— 判定是工业视觉的第一等输出。
        /// </summary>
        public static string FinalVerdict { get; set; } = "";

        /// <summary>
        /// 本轮"被跳过的动作"计数（没目标/超时/命令失败/变量缺失…）。
        /// 由运行器在动作算子报告跳过时累加；"结果聚合"节点据此把"该做却没做"也判成 NG。
        /// </summary>
        public static int SkippedActionCount { get; set; }

        /// <summary>本轮每条规则的判定明细（结果聚合节点汇总，运行记录留档）</summary>
        public static System.Collections.Generic.List<RuleResult> RuleResults { get; }
            = new();

        /// <summary>
        /// 标定比例尺：1 像素 = 多少毫米（0 = 没标定）。由"标定"节点写入。
        /// 表达式里可以用 毫米({像素值}) / 像素({毫米值}) 做换算。
        /// </summary>
        public static double MmPerPixel { get; set; }

        /// <summary>像素 → 毫米（没标定过返回 NaN，调用方据此给出"请先标定"的提示）</summary>
        public static double PixelToMm(double px) => MmPerPixel > 0 ? px * MmPerPixel : double.NaN;

        /// <summary>
        /// 最近这次截图的"图像像素 → 本进程屏幕坐标"换算比。
        ///
        /// 本程序是 DPI 不感知的：系统缩放 125%/150% 时，WinForms 与 SetCursorPos 用
        /// 虚拟化坐标（例如 1280x720），而 GDI 抓屏拿回来的是物理像素（1920x1080）。
        /// 不换算的话，图上量到的坐标直接喂给鼠标就会偏，偏差正好是缩放比 ——
        /// 这就是"截图与实际对不上、鼠标位置不对"的来源。换算逻辑见 CoordinateSpace。
        /// 100% 缩放下恒为 1.0，行为与以前完全一致。
        /// </summary>
        public static double CaptureImageToScreenScale { get; set; } = 1.0;

        /// <summary>
        /// 自动化"匹配"节点使用的模板图（由主界面在运行前注入）。
        /// 节点图里不放模板本身：模板是二进制大对象，塞进节点参数既难序列化又难比对。
        /// </summary>
        public static OpenCvSharp.Mat Template { get; set; }

        // ================================================================== 全局变量（节点图用来记录信息）

        /// <summary>
        /// 全局变量表：由"全局变量"节点写入，任何文本里都能用 {变量名} 引用。
        ///
        /// 每轮运行开始时清空（BeginRound）：变量是"本轮自动化过程中的信息"，
        /// 跨轮留着会让上一轮的值被误当成这一轮的结果。
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, string> Variables { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一个"文字类"算子的结果（OCR/条码/DPM），供"全局变量"节点记录</summary>
        public static string LastText { get; set; } = "";

        /// <summary>
        /// 本轮每个节点跑完后的输出（键 = 节点 id）。
        ///
        /// "全局变量"节点靠它引用**指定的**节点结果（不只是"上一个"）：
        /// 例如把第 3 个匹配节点算到的坐标、第 5 个截图存的路径记进变量。
        /// 每轮开始清空 —— 变量与节点输出都是"本轮过程中的信息"。
        /// </summary>
        public static System.Collections.Generic.Dictionary<int, NodeOutput> NodeOutputs { get; }
            = new();

        public static void SetNodeOutput(int nodeId, NodeOutput output)
        {
            if (output != null) NodeOutputs[nodeId] = output;
        }

        public static bool TryGetNodeOutput(int nodeId, out NodeOutput output)
            => NodeOutputs.TryGetValue(nodeId, out output);

        /// <summary>
        /// 默认截图目录：**程序所在目录**下的"截图"文件夹。
        ///
        /// 这里必须用 AppContext.BaseDirectory 而不是 Environment.CurrentDirectory：
        /// 后者是"启动时的工作目录"，快捷方式/计划任务/其它程序拉起来时可以完全无关
        /// （实测把起始位置设成 C:\Windows\System32 时它就真的是 System32）——
        /// 那样截图会存到莫名其妙的地方，多数还会因权限写不进去。
        /// 项目里 TesseractEngine / BlobStatsCsvTask 也都用 BaseDirectory。
        /// </summary>
        public static string DefaultSaveImageDir
            => System.IO.Path.Combine(AppContext.BaseDirectory, "截图");

        /// <summary>截图保存目录（屏幕截图节点开了"保存图片"且节点没填目录时用它）</summary>
        public static string SaveImageDir { get; set; } = DefaultSaveImageDir;

        public static void SetVariable(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Variables[name.Trim()] = value ?? "";
        }

        public static bool TryGetVariable(string name, out string value)
        {
            value = "";
            if (string.IsNullOrWhiteSpace(name)) return false;
            return Variables.TryGetValue(name.Trim(), out value);
        }

        /// <summary>
        /// 按"节点属性某个框里写的变量名"取变量值。空名字或没这个变量都返回 false，
        /// 由调用方给出说明（是没填名字，还是名字打错了/那一轮还没写过）。
        /// </summary>
        public static bool TryResolveVariable(string boxText, out string value)
        {
            value = "";
            string name = ExpandVariables(boxText ?? "").Trim();
            if (name.Length == 0) return false;
            return TryGetVariable(name, out value);
        }

        /// <summary>
        /// 把文本里的 {变量名} 换成变量值；没有的变量原样保留（便于用户一眼看出拼错了名字）。
        /// 只认单层：变量值里再写 {x} 不会再展开，避免自引用时死循环。
        /// </summary>
        public static string ExpandVariables(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf('{', i);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                int close = text.IndexOf('}', open + 1);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, open - i);
                string name = text.Substring(open + 1, close - open - 1).Trim();
                if (name.Length > 0 && Variables.TryGetValue(name, out string val)) sb.Append(val);
                else sb.Append(text, open, close - open + 1);   // 未知变量原样保留
                i = close + 1;
            }
            return sb.ToString();
        }

        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            OnLog?.Invoke(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message));
        }

        /// <summary>一轮自动化开始前调用：清空检测结果、动作计数与日志</summary>
        public static void BeginRound()
        {
            DetectionStore.Clear();
            Input.Log.Clear();
            AutomationContext_ResetCount();
            HasCapture = false;
            CaptureImageToScreenScale = 1.0;
            FinalVerdict = "";
            SkippedActionCount = 0;
            RuleResults.Clear();
            MmPerPixel = 0;
            Variables.Clear();       // 变量是"本轮过程中的信息"，跨轮留着会张冠李戴
            LastText = "";
            NodeOutputs.Clear();
        }

        private static void AutomationContext_ResetCount()
        {
            // ActionCount / FailsafeTripped 是只读属性，通过反射清零不合适；
            // 由 InputSimulator 暴露一个 Reset 方法更干净（见其 ResetCounters）。
            Input.ResetCounters();
        }
    }
}
