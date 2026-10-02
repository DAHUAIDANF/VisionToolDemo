using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 透视校正：在图内自动查找最大的四边形轮廓（标签/屏幕/文档边缘），
    /// 以 GetPerspectiveTransform 拉平为正视矩形输出。
    /// 输出仍为输入同尺寸画布（左上角放置校正结果并等比缩放，避免超出），
    /// 同时在原始位置以青色框出识别到的四边形。
    /// 参数：输出宽度/输出高度（0=按四边形最大边自动计算）。
    /// </summary>
    public class PerspectiveTask : IVisionTask, IResultReporter
    {
        public string TaskName => "透视校正";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "输出宽度0自动",
                Min = 0,
                Max = 2000,
                DefaultValue = 0,
                DisplayFormat = "W:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "输出高度0自动",
                Min = 0,
                Max = 2000,
                DefaultValue = 0,
                DisplayFormat = "H:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int outW = paramValues[0];
            int outH = paramValues[1];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat blur = new())
            using (Mat bin = new())
            {
                Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(5, 5), 0);
                Cv2.Threshold(blur, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                double imgArea = srcMat.Cols * (double)srcMat.Rows;
                Point[] quad = FindBestQuad(bin, imgArea);
                if (quad == null)
                {
                    // 深色目标在亮背景时 Otsu 正相会只剩整图边框轮廓，反相再找一次
                    Cv2.Threshold(blur, bin, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
                    quad = FindBestQuad(bin, imgArea);
                }

                if (quad == null)
                {
                    LastSummary = "透视校正: 未找到四边形目标 (可调整ROI或拍摄角度)";
                    // 未找到目标时直接显示原图，省去画布分配与整图灰度往返转换
                    return VisionHelper.ToBgrCopy(srcMat);
                }

                // 结果画布（深色衬底），仅在找到四边形后分配
                Mat dst = new(srcMat.Rows, srcMat.Cols, MatType.CV_8UC3, Scalar.All(30));

                Point2f[] srcPts = OrderQuad(quad);
                // 原位置画出找到的四边形
                DrawQuad(dst, srcPts, Scalar.Cyan, 2);

                int tw = outW > 0 ? outW : (int)Math.Max(Dist(srcPts[0], srcPts[1]), Dist(srcPts[3], srcPts[2]));
                int th = outH > 0 ? outH : (int)Math.Max(Dist(srcPts[1], srcPts[2]), Dist(srcPts[3], srcPts[0]));
                tw = Math.Min(4000, Math.Max(16, tw));
                th = Math.Min(4000, Math.Max(16, th));

                // 等比缩放放入结果画布左上角
                double scale = Math.Min(1.0, Math.Min(srcMat.Cols / (double)tw, srcMat.Rows / (double)th));
                int dw = Math.Max(1, (int)(tw * scale));
                int dh = Math.Max(1, (int)(th * scale));

                Point2f[] dstPts =
                {
                    new(0, 0),
                    new(tw - 1, 0),
                    new(tw - 1, th - 1),
                    new(0, th - 1)
                };
                using (Mat m = Cv2.GetPerspectiveTransform(srcPts, dstPts))
                using (Mat warp = new())
                {
                    Cv2.WarpPerspective(srcMat, warp, m, new OpenCvSharp.Size(tw, th));

                    using (Mat small = new())
                    {
                        Cv2.Resize(warp, small, new OpenCvSharp.Size(dw, dh), 0, 0, InterpolationFlags.Area);
                        using (Mat region = new(dst, new Rect(0, 0, dw, dh)))
                            small.CopyTo(region);
                    }
                }

                LastSummary = "透视校正: " + tw + "x" + th + " (缩放显示 " + dw + "x" + dh + ")";
                return dst;
            }
        }

        /// <summary>在二值图中找可逼近为四边形的最大轮廓（排除占满整图的边框）</summary>
        private static Point[] FindBestQuad(Mat bin, double imgArea)
        {
            Point[][] contours;
            HierarchyIndex[] hier;
            Cv2.FindContours(bin, out contours, out hier, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            double bestArea = imgArea * 0.08;
            Point[] best = null;
            foreach (Point[] c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area <= bestArea || area > imgArea * 0.995)
                    continue;
                Point[] ap = TryApproxQuad(c, area);
                if (ap == null)
                    continue;
                bestArea = area;
                best = ap;
            }
            return best;
        }

        /// <summary>多边形逼近到 4 点，各档松弛度依次尝试</summary>
        private static Point[] TryApproxQuad(Point[] contour, double area)
        {
            double peri = Cv2.ArcLength(contour, true);
            for (int i = 2; i <= 8; i++)
            {
                Point[] ap = Cv2.ApproxPolyDP(contour, peri * (0.01 * i), true);
                if (ap.Length == 4)
                {
                    // 简单凸性校验
                    if (Math.Abs(Cv2.ContourArea(ap)) > area * 0.5)
                        return ap;
                }
            }
            return null;
        }

        /// <summary>四角点排序：左上、右上、右下、左下（按质心角度排序后旋转到最上方点起始）</summary>
        private static Point2f[] OrderQuad(Point[] pts)
        {
            // 以质心为参考，按角度升序排序（y 向下坐标系：右→下→左→上 顺时针）
            double cx = 0, cy = 0;
            foreach (Point p in pts) { cx += p.X; cy += p.Y; }
            cx /= pts.Length; cy /= pts.Length;

            Point2f[] q = new Point2f[pts.Length];
            double[] ang = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                q[i] = new Point2f(pts[i].X, pts[i].Y);
                ang[i] = Math.Atan2(pts[i].Y - cy, pts[i].X - cx);
            }
            for (int i = 0; i < pts.Length - 1; i++)
                for (int j = i + 1; j < pts.Length; j++)
                    if (ang[j] < ang[i])
                    {
                        double ta = ang[i]; ang[i] = ang[j]; ang[j] = ta;
                        Point2f tp = q[i]; q[i] = q[j]; q[j] = tp;
                    }

            // 旋转到最上方（y 最小）的点为起点，得到 左上/右上/右下/左下
            int best = 0;
            for (int i = 1; i < 4; i++)
                if (q[i].Y < q[best].Y)
                    best = i;
            return new[]
            {
                q[best],
                q[(best + 1) % 4],
                q[(best + 2) % 4],
                q[(best + 3) % 4]
            };
        }

        private static double Dist(Point2f a, Point2f b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static void DrawQuad(Mat dst, Point2f[] pts, Scalar color, int thickness)
        {
            Point[] poly = new Point[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                poly[i] = new Point((int)Math.Round(pts[i].X), (int)Math.Round(pts[i].Y));
            Cv2.Polylines(dst, new[] { poly }, true, color, thickness, LineTypes.AntiAlias);
        }
    }
}
