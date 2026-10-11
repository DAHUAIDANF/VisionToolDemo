using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 亚像素圆测量算子：二值化 → 提取所有候选轮廓 → 逐轮廓最小二乘圆拟合（Kasa 代数法），
    /// 输出亚像素圆心与半径，并在原图上绘制全部检出圆。
    /// 支持多目标（多圆同时检出）；自动处理白底黑圆（前景占比过高时反相）。
    /// 参数：二值化阈值、半径下限/上限（像素，0=不限）。
    /// </summary>
    public class SubPixelCircleTask : IVisionTask, IResultReporter
    {
        public string TaskName => "亚像素圆测量";

        public string LastSummary { get; private set; } = "";

        public Point2f Center { get; private set; }

        public double Radius { get; private set; }

        public bool Found { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "二值化阈值",
                Min = 0, Max = 255, DefaultValue = 128,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度转二值的前景阈值"
            },
            new TaskParamDesc
            {
                ParamName = "半径下限",
                Min = 0, Max = 200, DefaultValue = 5,
                DisplayFormat = "Rmin:{0}",
                Tip = "拟合圆半径下限（像素）；0=不限"
            },
            new TaskParamDesc
            {
                ParamName = "半径上限",
                Min = 0, Max = 500, DefaultValue = 0,
                DisplayFormat = "Rmax:{0}",
                Tip = "拟合圆半径上限（像素）；0=不限"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Found = false;
            Radius = 0;
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int thresh = Math.Max(0, Math.Min(255, paramValues[0]));
            int rMin = paramValues[1];
            int rMax = paramValues[2];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat bin = new();
                Cv2.Threshold(gray, bin, thresh, 255, ThresholdTypes.Binary);

                // 白底黑圆等场景：前景（白）占比过高说明白是背景，反相后检测暗目标
                double whiteRatio = Cv2.CountNonZero(bin) / (double)(bin.Rows * bin.Cols);
                if (whiteRatio > 0.5)
                    Cv2.BitwiseNot(bin, bin);

                // 提取所有轮廓
                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy,
                    RetrievalModes.List, ContourApproximationModes.ApproxNone);
                bin.Dispose();

                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();

                // 逐轮廓拟合圆（Kasa 代数最小二乘法），收集有效圆
                var circles = new List<(Point2f c, double r, int pts)>();
                foreach (var cc in contours)
                {
                    if (cc.Length < 20) continue;

                    int n = cc.Length;
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
                    foreach (var p in cc)
                    {
                        double x = p.X, y = p.Y;
                        double z = x * x + y * y;
                        sx += x; sy += y; sxx += x * x; syy += y * y;
                        sxy += x * y; sxz += x * z; syz += y * z;
                    }
                    double C = n * sxx - sx * sx;
                    double D = n * sxy - sx * sy;
                    double G = n * syy - sy * sy;
                    double H = n * sxz - sx * (sxx + syy);
                    double I = n * syz - sy * (sxx + syy);
                    double denom = C * G - D * D;
                    if (Math.Abs(denom) < 1e-9) continue;   // 共线退化，跳过

                    // Kasa 法正规方程（2x2 消元）：a=(I*D-H*G)/denom, b=(H*D-C*I)/denom
                    double a = (I * D - H * G) / denom;
                    double b = (H * D - C * I) / denom;
                    double cFit = -(sxx + syy + a * sx + b * sy) / n;
                    double cx = -a / 2, cy = -b / 2;
                    double r = Math.Sqrt(cx * cx + cy * cy - cFit);
                    if (double.IsNaN(r) || double.IsInfinity(r)) continue;
                    if ((rMin > 0 && r < rMin) || (rMax > 0 && r > rMax)) continue;
                    // 圆心必须在图像范围内（排除整幅背景拟合出的假圆）
                    if (cx < 0 || cy < 0 || cx >= srcMat.Cols || cy >= srcMat.Rows) continue;

                    circles.Add((new Point2f((float)cx, (float)cy), r, n));
                }

                if (circles.Count == 0)
                {
                    LastSummary = "亚像素圆测量: 未检出圆（阈值 " + thresh + "）";
                    return dst;
                }

                // 全部绘制：绿圈 + 红心 + 半径标签
                foreach (var (c, r, pts) in circles)
                {
                    Cv2.Circle(dst, (int)c.X, (int)c.Y, (int)r, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
                    Cv2.Circle(dst, (int)c.X, (int)c.Y, 3, new Scalar(0, 0, 255), -1);
                    Cv2.PutText(dst, $"R={r:F2}", new Point((int)c.X + 10, (int)(c.Y - r - 5)),
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 1);
                }

                // 取点最多的圆作为主结果（兼容单值结果接口）
                var best = circles[0];
                foreach (var c in circles)
                    if (c.pts > best.pts) best = c;
                Found = true;
                Center = best.c;
                Radius = best.r;

                LastSummary = $"亚像素圆测量: 检出 {circles.Count} 个圆";
                return dst;
            }
        }
    }
}
