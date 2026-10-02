using System;
using System.Collections.Generic;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 字符识别（OCR）：二值化 → 连通域/投影切分字符 → 逐字符归一化识别 → 输出文本。
    ///
    /// 定位说明：这是**固定字库**识别，不是通用 OCR。字库覆盖数字、大写字母与 -.:/，
    /// 适合喷码、刻印、标签上的批号/日期/型号。优势是零外部依赖、可离线、
    /// 且不会把 0/O、1/I、8/B 这类工业关键字符认错（通用引擎常在这里翻车）。
    ///
    /// 切分策略（按"分割方式"选择）：
    ///   · 连通域 —— 字符之间不相连时最稳，能自动处理字距不均
    ///   · 垂直投影 —— 字符粘连或笔画断裂时按列投影的"谷底"切，更鲁棒
    /// </summary>
    public class OcrTask : IVisionTask, IResultReporter, Automation.IStringParamTask
    {
        public string TaskName => "字符识别OCR";

        public string LastSummary { get; private set; } = "";

        /// <summary>节点属性里的「文本」框：填"要定位的目标文字"（如 确定 / LOT-123），留空则定位整块文字。</summary>
        public string NodeText { get; set; } = "";

        /// <summary>未使用（接口要求）</summary>
        public string NodeKey { get; set; } = "";

        /// <summary>识别出的文本</summary>
        public string Text { get; private set; } = "";

        /// <summary>平均置信度（0~1）</summary>
        public double MeanConfidence { get; private set; } = double.NaN;

        /// <summary>最低置信度</summary>
        public double MinConfidence { get; private set; } = double.NaN;

        /// <summary>切分出的字符数</summary>
        public int CharCount { get; private set; }

        /// <summary>每个字符的置信度</summary>
        public double[] Confidences { get; private set; } = [];

        /// <summary>Tesseract 引擎的当前状态说明（供 UI 显示为什么没用上）</summary>
        public string EngineStatus { get; private set; } = "";

        /// <summary>本次实际使用的识别引擎："tesseract" 或 "builtin"</summary>
        public string UsedEngine { get; private set; } = "";

        /// <summary>
        /// 全局共享的 Tesseract 引擎。
        /// 引擎初始化要读几十 MB 语言包、耗时数百毫秒，每个算子实例各建一份会让
        /// 参数面板每次改动都卡顿一下，故按语言缓存复用。
        /// 失败时 TryInit 只记录状态不抛异常，算子回退到内置字模引擎。
        /// </summary>
        private static TesseractEngine _sharedTess;
        private static string _sharedTessLang;
        private static readonly object _sharedLock = new();

        private static TesseractEngine GetSharedEngine(string lang, out string status)
        {
            lock (_sharedLock)
            {
                if (_sharedTess is { Ready: true } && _sharedTessLang == lang)
                {
                    status = _sharedTess.Status;
                    return _sharedTess;
                }
                if (_sharedTessLang != lang)
                {
                    _sharedTess?.Dispose();
                    _sharedTess = null;
                }
                _sharedTess ??= new TesseractEngine();
                _sharedTess.TryInit(lang);
                _sharedTessLang = lang;
                status = _sharedTess.Status;
                return _sharedTess;
            }
        }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "识别引擎 0内置1Tesseract", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "引擎:{0}", Group = "引擎", Tip = "1Tesseract（默认优先）：调用 Tesseract 引擎，" +
                "能识别中英文与任意字体（多行/任意排版），语言包在程序目录 tessdata 下；缺失时自动回退内置引擎并在结果里说明。\n" +
                "0内置：自带矢量字模，零依赖、离线可用，对 0/O、1/I、8/B 这类易混字符更稳（工业喷码首选）。" +
                "支持多行文本，自动按行分组识别。" },
            new TaskParamDesc { ParamName = "Tesseract语言 0英1中英", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "语言:{0}", Group = "引擎", Tip = "0=eng（英文+数字，最快）。" +
                "1=eng+chi_sim（中英混排，需要 chi_sim.traineddata）。" },
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "二值化" },
            new TaskParamDesc { ParamName = "极性 0亮字1暗字", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "极性:{0}", Group = "二值化", Tip = "0亮字：黑底白字。1暗字：白底黑字（打印标签常见）。" },
            new TaskParamDesc { ParamName = "分割方式 0连通域1投影", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "分割:{0}", Group = "分割", Tip = "0连通域：字符彼此分开时最稳，字距不均也能处理。" +
                "1垂直投影：字符粘连或笔画断开时用，按列投影谷底切分。多行文本会先按行聚类，再行内排序，结果按行换行。" },
            new TaskParamDesc { ParamName = "最小字高", Min = 2, Max = 2000, DefaultValue = 8,
                DisplayFormat = "字高>={0}", Group = "分割", Tip = "小于此高度的连通块当噪点丢弃。" },
            new TaskParamDesc { ParamName = "最小字宽", Min = 1, Max = 2000, DefaultValue = 2,
                DisplayFormat = "字宽>={0}", Group = "分割" },
            new TaskParamDesc { ParamName = "最大字宽", Min = 2, Max = 4000, DefaultValue = 2000,
                DisplayFormat = "字宽<={0}", Group = "分割", Tip = "大于此宽度视为多个字符粘连，" +
                "会按宽度比例再切。设很大即关闭该保护。" },
            new TaskParamDesc { ParamName = "合并间距", Min = 0, Max = 100, DefaultValue = 2,
                DisplayFormat = "合并:{0}px", Group = "分割", Tip = "水平间距小于此值的两个块视为同一字符" +
                "（如 i 的点、断裂的笔画）。连通域模式下有效。" },
            new TaskParamDesc { ParamName = "字库 0数字1数字+字母2全部", Min = 0, Max = 2, DefaultValue = 1,
                DisplayFormat = "字库:{0}", Group = "字符集", Tip = "限定候选字符集能显著降低误识。" +
                "0只有数字（纯批号/日期最准）。1数字+大写字母。2再加 -.:/ 符号。" },
            new TaskParamDesc { ParamName = "形态学核", Min = 0, Max = 31, DefaultValue = 0,
                DisplayFormat = "核:{0}", Group = "后处理", Tip = "识别前做一次开运算去毛刺。" +
                "0 关闭。喷码有飞点/毛刺时设 3~5 能提升切分质量。" },
            new TaskParamDesc { ParamName = "显示 0框1文本", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "显示:{0}", Group = "输出" },
            // 新增参数一律追加到末尾：参数在流水线 JSON 里按索引保存，插中间会让老图错位。
            new TaskParamDesc { ParamName = "多行识别 0单行1自动分行", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "多行:{0}", Group = "分割",
                Tip = "0单行：整幅图只认一行（按 X 排序），适合只有一行喷码、忽略画面里无关行的情况。\n" +
                "1自动分行（默认）：先按水平投影把多行文本切成行带，再逐行识别、按行换行输出。" },
            new TaskParamDesc { ParamName = "发布检测目标 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "发布:{0}", Group = "输出",
                Tip = "识别完成后把文字位置发布为“检测目标”，供后面的“鼠标点击（目标来源=上次检测）”直接使用：\n" +
                "  · 节点属性 →「文本」框留空 → 发布整块文字的中心；\n" +
                "  · 填目标文字（如 确定 / LOT-123 / OK）→ 只发布包含该文字的那块的中心。\n" +
                "位置是图像坐标，鼠标点击节点会自动加上截图区域原点换算成屏幕坐标。\n" +
                "Tesseract 引擎不输出字符坐标，此项对它无效（会回退内置引擎才有定位）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Text = "";
            MeanConfidence = MinConfidence = double.NaN;
            CharCount = 0;
            Confidences = [];
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            int engine = paramValues[0], tessLang = paramValues[1];
            int threshold = paramValues[2], polarity = paramValues[3], splitMode = paramValues[4];
            int minH = paramValues[5], minW = paramValues[6], maxW = paramValues[7];
            int mergeGap = paramValues[8], charsetSel = paramValues[9];
            int morphK = paramValues[10], display = paramValues[11];
            // 老图参数数组长度不足 13 → 默认开启多行（与引入开关前的行为一致，不产生回归）
            int multiLine = paramValues.Length > 12 ? paramValues[12] : 1;
            // 老图（12 参数）没有发布位 → 默认不发布，行为与之前完全一致
            int publishOn = paramValues.Length > 13 ? Math.Clamp(paramValues[13], 0, 1) : 0;
            string targetText = Automation.AutomationContext.ExpandVariables(NodeText ?? "").Trim();

            string charset = charsetSel switch
            {
                0 => CharRecognizer.Digits,
                2 => CharRecognizer.Digits + CharRecognizer.Upper + CharRecognizer.Common,
                _ => CharRecognizer.Digits + CharRecognizer.Upper,
            };

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            if (morphK >= 3)
            {
                int k = morphK % 2 == 0 ? morphK + 1 : morphK;
                using Mat el = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, el);
            }

            // —— Tesseract 分支 ——
            // 直接喂预处理后的二值图，不做字符切分：Tesseract 自己会做版面分析，
            // 预切分会丢掉字间/行间上下文，反而降低准确率。
            if (engine == 1)
            {
                string lang = tessLang == 1 ? "eng+chi_sim" : "eng";
                TesseractEngine te = GetSharedEngine(lang, out string tessStatus);
                EngineStatus = tessStatus;
                if (te.Ready)
                {
                    // Tesseract 期待"黑字白底"；内置极性的 1=暗字 正好是白底黑字，
                    // 而 0=亮字（黑底白字）需要反相，否则识别为空。
                    using Mat tessInput = new();
                    if (polarity == 0) Cv2.BitwiseNot(bin, tessInput);
                    else bin.CopyTo(tessInput);

                    // 多行开关同时作用于 Tesseract：multiLine=1 → SingleBlock 版面模式并保留换行
                    string tessText = te.Recognize(tessInput, out double tessConf, multiLine == 1);
                    if (tessText != null)
                    {
                        UsedEngine = "tesseract";
                        Text = tessText.Trim('\n', '\r', ' ');
                        CharCount = Text.Replace("\n", "").Replace("\r", "").Length;
                        MeanConfidence = tessConf;
                        MinConfidence = tessConf;
                        Confidences = new double[Text.Length];
                        for (int i = 0; i < Confidences.Length; i++) Confidences[i] = tessConf;

                        // 多行安全绘制：GDI+ 自动换行；Tesseract 结果可能含 \n
                        MatDraw.DrawText(dst, string.Format("Tesseract [{0}] conf {1:F1}%",
                            lang, tessConf * 100), 6, 20, Scalar.LimeGreen, 12);
                        MatDraw.DrawText(dst, Text, 6, 42, Scalar.Cyan, 14);

                        string shown = Text.Replace("\n", " | ");
                        LastSummary = string.Format(
                            "字符识别OCR[Tesseract/{0}{1}]: \"{2}\"  ({3} 字, 置信 {4:F2})",
                            lang, multiLine == 1 ? "多行" : "单行", shown, CharCount, MeanConfidence);
                        if (publishOn == 1)
                            LastSummary += "  [发布开启，但 Tesseract 不输出字符坐标，未发布检测目标]";
                        return dst;
                    }
                    // 识别返回 null = 引擎内部异常，回退内置引擎继续跑
                }

                // —— 回退：把原因明确写进结果，而不是静默地换个引擎 ——
                EngineStatus = "Tesseract 不可用，已回退内置引擎：" + tessStatus;
            }

            UsedEngine = "builtin";
            if (engine == 1)
                MatDraw.DrawText(dst, "回退内置引擎（Tesseract 不可用）", 6, 60, Scalar.Orange, 12);

            // —— 切分：多行模式先按水平投影切出行带，再对每行分别切字符；单行模式整图切一次 ——
            var lines = new List<List<(char ch, Rect box, double conf, double score)>>();
            var chars = new List<(char ch, Rect box, double conf, double score)>();

            if (multiLine == 1)
            {
                foreach (Rect band in SplitRows(bin, minH))
                {
                    using Mat rowBin = new(bin, band);
                    List<Rect> boxes = splitMode == 1
                        ? SplitByProjection(rowBin, minW)
                        : SplitByComponents(rowBin, minH, minW, maxW, mergeGap);

                    var rowChars = new List<(char ch, Rect box, double conf, double score)>();
                    foreach (Rect b in boxes)
                    {
                        if (b.Width < 1 || b.Height < 1 ||
                            b.X < 0 || b.Y < 0 || b.Right > rowBin.Cols || b.Bottom > rowBin.Rows) continue;
                        using Mat crop = new(rowBin, b);
                        CharRecognizer.Match mt = CharRecognizer.Recognize(crop, charset, out _);
                        // 行带局部坐标 → 全图坐标（用于绘制）
                        Rect gb = new(b.X, b.Y + band.Y, b.Width, b.Height);
                        rowChars.Add((mt.Ch, gb, mt.Confidence, mt.Score));
                    }
                    rowChars.Sort((a, b) => a.box.X.CompareTo(b.box.X));
                    if (rowChars.Count > 0)
                    {
                        lines.Add(rowChars);
                        chars.AddRange(rowChars);
                    }
                }
            }
            else
            {
                // 单行模式：整幅图直接切，按 X 排序认成一行（老行为，作为可选项保留）
                List<Rect> boxes = splitMode == 1
                    ? SplitByProjection(bin, minW)
                    : SplitByComponents(bin, minH, minW, maxW, mergeGap);
                var rowChars = new List<(char ch, Rect box, double conf, double score)>();
                foreach (Rect b in boxes)
                {
                    if (b.Width < 1 || b.Height < 1 ||
                        b.X < 0 || b.Y < 0 || b.Right > bin.Cols || b.Bottom > bin.Rows) continue;
                    using Mat crop = new(bin, b);
                    CharRecognizer.Match mt = CharRecognizer.Recognize(crop, charset, out _);
                    rowChars.Add((mt.Ch, b, mt.Confidence, mt.Score));
                }
                rowChars.Sort((a, b) => a.box.X.CompareTo(b.box.X));
                if (rowChars.Count > 0)
                {
                    lines.Add(rowChars);
                    chars.AddRange(rowChars);
                }
            }

            if (chars.Count == 0)
            {
                LastSummary = "字符识别OCR: 未切分出字符（检查阈值/极性/最小字高）";
                return dst;
            }

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                foreach (var (ch, _, _, _) in line) sb.Append(ch);
                sb.Append('\n');
            }
            if (sb.Length > 0) sb.Length--;   // 去掉末尾换行
            Text = sb.ToString();

            Confidences = new double[chars.Count];
            for (int i = 0; i < chars.Count; i++) Confidences[i] = chars[i].conf;
            CharCount = chars.Count;
            double sum = 0, min = double.MaxValue;
            foreach (var (_, _, conf, _) in chars) { sum += conf; if (conf < min) min = conf; }
            MeanConfidence = sum / chars.Count;
            MinConfidence = min;

            // —— 结果绘制 ——
            foreach (var (ch, box, conf, score) in chars)
            {
                // 低置信用红框提示复核，高置信用绿框
                Scalar col = conf >= 0.15 ? Scalar.LimeGreen : Scalar.Red;
                Cv2.Rectangle(dst, box, col, 1);
                if (display == 1)
                {
                    Cv2.PutText(dst, ch.ToString(),
                        new Point(box.X + (box.Width / 4), Math.Max(14, box.Y - 3)),
                        HersheyFonts.HersheySimplex, 0.45, Scalar.Yellow, 1, LineTypes.AntiAlias);
                }
                else
                {
                    Cv2.PutText(dst, string.Format("{0}{1:F0}", ch, conf * 100),
                        new Point(box.X, Math.Max(11, box.Y - 3)),
                        HersheyFonts.HersheySimplex, 0.34, Scalar.Yellow, 1, LineTypes.AntiAlias);
                }
            }
            // 顶部结果文本：多行时 GDI+ 自动换行
            if (engine == 1)
                MatDraw.DrawText(dst, "回退内置引擎（Tesseract 不可用）", 6, 20, Scalar.Orange, 12);
            MatDraw.DrawText(dst, Text, 6, engine == 1 ? 42 : 20, Scalar.Cyan, 14);

            string engineNote = engine == 1 ? " [内置引擎]" : "";
            if (engine == 1 && !string.IsNullOrEmpty(EngineStatus))
                engineNote = " [" + EngineStatus + "]";

            string shownText = Text.Replace("\n", " | ");
            string publishNote = "";
            if (publishOn == 1)
                publishNote = "  " + PublishTarget(lines, targetText);
            LastSummary = string.Format(
                "字符识别OCR{0}: \"{1}\"  ({2} 字, {3} 行, 平均置信 {4:F2}, 最低 {5:F2}){6}",
                engineNote, shownText, CharCount, lines.Count, MeanConfidence, MinConfidence, publishNote);
            return dst;
        }

        /// <summary>
        /// 把识别文字的位置发布为“检测目标”，供后面的“鼠标点击（目标来源=上次检测）”使用。
        /// 目标文字非空 → 定位包含该文字的那一块（行内连续匹配，忽略大小写）；
        /// 留空 → 发布整块文字的中心。发布的是**图像坐标**，鼠标点击节点会自行换算成屏幕坐标。
        /// 返回给摘要的定位描述（空 = 无需说明）。
        /// </summary>
        private string PublishTarget(
            List<List<(char ch, Rect box, double conf, double score)>> lines, string target)
        {
            if (lines.Count == 0) return "";

            List<(char ch, Rect box, double conf, double score)> hit;
            Rect box;
            string what;
            if (target.Length > 0)
            {
                hit = FindTarget(lines, target);
                if (hit == null)
                    return string.Format("目标文字“{0}”未识别到，未发布检测目标", target);
                box = UnionBoxes(hit);
                what = target;
            }
            else
            {
                var all = new List<(char ch, Rect box, double conf, double score)>();
                foreach (var line in lines) all.AddRange(line);
                if (all.Count == 0) return "";
                box = UnionBoxes(all);
                what = "整块文字";
            }

            float cx = box.X + box.Width / 2.0f;
            float cy = box.Y + box.Height / 2.0f;
            Automation.DetectionStore.Publish(new Point2f(cx, cy), MeanConfidence, "OCR", box, 100);
            return string.Format("已定位{0}中心 图像({1},{2}) 发布为检测目标", what, (int)cx, (int)cy);
        }

        /// <summary>在行内找目标文字的连续字符子序列（忽略大小写；OCR 字库输出全大写）。</summary>
        private static List<(char ch, Rect box, double conf, double score)> FindTarget(
            List<List<(char ch, Rect box, double conf, double score)>> lines, string target)
        {
            string t = target.ToUpperInvariant();
            if (t.Length == 0) return null;
            foreach (var line in lines)
            {
                for (int i = 0; i + t.Length <= line.Count; i++)
                {
                    bool ok = true;
                    for (int j = 0; j < t.Length; j++)
                        if (char.ToUpperInvariant(line[i + j].ch) != t[j]) { ok = false; break; }
                    if (!ok) continue;
                    var hit = new List<(char ch, Rect box, double conf, double score)>();
                    for (int j = 0; j < t.Length; j++) hit.Add(line[i + j]);
                    return hit;
                }
            }
            return null;
        }

        /// <summary>字符框并集（用于“目标文字块 / 整块文字”的定位框）</summary>
        private static Rect UnionBoxes(List<(char ch, Rect box, double conf, double score)> cs)
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (var (_, b, _, _) in cs)
            {
                if (b.X < x1) x1 = b.X;
                if (b.Y < y1) y1 = b.Y;
                if (b.Right > x2) x2 = b.Right;
                if (b.Bottom > y2) y2 = b.Bottom;
            }
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        /// <summary>
        /// 行分割：按水平投影（每行前景像素数）找"空白行"分隔，把多行文本切成
        /// 一个或多个行带（返回全图坐标的 Rect）。这是多行识别的第一步：
        /// 先分行，行内再切字符，避免上下行同列字符在切分阶段被并到一起。
        ///
        /// 投影用 Cv2.Reduce 一次矩阵求和得到（原生实现），比逐像素 At&lt;byte&gt; 快一个数量级；
        /// "空白行"判定带容差（前景数 ≤ 最大行的 0.5% 视为空白），轻微噪点/倾斜不再把整图粘成一行。
        /// </summary>
        private static List<Rect> SplitRows(Mat bin, int minH)
        {
            var result = new List<Rect>();
            int h = bin.Rows;
            int[] proj = RowProjection(bin);
            int blankCap = Math.Max(1, MaxOf(proj) / 200);   // 0.5% 容差

            int start = -1;
            for (int y = 0; y < h; y++)
            {
                bool blank = proj[y] <= blankCap;
                if (!blank && start < 0) start = y;
                else if ((blank || y == h - 1) && start >= 0)
                {
                    int end = blank ? y : y + 1;
                    if (end - start >= minH)
                        result.Add(new Rect(0, start, bin.Cols, end - start));
                    start = -1;
                }
            }
            return result;
        }

        /// <summary>每行前景像素数（Cv2.Reduce 按行求和 → 单列，CV_32S 防溢出）</summary>
        private static int[] RowProjection(Mat bin)
        {
            using Mat rowSum = new();
            Cv2.Reduce(bin, rowSum, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32S);
            int[] proj = new int[bin.Rows];
            for (int y = 0; y < bin.Rows; y++) proj[y] = rowSum.At<int>(y, 0);
            return proj;
        }

        private static int MaxOf(int[] a)
        {
            int m = 0;
            foreach (int v in a) if (v > m) m = v;
            return m;
        }

        /// <summary>
        /// 连通域切分：取外接矩形，按水平间距把"靠得近"的块合并成同一字符
        /// （i 的点、断裂的笔画都是分开的块，但属于同一个字）。
        /// </summary>
        private static List<Rect> SplitByComponents(Mat bin, int minH, int minW, int maxW, int mergeGap)
        {
            var result = new List<Rect>();
            Cv2.FindContours(bin, out Point[][] cs, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            var rects = new List<Rect>();
            foreach (Point[] c in cs)
            {
                Rect r = Cv2.BoundingRect(c);
                if (r.Height < minH || r.Width < minW) continue;
                rects.Add(r);
            }
            if (rects.Count == 0) return result;
            rects.Sort((a, b) => a.X.CompareTo(b.X));

            // 合并：与当前组水平重叠或间距很小 -> 同组
            var group = new List<Rect> { rects[0] };
            for (int i = 1; i < rects.Count; i++)
            {
                Rect cur = rects[i];
                int gLeft = int.MaxValue, gRight = int.MinValue;
                foreach (Rect g in group)
                {
                    if (g.X < gLeft) gLeft = g.X;
                    if (g.Right > gRight) gRight = g.Right;
                }
                // 水平间隙 ≤ mergeGap 视为同一字符
                bool same = cur.X <= gRight + mergeGap;
                if (same) group.Add(cur);
                else { result.Add(UnionOf(group)); group = [cur]; }
            }
            result.Add(UnionOf(group));

            // 超宽块再按宽度比例切（粘连字符保护）
            var final = new List<Rect>();
            foreach (Rect r in result)
            {
                if (maxW > 0 && r.Width > maxW * 1.6)
                {
                    int parts = Math.Max(2, (int)Math.Round((double)r.Width / maxW));
                    int pw = r.Width / parts;
                    for (int p = 0; p < parts; p++)
                    {
                        int x = r.X + (p * pw);
                        int w = p == parts - 1 ? r.Right - x : pw;
                        if (w >= minW && r.Height >= minH) final.Add(new Rect(x, r.Y, w, r.Height));
                    }
                }
                else final.Add(r);
            }
            return final;
        }

        private static Rect UnionOf(List<Rect> rs)
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (Rect r in rs)
            {
                if (r.X < x1) x1 = r.X;
                if (r.Y < y1) y1 = r.Y;
                if (r.Right > x2) x2 = r.Right;
                if (r.Bottom > y2) y2 = r.Bottom;
            }
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        /// <summary>
        /// 垂直投影切分：对每列求前景像素数，平滑后在"谷底"（投影极小）处切。
        /// 相比连通域，它对笔画断裂（一个字被切成两块）更鲁棒，
        /// 但对字符粘连仍然无能为力 —— 两者是互补的，故都提供。
        ///
        /// 列投影用 Cv2.Reduce 一次矩阵求和（原生实现）；字符带的纵向范围用
        /// Cv2.FindNonZero 一次扫描取得，替代逐像素 At 循环。
        /// </summary>
        private static List<Rect> SplitByProjection(Mat bin, int minW)
        {
            var result = new List<Rect>();
            int w = bin.Cols;
            int[] proj = ColProjection(bin);

            // 平滑投影抑制毛刺造成的假谷
            int[] sm = new int[w];
            const int half = 2;
            for (int x = 0; x < w; x++)
            {
                int s = 0, n = 0;
                for (int d = -half; d <= half; d++)
                {
                    int xx = x + d;
                    if (xx < 0 || xx >= w) continue;
                    s += proj[xx]; n++;
                }
                sm[x] = n > 0 ? s / n : 0;
            }

            // 阈值取投影最大值的一个比例，低于它即"空白列"
            // 只要有任一前景像素就不算空白列（投影切分只依赖"列是否为空"）
            int emptyThresh = 0;

            int start = -1;
            for (int x = 0; x < w; x++)
            {
                bool blank = sm[x] <= emptyThresh;
                if (!blank && start < 0) start = x;
                else if ((blank || x == w - 1) && start >= 0)
                {
                    int end = blank ? x : x + 1;
                    int bw = end - start;
                    if (bw >= minW)
                    {
                        // 该列段的纵向范围：一次 FindNonZero 拿所有前景像素，取 y 的 min/max
                        int y1 = int.MaxValue, y2 = int.MinValue;
                        using Mat sub = bin.ColRange(start, end);   // 视图，不拷贝
                        using Mat nz = new();
                        Cv2.FindNonZero(sub, nz);
                        for (int i = 0; i < nz.Rows; i++)
                        {
                            Vec2i p = nz.Get<Vec2i>(i, 0);
                            if (p.Item1 < y1) y1 = p.Item1;
                            if (p.Item1 > y2) y2 = p.Item1;
                        }
                        if (y2 >= y1) result.Add(new Rect(start, y1, bw, y2 - y1 + 1));
                    }
                    start = -1;
                }
            }
            return result;
        }

        /// <summary>每列前景像素数（Cv2.Reduce 按列求和 → 单行，CV_32S 防溢出）</summary>
        private static int[] ColProjection(Mat bin)
        {
            using Mat colSum = new();
            Cv2.Reduce(bin, colSum, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            int[] proj = new int[bin.Cols];
            for (int x = 0; x < bin.Cols; x++) proj[x] = colSum.At<int>(0, x);
            return proj;
        }
    }
}
