using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>轮廓检测算子：二值化后找外轮廓，按最小面积过滤，绿色外接矩形标注。
    /// 参数：二值阈值、最小面积。</summary>
    public class FindContourTask : IVisionTask, IResultReporter
    {
        public string TaskName => "轮廓检测";

        /// <summary>最近一次 Execute 的结果摘要（轮廓数量）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 10,
                Max = 5000,
                DefaultValue = 50,
                DisplayFormat = "最小面积:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 固定阈值二值化，得到轮廓搜索用的黑白图
                Mat bin = new();
                Cv2.Threshold(gray, bin, paramValues[0], 255, ThresholdTypes.Binary);

                // 输出画在 BGR 副本上
                Mat dst = VisionHelper.ToBgrCopy(srcMat);

                // 只取最外层轮廓（External），忽略嵌套孔洞
                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                // 面积小于参数的轮廓是噪点，不标注
                int kept = 0;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area > paramValues[1])
                    {
                        kept++;
                        Rect rect = Cv2.BoundingRect(cnt);
                        Cv2.Rectangle(dst, rect, Scalar.Green, 2);
                    }
                }

                LastSummary = $"轮廓检测: 轮廓 {contours.Length} 个, 保留 {kept} 个 (最小面积 {paramValues[1]})";
                bin.Dispose();
                return dst;
            }
        }
    }
}