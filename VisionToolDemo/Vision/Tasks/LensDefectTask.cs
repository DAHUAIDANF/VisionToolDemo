using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Lens 缺陷检测：在镜头**有效区（镜片）内**找划痕、麻点、气泡、脏污等局部异常，
    /// 排除镜头边缘/倒角/背景造成的干扰。
    ///
    /// 与"斑点检测/轮廓筛选"的区别：那些在全图找异常，镜头检测里最常见的误报恰恰来自
    /// **镜头外圈与背景**（高反光倒角、镜筒、料盘）。本算子先用圆把有效区抠出来，
    /// 只在镜片内部判定，并按"背景减除 + 局部对比"凸显缺陷，再按面积/长度过滤。
    ///
    /// 两种检出方式：
    ///   · 暗于周围 —— 麻点、气泡、脏污（比局部背景暗）
    ///   · 亮于周围 —— 划痕反光、崩边亮点（比局部背景亮）
    /// </summary>
    public class LensDefectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Lens缺陷检测";

        public string LastSummary { get; private set; } = "";

        public int DefectCount { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "thr:{0}",
                Group = "定位",
                Tip = "定位镜头圆用的阈值，0 = Otsu 自动。"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "定位",
                Tip = "镜头比背景暗选 1（默认）；比背景亮选 0。"
            },
            new TaskParamDesc
            {
                ParamName = "有效区收缩%",
                Min = 0, Max = 40, DefaultValue = 10,
                DisplayFormat = "shrink:{0}%",
                Group = "定位",
                Tip = "把镜片圆缩小这个比例再检测，避开镜头**外圈倒角**的高反光误报（最关键的抗误报参数）。"
            },
            new TaskParamDesc
            {
                ParamName = "背景核大小",
                Min = 3, Max = 151, DefaultValue = 31,
                DisplayFormat = "bg:{0}",
                ForceOdd = true,
                Group = "检测",
                Tip = "估计局部背景的核。**要明显大于缺陷尺寸**，否则缺陷会被当成背景抹掉；" +
                      "太小会把正常纹理当成缺陷。"
            },
            new TaskParamDesc
            {
                ParamName = "缺陷对比度门限",
                Min = 1, Max = 120, DefaultValue = 12,
                DisplayFormat = "diff≥{0}",
                Group = "检测",
                Tip = "与局部背景的灰度差超过该值才算缺陷。调高只抓明显缺陷。"
            },
            new TaskParamDesc
            {
                ParamName = "检出 0暗于周围1亮于周围2两者",
                Min = 0, Max = 2, DefaultValue = 2,
                DisplayFormat = "kind:{0}",
                Group = "检测",
                Tip = "0 = 只找麻点/气泡/脏污（暗）；1 = 只找划痕反光/亮点（亮）；2 = 两者都找。"
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 1, Max = 100000, DefaultValue = 8,
                DisplayFormat = "area≥{0}",
                Group = "判定",
                Tip = "小于该面积不计为缺陷（滤掉像素级噪点）。"
            },
            new TaskParamDesc
            {
                ParamName = "最大面积",
                Min = 10, Max = 1000000, DefaultValue = 20000,
                DisplayFormat = "area≤{0}",
                Group = "判定",
                Tip = "大于该面积不计为缺陷（避免把大片反光/整块阴影当缺陷）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            DefectCount = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "Lens缺陷: 输入为空";
                return srcMat?.Clone();
            }

            int thr = paramValues[0];
            int polarity = paramValues[1];
            int shrinkPct = paramValues[2];
            int bgK = paramValues[3];
            int diffThresh = paramValues[4];
            int kind = paramValues[5];
            int minArea = paramValues[6];
            int maxArea = paramValues[7];

            if (bgK % 2 == 0) bgK++;
            if (bgK < 3) bgK = 3;
            if (maxArea < minArea) maxArea = minArea;

            using Mat gray = VisionHelper.ToGray(srcMat);
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 1. 用圆形定位镜片（复用 Lens镜头检测 的拟合思路） ——
            using Mat bin = new Mat();
            ThresholdTypes tt = polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            if (thr <= 0) Cv2.Threshold(gray, bin, 0, 255, tt | ThresholdTypes.Otsu);
            else Cv2.Threshold(gray, bin, thr, 255, tt);

            using Mat k5 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, k5);
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open, k5);

            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxNone);

            Point2f center = new();
            double radius = 0;
            bool located = false;
            double bestScore = -1;
            foreach (var c in contours)
            {
                if (c.Length < 20) continue;
                double area = Cv2.ContourArea(c);
                double perim = Cv2.ArcLength(c, true);
                if (perim < 1e-6) continue;
                double circ = 4 * Math.PI * area / (perim * perim);
                if (circ < 0.6) continue;
                var (cx, cy, r) = FitCircle(c);
                if (r < 20) continue;
                double score = circ * 1000 + r;
                if (score > bestScore)
                {
                    bestScore = score;
                    center = new Point2f((float)cx, (float)cy);
                    radius = r;
                    located = true;
                }
            }

            if (!located)
            {
                LastSummary = "Lens缺陷: 未能定位镜头圆（检查二值阈值/极性）";
                return dst;
            }

            double effR = radius * (1.0 - shrinkPct / 100.0);

            // —— 2. 有效区掩膜（圆内，收缩后） ——
            using Mat roiMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.Circle(roiMask, new Point((int)Math.Round(center.X), (int)Math.Round(center.Y)),
                (int)Math.Round(effR), Scalar.White, -1);

            // —— 3. 局部背景减除，凸显局部异常 ——
            using Mat bg = new Mat();
            Cv2.MedianBlur(gray, bg, bgK);   // 中值对缺陷遮挡不敏感，背景更干净

            using Mat darkDef = new Mat();
            using Mat brightDef = new Mat();
            using Mat diffSub = new Mat();
            Cv2.Subtract(bg, gray, diffSub);         // 暗于周围 -> 正值
            Cv2.Threshold(diffSub, darkDef, diffThresh - 1, 255, ThresholdTypes.Binary);
            using Mat diffAdd = new Mat();
            Cv2.Subtract(gray, bg, diffAdd);         // 亮于周围 -> 正值
            Cv2.Threshold(diffAdd, brightDef, diffThresh - 1, 255, ThresholdTypes.Binary);

            using Mat defects = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            if (kind == 0 || kind == 2) darkDef.CopyTo(defects, darkDef);
            if (kind == 1 || kind == 2)
            {
                using Mat tmp = new Mat();
                Cv2.BitwiseOr(defects, brightDef, tmp);
                tmp.CopyTo(defects);
            }
            Cv2.BitwiseAnd(defects, roiMask, defects);   // 只在镜片有效区内

            // —— 4. 按面积过滤并标注 ——
            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(defects, labels, stats, centroids, PixelConnectivity.Connectivity8);

            var kept = new List<Rect>();
            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea || area > maxArea) continue;
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                kept.Add(new Rect(x, y, w, h));
            }
            DefectCount = kept.Count;

            Cv2.Circle(dst, new Point((int)Math.Round(center.X), (int)Math.Round(center.Y)),
                (int)Math.Round(radius), Scalar.Yellow, 2);
            Cv2.Circle(dst, new Point((int)Math.Round(center.X), (int)Math.Round(center.Y)),
                (int)Math.Round(effR), Scalar.Gray, 1);
            foreach (var r in kept)
            {
                Cv2.Rectangle(dst, r, Scalar.Red, 1);
                Cv2.Rectangle(dst, new Rect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), Scalar.Red, 1);
            }
            MatDraw.DrawText(dst, "缺陷 " + DefectCount, 8, 22, DefectCount > 0 ? Scalar.Red : Scalar.Lime, 16);

            LastSummary = string.Format("Lens缺陷: 检出 {0} 处  R={1:F1} 有效区R={2:F1} 门限{3}",
                DefectCount, radius, effR, diffThresh);
            return dst;
        }

        /// <summary>Kåsa 代数法最小二乘拟合圆（同 Lens镜头检测，保持两算子结论一致）</summary>
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
                sumX += x; sumY += y; sumX2 += x2; sumY2 += y2; sumXY += x * y;
                sumX3 += x2 * x; sumY3 += y2 * y; sumX1Y2 += x * y2; sumX2Y1 += x2 * y;
            }
            double a11 = 2 * (sumX2 - (sumX * sumX / n));
            double a12 = 2 * (sumXY - (sumX * sumY / n));
            double a22 = 2 * (sumY2 - (sumY * sumY / n));
            double b1 = sumX3 + sumX1Y2 - ((sumX2 + sumY2) * sumX / n);
            double b2 = sumX2Y1 + sumY3 - ((sumX2 + sumY2) * sumY / n);
            double det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) < 1e-9) return (0, 0, 0);
            double cx = ((b1 * a22) - (b2 * a12)) / det;
            double cy = ((a11 * b2) - (a12 * b1)) / det;
            double rSum = 0;
            foreach (var p in pts)
                rSum += Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
            return (cx, cy, rSum / n);
        }
    }
}
