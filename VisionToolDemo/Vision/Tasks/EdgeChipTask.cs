using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 边缘崩缺/毛刺检测：沿目标的边缘轮廓，量测**法向**上的凸起与凹陷量。
    ///
    /// 做法：
    ///   1. 取目标外轮廓，先做一次大的形态学闭运算得到"理想轮廓"（把崩缺/毛刺填平/削掉）
    ///      注意这里用的结构元要明显大于缺陷尺度但小于工件尺度 —— 这是本算子的关键参数
    ///   2. 对原始轮廓的每个点，量它到理想轮廓的法向距离（带符号）
    ///   3. 距离超过门限且持续点数足够长的段，判为一个崩缺（内凹）或毛刺（外凸）
    ///
    /// 之所以用"形态学理想轮廓"而不是拟合圆/矩形：崩缺和毛刺恰恰是让形状
    /// **不是**标准几何的缺陷，用圆/矩形去拟合会被缺陷本身带偏，
    /// 反而量不准。形态学参考轮廓对任意形状都成立。
    /// </summary>
    public class EdgeChipTask : IVisionTask, IResultReporter
    {
        public string TaskName => "边缘崩缺毛刺";

        public string LastSummary { get; private set; } = "";

        /// <summary>崩缺（内凹）数量</summary>
        public int ChipCount { get; private set; }

        /// <summary>毛刺（外凸）数量</summary>
        public int BurrCount { get; private set; }

        /// <summary>最深崩缺（px）</summary>
        public double MaxChipDepth { get; private set; } = double.NaN;

        /// <summary>最高毛刺（px）</summary>
        public double MaxBurrHeight { get; private set; } = double.NaN;

        /// <summary>沿轮廓的最大法向偏差（px，绝对值）</summary>
        public double MaxDeviation { get; private set; } = double.NaN;

        /// <summary>轮廓总长（px）</summary>
        public double Perimeter { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "二值化" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "二值化" },
            new TaskParamDesc { ParamName = "参考核大小", Min = 3, Max = 301, DefaultValue = 31,
                DisplayFormat = "参考核:{0}", Group = "检测", Tip = "用于生成“理想轮廓”的闭运算核大小。" +
                "必须**大于缺陷宽度**（否则缺陷被当成真实形状保留）、**小于工件特征尺寸**" +
                "（否则把圆角/尖角也抹平了）。缺陷宽 5~15px 时取 21~41。" },
            new TaskParamDesc { ParamName = "偏差门限", Min = 1, Max = 200, DefaultValue = 3,
                DisplayFormat = "门限:{0}px", Group = "检测", Tip = "法向偏差超过多少像素算缺陷。" +
                "取 2~4 可捕捉亚像素级崩边，取大些只报明显缺陷。" },
            new TaskParamDesc { ParamName = "最小持续长度", Min = 1, Max = 200, DefaultValue = 4,
                DisplayFormat = "持续>={0}", Group = "检测", Tip = "偏差连续超过门限多少个轮廓点才算一个缺陷。" +
                "调大可过滤单点噪声，调小能捕捉针尖状毛刺。" },
            new TaskParamDesc { ParamName = "轮廓重采样", Min = 1, Max = 20, DefaultValue = 2,
                DisplayFormat = "重采样:{0}", Group = "检测", Tip = "每隔几个轮廓点取一个。" +
                "调大可加速，但会降低可捕捉的最小缺陷尺度。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ChipCount = BurrCount = 0;
            MaxChipDepth = MaxBurrHeight = MaxDeviation = Perimeter = double.NaN;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            int threshold = paramValues[0], polarity = paramValues[1];
            int refK = paramValues[2];
            if (refK % 2 == 0) refK++;
            double devThresh = paramValues[3];
            int minRun = Math.Max(1, paramValues[4]);
            int resample = Math.Max(1, paramValues[5]);
            double mmPerPx = paramValues[6] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            // 阈值拉到极值（0/255）会让二值图几乎全一色：FindContours 得到的是整幅图的外框，
            // 后续形态学/距离变换在这种图上会 native abort —— 直接提示"未找到轮廓"优雅返回
            long fg = Cv2.CountNonZero(bin);
            double fgRatio = fg / (double)(bin.Cols * (long)bin.Rows);
            if (fgRatio > 0.999 || fgRatio < 0.001)
            {
                LastSummary = "边缘崩缺毛刺: 二值图几乎全一色（阈值拉到极值），没有可测轮廓";
                return dst;
            }

            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxNone);
            if (contours.Length == 0)
            {
                LastSummary = "边缘崩缺毛刺: 未找到轮廓（检查阈值/极性）";
                return dst;
            }

            // 取最大轮廓
            Point[] best = null; double bestArea = 0;
            foreach (Point[] c in contours)
            {
                double a = Cv2.ContourArea(c);
                if (a > bestArea) { bestArea = a; best = c; }
            }
            if (best == null || best.Length < 8)
            {
                LastSummary = "边缘崩缺毛刺: 轮廓点太少（" + (best?.Length ?? 0) + "）";
                return dst;
            }

            Perimeter = Cv2.ArcLength(best, true);

            // —— 生成参考（理想）轮廓 ——
            using Mat filled = new(bin.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.DrawContours(filled, new[] { best }, -1, Scalar.White, -1);
            using Mat el = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(refK, refK));
            using Mat closed = new();
            Cv2.MorphologyEx(filled, closed, MorphTypes.Close, el);
            // 开运算把残留毛刺也削掉，得到光滑参考
            Cv2.MorphologyEx(closed, closed, MorphTypes.Open, el);

            Cv2.FindContours(closed, out Point[][] refCs, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxNone);
            if (refCs.Length == 0)
            {
                LastSummary = "边缘崩缺毛刺: 参考轮廓生成失败（参考核可能过大，把目标吃掉了）";
                return dst;
            }
            Point[] refC = refCs[0];
            double refArea = 0;
            foreach (Point[] c in refCs)
            {
                double a = Cv2.ContourArea(c);
                if (a > refArea) { refArea = a; refC = c; }
            }

            // —— 逐点量法向偏差 ——
            // 用"到参考轮廓的有符号距离"：正=在参考之外(毛刺)，负=在参考之内(崩缺)。
            // 实现上把参考轮廓画成填充图，膨胀/腐蚀各 refBand 次得到内外带，
            // 再按点落在哪一带近似偏差大小；为得到亚像素量级，这里改用
            // 距离变换：distOut = 到参考形状的距离（在外侧时 >0），
            // distIn  = 到参考补集的距离（在内侧时 >0）。
            using Mat notRef = new();
            Cv2.BitwiseNot(closed, notRef);
            using Mat distOut = new(), distIn = new();
            Cv2.DistanceTransform(notRef, distOut, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            Cv2.DistanceTransform(closed, distIn, DistanceTypes.L2, DistanceTransformMasks.Mask3);

            var deviations = new double[best.Length];
            var inside = new bool[best.Length];
            for (int i = 0; i < best.Length; i++)
            {
                int x = Math.Clamp(best[i].X, 0, bin.Cols - 1);
                int y = Math.Clamp(best[i].Y, 0, bin.Rows - 1);
                inside[i] = closed.At<byte>(y, x) > 127;
                deviations[i] = inside[i] ? -distIn.At<float>(y, x) : distOut.At<float>(y, x);
            }

            // —— 找连续超限段 ——
            double maxDev = 0, maxChip = 0, maxBurr = 0;
            int chips = 0, burrs = 0;
            var marks = new List<(Point at, double dev, bool burr)>();

            int n = deviations.Length;
            int idx = 0;
            while (idx < n)
            {
                if (Math.Abs(deviations[idx]) <= devThresh) { idx++; continue; }
                bool isBurr = deviations[idx] > 0;
                int start = idx;
                double segMax = deviations[idx];
                while (idx < n && Math.Abs(deviations[idx]) > devThresh && (deviations[idx] > 0) == isBurr)
                {
                    if (Math.Abs(deviations[idx]) > Math.Abs(segMax)) segMax = deviations[idx];
                    idx++;
                }
                // 轮廓是闭合的，首尾相连的段要合并（这里简化：分别计数，不做环绕合并）
                int run = idx - start;
                if (run < minRun) continue;
                if (isBurr) { burrs++; if (segMax > maxBurr) maxBurr = segMax; }
                else { chips++; if (-segMax > maxChip) maxChip = -segMax; }
                marks.Add((best[start + (run / 2)], segMax, isBurr));
            }

            for (int i = 0; i < n; i++)
            {
                double ad = Math.Abs(deviations[i]);
                if (ad > maxDev) maxDev = ad;
            }

            ChipCount = chips;
            BurrCount = burrs;
            MaxChipDepth = maxChip > 0 ? maxChip : double.NaN;
            MaxBurrHeight = maxBurr > 0 ? maxBurr : double.NaN;
            MaxDeviation = maxDev;

            // —— 绘制：原轮廓（灰）、参考轮廓（绿）、超限段（红=崩缺 / 橙=毛刺） ——
            for (int i = 0; i < n; i += resample)
            {
                double d = deviations[i];
                if (Math.Abs(d) <= devThresh) continue;
                int x = Math.Clamp(best[i].X, 0, dst.Cols - 1);
                int y = Math.Clamp(best[i].Y, 0, dst.Rows - 1);
                dst.Set(y, x, d > 0 ? Scalar.Orange : Scalar.Red);
            }
            Cv2.DrawContours(dst, new[] { refC }, -1, Scalar.LimeGreen, 1, LineTypes.AntiAlias);

            foreach (var (at, dev, burr) in marks)
            {
                var p = new Point(Math.Clamp(at.X, 2, dst.Cols - 3), Math.Clamp(at.Y, 2, dst.Rows - 3));
                Cv2.Circle(dst, p, 7, burr ? Scalar.Orange : Scalar.Red, 1, LineTypes.AntiAlias);
                string label = string.Format("{0}{1:F1}", burr ? "+" : "-", Math.Abs(dev));
                Cv2.PutText(dst, label, new Point(Math.Min(dst.Cols - 45, p.X + 9), Math.Max(12, p.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.36, Scalar.Yellow, 1, LineTypes.AntiAlias);
            }

            MatDraw.DrawText(dst, string.Format("崩缺 {0} (最深 {1:F2}px)  毛刺 {2} (最高 {3:F2}px)",
                chips, maxChip, burrs, maxBurr), 6, 20, (chips + burrs) > 0 ? Scalar.Red : Scalar.LimeGreen, 13);

            if (chips + burrs == 0)
            {
                LastSummary = string.Format("边缘崩缺毛刺: OK, 最大法向偏差 {0:F3}px (门限 {1:F1}px, 周长 {2:F1}px)",
                    maxDev, devThresh, Perimeter);
                return dst;
            }

            LastSummary = string.Format(
                "边缘崩缺毛刺: 崩缺 {0} 处(最深 {1:F3}px), 毛刺 {2} 处(最高 {3:F3}px), " +
                "最大偏差 {4:F3}px, 周长 {5:F1}px{6}",
                chips, maxChip, burrs, maxBurr, maxDev, Perimeter,
                mmPerPx > 0 ? string.Format(", 最深 {0:F4}mm", Math.Max(maxChip, maxBurr) * mmPerPx) : "");
            return dst;
        }
    }
}
