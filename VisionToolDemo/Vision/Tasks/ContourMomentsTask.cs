using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>轮廓质心算子：用图像矩计算每个轮廓的质心并画红点。
    /// 参数：二值阈值、最小轮廓面积。</summary>
    public class ContourMomentsTask : IVisionTask, IResultReporter
    {
        public string TaskName => "轮廓质心";

        /// <summary>最近一次 Execute 的结果摘要（质心数量与首个质心坐标）</summary>
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
                double firstCx = 0, firstCy = 0;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area < minArea)
                        continue;

                    // 一阶矩/零阶矩 = 质心；M00=0 说明轮廓退化（面积0），跳过
                    Moments m = Cv2.Moments(cnt);
                    if (m.M00 == 0) continue;

                    double cxM = m.M10 / m.M00;
                    double cyM = m.M01 / m.M00;
                    if (kept == 0)
                    {
                        firstCx = cxM;
                        firstCy = cyM;
                    }
                    kept++;
                    Cv2.Circle(dst, new Point((int)cxM, (int)cyM), 4, Scalar.Red, -1);
                }

                LastSummary = kept > 0
                    ? $"质心: {kept} 个, 首个 ({firstCx:F1},{firstCy:F1})"
                    : "质心: 未检出（可调低阈值或最小面积）";

                bin.Dispose();
                return dst;
            }
        }
    }
}