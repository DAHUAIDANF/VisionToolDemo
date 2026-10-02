using System;
using System.Collections.Generic;
using System.Drawing;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 浏览器元素操作：识别网页里的按钮 / 输入框 / 链接 / 文本，然后点击、输入、读文本。
    ///
    /// 三种定位方式：
    ///   · UIA 控件（默认）：读浏览器辅助功能树，按控件名称/自动化ID找真实控件
    ///     （按钮/输入框/链接），点按钮走 InvokePattern、填输入框走 ValuePattern；
    ///     Edge 支持最好，Chrome 需开启"辅助功能"模式。
    ///   · OCR 内置：截浏览器窗口 → 二值化 → 连通域切分 → 内置字模识别器找文字位置，
    ///     点文字中心。不依赖浏览器设置；只识别数字/大写字母/常用符号。
    ///   · OCR-Tesseract：同一张截图切成网格块，逐块用 Tesseract（eng+chi_sim）
    ///     识别，命中关键字的块中心就是目标 —— 支持中文页面文字定位，
    ///     但要求程序目录下有 tessdata 语言包，且网格识别较慢（适合等待定位场景）。
    ///
    /// 动作：点击 / 输入文字 / 读取元素文本(写变量) / 聚焦 / 读取整页文本(写变量)。
    /// 等待：等待超时秒 > 0 时，先轮询"元素出现"再执行动作（页面未加载完也能用）。
    /// 字符串槽：0=元素关键字（UIA 控件名/ID 片段，或 OCR 页面文字）
    ///           1=输入内容（动作=输入文字时）
    ///           2=结果变量名（读取元素文本 / 读取整页文本 / 未找到时写入）
    /// 干跑只记录不真操作；找不到元素可跳过或中止本轮。
    /// </summary>
    public class BrowserElementTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "浏览器元素";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "定位 0UIA控件1OCR内置2OCR-Tesseract", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "定位:{0}", Group = "浏览器",
                Tip = "0 UIA控件 = 读浏览器辅助功能树，按控件名称/ID 找真实按钮、输入框（Edge 最佳，Chrome 需开辅助功能）；\n" +
                      "1 OCR内置 = 截图二值化找文字位置（不依赖浏览器设置，只认数字/大写/常用符号）；\n" +
                      "2 OCR-Tesseract = 截图分块用 Tesseract 识别，支持中文页面文字（需 tessdata 语言包，较慢）。" },
            new TaskParamDesc { ParamName = "动作 0点击1输入文字2读取元素3聚焦4读取整页", Min = 0, Max = 4, DefaultValue = 0,
                DisplayFormat = "动作:{0}", Group = "浏览器",
                Tip = "0 点击该元素；1 点击后输入文字（输入框）；2 读取元素文本写进变量（UIA=控件名，OCR=命中文字）；\n" +
                      "3 聚焦元素；4 读取整页可见文本写进变量（UIA=辅助功能树拼接，OCR=全页识别）。" },
            new TaskParamDesc { ParamName = "找不到时 0跳过1中止本轮", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "找不到:{0}", Group = "浏览器",
                Tip = "0 = 跳过继续（默认）；1 = 中止整轮（关键元素没出现就没必要往下点了）。" },
            new TaskParamDesc { ParamName = "等待超时秒 0=不等待", Min = 0, Max = 120, DefaultValue = 0,
                DisplayFormat = "等待:{0}s", Group = "浏览器",
                Tip = ">0 时先轮询\"元素出现\"再执行动作（0.5 秒一次），页面加载慢/元素后出现都能等；\n" +
                      "超时仍未出现按「找不到时」处理。0 = 只找一次。" },
        ];

        // Tesseract 共享引擎（eng+chi_sim，支持中文定位）；懒初始化
        private static readonly object _tessLock = new();
        private static TesseractEngine _tess;
        private static TesseractEngine GetTess()
        {
            lock (_tessLock)
            {
                _tess ??= new TesseractEngine();
                if (!_tess.Ready) _tess.TryInit("eng+chi_sim");
                return _tess;
            }
        }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Skipped = false;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int locate = Math.Clamp(paramValues[0], 0, 2);
            int action = Math.Clamp(paramValues[1], 0, 4);
            bool abortIfMissing = paramValues.Length > 2 && paramValues[2] == 1;
            int waitSeconds = paramValues.Length > 3 ? Math.Clamp(paramValues[3], 0, 120) : 0;

            string keyword = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(0) ?? "").Trim());
            string inputText = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(1) ?? ""));
            string varName = Automation.AutomationContext.ExpandVariables(
                (NodeStringProvider?.Invoke(2) ?? "").Trim());

            string locateName = locate switch { 1 => "OCR内置", 2 => "OCR-Tesseract", _ => "UIA控件" };
            string actName = action switch { 1 => "输入文字", 2 => "读取元素", 3 => "聚焦", 4 => "读取整页", _ => "点击" };

            // 动作 4（读取整页）不需要关键字；其余动作必须有元素关键字
            if (action != 4 && keyword.Length == 0)
            {
                Skipped = true;
                LastSummary = "浏览器元素: 跳过 —— 没填元素关键字（节点属性 → 输入内容 → 元素关键字）";
                return dst;
            }

            // 干跑：只记录，不触碰 UIA / 截图 / 鼠标（也保证 Linux 无界面测试安全）
            if (Automation.AutomationContext.DryRun)
            {
                LastSummary = string.Format("浏览器元素: {0}{1}({2})  ★干跑，未真实操作",
                    actName, action == 4 ? "" : "「" + keyword + "」", locateName);
                return dst;
            }

            // 找浏览器窗口（进程名优先，标题兜底）；等窗口出现也算在等待超时里
            var win = FindWindow(keyword, waitSeconds);
            if (win == null)
            {
                Skipped = true;
                LastSummary = "浏览器元素: 跳过 —— 没找到浏览器窗口（先放一个「浏览器操作」节点激活它）";
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "");
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            try
            {
                return locate switch
                {
                    1 => ExecuteOcrBuiltin(win, action, keyword, inputText, varName, abortIfMissing, waitSeconds, dst),
                    2 => ExecuteOcrTesseract(win, action, keyword, inputText, varName, abortIfMissing, waitSeconds, dst),
                    _ => ExecuteUia(win, action, keyword, inputText, varName, abortIfMissing, waitSeconds, dst),
                };
            }
            catch (Exception ex)
            {
                // 平台缺 UIA 程序集 / 浏览器未暴露辅助功能树等，统一转跳过
                Skipped = true;
                LastSummary = "浏览器元素: " + locateName + "定位失败 —— " + ex.Message;
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "");
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }
        }

        /// <summary>等浏览器窗口出现（进程关键字优先，标题兜底）；等待超时秒>0 时轮询</summary>
        private static Automation.TopWindow FindWindow(string keyword, int waitSeconds)
        {
            var deadline = DateTime.Now.AddSeconds(Math.Max(1, waitSeconds));
            while (true)
            {
                var w = Automation.WindowHelper.FindByProcess(keyword) ?? Automation.WindowHelper.Find(keyword);
                if (w != null) return w;
                if (DateTime.Now >= deadline) return null;
                System.Threading.Thread.Sleep(500);
            }
        }

        // ================================================================ UIA 路径

        private Mat ExecuteUia(Automation.TopWindow win, int action, string keyword, string inputText,
            string varName, bool abortIfMissing, int waitSeconds, Mat dst)
        {
            string actName = action switch { 1 => "输入文字", 2 => "读取元素", 3 => "聚焦", 4 => "读取整页", _ => "点击" };
            // 动作 4：读取整页文本，不需要找元素
            if (action == 4)
            {
                string page = ReadPageTextUia(win.Handle);
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, page);
                LastSummary = string.Format("浏览器元素: 读取整页文本（UIA）→ {0} 字", page.Length);
                return dst;
            }

            var deadline = DateTime.Now.AddSeconds(Math.Max(1, waitSeconds));
            var el = FindUiaElement(win.Handle, keyword, deadline);
            if (el == null)
            {
                Skipped = true;
                LastSummary = string.Format("浏览器元素: {0} —— UIA 未找到控件「{1}」（Chrome 需在无障碍里开启辅助功能）", actName, keyword);
                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "");
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            string foundName = "";
            try { foundName = (string)el.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.NameProperty) ?? ""; }
            catch { }

            var ctypeFinal = (System.Windows.Automation.ControlType)el.GetCurrentPropertyValue(
                System.Windows.Automation.AutomationElement.ControlTypeProperty);

            switch (action)
            {
                case 1:   // 输入文字：输入框优先 ValuePattern，否则聚焦后用键盘输入
                    {
                        var valuePattern = el.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern) as System.Windows.Automation.ValuePattern;
                        if (valuePattern != null)
                        {
                            try { valuePattern.SetValue(inputText); }
                            catch { try { valuePattern.SetValue(""); } catch { } Automation.AutomationContext.Input.TypeText(inputText); }
                        }
                        else
                        {
                            el.SetFocus();
                            System.Threading.Thread.Sleep(120);
                            Automation.AutomationContext.Input.TypeText(inputText);
                        }
                        break;
                    }
                case 2:   // 读取元素文本：写控件名称（按钮文字/输入框值）
                    if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, foundName);
                    break;
                case 3:   // 聚焦
                    el.SetFocus();
                    break;
                default:  // 点击：按钮走 Invoke，其余用 Focus+回车
                    {
                        var invoke = el.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern) as System.Windows.Automation.InvokePattern;
                        if (invoke != null && ctypeFinal == System.Windows.Automation.ControlType.Button)
                        {
                            invoke.Invoke();
                        }
                        else
                        {
                            el.SetFocus();
                            Automation.AutomationContext.Input.PressKey("Enter");
                        }
                        break;
                    }
            }

            LastSummary = string.Format("浏览器元素: {0}「{1}」→ 控件「{2}」({3})", actName, keyword, foundName, ctypeFinal.ProgrammaticName);
            return dst;
        }

        /// <summary>在窗口辅助功能树里找名称/自动化ID 含关键字的可见元素，超时前轮询</summary>
        private static System.Windows.Automation.AutomationElement FindUiaElement(
            IntPtr hwnd, string keyword, DateTime deadline)
        {
            while (true)
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var all = root.FindAll(System.Windows.Automation.TreeScope.Subtree,
                    System.Windows.Automation.Condition.TrueCondition);
                System.Windows.Automation.AutomationElement best = null;
                int bestScore = int.MinValue;
                foreach (System.Windows.Automation.AutomationElement el in all)
                {
                    if (el == null) continue;
                    try
                    {
                        string name = (string)el.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.NameProperty) ?? "";
                        string aid = (string)el.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.AutomationIdProperty) ?? "";
                        if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                            && aid.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var ctype = (System.Windows.Automation.ControlType)el.GetCurrentPropertyValue(
                            System.Windows.Automation.AutomationElement.ControlTypeProperty);
                        int score = ctype == System.Windows.Automation.ControlType.Button || ctype == System.Windows.Automation.ControlType.Edit ? 3
                            : ctype == System.Windows.Automation.ControlType.Hyperlink || ctype == System.Windows.Automation.ControlType.ListItem ? 2 : 1;
                        if (score > bestScore) { bestScore = score; best = el; }
                    }
                    catch { }
                }
                if (best != null) return best;
                if (DateTime.Now >= deadline) return null;
                System.Threading.Thread.Sleep(500);
            }
        }

        /// <summary>UIA 读取整页可见文本：遍历树收集 Name，去重后拼接（截断 4000 字防爆炸）</summary>
        private static string ReadPageTextUia(IntPtr hwnd)
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var all = root.FindAll(System.Windows.Automation.TreeScope.Subtree,
                System.Windows.Automation.Condition.TrueCondition);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            foreach (System.Windows.Automation.AutomationElement el in all)
            {
                if (el == null) continue;
                try
                {
                    string name = (string)el.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.NameProperty) ?? "";
                    if (name.Length == 0 || name.Length > 300) continue;   // 过滤空与超长控件
                    if (!seen.Add(name)) continue;                          // 去重（父容器重复子文本）
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(name);
                    if (sb.Length > 4000) break;
                }
                catch { }
            }
            return sb.ToString();
        }

        // ================================================================ OCR 内置路径

        private Mat ExecuteOcrBuiltin(Automation.TopWindow win, int action, string keyword, string inputText,
            string varName, bool abortIfMissing, int waitSeconds, Mat dst)
        {
            string actName = action switch { 1 => "输入文字", 2 => "读取元素", 3 => "聚焦", 4 => "读取整页", _ => "点击" };
            var deadline = DateTime.Now.AddSeconds(Math.Max(1, waitSeconds));
            while (true)
            {
                if (action == 4)
                {
                    using (var shot = CaptureWindow(win, out string capErr))
                    {
                        if (shot == null)
                        {
                            Skipped = true; LastSummary = "浏览器元素: OCR读取整页 —— 截图失败 " + capErr;
                            if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                            return dst;
                        }
                        RecognizePage(shot, out string full);
                        if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, full);
                        LastSummary = string.Format("浏览器元素: 读取整页文本（OCR内置）→ {0} 字", full.Length);
                        return dst;
                    }
                }

                if (TryFindOcrBuiltin(win, keyword, out OpenCvSharp.Rect box, out string shotErr))
                {
                    var bounds = win.Bounds;
                    int cx = bounds.Left + box.Left + box.Width / 2;
                    int cy = bounds.Top + box.Top + box.Height / 2;
                    return ActOnOcrPoint(cx, cy, action, keyword, inputText, varName, dst);
                }
                if (shotErr != null)
                {
                    Skipped = true; LastSummary = "浏览器元素: OCR定位 —— 截图失败 " + shotErr;
                    if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                    return dst;
                }
                if (DateTime.Now >= deadline)
                {
                    Skipped = true;
                    LastSummary = string.Format("浏览器元素: {0} —— OCR 没找到文字「{1}」", actName, keyword);
                    if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "");
                    if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                    return dst;
                }
                System.Threading.Thread.Sleep(500);
            }
        }

        /// <summary>内置 OCR：截窗口 → 二值 → 连通域切分 → 逐字识别 → 找关键字子串，返回命中字符位置</summary>
        private static bool TryFindOcrBuiltin(Automation.TopWindow win, string keyword,
            out OpenCvSharp.Rect box, out string error)
        {
            box = default; error = null;
            using var shot = CaptureWindow(win, out string capErr);
            if (shot == null) { error = capErr; return false; }
            var chars = RecognizePage(shot, out _);
            if (chars.Count == 0) return false;
            var sb = new System.Text.StringBuilder();
            foreach (var c in chars) sb.Append(c.ch);
            string full = sb.ToString();
            int hit = full.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) return false;
            box = chars[hit].box;
            return true;
        }

        /// <summary>整页识别：连通域切分每个字符 → CharRecognizer 逐字识别 → 按阅读顺序输出 (字符, 位置)</summary>
        private static List<(char ch, OpenCvSharp.Rect box)> RecognizePage(Mat shot, out string text)
        {
            var result = new List<(char ch, OpenCvSharp.Rect box)>();
            using Mat gray = VisionHelper.ToGray(shot);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, 140, 255, ThresholdTypes.BinaryInv);
            using Mat el = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, el);
            Mat labels = new Mat(), stats = new Mat(), centroids = new Mat();
            try
            {
                Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, OpenCvSharp.PixelConnectivity.Connectivity8);
                int n = stats.Rows;
                string charset = CharRecognizer.Digits + CharRecognizer.Upper + CharRecognizer.Common;
                for (int i = 1; i < n; i++)
                {
                    int x = (int)stats.At<int>(i, 0);
                    int y = (int)stats.At<int>(i, 1);
                    int w = (int)stats.At<int>(i, 2);
                    int h = (int)stats.At<int>(i, 3);
                    int area = (int)stats.At<int>(i, 4);
                    if (w < 3 || h < 8 || area < 6 || w > 120 || h > 120) continue;
                    if (h < 6 || (double)w / h > 3.0) continue;
                    using Mat sub = new(bin, new OpenCvSharp.Rect(x, y, w, h));
                    var m = CharRecognizer.Recognize(sub, charset, out _);
                    if (m.Confidence < 0.45) continue;
                    result.Add((m.Ch, new OpenCvSharp.Rect(x, y, w, h)));
                }
            }
            finally { labels.Dispose(); stats.Dispose(); centroids.Dispose(); }
            result.Sort((a, b) =>
            {
                int by = a.box.Y / 24 - b.box.Y / 24;
                return by != 0 ? by : a.box.X.CompareTo(b.box.X);
            });
            var sb = new System.Text.StringBuilder();
            foreach (var c in result) sb.Append(c.ch);
            text = sb.ToString();
            return result;
        }

        // ================================================================ OCR-Tesseract 路径（支持中文）

        private Mat ExecuteOcrTesseract(Automation.TopWindow win, int action, string keyword, string inputText,
            string varName, bool abortIfMissing, int waitSeconds, Mat dst)
        {
            string actName = action switch { 1 => "输入文字", 2 => "读取元素", 3 => "聚焦", 4 => "读取整页", _ => "点击" };
            var tess = GetTess();
            if (!tess.Ready)
            {
                Skipped = true;
                LastSummary = "浏览器元素: OCR-Tesseract —— Tesseract 不可用：" + tess.Status;
                if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                return dst;
            }

            var deadline = DateTime.Now.AddSeconds(Math.Max(1, waitSeconds));
            while (true)
            {
                if (action == 4)
                {
                    using (var shot = CaptureWindow(win, out string capErr))
                    {
                        if (shot == null)
                        {
                            Skipped = true; LastSummary = "浏览器元素: OCR读取整页 —— 截图失败 " + capErr;
                            if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                            return dst;
                        }
                        string full = RecognizeWholeTess(shot);
                        if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, full);
                        LastSummary = string.Format("浏览器元素: 读取整页文本（Tesseract）→ {0} 字", full.Length);
                        return dst;
                    }
                }

                if (TryFindOcrTess(win, keyword, out OpenCvSharp.Rect box, out string shotErr))
                {
                    var bounds = win.Bounds;
                    int cx = bounds.Left + box.Left + box.Width / 2;
                    int cy = bounds.Top + box.Top + box.Height / 2;
                    return ActOnOcrPoint(cx, cy, action, keyword, inputText, varName, dst);
                }
                if (shotErr != null)
                {
                    Skipped = true; LastSummary = "浏览器元素: OCR-Tesseract —— 截图失败 " + shotErr;
                    if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                    return dst;
                }
                if (DateTime.Now >= deadline)
                {
                    Skipped = true;
                    LastSummary = string.Format("浏览器元素: {0} —— Tesseract 没找到文字「{1}」", actName, keyword);
                    if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, "");
                    if (abortIfMissing) throw new InvalidOperationException(LastSummary);
                    return dst;
                }
                System.Threading.Thread.Sleep(500);
            }
        }

        /// <summary>
        /// Tesseract 中文定位：截图 → 4×4 网格分块（带重叠）→ 每块 Tesseract 识别 →
        /// 命中关键字的块中心为目标。Tesseract 引擎不输出字符坐标，用"块中心"近似，
        /// 块足够小时足够点中按钮/输入框。
        /// </summary>
        private static bool TryFindOcrTess(Automation.TopWindow win, string keyword,
            out OpenCvSharp.Rect box, out string error)
        {
            box = default; error = null;
            using var shot = CaptureWindow(win, out string capErr);
            if (shot == null) { error = capErr; return false; }
            var tess = GetTess();
            if (!tess.Ready) return false;

            int w = shot.Width, h = shot.Height;
            int cols = 4, rows = 4;
            int cw = w / cols, chh = h / rows;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // 20% 重叠，防止关键字正好跨在块边界
                    int x0 = Math.Max(0, c * cw - cw / 5);
                    int y0 = Math.Max(0, r * chh - chh / 5);
                    int w1 = Math.Min(w - x0, cw + cw / 5);
                    int h1 = Math.Min(h - y0, chh + chh / 5);
                    using Mat sub = new(shot, new OpenCvSharp.Rect(x0, y0, w1, h1));
                    string txt = tess.Recognize(sub, out _, true);
                    if (txt != null && txt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        box = new OpenCvSharp.Rect(x0, y0, w1, h1);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>全图 Tesseract 识别（读取整页文本用）</summary>
        private static string RecognizeWholeTess(Mat shot)
        {
            var tess = GetTess();
            if (!tess.Ready) return "";
            string txt = tess.Recognize(shot, out _, true);
            return (txt ?? "").Trim('\n', '\r', ' ');
        }

        // ================================================================ 公共动作

        /// <summary>OCR 命中坐标上的动作：点击/输入/聚焦/读取命中文本</summary>
        private Mat ActOnOcrPoint(int cx, int cy, int action, string keyword, string inputText, string varName, Mat dst)
        {
            switch (action)
            {
                case 1:
                    Automation.AutomationContext.Input.Click(cx, cy);
                    System.Threading.Thread.Sleep(150);
                    Automation.AutomationContext.Input.TypeText(inputText);
                    break;
                case 2:
                    if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, keyword);
                    break;
                case 3:
                    Automation.AutomationContext.Input.MoveTo(cx, cy);
                    break;
                default:
                    Automation.AutomationContext.Input.Click(cx, cy);
                    break;
            }
            string actName = action switch { 1 => "输入文字", 2 => "读取元素", 3 => "聚焦", _ => "点击" };
            LastSummary = string.Format("浏览器元素: {0}「{1}」→ 页面坐标({2},{3})", actName, keyword, cx, cy);
            return dst;
        }

        /// <summary>截浏览器窗口区域（含标题栏，OCR 文字定位更全）</summary>
        private static Mat CaptureWindow(Automation.TopWindow win, out string error)
        {
            error = null;
            var bounds = win.Bounds;
            if (bounds.Width < 10 || bounds.Height < 10) { error = "窗口尺寸无效"; return null; }
            if (!Automation.ScreenCapture.TryCapture(bounds, out Mat mat, out string capErr))
            {
                error = capErr;
                return null;
            }
            return mat;
        }
    }
}
