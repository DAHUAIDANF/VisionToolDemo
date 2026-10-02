using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>轮廓多边形拟合算子：用 Douglas-Peucker 算法把轮廓简化为多边形，蓝色描边。
    /// 参数：二值阈值、最小轮廓面积、拟合精度 epsilon（越大顶点越少）。</summary>
    public class ContourApproxPolyTask : IVisionTask, IResultReporter
    {
        public string TaskName => "轮廓多边形拟合";

        /// <summary>最近一次 Execute 的结果摘要（拟合多边形数量与首个多边形顶点数）</summary>
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
            },
            new TaskParamDesc
            {
                ParamName = "拟合精度epsilon",
                Min = 1,
                Max = 50,
                DefaultValue = 5,
                DisplayFormat = "epsilon:{0}",
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
                int firstVerts = 0;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area < minArea)
                        continue;

                    // epsilon 为拟合精度：越大顶点越少（更粗糙）；true=闭合多边形
                    Point[] approx = Cv2.ApproxPolyDP(cnt, paramValues[2], true);
                    if (kept == 0)
                        firstVerts = approx.Length;
                    kept++;
                    Cv2.Polylines(dst, new[] { approx }, true, Scalar.Blue, 2);
                }

                LastSummary = kept > 0
                    ? $"多边形拟合: {kept} 个, 首个 {firstVerts} 顶点 (epsilon {paramValues[2]})"
                    : "多边形拟合: 未检出（可调低阈值或最小面积）";

                bin.Dispose();
                return dst;
            }
        }
    }
}