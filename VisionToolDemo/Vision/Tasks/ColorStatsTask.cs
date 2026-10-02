using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 颜色统计：对（ROI 内）图像统计 BGR 三通道均值与标准差及加权亮度，
    /// 结果图上标注各通道均值；摘要输出全部统计值。
    /// 用于色差/曝光一致性监控，无几何检出。
    /// 参数：无。
    /// </summary>
    public class ColorStatsTask : IVisionTask, IResultReporter
    {
        public string TaskName => "颜色统计";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new TaskParamDesc[0];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            Cv2.MeanStdDev(srcMat, out Scalar mean, out Scalar std);
            double lum = (0.114 * mean.Val0) + (0.587 * mean.Val1) + (0.299 * mean.Val2);
            LastSummary = string.Format(
                "颜色统计: B {0:F1}±{1:F1} G {2:F1}±{3:F1} R {4:F1}±{5:F1} 亮度 {6:F1}",
                mean.Val0, std.Val0, mean.Val1, std.Val1, mean.Val2, std.Val2, lum);

            Cv2.PutText(dst, "B=" + mean.Val0.ToString("F0"), new Point(10, 24),
                HersheyFonts.HersheySimplex, 0.7, Scalar.Orange, 2, LineTypes.AntiAlias);
            Cv2.PutText(dst, "G=" + mean.Val1.ToString("F0"), new Point(10, 50),
                HersheyFonts.HersheySimplex, 0.7, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
            Cv2.PutText(dst, "R=" + mean.Val2.ToString("F0"), new Point(10, 76),
                HersheyFonts.HersheySimplex, 0.7, Scalar.Red, 2, LineTypes.AntiAlias);
            return dst;
        }
    }
}
