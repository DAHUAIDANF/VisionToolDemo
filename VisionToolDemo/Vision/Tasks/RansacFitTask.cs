using System;
using System.Collections.Generic;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// RANSAC 抗噪拟合：直线 / 圆 / 椭圆。
    ///
    /// 为什么需要它：最小二乘拟合会被离群点（脏污、毛刺、边缘噪声）严重带偏；
    /// RANSAC 随机采样最小点集反复试模型，只保留内点最多的那个，对噪声/杂点
    /// 非常鲁棒。典型场景：
    ///   · 边缘点里混了螺丝/焊点，拟合圆仍能贴合真实轮廓；
    ///   · 直线边缘有划痕缺口，仍能求出主方向。
    ///
    /// 输入：二值图（白色像素 = 待拟合点集），可从二值化/边缘/轮廓结果接入。
    /// 输出：原图（灰度化后叠加拟合线/圆/椭圆 + 几何参数文字）。
    /// </summary>
    public class RansacFitTask : IVisionTask, IResultReporter
    {
        public string TaskName => "RANSAC拟合";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次拟合的内点占比（0~1，越高说明点集越贴合模型）</summary>
        public double InlierRatio { get; private set; }

        /// <summary>最近一次拟合的圆心（圆/椭圆）或直线上最近点（像素坐标）</summary>
        public Point2f Center { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "拟合类型 0直线1圆2椭圆",
                Min = 0,
                Max = 2,
                DefaultValue = 1,
                DisplayFormat = "类型:{0}",
                Tip = "0=直线（y=kx+b）1=圆（圆心+半径）2=椭圆（主轴+角度，最少需要约50个点才有意义）"
            },
            new TaskParamDesc
            {
                ParamName = "迭代次数",
                Min = 20,
                Max = 5000,
                DefaultValue = 300,
                DisplayFormat = "迭代:{0}",
                Tip = "RANSAC 尝试次数。点数多/噪声大时加大；用 300~800 通常够"
            },
            new TaskParamDesc
            {
                ParamName = "内点阈值(像素)",
                Min = 1,
                Max = 50,
                DefaultValue = 2,
                DisplayFormat = "阈值:{0}px",
                Tip = "点到模型的距离小于该值视为内点。亚像素级边缘用 1~2，粗边缘用 3~5"
            },
            new TaskParamDesc
            {
                ParamName = "最小内点比例%",
                Min = 5,
                Max = 100,
                DefaultValue = 30,
                DisplayFormat = "内点比:{0}%",
                Tip = "内点数不足总点数该比例时判定拟合失败（输出空结果并提示）"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int type = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 1, 0, 2);
            int iters = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 300, 20, 5000);
            float thresh = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 2, 1, 50);
            int minRatioPct = Math.Clamp(paramValues.Length > 3 ? paramValues[3] : 30, 5, 100);

            // 从二值图收集白色像素
            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, 127, 255, ThresholdTypes.Binary);
            var pts = new List<Point2f>();
            for (int y = 0; y < bin.Rows; y++)
                for (int x = 0; x < bin.Cols; x++)
                    if (bin.At<byte>(y, x) > 0)
                        pts.Add(new Point2f(x, y));

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (pts.Count < 5)
            {
                LastSummary = string.Format("RANSAC拟合: 白色像素仅 {0} 个，无法拟合（请先接二值化/边缘图）", pts.Count);
                return dst;
            }

            int need = type == 0 ? 2 : type == 1 ? 3 : 5;
            int bestInliers = 0;
            double bestScore = double.MaxValue;
            double bestA = 0, bestB = 0, bestC = 0;   // 线: A x + B y + C = 0
            Point2f bestCenter = default;
            float bestRadius = 0;
            double bestAngle = 0, bestAxis1 = 0, bestAxis2 = 0;

            var rng = new Random(12345);
            int minInliers = Math.Max(need + 1, pts.Count * minRatioPct / 100);
            Point2f[] arr = pts.ToArray();

            // 栈采样缓冲提到 RANSAC 循环外，避免 CA2014 潜在栈溢出警告
            Span<int> s = stackalloc int[5];
            for (int it = 0; it < iters && bestInliers < pts.Count; it++)
            {
                // 采样最小点集（复用循环外的栈缓冲）
                for (int k = 0; k < need; k++)
                {
                    bool dup;
                    int pick;
                    do
                    {
                        pick = rng.Next(arr.Length);
                        dup = false;
                        for (int m = 0; m < k; m++) if (s[m] == pick) { dup = true; break; }
                    } while (dup);
                    s[k] = pick;
                }

                // 拟合候选模型
                if (type == 0)
                {
                    Point2f p1 = arr[s[0]], p2 = arr[s[1]];
                    if (Math.Abs(p1.X - p2.X) < 0.5f && Math.Abs(p1.Y - p2.Y) < 0.5f) continue;
                    double a = p2.Y - p1.Y, b = p1.X - p2.X, c = p2.X * p1.Y - p1.X * p2.Y;
                    double norm = Math.Sqrt(a * a + b * b);
                    if (norm < 1e-9) continue;
                    a /= norm; b /= norm; c /= norm;
                    CountInliers(arr, need, a, b, c, 0, 0, 0, 0, 0, 0, thresh, out int inl, out double score);
                    if (inl > bestInliers || (inl == bestInliers && score < bestScore))
                    {
                        bestInliers = inl; bestScore = score;
                        bestA = a; bestB = b; bestC = c;
                    }
                }
                else if (type == 1)
                {
                    // 三点定圆
                    Point2f p1 = arr[s[0]], p2 = arr[s[1]], p3 = arr[s[2]];
                    double d = 2 * (p1.X * (p2.Y - p3.Y) + p2.X * (p3.Y - p1.Y) + p3.X * (p1.Y - p2.Y));
                    if (Math.Abs(d) < 1e-6) continue;
                    double ux = ((p1.X * p1.X + p1.Y * p1.Y) * (p2.Y - p3.Y) +
                                 (p2.X * p2.X + p2.Y * p2.Y) * (p3.Y - p1.Y) +
                                 (p3.X * p3.X + p3.Y * p3.Y) * (p1.Y - p2.Y)) / d;
                    double uy = ((p1.X * p1.X + p1.Y * p1.Y) * (p3.X - p2.X) +
                                 (p2.X * p2.X + p2.Y * p2.Y) * (p1.X - p3.X) +
                                 (p3.X * p3.X + p3.Y * p3.Y) * (p2.X - p1.X)) / d;
                    double r = Math.Sqrt((p1.X - ux) * (p1.X - ux) + (p1.Y - uy) * (p1.Y - uy));
                    if (r < 0.5 || r > Math.Max(dst.Cols, dst.Rows)) continue;
                    CountInliers(arr, need, 0, 0, 0, (float)ux, (float)uy, (float)r, 0, 0, 0, thresh, out int inl, out double score);
                    if (inl > bestInliers || (inl == bestInliers && score < bestScore))
                    {
                        bestInliers = inl; bestScore = score;
                        bestCenter = new Point2f((float)ux, (float)uy);
                        bestRadius = (float)r;
                    }
                }
                else
                {
                    // 5 点代数拟合椭圆：D·v=0（Fitzgibbon），取最小特征向量
                    if (!FitEllipseAlgebraic(arr, s, out double eA, out double eB, out double eC, out double eD, out double eE, out double eF))
                        continue;
                    // 转几何参数：中心/主轴/角度
                    if (!EllipseParams(eA, eB, eC, eD, eE, eF, out Point2f c, out double a1, out double a2, out double ang))
                        continue;
                    CountInliers(arr, need, 0, 0, 0, 0, 0, 0, eA, eB, eC, thresh, out int inl, out double score,
                        eD, eE, eF);
                    if (inl > bestInliers || (inl == bestInliers && score < bestScore))
                    {
                        bestInliers = inl; bestScore = score;
                        bestCenter = c; bestAxis1 = a1; bestAxis2 = a2; bestAngle = ang;
                    }
                }
            }

            if (bestInliers < minInliers)
            {
                LastSummary = string.Format(
                    "RANSAC拟合: 最佳内点 {0}/{1}（{2:P0}），低于最低要求 {3:P0}，请调低内点阈值或改用更少噪声的图",
                    bestInliers, pts.Count, (double)bestInliers / pts.Count, minRatioPct / 100.0);
                InlierRatio = (double)bestInliers / pts.Count;
                return dst;
            }

            InlierRatio = (double)bestInliers / pts.Count;
            var sb = new StringBuilder();
            if (type == 0)
            {
                // 画线：两端延伸到图像边缘
                Point2f p1 = LineEdge(bestA, bestB, bestC, dst.Cols, dst.Rows, true);
                Point2f p2 = LineEdge(bestA, bestB, bestC, dst.Cols, dst.Rows, false);
                Cv2.Line(dst, new Point((int)p1.X, (int)p1.Y), new Point((int)p2.X, (int)p2.Y),
                    Scalar.Lime, 2, LineTypes.AntiAlias);
                sb.AppendFormat("RANSAC拟合: 直线（法向 {0:F3},{1:F3} 截距 {2:F1}） 内点 {3}/{4} ({5:P0})",
                    bestA, bestB, bestC, bestInliers, pts.Count, InlierRatio);
            }
            else if (type == 1)
            {
                Cv2.Circle(dst, (int)bestCenter.X, (int)bestCenter.Y, (int)Math.Round(bestRadius),
                    Scalar.Lime, 2, LineTypes.AntiAlias);
                Cv2.Circle(dst, (int)bestCenter.X, (int)bestCenter.Y, 4, Scalar.Red, -1);
                sb.AppendFormat("RANSAC拟合: 圆 圆心({0:F1},{1:F1}) 半径={2:F2}px 内点 {3}/{4} ({5:P0})",
                    bestCenter.X, bestCenter.Y, bestRadius, bestInliers, pts.Count, InlierRatio);
                Center = bestCenter;
            }
            else
            {
                DrawEllipse(dst, bestCenter, bestAxis1, bestAxis2, bestAngle, Scalar.Lime, 2);
                Cv2.Circle(dst, (int)bestCenter.X, (int)bestCenter.Y, 4, Scalar.Red, -1);
                sb.AppendFormat("RANSAC拟合: 椭圆 中心({0:F1},{1:F1}) 长轴={2:F1} 短轴={3:F1} 角={4:F1}° 内点 {5}/{6} ({7:P0})",
                    bestCenter.X, bestCenter.Y, bestAxis1, bestAxis2, bestAngle, bestInliers, pts.Count, InlierRatio);
                Center = bestCenter;
            }
            LastSummary = sb.ToString();
            return dst;
        }

        /// <summary>统计点到模型的距离平方和（score），内点计数</summary>
        private static void CountInliers(Point2f[] pts, int need,
            double a, double b, double c, float cx, float cy, float r,
            double eA, double eB, double eC, float thresh, out int inliers, out double score,
            double eD = 0, double eE = 0, double eF = 0)
        {
            inliers = 0; score = 0;
            float t2 = thresh * thresh;
            bool line = Math.Abs(a) > 0 || Math.Abs(b) > 0;
            bool circle = !line && r > 0;
            for (int i = 0; i < pts.Length; i++)
            {
                float d2;
                if (line)
                {
                    double v = a * pts[i].X + b * pts[i].Y + c;
                    d2 = (float)(v * v);
                }
                else if (circle)
                {
                    float dx = pts[i].X - cx, dy = pts[i].Y - cy;
                    float dr = (float)Math.Sqrt(dx * dx + dy * dy) - r;
                    d2 = dr * dr;
                }
                else
                {
                    // 椭圆代数残差（归一化到梯度幅值近似几何距离）
                    double x = pts[i].X, y = pts[i].Y;
                    double v = eA * x * x + eB * x * y + eC * y * y + eD * x + eE * y + eF;
                    double gx = 2 * eA * x + eB * y + eD;
                    double gy = eB * x + 2 * eC * y + eE;
                    double g = gx * gx + gy * gy + 1e-9;
                    d2 = (float)(v * v / g);
                }
                if (d2 <= t2) inliers++;
                else score += d2;
            }
        }

        /// <summary>5 点最小二乘解椭圆系数（D·v=0，v=[A B C D E F]，取最小特征向量）</summary>
        private static bool FitEllipseAlgebraic(Point2f[] pts, Span<int> s,
            out double A, out double B, out double C, out double D, out double E, out double F)
        {
            A = B = C = D = E = F = 0;
            using Mat m = new Mat(5, 6, MatType.CV_64FC1);
            for (int i = 0; i < 5; i++)
            {
                double x = pts[s[i]].X, y = pts[s[i]].Y;
                m.Set(i, 0, x * x);
                m.Set(i, 1, x * y);
                m.Set(i, 2, y * y);
                m.Set(i, 3, x);
                m.Set(i, 4, y);
                m.Set(i, 5, 1.0);
            }
            using Mat DtD = new();
            Cv2.MulTransposed(m, DtD, true);          // 6x6
            Mat evals = new();
            Mat evecs = new();
            Cv2.Eigen(DtD, evals, evecs);
            if (evals.Rows < 6) { evals.Dispose(); evecs.Dispose(); return false; }
            // 特征向量行序与特征值对应，取最小特征值那一行
            int minRow = 0;
            double minV = double.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                double v = evals.At<double>(i, 0);
                if (v < minV) { minV = v; minRow = i; }
            }
            A = evecs.At<double>(minRow, 0);
            B = evecs.At<double>(minRow, 1);
            C = evecs.At<double>(minRow, 2);
            D = evecs.At<double>(minRow, 3);
            E = evecs.At<double>(minRow, 4);
            F = evecs.At<double>(minRow, 5);
            evals.Dispose();
            evecs.Dispose();
            return true;
        }

        /// <summary>代数系数 → 几何参数（中心/半长轴/半短轴/角度°）。椭圆判别式需成立</summary>
        private static bool EllipseParams(double A, double B, double C, double D, double E, double F,
            out Point2f center, out double axis1, out double axis2, out double angle)
        {
            center = default; axis1 = axis2 = angle = 0;
            double denom = B * B - 4 * A * C;
            if (Math.Abs(denom) < 1e-12) return false;   // 不是椭圆（抛物线/退化）
            double cx = (2 * C * D - B * E) / denom;
            double cy = (2 * A * E - B * D) / denom;
            double k = A * cx * cx + B * cx * cy + C * cy * cy - F;
            if (k * denom >= 0) return false;            // 无实数解
            double num = 2 * (A * E * E + C * D * D - B * D * E + denom * F);
            double tmp = Math.Sqrt((A - C) * (A - C) + B * B);
            double l1 = (A + C + tmp) / 2, l2 = (A + C - tmp) / 2;
            if (Math.Abs(l1) < 1e-12 || Math.Abs(l2) < 1e-12) return false;
            double a1 = Math.Sqrt(Math.Abs(-k / l1));
            double a2 = Math.Sqrt(Math.Abs(-k / l2));
            if (a1 < a2) { (a1, a2) = (a2, a1); }
            angle = Math.Atan2(B, A - C) * 180 / Math.PI / 2;
            center = new Point2f((float)cx, (float)cy);
            axis1 = a1; axis2 = a2;
            return a1 > 0.5 && a2 > 0.5;
        }

        /// <summary>按直线方程求其与图像边界的交点（end=true 取右/下方向）</summary>
        private static Point2f LineEdge(double a, double b, double c, int w, int h, bool end)
        {
            // 与四条边求交，取最远两端
            var cand = new List<Point2f>();
            void Add(double x, double y)
            {
                if (x >= -0.5 && x <= w + 0.5 && y >= -0.5 && y <= h + 0.5)
                    cand.Add(new Point2f((float)x, (float)y));
            }
            if (Math.Abs(a) > 1e-9) { Add(-c / a, 0); Add(-(c + b * h) / a, h); }
            if (Math.Abs(b) > 1e-9) { Add(0, -c / b); Add(w, -(c + a * w) / b); }
            if (cand.Count < 2) return new Point2f(0, 0);
            double cx = w / 2.0, cy = h / 2.0;
            cand.Sort((p, q) =>
            {
                double dp = (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
                double dq = (q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy);
                return dp.CompareTo(dq);
            });
            return end ? cand[^1] : cand[0];
        }

        /// <summary>按中心/轴/角度画椭圆</summary>
        private static void DrawEllipse(Mat img, Point2f center, double axis1, double axis2, double angleDeg, Scalar color, int thickness)
        {
            RotatedRect rr = new(center, new Size2f((float)axis1 * 2, (float)axis2 * 2), (float)angleDeg);
            Cv2.Ellipse(img, rr, color, thickness, LineTypes.AntiAlias);
        }
    }
}
