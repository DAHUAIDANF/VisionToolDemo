using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 表面划伤检测：检出**线状**缺陷（划痕、裂纹、拉丝）。
    ///
    /// 与"脏污坏点检测"的分工：那个用 TOPHAT 找**点状**的孤立凸起/凹陷，
    /// 细长划伤在 TOPHAT 下会被形态学开运算直接吃掉（结构元是方的，长条被当背景）。
    /// 本算子改用**方向性响应**：
    ///   · 用 8 个方向的线性结构元各做一次形态学黑帽/顶帽，取最大响应
    ///     —— 沿划伤方向的响应远大于垂直方向，从而把线状结构从各向同性的背景里分离
    ///   · 再用长宽比 + 线性度筛选连通域：只有细长目标才算划伤
    ///
    /// 这样能区分"一块脏"和"一道划痕"——两者在灰度上都表现为暗（或亮）区域，
    /// 只有几何形状能区分。
    /// </summary>
    public class SurfaceScratchTask : IVisionTask, IResultReporter
    {
        public string TaskName => "表面划伤检测";

        public string LastSummary { get; private set; } = "";

        /// <summary>检出划伤数</summary>
        public int ScratchCount { get; private set; }

        /// <summary>最长划伤长度（px）</summary>
        public double MaxLength { get; private set; } = double.NaN;

        /// <summary>划伤总长（px）</summary>
        public double TotalLength { get; private set; } = double.NaN;

        /// <summary>最大线性度（0~1，1 = 完全笔直）</summary>
        public double MaxLinearity { get; private set; } = double.NaN;

        /// <summary>划伤总面积占比（%）</summary>
        public double AreaPercent { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "检测 0暗划伤1亮划伤", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "类型:{0}", Group = "检测", Tip = "0暗划伤：划痕比背景暗（多数情况）。" +
                "1亮划伤：划痕反光比背景亮（抛光面/镜面）。" },
            new TaskParamDesc { ParamName = "结构元长度", Min = 3, Max = 101, DefaultValue = 15,
                DisplayFormat = "线长:{0}px", Group = "检测", Tip = "方向性结构元的长度。" +
                "要略大于划伤宽度、小于划伤长度。细划伤用 9~15，粗划伤用 21~41。" },
            new TaskParamDesc { ParamName = "对比度门限", Min = 1, Max = 255, DefaultValue = 18,
                DisplayFormat = "门限:{0}", Group = "检测", Tip = "划伤与背景的最小灰度差。" +
                "太低会把纹理/噪声当划伤，太高会漏掉浅划伤。" },
            new TaskParamDesc { ParamName = "方向数", Min = 2, Max = 12, DefaultValue = 8,
                DisplayFormat = "方向:{0}", Group = "检测", Tip = "用多少个方向的线结构元。" +
                "8 个（每 22.5°）通常够，任意方向划伤都能捕捉。" },
            new TaskParamDesc { ParamName = "最小长度", Min = 2, Max = 2000, DefaultValue = 20,
                DisplayFormat = "长度>={0}", Group = "筛选", Tip = "短于此长度的检出丢弃。" +
                "这一条是区分“划伤”和“脏点”的关键：脏点各向同性，最大方向响应也很短。" },
            new TaskParamDesc { ParamName = "最小线性度x100", Min = 0, Max = 100, DefaultValue = 55,
                DisplayFormat = "线性>={0}", Group = "筛选", Tip = "线性度 = 轮廓长度 / 外接框对角线。" +
                "直线段接近 1，团块接近 2。55 表示 0.55。调高只保留更直的划痕。" },
            new TaskParamDesc { ParamName = "最大宽度", Min = 2, Max = 500, DefaultValue = 25,
                DisplayFormat = "宽度<={0}", Group = "筛选", Tip = "宽度超过此值视为面状污渍而非划伤。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
            new TaskParamDesc { ParamName = "输出叠加图", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "叠加:{0}", Group = "输出" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ScratchCount = 0;
            MaxLength = TotalLength = MaxLinearity = AreaPercent = double.NaN;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            int dark = paramValues[0];
            int lineLen = Math.Max(3, paramValues[1]);
            if (lineLen % 2 == 0) lineLen++;
            int contrast = paramValues[2];
            int dirs = Math.Max(2, Math.Min(12, paramValues[3]));
            double minLen = paramValues[4];
            double minLin = paramValues[5] / 100.0;
            double maxW = paramValues[6];
            double mmPerPx = paramValues[7] / 1000.0;
            int overlay = paramValues[8];

            using Mat gray = VisionHelper.ToGray(srcMat);

            // —— 方向性形态学：每个方向都做一次，取响应的逐像素最大值 ——
            using Mat response = new(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            for (int d = 0; d < dirs; d++)
            {
                double ang = Math.PI * d / dirs;
                int k = LineKernelSize(lineLen, ang);
                using Mat el = BuildLineKernel(lineLen, ang);
                using Mat resp = new();
                if (dark == 0)
                {
                    // 暗划伤：黑帽（闭运算 − 原图）会把暗的细线凸显出来
                    Cv2.MorphologyEx(gray, resp, MorphTypes.BlackHat, el);
                }
                else
                {
                    // 亮划伤：顶帽（原图 − 开运算）
                    Cv2.MorphologyEx(gray, resp, MorphTypes.TopHat, el);
                }
                Cv2.Max(response, resp, response);
                _ = (k, ang);
            }

            using Mat mask = new();
            Cv2.Threshold(response, mask, contrast, 255, ThresholdTypes.Binary);

            // 连接同一划伤的断续段：沿形态学闭运算
            using (Mat elc = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, elc);

            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            double totalLen = 0, maxLen = 0, maxLin = 0, areaSum = 0;
            int count = 0;

            foreach (Point[] c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < 2) continue;
                Rect r = Cv2.BoundingRect(c);
                double diag = Math.Sqrt((r.Width * (double)r.Width) + (r.Height * (double)r.Height));
                if (diag < 1e-6) continue;

                double perim = Cv2.ArcLength(c, false);
                // 线性度：轮廓长度 / 外接框对角线。直线段 ~π/2*长度/长度… 用周长比更稳：
                // 对一条长 L 宽 W 的细条，周长 ≈ 2L，外接框对角线 ≈ L，比值 ≈ 2；
                // 对圆团，周长 ≈ πD，对外接框对角线 ≈ D，比值 ≈ 3.14。
                // 所以用 (2*L)/周长 作为线性度：细条 -> 1，团块 -> 0.6。
                double lin = perim > 1e-6 ? Math.Min(1.0, 2.0 * diag / perim) : 0;

                if (diag < minLen) continue;
                if (lin < minLin) continue;

                // 宽度必须用 **面积 / 长度** 估计，不能用外接框的短边：
                // 一条 45° 的细划伤，外接框宽高都很大（本例 385x145），
                // 用 min(宽,高)=145 会被"最大宽度 25"整条否掉，
                // 于是所有斜向划伤都检不出来（这正是本算子最初的 bug）。
                // 面积/长度 对任意走向都等于平均笔画宽度：细条 ≈ 宽度，团块 ≈ 直径。
                double strokeW = diag > 1e-6 ? area / diag : area;
                if (strokeW > maxW) continue;
                _ = r;

                count++;
                totalLen += diag;
                areaSum += area;
                if (diag > maxLen) maxLen = diag;
                if (lin > maxLin) maxLin = lin;

                if (overlay == 1)
                {
                    Cv2.Rectangle(dst, r, Scalar.Orange, 1);
                    // 划伤主轴：用拟合直线画出来，比矩形框更直观
                    var pts = new List<GeometryFit.P2>(c.Length);
                    foreach (Point p in c) pts.Add(new GeometryFit.P2(p.X, p.Y));
                    if (GeometryFit.FitLine(pts, out GeometryFit.Line2 ln))
                    {
                        double t = diag / 2.0;
                        var p1 = new Point((int)Math.Round(ln.Px - (t * ln.Dx)), (int)Math.Round(ln.Py - (t * ln.Dy)));
                        var p2 = new Point((int)Math.Round(ln.Px + (t * ln.Dx)), (int)Math.Round(ln.Py + (t * ln.Dy)));
                        Cv2.Line(dst, p1, p2, Scalar.Red, 1, LineTypes.AntiAlias);
                    }
                }
            }

            ScratchCount = count;
            if (count > 0)
            {
                MaxLength = maxLen;
                TotalLength = totalLen;
                MaxLinearity = maxLin;
                AreaPercent = 100.0 * areaSum / (srcMat.Cols * (double)srcMat.Rows);
            }

            if (overlay == 1)
            {
                MatDraw.DrawText(dst, string.Format("划伤 {0} 处, 最长 {1:F1}px", count, maxLen), 6, 20, count > 0 ? Scalar.Red : Scalar.LimeGreen, 14);
                if (mmPerPx > 0 && count > 0)
                    MatDraw.DrawText(dst, string.Format("最长 {0:F3}mm", maxLen * mmPerPx), 6, 40, Scalar.Yellow, 13);
            }

            if (count == 0)
            {
                LastSummary = "表面划伤检测: 未检出划伤（检查对比度门限/最小长度）";
                return dst;
            }

            LastSummary = string.Format(
                "表面划伤检测: {0} 处, 最长 {1:F2}px{2}, 总长 {3:F2}px, 最大线性度 {4:F3}, 面积占比 {5:F3}%",
                count, maxLen, mmPerPx > 0 ? string.Format("/{0:F4}mm", maxLen * mmPerPx) : "",
                totalLen, maxLin, AreaPercent);
            return dst;
        }

        /// <summary>
        /// 构造指定角度的线形结构元。
        /// 直接用直线线段画：比用 RotatedRect 更精确地得到"单像素宽"的线核，
        /// 而核越细，对细划伤的响应越尖锐。
        /// </summary>
        private static Mat BuildLineKernel(int len, double angle)
        {
            var k = new Mat(len, len, MatType.CV_8UC1, Scalar.Black);
            int c = len / 2;
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            var p1 = new Point((int)Math.Round(c - (c * dx)), (int)Math.Round(c - (c * dy)));
            var p2 = new Point((int)Math.Round(c + (c * dx)), (int)Math.Round(c + (c * dy)));
            Cv2.Line(k, p1, p2, Scalar.White, 1, LineTypes.Link8);
            // 保证核内至少有一个前景像素，否则形态学会返回全零
            if (Cv2.CountNonZero(k) == 0) k.Set(c, c, (byte)255);
            return k;
        }

        private static int LineKernelSize(int len, double angle)
        {
            double dx = Math.Abs(Math.Cos(angle)), dy = Math.Abs(Math.Sin(angle));
            double extent = (len * dx) + (len * dy);
            int s = (int)Math.Round(extent);
            if (s % 2 == 0) s++;
            return Math.Max(3, Math.Min(s, len * 2 + 1));
        }
    }
}
