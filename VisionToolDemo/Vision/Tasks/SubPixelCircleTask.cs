using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 亚像素圆测量算子：二值化 → 找最大轮廓 → 最小二乘圆拟合（代数法），
    /// 输出亚像素圆心与半径，在原图上绘制。用于圆孔/圆柱高精度测量。
    /// 参数：二值化阈值、半径下限/上限（像素，0=不限）、拟合方式。
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

                // 找最大轮廓（目标主体）
                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy,
                    RetrievalModes.List, ContourApproximationModes.ApproxNone);
                bin.Dispose();

                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();

                Point[] best = null;
                foreach (var cc in contours)
                    if (cc.Length >= 20 && (best == null || cc.Length > best.Length))
                        best = cc;
                if (best == null)
                {
                    LastSummary = "亚像素圆测量: 未找到轮廓";
                    return dst;
                }

                // 代数最小二乘圆拟合（Kasa 法）
                int n = best.Length;
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
                foreach (var p in best)
                {
                    double x = p.X, y = p.Y;
                    double z = x * x + y * y;
                    sx += x; sy += y; sxx += x * x; syy += y * y;
                    sxy += x * y; sxz += x * z; syz += y * z;
                }
                double C = n * sxx - sx * sx;
                double D = n * sxy - sx * sy;
                double E = n * sxx + n * syy - sx * sx - sy * sy;
                double G = n * syy - sy * sy;
                double H = n * sxz - sx * (sxx + syy);
                double I = n * syz - sy * (sxx + syy);
                double denom = C * G - D * D;
                if (Math.Abs(denom) < 1e-9)
                {
                    LastSummary = "亚像素圆测量: 拟合退化（点共线）";
                    return dst;
                }
                double a = (H * G - D * I) / denom;
                double b = (C * I - D * H) / denom;
                double cFit = -(sxx + syy + a * sx + b * sy) / n;
                double cx = -a / 2, cy = -b / 2;
                double r = Math.Sqrt(cx * cx + cy * cy - cFit);

                if ((rMin > 0 && r < rMin) || (rMax > 0 && r > rMax))
                {
                    LastSummary = $"亚像素圆测量: 半径 {r:F2} 超出范围（{rMin}~{(rMax > 0 ? rMax.ToString() : "∞")}）";
                    return dst;
                }

                Found = true;
                Center = new Point2f((float)cx, (float)cy);
                Radius = r;
                Cv2.Circle(dst, (int)cx, (int)cy, (int)r, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
                Cv2.Circle(dst, (int)cx, (int)cy, 3, new Scalar(0, 0, 255), -1);
                Cv2.PutText(dst, $"R={r:F2}", new Point((int)cx + 10, (int)(cy - r - 5)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 1);

                LastSummary = $"亚像素圆测量: 圆心({cx:F1},{cy:F1}) 半径{r:F2}px";
                return dst;
            }
        }
    }
}
