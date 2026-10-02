using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>凸包检测算子：对每个轮廓求凸包（最小外接多边形），橙红描边 + 绿色顶点。
    /// 参数：二值阈值、最小轮廓面积。</summary>
    public class ContourConvexHullTask : IVisionTask, IResultReporter
    {
        public string TaskName => "凸包检测";

        /// <summary>最近一次 Execute 的结果摘要（凸包数量）</summary>
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

                // 面积不足的轮廓视为噪点跳过
                int kept = 0;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area < minArea)
                        continue;

                    // 求凸包顶点并闭合描边；顶点用绿点标出，便于核对包得对不对
                    kept++;
                    Point[] hull = Cv2.ConvexHull(cnt);
                    Cv2.Polylines(dst, new[] { hull }, true, Scalar.OrangeRed, 2);
                    foreach (var pt in hull)
                    {
                        Cv2.Circle(dst, pt, 3, Scalar.LimeGreen, -1);
                    }
                }

                LastSummary = $"凸包: {kept} 个";

                bin.Dispose();
                return dst;
            }
        }
    }
}