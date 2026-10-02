using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>距离变换算子：计算每个前景像素到最近背景的距离，输出归一化灰度图。
    /// 常用于分水岭分割前奏、测宽度等。参数：二值阈值、掩模尺寸（3/5）。</summary>
    public class DistanceTransformTask : IVisionTask, IResultReporter
    {
        public string TaskName => "距离变换";

        /// <summary>最近一次 Execute 的结果摘要（最大距离值）</summary>
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
                ParamName = "掩模尺寸",
                Min = 3,
                Max = 5,
                DefaultValue = 3,
                DisplayFormat = "mask:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            var mask = paramValues[1] >= 5 ? DistanceTransformMasks.Mask5 : DistanceTransformMasks.Mask3;   // 掩模 3×3 或 5×5

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bin = new())
            using (Mat dist32f = new())
            {
                // 先二值化：前景=白，距离对象是"白到最近黑的距离"
                Cv2.Threshold(gray, bin, paramValues[0], 255, ThresholdTypes.Binary);
                Cv2.DistanceTransform(bin, dist32f, DistanceTypes.L2, mask);

                // 归一化到 0-255 便于显示
                Mat dst = new();
                Cv2.Normalize(dist32f, dst, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);

                // 最大距离 ≈ 前景最厚处的半宽，做尺寸估计
                Cv2.MinMaxLoc(dist32f, out double dMin, out double dMax);
                LastSummary = $"距离变换: 最大距离 {dMax:F1}px";
                return dst;
            }
        }
    }
}
