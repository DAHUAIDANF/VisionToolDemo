using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>HSV 颜色提取算子：按 H/S/V 范围框选颜色区域，输出该颜色的原图内容。
    /// 参数：H/S/V 各自的高低位。常用于按颜色挑目标（如红色喷码）。</summary>
    public class HsvRangeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "HSV颜色提取";

        /// <summary>最近一次 Execute 的结果摘要（范围内像素占比）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "H低",
                Min = 0,
                Max = 179,
                DefaultValue = 0,
                DisplayFormat = "H低:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "H高",
                Min = 0,
                Max = 179,
                DefaultValue = 30,
                DisplayFormat = "H高:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "S低",
                Min = 0,
                Max = 255,
                DefaultValue = 120,
                DisplayFormat = "S低:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "S高",
                Min = 0,
                Max = 255,
                DefaultValue = 255,
                DisplayFormat = "S高:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "V低",
                Min = 0,
                Max = 255,
                DefaultValue = 100,
                DisplayFormat = "V低:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "V高",
                Min = 0,
                Max = 255,
                DefaultValue = 255,
                DisplayFormat = "V高:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            Mat dst = new();
            // HSV 转换要求 8U / 32F，且输入必须是 3 通道 BGR。
            // 16 位 RAW 或单通道灰度直接进来都会抛异常，这里统一规整。
            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);
            using (Mat hsv = new())
            using (Mat mask = new())
            {
                Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                Cv2.InRange(hsv, new Scalar(paramValues[0], paramValues[2], paramValues[4]),
                                 new Scalar(paramValues[1], paramValues[3], paramValues[5]), mask);
                Cv2.BitwiseAnd(bgr, bgr, dst, mask);

                int total = mask.Rows * mask.Cols;
                int kept = Cv2.CountNonZero(mask);
                LastSummary = $"HSV提取: 范围内 {100.0 * kept / total:F1}% ({kept} px)";
            }
            return dst;
        }
    }
}