using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 圆卡尺工具：以名义圆为基准，沿多个径向扫描线（射线）搜索灰度边缘，
    /// 将各方向找到的边缘点最小二乘拟合成一个圆并标注。
    /// 圆的位置/半径由 UI 层在原图上拖动注入（起点为圆心，拖动距离为半径，真实像素坐标）；
    /// 可调参数：边缘阈值（径向梯度阈值）、检测带宽（径向搜索范围 ±带宽/2）。
    /// 显示分工：圆环带（黄）+ 扫描方向箭头（青）由 UI 层叠加在原图上；
    /// 本任务在结果图上只绘制拟合出的圆（绿）。
    /// </summary>
    public class CircleCaliperTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "圆卡尺";

        /// <summary>圆心与半径（真实像素坐标），UI 层拖动后赋值</summary>
        public Point Center { get; private set; }

        public int Radius { get; private set; }
        public bool HasCircle { get; private set; }

        /// <summary>最近一次执行的拟合结果</summary>
        public bool FitOk { get; private set; }
        public Point2d FitCenter { get; private set; }
        public double FitRadius { get; private set; }
        public int FitPointCount { get; private set; }

        /// <summary>最近一次 Execute 的结果摘要（拟合圆参数或未检出提示）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>UI层拖动后调用，设置圆心/半径并标记圆卡尺有效</summary>
        public void SetCircle(Point center, int radius)
        {
            Center = center;
            Radius = Math.Max(1, radius);
            HasCircle = true;
        }

        public void ClearCircle()
        {
            Center = new Point();
            Radius = 0;
            HasCircle = false;
            FitOk = false;
            FitPointCount = 0;
        }

        /// <summary>状态 = 圆 [cx, cy, r]（未画圆时不保存）</summary>
        public string SaveState()
        {
            return HasCircle
                ? JsonConvert.SerializeObject(new[] { Center.X, Center.Y, Radius })
                : null;
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p is { Length: 3 })
                    SetCircle(new Point(p[0], p[1]), p[2]);
            }
            catch
            {
                // 状态内容损坏时忽略，保持未画圆
            }
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "边缘阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 30,
                DisplayFormat = "阈值:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "检测带宽",
                Min = 1,
                Max = 600,
                DefaultValue = 100,
                DisplayFormat = "带宽:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int edgeThresh = paramValues[0];
            int bandHalf = Math.Max(1, paramValues[1] / 2);
            // 角度范围 + 极性（老链 Values 可能没有这些参数，缺省=整圆+任意）
            int startDeg = paramValues.Length > 2 ? paramValues[2] : 0;
            int endDeg = paramValues.Length > 3 ? paramValues[3] : 360;
            int polar = paramValues.Length > 4 ? paramValues[4] : 0;
            if (endDeg < startDeg || endDeg - startDeg >= 360) endDeg = startDeg + 360;
            bool fullCircle = (endDeg - startDeg) >= 360;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            FitOk = false;
            FitPointCount = 0;
            LastSummary = "";

            if (!HasCircle || Radius < 2)
                return dst; // 未画圆时只返回原图

            double cx = Center.X;
            double cy = Center.Y;

            // 扫描方向数：弧长步长 1 像素（上限 1000），径向在 [r-bandHalf, r+bandHalf] 搜索边缘。
            // VisionMaster 风格：角度范围（起始角→终止角）限定扫描圆弧段。
            double sweepRad = (endDeg - startDeg) * Math.PI / 180.0;
            int rayCount = Math.Min((int)(sweepRad * Radius), 1000);
            if (rayCount < 5)
                return dst;

            int rMin = Math.Max(1, Radius - bandHalf);
            int radialCount = Radius + bandHalf - rMin + 1;

            List<Point2d> risePts = [];
            List<Point2d> fallPts = [];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                for (int i = 0; i < rayCount; i++)
                {
                    double ang = startDeg * Math.PI / 180.0 + (sweepRad * i / Math.Max(1, rayCount - (fullCircle ? 1 : 0)));
                    double cosT = Math.Cos(ang), sinT = Math.Sin(ang);
                    double tX = -sinT, tY = cosT; // 切向（垂直于径向扫描方向）

                    // 径向剖面：每个半径处沿切向 ±1 像素取均值抑制噪声（双线性取亚像素）
                    double[] profile = new double[radialCount];
                    for (int j = 0; j < radialCount; j++)
                    {
                        double rr = rMin + j;
                        double px = cx + (rr * cosT);
                        double py = cy + (rr * sinT);
                        if (px < 1 || py < 1 || px >= gray.Cols - 2 || py >= gray.Rows - 2)
                            continue;
                        double sum = SampleBilinear(gray, px, py)
                                   + SampleBilinear(gray, px + tX, py + tY)
                                   + SampleBilinear(gray, px - tX, py - tY);
                        profile[j] = sum / 3;
                    }

                    // 径向一阶差分，找该方向最强的上升/下降沿（按极性过滤）
                    int bestRise = -1, bestFall = -1;
                    double maxGrad = 0, minGrad = 0;
                    for (int j = 1; j < radialCount; j++)
                    {
                        double g = profile[j] - profile[j - 1];
                        if (polar == 2 && g <= 0) continue;   // 只要暗到亮：忽略下降
                        if (polar == 1 && g >= 0) continue;   // 只要亮到暗：忽略上升
                        if (g > maxGrad) { maxGrad = g; bestRise = j; }
                        if (g < minGrad) { minGrad = g; bestFall = j; }
                    }
                    if (bestRise < 0 && bestFall < 0)
                        continue; // 整条径向线都在图外（只找一种极性时，另一种没有不算越界）

                    bool isRise = polar == 2 || (polar == 0 && maxGrad >= -minGrad);
                    int jBest = isRise ? bestRise : bestFall;
                    double gPeak = isRise ? maxGrad : minGrad;
                    if (Math.Abs(gPeak) < edgeThresh)
                        continue;

                    // 梯度峰抛物线内插求亚像素半径
                    double g0 = jBest - 1 >= 1 ? profile[jBest - 1] - profile[jBest - 2] : gPeak;
                    double g2 = jBest + 1 < radialCount ? profile[jBest + 1] - profile[jBest] : gPeak;
                    double denom = g2 - (2 * gPeak) + g0;
                    double delta = Math.Abs(denom) > 1e-9 ? 0.5 + ((gPeak - g2) / denom) : 0;
                    if (delta < -0.5) delta = -0.5;
                    if (delta > 0.5) delta = 0.5;
                    double rEdge = rMin + jBest + delta - 0.5;

                    Point2d edgePt = new(cx + (rEdge * cosT), cy + (rEdge * sinT));
                    if (isRise) risePts.Add(edgePt); else fallPts.Add(edgePt);
                }

                // —— 拟合圆（Kåsa 最小二乘，SVD 求解）：x²+y² = 2a·x + 2b·y + c ——
                List<Point2d> allPts = [.. risePts, .. fallPts];
                FitPointCount = allPts.Count;

                if (allPts.Count >= 5)
                {
                    using (Mat A = new(allPts.Count, 3, MatType.CV_64FC1))
                    using (Mat B = new(allPts.Count, 1, MatType.CV_64FC1))
                    using (Mat X = new(3, 1, MatType.CV_64FC1))
                    {
                        for (int i = 0; i < allPts.Count; i++)
                        {
                            A.Set(i, 0, 2 * allPts[i].X);
                            A.Set(i, 1, 2 * allPts[i].Y);
                            A.Set(i, 2, 1.0);
                            B.Set(i, (allPts[i].X * allPts[i].X) + (allPts[i].Y * allPts[i].Y));
                        }
                        if (Cv2.Solve(A, B, X, DecompTypes.SVD))
                        {
                            double fa = X.Get<double>(0, 0);
                            double fb = X.Get<double>(1, 0);
                            double fc = X.Get<double>(2, 0);
                            double rr2 = fc + (fa * fa) + (fb * fb);
                            if (rr2 > 1 && !double.IsNaN(fa) && !double.IsInfinity(fa)
                                && Math.Sqrt(rr2) < 10.0 * Math.Max(srcMat.Cols, srcMat.Rows))
                            {
                                FitCenter = new Point2d(fa, fb);
                                FitRadius = Math.Sqrt(rr2);
                                FitOk = true;
                            }
                        }
                    }
                }

                // —— 绘制：结果图上只画拟合出的圆 ——
                if (FitOk)
                {
                    Cv2.Circle(dst, new Point((int)Math.Round(FitCenter.X), (int)Math.Round(FitCenter.Y)),
                               (int)Math.Round(FitRadius), Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                    LastSummary = $"圆卡尺: 检出圆心({FitCenter.X:F1},{FitCenter.Y:F1}) 半径:{FitRadius:F1} 边缘点:{FitPointCount}";
                }
                else
                {
                    LastSummary = "圆卡尺: 未检出边缘（可调低阈值或加大带宽）";
                }

                return dst;
            }
        }

        /// <summary>双线性插值采样，获得亚像素灰度值</summary>
        private static double SampleBilinear(Mat gray, double x, double y)
        {
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;

            double v00 = gray.Get<byte>(y0, x0);
            double v10 = gray.Get<byte>(y0, x0 + 1);
            double v01 = gray.Get<byte>(y0 + 1, x0);
            double v11 = gray.Get<byte>(y0 + 1, x0 + 1);

            return (((v00 * (1 - fx)) + (v10 * fx)) * (1 - fy))
                 + (((v01 * (1 - fx)) + (v11 * fx)) * fy);
        }
    }
}
