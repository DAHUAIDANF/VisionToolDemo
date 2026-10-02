using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Flare / 鬼影（眩光与鬼像）检测：量化强光源造成的两种杂散光。
    ///
    ///   · **Veiling glare（雾化眩光）** —— 光源周围大面积的亮度抬升，
    ///     表现为对比度下降。用"光源邻近区 vs 远处暗区"的亮度比衡量。
    ///   · **Ghost（鬼影）** —— 镜片间多次反射形成的离散亮斑。
    ///     用阈值 + 连通域找出来，统计**面积占比**与形态。
    ///
    /// 两者机理不同、评价方式也不同，所以本算子分开输出，不混成一个"flare 分"。
    /// </summary>
    public class FlareGhostTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Flare鬼影检测";

        public string LastSummary { get; private set; } = "";

        /// <summary>鬼影总面积占画面比例（%）</summary>
        public double GhostAreaPercent { get; private set; } = double.NaN;
        /// <summary>鬼影个数</summary>
        public int GhostCount { get; private set; }
        /// <summary>雾化眩光指数：邻近区亮度 / 远端暗区亮度（1=无眩光）</summary>
        public double VeilingIndex { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "光源阈值",
                Min = 1, Max = 255, DefaultValue = 240,
                DisplayFormat = "src:{0}",
                Group = "光源",
                Tip = "高于该灰度视为强光源像素（用于自动定位光源位置）。通常取接近饱和的值。"
            },
            new TaskParamDesc
            {
                ParamName = "鬼影阈值",
                Min = 1, Max = 255, DefaultValue = 40,
                DisplayFormat = "ghost:{0}",
                Group = "鬼影",
                Tip = "背景减除后，高于该灰度的才判为鬼影。调低更灵敏（也更容易把噪声当鬼影）。"
            },
            new TaskParamDesc
            {
                ParamName = "背景核大小",
                Min = 3, Max = 301, DefaultValue = 101,
                DisplayFormat = "bg:{0}",
                ForceOdd = true,
                Group = "鬼影",
                Tip = "估计背景照明的核。**要远大于鬼影尺寸**，否则鬼影会被当成背景一起抹掉。"
            },
            new TaskParamDesc
            {
                ParamName = "最小鬼影面积",
                Min = 1, Max = 100000, DefaultValue = 12,
                DisplayFormat = "area≥{0}",
                Group = "鬼影",
                Tip = "小于该面积的亮点不计为鬼影（滤掉噪点）。"
            },
            new TaskParamDesc
            {
                ParamName = "邻近区半径%",
                Min = 1, Max = 50, DefaultValue = 12,
                DisplayFormat = "near:{0}%",
                Group = "眩光",
                Tip = "以光源为中心、该比例画面尺寸为半径的区域，用于测雾化眩光。"
            },
            new TaskParamDesc
            {
                ParamName = "远端区环距%",
                Min = 5, Max = 100, DefaultValue = 45,
                DisplayFormat = "far:{0}%",
                Group = "眩光",
                Tip = "取离光源较远（该比例半径以外）的暗区作参考亮度。应选没有其他光源的区域。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0原图标注1鬼影掩膜2照明图",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 标注光源/鬼影/测光区；1 = 鬼影二值掩膜；2 = 估计出的背景照明（核对核大小）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            GhostAreaPercent = double.NaN;
            GhostCount = 0;
            VeilingIndex = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "Flare: 输入为空";
                return srcMat?.Clone();
            }

            int srcThr = paramValues[0];
            int ghostThr = paramValues[1];
            int bgK = paramValues[2] % 2 == 0 ? paramValues[2] + 1 : paramValues[2];
            int minArea = paramValues[3];
            int nearPct = Math.Clamp(paramValues[4], 1, 50);
            int farPct = Math.Clamp(paramValues[5], 5, 100);
            int outMode = paramValues[6];
            if (bgK < 3) bgK = 3;

            using Mat gray = VisionHelper.ToGray(srcMat);
            int w = gray.Cols, h = gray.Rows;
            int diag = (int)Math.Sqrt((w * w) + (h * h));

            // —— 1. 自动定位光源（最亮连通域的重心） ——
            using Mat bright = new Mat();
            Cv2.Threshold(gray, bright, srcThr, 255, ThresholdTypes.Binary);
            Cv2.FindContours(bright, out Point[][] srcContours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            Point srcCenter = new(w / 2, h / 2);
            double srcArea = 0;
            bool srcFound = false;
            foreach (var c in srcContours)
            {
                double a = Cv2.ContourArea(c);
                if (a > srcArea)
                {
                    srcArea = a;
                    var m = Cv2.Moments(c);
                    if (Math.Abs(m.M00) > 1e-6)
                    {
                        srcCenter = new Point((int)(m.M10 / m.M00), (int)(m.M01 / m.M00));
                        srcFound = true;
                    }
                }
            }

            // —— 2. 雾化眩光：近区亮度 / 远区亮度 ——
            int nearR = Math.Max(4, diag * nearPct / 100);
            int farR = Math.Max(nearR + 4, diag * farPct / 100);
            using Mat nearMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            using Mat farMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.Circle(nearMask, srcCenter, nearR, Scalar.White, -1);
            Cv2.Circle(farMask, srcCenter, diag, Scalar.White, -1);
            Cv2.Circle(farMask, srcCenter, farR, Scalar.Black, -1);   // 环形远端区

            // 近区/远区都要排除光源本体（否则测的是光源亮度而非眩光）
            using Mat notSrc = new Mat();
            Cv2.BitwiseNot(bright, notSrc);
            Cv2.BitwiseAnd(nearMask, notSrc, nearMask);
            Cv2.BitwiseAnd(farMask, notSrc, farMask);

            double nearMean = MaskedMean(gray, nearMask, out int nearCnt);
            double farMean = MaskedMean(gray, farMask, out int farCnt);
            if (nearCnt > 20 && farCnt > 20 && farMean > 0.5)
                VeilingIndex = nearMean / farMean;

            // —— 3. 鬼影：背景减除 + 连通域 ——
            using Mat bg = new Mat();
            Cv2.MedianBlur(gray, bg, bgK);
            using Mat diff = new Mat();
            Cv2.Subtract(gray, bg, diff);                  // 比背景亮的局部亮点
            using Mat ghosts = new Mat();
            Cv2.Threshold(diff, ghosts, ghostThr, 255, ThresholdTypes.Binary);
            // 排除光源本体
            Cv2.BitwiseAnd(ghosts, notSrc, ghosts);

            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(ghosts, labels, stats, centroids, PixelConnectivity.Connectivity8);

            long ghostPx = 0;
            int kept = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea) continue;
                kept++;
                ghostPx += area;

                if (outMode == 0)
                {
                    int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                    int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                    int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                    int bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                    var r = new Rect(x, y, bw, bh);
                    Cv2.Rectangle(dst, r, Scalar.Red, 1);

                    // 用最小外接圆/椭圆反映鬼影形态（圆形=反射斑，长条=条纹反光）
                    using Mat comp = new Mat();
                    Cv2.Compare(labels, i, comp, CmpTypes.EQ);
                    Cv2.FindContours(comp, out Point[][] cs, out _, RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);
                    if (cs.Length > 0 && cs[0].Length >= 5)
                    {
                        var ell = Cv2.FitEllipse(cs[0]);
                        Cv2.Ellipse(dst, ell, Scalar.Yellow, 1);
                    }
                    if (cs.Length > 0 && cs[0].Length >= 3)
                    {
                        Cv2.MinEnclosingCircle(cs[0], out Point2f cc, out float cr);
                        Cv2.Circle(dst, new Point((int)cc.X, (int)cc.Y), (int)cr, Scalar.Magenta, 1);
                    }
                }
            }
            GhostCount = kept;
            GhostAreaPercent = 100.0 * ghostPx / (w * (double)h);

            if (outMode == 0)
            {
                Cv2.Circle(dst, srcCenter, nearR, Scalar.Cyan, 1);
                Cv2.Circle(dst, srcCenter, farR, Scalar.Orange, 1);
                if (srcFound) Cv2.DrawMarker(dst, srcCenter, Scalar.Cyan, MarkerTypes.Cross, 20, 2);
                MatDraw.DrawText(dst, string.Format("鬼影 {0} 处 {1:F3}%  眩光指数 {2:F2}",
                    GhostCount, GhostAreaPercent, VeilingIndex), 8, 22, Scalar.Yellow, 13);
            }
            else if (outMode == 1)
            {
                dst.Dispose();
                dst = ghosts.Clone();
            }
            else
            {
                dst.Dispose();
                dst = new Mat();
                bg.ConvertTo(dst, MatType.CV_8UC1);
            }

            LastSummary = string.Format("Flare: 鬼影 {0} 处，占面积 {1:F3}%；雾化眩光指数 {2:F2}（近/远亮度比）",
                GhostCount, GhostAreaPercent, VeilingIndex);
            return dst;
        }

        private static double MaskedMean(Mat gray, Mat mask, out int count)
        {
            count = Cv2.CountNonZero(mask);
            if (count == 0) return 0;
            Scalar m = Cv2.Mean(gray, mask);
            return m.Val0;
        }
    }
}
