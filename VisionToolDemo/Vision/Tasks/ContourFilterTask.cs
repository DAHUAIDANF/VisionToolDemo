using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 轮廓筛选：Otsu 二值化取外轮廓，按面积范围与圆度 4πA/P² 过滤，
    /// 绿色画出保留轮廓并编号，摘要输出轮廓总数与保留数。
    /// 用于按形状/尺寸筛选拾取目标（圆形工件 vs 杂质）。
    /// 参数：最小面积、最大面积、圆度阈值(%)。
    /// </summary>
    public class ContourFilterTask : IVisionTask, IResultReporter
    {
        public string TaskName => "轮廓筛选";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 1,
                Max = 200000,
                DefaultValue = 500,
                DisplayFormat = "Min:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "最大面积",
                Min = 1,
                Max = 200000,
                DefaultValue = 50000,
                DisplayFormat = "Max:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "圆度%",
                Min = 1,
                Max = 100,
                DefaultValue = 60,
                DisplayFormat = "圆度≥{0}%"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int minA = paramValues[0];
            int maxA = paramValues[1];
            double circMin = paramValues[2] / 100.0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bin = new())
            {
                Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Point[][] contours;
                HierarchyIndex[] hier;
                Cv2.FindContours(bin, out contours, out hier, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                int kept = 0;
                foreach (Point[] c in contours)
                {
                    double area = Cv2.ContourArea(c);
                    if (area < minA || area > maxA)
                        continue;
                    double peri = Cv2.ArcLength(c, true);
                    double circ = peri > 0 ? 4 * Math.PI * area / (peri * peri) : 0;
                    if (circ < circMin)
                        continue;

                    kept++;
                    Cv2.Polylines(dst, new[] { c }, true, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                    Rect r = Cv2.BoundingRect(c);
                    Cv2.PutText(dst, kept.ToString(), new Point(r.X, Math.Max(12, r.Y - 4)),
                        HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                }

                LastSummary = "轮廓筛选: 轮廓 " + contours.Length + " 个, 保留 " + kept +
                    " 个 (圆度≥" + circMin.ToString("F2") + ")";
                return dst;
            }
        }
    }
}
