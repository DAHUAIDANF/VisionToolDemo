using OpenCvSharp;
using System.Linq;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>最小外接矩形算子：对每个轮廓求可旋转的最小矩形（含角度），黄色四边形 + 红色中心。
    /// 参数：二值阈值、最小轮廓面积。</summary>
    public class ContourMinRectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "最小旋转外接矩形";

        /// <summary>最近一次 Execute 的结果摘要（检出矩形数量与首个矩形参数）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "thresh:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最小轮廓面积",
                Min = 5,
                Max = 5000,
                DefaultValue = 30,
                DisplayFormat = "minArea:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            int minArea = paramValues[1];
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat bin = new();
                Cv2.Threshold(gray, bin, paramValues[0], 255, ThresholdTypes.Binary);

                Mat dst = VisionHelper.ToBgrCopy(srcMat);

                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                int kept = 0;
                RotatedRect firstRect = default;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area < minArea)
                        continue;

                    // 最小外接矩形可随物体旋转，四个角点来自 RotatedRect
                    RotatedRect minRect = Cv2.MinAreaRect(cnt);
                    if (kept == 0)
                        firstRect = minRect;
                    kept++;

                    Point2f[] pts = minRect.Points();
                    Point[] cornerPts = pts.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
                    Cv2.Polylines(dst, new[] { cornerPts }, true, Scalar.Yellow, 2);
                    Cv2.Circle(dst, new Point((int)minRect.Center.X, (int)minRect.Center.Y), 3, Scalar.Red, -1);
                }

                LastSummary = kept > 0
                    ? $"最小外接矩形: {kept} 个, 首个 {firstRect.Size.Width:F0}×{firstRect.Size.Height:F0} 角度 {firstRect.Angle:F1}°"
                    : "最小外接矩形: 未检出（可调低阈值或最小面积）";

                bin.Dispose();
                return dst;
            }
        }
    }
}