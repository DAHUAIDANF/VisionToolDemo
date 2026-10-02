using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Lens 镜头检测：定位镜头（圆形）本体，并给出常见的外观量测项。
    ///
    /// 面向模组/镜头的常规外观检查，一次给出：
    ///   · 镜片圆心与半径（亚像素：用轮廓点到圆的代数拟合）
    ///   · 圆度（4πA/P²，1 = 理想圆）与外径尺寸
    ///   · 同心度：镜头外圆与内圈（镜片）圆心的偏移 —— 组装偏心的直接指标
    ///   · 崩边/缺损：外圆轮廓与拟合圆的最大偏差角位置
    ///
    /// 参数按"外圆找镜头、内圈找镜片"两级搜索，适应不同对比度。
    /// </summary>
    public class LensDetectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Lens镜头检测";

        public string LastSummary { get; private set; } = "";

        public bool Found { get; private set; }
        public Point2f Center { get; private set; }
        public double Radius { get; private set; }
        /// <summary>圆度 4πA/P²（1=理想圆）</summary>
        public double Circularity { get; private set; }
        /// <summary>内外圆心偏移（像素），-1 表示未找到内圈</summary>
        public double Eccentricity { get; private set; } = -1;
        /// <summary>外轮廓与拟合圆的最大径向偏差（像素），崩边指标</summary>
        public double MaxDeviation { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "thr:{0}",
                Group = "检测",
                Tip = "0 = Otsu 自动（推荐先试）。镜头在亮背景上通常能得到干净的圆形轮廓。"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "检测",
                Tip = "0 = 比背景亮的是镜头；1 = 比背景暗的是镜头。"
            },
            new TaskParamDesc
            {
                ParamName = "最小半径",
                Min = 5, Max = 4000, DefaultValue = 40,
                DisplayFormat = "r≥{0}",
                ForceOdd = false,
                Group = "检测",
                Tip = "小于该半径的圆不当作镜头，用于排除小圆（螺钉、字符孔等）干扰。"
            },
            new TaskParamDesc
            {
                ParamName = "最大半径",
                Min = 5, Max = 8000, DefaultValue = 3000,
                DisplayFormat = "r≤{0}",
                Group = "检测",
                Tip = "大于该半径的圆不当作镜头，用于排除料盘外框等大圆干扰。"
            },
            new TaskParamDesc
            {
                ParamName = "检测内圈 0关1开",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "inner:{0}",
                Group = "内圈",
                Tip = "开：在镜头内部再找一个较小的圆作为镜片，用于算同心度（组装偏心）。" +
                      "镜片与外圈对比不明显时可关掉。"
            },
            new TaskParamDesc
            {
                ParamName = "内圈最大半径比%",
                Min = 10, Max = 95, DefaultValue = 70,
                DisplayFormat = "≤{0}%",
                Group = "内圈",
                Tip = "内圈半径不得超过外圈半径的这个比例，避免把外圈自己再找一遍。"
            },
            new TaskParamDesc
            {
                ParamName = "崩边偏差门限",
                Min = 1, Max = 100, DefaultValue = 8,
                DisplayFormat = "dev≥{0}px",
                Group = "判定",
                Tip = "轮廓偏离拟合圆超过该像素数即判为崩边/缺损，并在结果图上标出位置。"
            },
            new TaskParamDesc
            {
                ParamName = "显示 0标注1仅轮廓2原图",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "show:{0}",
                Group = "输出",
                Tip = "0 = 画拟合圆+圆心+崩边位置；1 = 只画检测到的轮廓；2 = 原图（只出数据，不看标注）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Found = false;
            Circularity = 0;
            Eccentricity = -1;
            MaxDeviation = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "Lens检测: 输入为空";
                return srcMat?.Clone();
            }

            int thr = paramValues[0];
            int polarity = paramValues[1];
            int minR = paramValues[2];
            int maxR = paramValues[3];
            bool detectInner = paramValues[4] == 1;
            int innerRatio = paramValues[5];
            int devThresh = paramValues[6];
            int showMode = paramValues[7];

            using Mat gray = VisionHelper.ToGray(srcMat);
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            if (showMode == 2)
            {
                // 仍需计算数据，只是不标注；这里先走完流程，最后返回原图
            }

            // —— 1. 二值化 ——
            using Mat bin = new Mat();
            double usedThr;
            ThresholdTypes tt = polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            if (thr <= 0)
                usedThr = Cv2.Threshold(gray, bin, 0, 255, tt | ThresholdTypes.Otsu);
            else
            {
                usedThr = thr;
                Cv2.Threshold(gray, bin, thr, 255, tt);
            }

            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel);
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel);

            // —— 2. 找候选轮廓，挑最"像镜头"的一个 ——
            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxNone);

            Point[] best = null;
            double bestScore = -1;
            Point2f bestCenter = new();
            double bestR = 0;
            foreach (var c in contours)
            {
                if (c.Length < 20) continue;
                double area = Cv2.ContourArea(c);
                double perim = Cv2.ArcLength(c, true);
                if (perim < 1e-6) continue;
                double circ = 4 * Math.PI * area / (perim * perim);
                if (circ < 0.5) continue;                 // 明显不是圆

                var (ccx, ccy, cr) = FitCircle(c);
                if (cr < minR || cr > maxR) continue;     // 半径门限

                // 评分：圆度为主，圆越大越优先（镜头通常是画面里最大的圆）
                double score = circ * 100 + Math.Min(cr, 1000) / 1000.0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                    bestCenter = new Point2f((float)ccx, (float)ccy);
                    bestR = cr;
                }
            }

            if (best == null)
            {
                LastSummary = string.Format("Lens检测: 未找到符合条件的圆 (半径 {0}~{1}, 阈值 {2:F0})",
                    minR, maxR, usedThr);
                return dst;
            }

            Found = true;
            Center = bestCenter;
            Radius = bestR;
            double bArea = Cv2.ContourArea(best);
            double bPerim = Cv2.ArcLength(best, true);
            Circularity = bPerim > 1e-6 ? 4 * Math.PI * bArea / (bPerim * bPerim) : 0;

            // —— 3. 崩边检测：轮廓点到拟合圆的径向偏差 ——
            double maxDev = 0;
            Point worstPt = new();
            foreach (var p in best)
            {
                double d = Math.Sqrt(((p.X - Center.X) * (p.X - Center.X)) + ((p.Y - Center.Y) * (p.Y - Center.Y)));
                double dev = Math.Abs(d - Radius);
                if (dev > maxDev) { maxDev = dev; worstPt = p; }
            }
            MaxDeviation = maxDev;

            // —— 4. 内圈（镜片）与同心度 ——
            Point2f innerCenter = new();
            double innerR = 0;
            bool innerFound = false;
            if (detectInner)
            {
                // 内圈不能用"二值轮廓"找：内圈与外圈的明暗关系随镜头/光照而变，
                // 二值化后内圈常常根本不构成一个独立区域（实测外环比内圈暗时，
                // 内圈与背景同属一类，External/List 都拿不到它）。
                // 因此改为在镜头内部做 Canny + 霍夫圆：只看**边缘**，与谁亮谁暗无关。
                double maxInnerR = Radius * innerRatio / 100.0;
                double minInnerR = Math.Max(4.0, Radius * 0.12);

                using Mat innerRoi = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
                // 只保留镜头内部（略收缩，避开外圈自身的边）
                Cv2.Circle(innerRoi, new Point((int)Math.Round(Center.X), (int)Math.Round(Center.Y)),
                    (int)Math.Round(Radius * 0.95), Scalar.White, -1);
                using Mat masked = new Mat();
                gray.CopyTo(masked, innerRoi);

                using Mat edges = new Mat();
                Cv2.Canny(masked, edges, 40, 120);
                using Mat edgesMasked = new Mat();
                Cv2.BitwiseAnd(edges, innerRoi, edgesMasked);

                var circles = Cv2.HoughCircles(edgesMasked, HoughModes.Gradient, 1.5,
                    Math.Max(10.0, Radius * 0.3), 100, 30,
                    (int)Math.Round(minInnerR), (int)Math.Round(maxInnerR));

                double bestInnerScore = -1;
                if (circles != null)
                {
                    foreach (var c in circles)
                    {
                        double dc = Math.Sqrt(((c.Center.X - Center.X) * (c.Center.X - Center.X))
                                            + ((c.Center.Y - Center.Y) * (c.Center.Y - Center.Y)));
                        if (dc + c.Radius > Radius) continue;          // 必须完全在镜头内
                        if (c.Radius >= Radius * 0.95) continue;        // 不能是外圈自己
                        // 评分：越接近同心、半径越大越可能是镜片
                        double score = c.Radius - (dc * 0.5);
                        if (score > bestInnerScore)
                        {
                            bestInnerScore = score;
                            innerCenter = c.Center;
                            innerR = c.Radius;
                            innerFound = true;
                        }
                    }
                }
            }
            if (innerFound)
                Eccentricity = Math.Sqrt(((innerCenter.X - Center.X) * (innerCenter.X - Center.X))
                                       + ((innerCenter.Y - Center.Y) * (innerCenter.Y - Center.Y)));

            // —— 5. 绘制 ——
            if (showMode == 0)
            {
                Cv2.DrawContours(dst, new[] { best }, -1, Scalar.Lime, 1);
                Cv2.Circle(dst, new Point((int)Center.X, (int)Center.Y), (int)Math.Round(Radius), Scalar.Yellow, 2);
                Cv2.DrawMarker(dst, new Point((int)Center.X, (int)Center.Y), Scalar.Cyan,
                    MarkerTypes.Cross, 18, 2);
                if (innerFound)
                {
                    Cv2.Circle(dst, new Point((int)innerCenter.X, (int)innerCenter.Y),
                        (int)Math.Round(innerR), Scalar.Orange, 2);
                    Cv2.DrawMarker(dst, new Point((int)innerCenter.X, (int)innerCenter.Y), Scalar.Orange,
                        MarkerTypes.Cross, 12, 2);
                    Cv2.Line(dst, new Point((int)Center.X, (int)Center.Y),
                        new Point((int)innerCenter.X, (int)innerCenter.Y), Scalar.Magenta, 1);
                }
                if (maxDev >= devThresh)
                    Cv2.Circle(dst, worstPt, 10, Scalar.Red, 2);

                // 关键数据写在图上，便于截图留档
                string info = string.Format("R={0:F1}px 圆度={1:F3} 偏心={2}", Radius, Circularity,
                    innerFound ? Eccentricity.ToString("F2") + "px" : "—");
                Cv2.PutText(dst, info, new Point(8, 22), HersheyFonts.HersheySimplex, 0.55, Scalar.Yellow, 1);
            }
            else if (showMode == 1)
            {
                Cv2.DrawContours(dst, new[] { best }, -1, Scalar.Lime, 2);
            }
            else
            {
                dst.Dispose();
                dst = VisionHelper.ToBgrCopy(srcMat);
            }

            string ecc = innerFound ? string.Format("{0:F2}px({1:F1}%)", Eccentricity, 100 * Eccentricity / Radius) : "—";
            LastSummary = string.Format("Lens: R={0:F1} 圆度={1:F3} 偏心={2} 最大偏差={3:F1}px{4}",
                Radius, Circularity, ecc, MaxDeviation, maxDev >= devThresh ? " ⚠崩边" : "");
            return dst;
        }

        /// <summary>
        /// 用 Kåsa 代数法拟合圆（最小二乘），比"取轮廓重心 + 平均半径"更抗轮廓缺失/毛刺。
        /// 求解 x²+y² + Dx + Ey + F = 0 的最小二乘解。
        /// </summary>
        private static (double cx, double cy, double r) FitCircle(Point[] pts)
        {
            int n = pts.Length;
            if (n < 3) return (0, 0, 0);

            double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;
            double sumX3 = 0, sumY3 = 0, sumX1Y2 = 0, sumX2Y1 = 0;
            foreach (var p in pts)
            {
                double x = p.X, y = p.Y;
                double x2 = x * x, y2 = y * y;
                sumX += x; sumY += y;
                sumX2 += x2; sumY2 += y2;
                sumXY += x * y;
                sumX3 += x2 * x; sumY3 += y2 * y;
                sumX1Y2 += x * y2; sumX2Y1 += x2 * y;
            }

            // 解 2x2 正规方程
            double a11 = 2 * (sumX2 - (sumX * sumX / n));
            double a12 = 2 * (sumXY - (sumX * sumY / n));
            double a22 = 2 * (sumY2 - (sumY * sumY / n));
            double b1 = sumX3 + sumX1Y2 - ((sumX2 + sumY2) * sumX / n);
            double b2 = sumX2Y1 + sumY3 - ((sumX2 + sumY2) * sumY / n);

            double det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) < 1e-9) return (0, 0, 0);

            double cx = ((b1 * a22) - (b2 * a12)) / det;
            double cy = ((a11 * b2) - (a12 * b1)) / det;

            // 半径取各点到圆心的平均距离（比用 F 更稳）
            double rSum = 0;
            foreach (var p in pts)
                rSum += Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
            return (cx, cy, rSum / n);
        }
    }
}
