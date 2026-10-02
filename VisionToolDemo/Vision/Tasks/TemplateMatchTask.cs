using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    public class TemplateMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "模板匹配";

        // UI层赋值：选择的模板图片
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>
        /// 最近一次匹配的**最高分**（0~1，归一化相关）。
        ///
        /// 为什么必须暴露它：未命中时只告诉用户"没匹配上"是无法排查的 ——
        /// 分数 0.05（完全是别的东西）和 0.55（几乎命中但阈值太高/分辨率不一致）
        /// 需要完全不同的处理。实测一个点阵 DPM 码的案例：阈值 80% 报未命中，
        /// 而实际最高分是 0.549，真实原因是模板比图中目标小了 2.75 倍。
        /// 不把这个数字显示出来，用户只能反复盲试。
        /// </summary>
        /// <summary>本次是否命中（供自动化的"条件"节点分支用）</summary>
        public bool Found { get; private set; }

        public double BestScore { get; private set; } = double.NaN;

        /// <summary>最高分出现的位置（原图坐标，模板左上角）</summary>
        public Point BestLocation { get; private set; }

        /// <summary>命中的目标中心（原图坐标）—— 鼠标定位要的就是它</summary>
        public Point2f BestCenter { get; private set; }

        /// <summary>命中框（原图坐标：左上角 + 尺寸）</summary>
        public Rect BestBox { get; private set; }

        /// <summary>命中时的模板倍率（%）。单尺度匹配时等于实际使用的缩放值</summary>
        public int BestScale { get; private set; }

        /// <summary>阈值以上的命中点个数（原始计数，重叠位置也算）</summary>
        public int MatchCount { get; private set; }

        /// <summary>去重后真正画出来的框个数（互相重叠的只留一个）</summary>
        public int DistinctCount { get; private set; }

        /// <summary>本次实际尝试的倍率个数（多尺度范围被步数上限截断时也反映在这）</summary>
        public int ScalesTried { get; private set; }

        /// <summary>本次实际尝试的倍率列表（调试/测试用）</summary>
        public int[] UsedScales { get; private set; } = [];

        /// <summary>状态 = 模板图（base64 PNG）</summary>
        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "匹配阈值",
                Min = 50,
                Max = 100,
                DefaultValue = 80,
                DisplayFormat = "matchThresh:{0}%",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 多尺度：模板先按此百分比缩放再匹配（模板与原图分辨率不一致时用），
                // 100% = 原尺寸，不做任何缩放
                ParamName = "模板缩放",
                Min = 10,
                Max = 400,
                DefaultValue = 100,
                DisplayFormat = "tplScale:{0}%",
                ForceOdd = false,
                Tip = "单一倍率。只在下面的「缩放下限」=0（关闭多尺度）时使用。"
            },
            // 下面的多尺度参数一律追加在末尾：参数在流水线/节点图 JSON 里按索引保存，
            // 插到中间会让已保存的图全部错位。
            new TaskParamDesc
            {
                ParamName = "缩放下限% 0=关",
                Min = 0,
                Max = 400,
                DefaultValue = 0,
                DisplayFormat = "缩放下限:{0}%",
                ForceOdd = false,
                Tip = "多尺度搜索的下限。0 = 关闭多尺度，只用「模板缩放」那一个倍率。\n" +
                "模板与图中目标分辨率不一致时打开它：例如模板 100x75、图中目标约 160x120，\n" +
                "就填下限 150、上限 180（命中后会报出实际倍率）。"
            },
            new TaskParamDesc
            {
                ParamName = "缩放上限%",
                Min = 0,
                Max = 400,
                DefaultValue = 0,
                DisplayFormat = "缩放上限:{0}%",
                ForceOdd = false,
                Tip = "多尺度搜索的上限（会试到）。必须 ≥ 下限，否则按下限处理。"
            },
            new TaskParamDesc
            {
                ParamName = "缩放步进%",
                Min = 1,
                Max = 50,
                DefaultValue = 5,
                DisplayFormat = "缩放步进:{0}%",
                ForceOdd = false,
                Tip = "每档缩放间隔。越小越准但越慢；步进细 + 范围宽会被自动压到最多 " +
                MaxScaleSteps + " 档（每次都要重扫整图，实测每档 ~10-40ms）。"
            }
        };

        /// <summary>多尺度扫描的档数上限。范围宽 + 步进细会变成几百次全图匹配，必须封顶</summary>
        private const int MaxScaleSteps = 40;

        /// <summary>倍率最小可匹配尺寸（缩放后模板小于这个尺寸没有匹配意义）</summary>
        private const int MinTileSize = 8;

        /// <summary>
        /// 算出这次要试哪些倍率。
        /// 下限 = 0 → 关闭多尺度，只试「模板缩放」那一个倍率（保持老行为）。
        /// 下限 &gt; 0 → 在 [下限, 上限] 内按步进取值，上限一定会试到；
        /// 档数超过 MaxScaleSteps 时自动放大步进（宁可粗一点，也不能把界面卡死）。
        /// </summary>
        public static List<int> BuildScales(int singleScale, int lower, int upper, int step)
        {
            var list = new List<int>();
            if (lower <= 0)
            {
                list.Add(Math.Clamp(singleScale, 10, 400));
                return list;
            }

            int lo = Math.Clamp(lower, 10, 400);
            int hi = Math.Clamp(upper < lo ? lo : upper, lo, 400);
            if (hi == lo) { list.Add(lo); return list; }

            int st = Math.Clamp(step, 1, 50);
            if (((hi - lo) / st) + 1 > MaxScaleSteps)
                st = (int)Math.Ceiling((hi - lo) / (double)(MaxScaleSteps - 1));

            for (int s = lo; s < hi && list.Count < MaxScaleSteps - 1; s += st) list.Add(s);
            list.Add(hi);       // 上限一定要试到
            return list;
        }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            Found = false;
            BestScore = double.NaN;
            BestLocation = new Point();
            BestCenter = new Point2f();
            BestBox = new Rect();
            BestScale = 0;
            MatchCount = 0;
            DistinctCount = 0;
            ScalesTried = 0;
            UsedScales = [];
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "模板匹配: 请先导入模板图片";
                return srcMat.Clone();
            }

            int thresholdPercent = paramValues[0];
            int scalePercent = paramValues[1];
            // 旧图（保存时还没有这三个参数）数组更短，越界读会抛
            int lower = paramValues.Length > 2 ? paramValues[2] : 0;
            int upper = paramValues.Length > 3 ? paramValues[3] : 0;
            int step = paramValues.Length > 4 ? paramValues[4] : 5;
            double matchThresh = thresholdPercent / 100.0;

            var scales = BuildScales(scalePercent, lower, upper, step);
            UsedScales = scales.ToArray();
            int multiScale = scales.Count > 1 ? 1 : 0;

            using (Mat srcGray = VisionHelper.ToGray(srcMat))
            using (Mat templFull = VisionHelper.ToGray(TemplateMat))
            {
                // 纯色/几乎没有纹理的模板：CCoeffNormed 要除以模板自身的标准差，
                // 分母趋零时会给出"恒等于 1"的假高分（实测纯黑 40x40 模板在任意图上
                // 都得 100%），命中位置随之变成垃圾值 —— 自动化里这就是照着错的坐标点下去。
                // 宁可明确报错让用户重框，也不能输出一个假坐标。
                Cv2.MeanStdDev(templFull, out _, out Scalar tplStd);
                if (tplStd.Val0 < MinTemplateStdDev)
                {
                    LastSummary = string.Format(
                        "模板匹配: 模板几乎是纯色（灰度标准差 {0:F1} < {1:F0}），无法定位。" +
                        "请重新框选**有纹理/边缘**的区域作为模板",
                        tplStd.Val0, MinTemplateStdDev);
                    Automation.DetectionStore.Invalidate("模板无纹理（纯色模板会给出假高分）");
                    return VisionHelper.ToBgrCopy(srcMat);
                }

                double bestVal = -1;
                Point bestLoc = default;
                int bestTw = 0, bestTh = 0, bestScalePct = 0;
                Mat bestResult = null;      // 最佳倍率那张响应图：命中框要在它上面找
                int tooBig = 0;
                try
                {
                    foreach (int pct in scales)
                    {
                        int nw = (int)Math.Round(templFull.Cols * pct / 100.0);
                        int nh = (int)Math.Round(templFull.Rows * pct / 100.0);
                        if (nw < MinTileSize || nh < MinTileSize) { tooBig++; continue; }
                        if (nw > srcGray.Cols || nh > srcGray.Rows) { tooBig++; continue; }

                        // 尺寸不变时 resized 为 null（不重复缩放），templGray 直接用原模板
                        using Mat resized = (nw == templFull.Cols && nh == templFull.Rows)
                            ? null
                            : ResizeTemplate(templFull, nw, nh);
                        Mat templGray = resized ?? templFull;

                        using Mat result = new();
                        Cv2.MatchTemplate(srcGray, templGray, result, TemplateMatchModes.CCoeffNormed);

                        // 最高分与位置：未命中时也要报告，否则用户无法判断
                        // "差一点"还是"完全不是这个目标"
                        Cv2.MinMaxLoc(result, out _, out double v, out _, out Point loc);
                        ScalesTried++;
                        if (v > bestVal)
                        {
                            bestVal = v;
                            bestLoc = loc;
                            bestTw = templGray.Cols;
                            bestTh = templGray.Rows;
                            bestScalePct = pct;
                            bestResult?.Dispose();
                            bestResult = result.Clone();
                        }
                    }

                    if (bestResult == null)
                    {
                        LastSummary = string.Format(
                            "模板匹配: 缩放 {0} 后模板都放不进原图 {1}x{2}（试过 {3} 档）",
                            multiScale == 1 ? string.Format("{0}~{1}%", scales[0], scales[scales.Count - 1])
                                            : scalePercent + "%",
                            srcGray.Cols, srcGray.Rows, scales.Count);
                        return VisionHelper.ToBgrCopy(srcMat);
                    }

                    BestScore = bestVal;
                    BestLocation = bestLoc;
                    BestScale = bestScalePct;
                    BestBox = new Rect(bestLoc.X, bestLoc.Y, bestTw, bestTh);
                    BestCenter = new Point2f(bestLoc.X + (bestTw / 2f), bestLoc.Y + (bestTh / 2f));

                    Mat dst = VisionHelper.ToBgrCopy(srcMat);
                    // 多尺度时既要说清"试了哪个范围"，更要说清"最后落在哪个倍率" ——
                    // 用户拿这个倍率去判断目标到底比模板大多少
                    string scaleInfo = multiScale == 1
                        ? string.Format("多尺度 {0}~{1}%/{2}% 试 {3} 档，命中倍率 {4}%",
                            scales[0], scales[scales.Count - 1], step, ScalesTried, bestScalePct)
                        : string.Format("倍率 {0}%", bestScalePct);
                    string tplInfo = string.Format("模板 {0}x{1} ({2})", bestTw, bestTh, scaleInfo);
                    string coordInfo = CoordText(BestCenter);

                    // 原生阈值化 + FindNonZero 一次取出全部命中点，
                    // 替代逐像素 Get 的托管循环（且 CV_32F 结果按 double 读值是错的）
                    using (Mat hits = new())
                    using (Mat nz = new())
                    {
                        Cv2.Threshold(bestResult, hits, matchThresh - 1e-6, 255, ThresholdTypes.Binary);
                        Cv2.FindNonZero(hits, nz);

                        // 相关性曲面在目标附近是一大片平台，阈值化后同一个目标会给出成百上千个
                        // 相邻位置。全画出来结果图就被红框糊满了（实测 174 个框），
                        // 既看不出目标在哪、也盖住了要核对的位置 —— 按重叠度去重后再画。
                        var raw = new List<(float Score, Rect R)>(nz.Rows);
                        for (int i = 0; i < nz.Rows; i++)
                        {
                            Vec2i p = nz.Get<Vec2i>(i, 0);
                            raw.Add((bestResult.At<float>(p.Item1, p.Item0), new Rect(p.Item0, p.Item1, bestTw, bestTh)));
                        }
                        MatchCount = raw.Count;
                        var kept = SuppressOverlaps(raw, MaxDrawnBoxes);
                        DistinctCount = kept.Count;
                        foreach (var r in kept) Cv2.Rectangle(dst, r, Scalar.Red, 2);

                        Found = MatchCount > 0;
                        if (Found)
                        {
                            // 发布命中位置给动作算子（"鼠标点击"的"目标来源=上次检测"就取这里）。
                            // 响应图坐标是模板左上角，换算成模板中心再发布 ——
                            // 动作算子要的是"目标中心"，不是左上角。
                            Automation.DetectionStore.Publish(BestCenter, bestVal, "模板匹配", BestBox, bestScalePct);

                            // 摘要里明确给出坐标：自动化里"命中"只是前提，
                            // 用户真正要确认的是"它准备点哪里"，不给坐标就只能靠干跑猜。
                            LastSummary = string.Format("模板匹配: 命中 {0} 处{1} (最高分 {2:F0}%, 阈值 {3}%, {4}) {5}",
                                MatchCount,
                                DistinctCount != MatchCount ? string.Format("（去重后 {0} 处）", DistinctCount) : "",
                                bestVal * 100, thresholdPercent, tplInfo, coordInfo);
                        }
                        else
                        {
                            // 未命中时给出最高分 + 可操作提示。
                            // 分数接近阈值 ⇒ 大概率是"模板与图像分辨率不一致"或"阈值偏高"，
                            // 这两种情况调一下参数就能救回来，必须提示，否则用户只会以为"没有这个目标"。
                            string hint = "";
                            if (multiScale == 0 && bestVal >= 0.35)
                                hint = "。分数已接近阈值：多半是模板与图像分辨率不一致（试填「缩放下限/上限」，" +
                                       "例如 80~150）或阈值偏高；也可能是目标有旋转（模板匹配不支持旋转，改用「形状匹配」）";
                            else if (multiScale == 1 && bestVal >= 0.35)
                                hint = "。分数已接近阈值：把「缩放下限/上限」范围放宽一点，或确认目标有旋转（改用「形状匹配」）";
                            else if (bestVal >= 0.15)
                                hint = "。分数偏低：请确认模板是从本图**同一分辨率**下裁出来的";

                            LastSummary = string.Format(
                                "模板匹配: 未命中 (最高分 {0:F0}% @ ({1},{2}), 阈值 {3}%, {4}){5}",
                                bestVal * 100, bestLoc.X, bestLoc.Y, thresholdPercent, tplInfo, hint);

                            // 作废目标：否则后面的"鼠标点击"会用这一轮更早某个匹配节点留下的
                            // 陈旧坐标点下去（目标早就不在那儿了）
                            Automation.DetectionStore.Invalidate(string.Format(
                                "模板匹配未命中（最高分 {0:F0}% < 阈值 {1}%; {2}）",
                                bestVal * 100, thresholdPercent, tplInfo));

                            // 把最高分位置画出来，方便目视判断"差在哪"
                            if (bestVal > 0)
                                Cv2.Rectangle(dst, new Rect(bestLoc.X, bestLoc.Y, bestTw, bestTh), Scalar.Orange, 2);
                        }
                    }

                    return dst;
                }
                finally { bestResult?.Dispose(); }
            }
        }

        /// <summary>模板灰度的最小标准差：低于它视为纯色/无纹理</summary>
        private const double MinTemplateStdDev = 2.0;

        /// <summary>结果图上最多画几个框（去重之后）</summary>
        private const int MaxDrawnBoxes = 20;

        /// <summary>重叠度超过这个值就认为是同一个目标的相邻位置</summary>
        private const double OverlapKeepThreshold = 0.3;

        /// <summary>
        /// 贪心去重：按分数从高到低保留，和已保留框重叠度超阈值就丢掉。
        /// O(n·k)，n 是阈值以上的位置数、k 是保留数（上限 20），实测几毫秒。
        /// </summary>
        private static List<Rect> SuppressOverlaps(List<(float Score, Rect R)> hits, int maxKeep)
        {
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<Rect>();
            foreach (var h in hits)
            {
                if (kept.Count >= maxKeep) break;
                bool overlapped = false;
                foreach (var k in kept)
                {
                    if (OverlapRatio(k, h.R) > OverlapKeepThreshold) { overlapped = true; break; }
                }
                if (!overlapped) kept.Add(h.R);
            }
            return kept;
        }

        /// <summary>交集面积 / 较小框面积（用较小框做分母：一大一小叠在一起也算同一个目标）</summary>
        private static double OverlapRatio(Rect a, Rect b)
        {
            int x1 = Math.Max(a.Left, b.Left), y1 = Math.Max(a.Top, b.Top);
            int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
            if (x2 <= x1 || y2 <= y1) return 0;
            double inter = (x2 - x1) * (double)(y2 - y1);
            double smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
            return smaller <= 0 ? 0 : inter / smaller;
        }

        /// <summary>
        /// 坐标文本：图像坐标 + （本轮确实抓过屏时）换算好的屏幕坐标。
        /// 鼠标点击算子用的就是"屏幕坐标 = 截图原点 + 图像坐标"这个换算，
        /// 这里提前算出来，用户不用自己加。
        /// </summary>
        private static string CoordText(Point2f center)
        {
            string s = string.Format("目标中心 图像({0:F0},{1:F0})", center.X, center.Y);
            if (Automation.AutomationContext.HasCapture)
            {
                int sx = (int)Math.Round(Automation.AutomationContext.CaptureOriginX + center.X);
                int sy = (int)Math.Round(Automation.AutomationContext.CaptureOriginY + center.Y);
                s += string.Format(" 屏幕({0},{1})", sx, sy);
            }
            return s;
        }

        // 统一 Linear：Area 在大幅缩小 QR/纹理类模板时会把模块平均成灰块，分数反而掉
        private static Mat ResizeTemplate(Mat templ, int nw, int nh)
        {
            Mat m = new();
            Cv2.Resize(templ, m, new Size(nw, nh), 0, 0, InterpolationFlags.Linear);
            return m;
        }
    }
}