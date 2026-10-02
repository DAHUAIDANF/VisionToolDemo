using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>图像缩放算子：按百分比缩放图像，输出尺寸进结果摘要。
    /// 参数：缩放比例%（10~200，100=原尺寸）。</summary>
    public class ResizeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "图像缩放";

        /// <summary>最近一次 Execute 的结果摘要（输出尺寸）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "缩放比例%",
                Min = 10,
                Max = 200,
                DefaultValue = 100,
                DisplayFormat = "缩放:{0}%",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";   // 先清空上次摘要
            double scale = paramValues[0] / 100.0;   // 百分比 → 倍率
            Mat dst = new();
            // 不指定目标尺寸，由 scale 推算；区域插值适合缩小，缩放不失真
            Cv2.Resize(srcMat, dst, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
            // 汇报输出尺寸给链摘要
            LastSummary = $"缩放: {dst.Cols}×{dst.Rows} ({paramValues[0]}%)";
            return dst;
        }
    }
}