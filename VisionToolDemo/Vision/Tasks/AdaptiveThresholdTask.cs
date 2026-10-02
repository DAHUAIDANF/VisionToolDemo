using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>自适应阈值算子：每个像素按邻域均值/高斯加权决定自身阈值，
    /// 对光照不均图片比固定阈值更稳。参数：最大值、块大小（奇数）、C 偏移。</summary>
    public class AdaptiveThresholdTask : IVisionTask, IResultReporter
    {
        public string TaskName => "自适应阈值";

        /// <summary>最近一次 Execute 的结果摘要（黑白像素占比）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "最大值",
                Min = 0,
                Max = 255,
                DefaultValue = 255,
                DisplayFormat = "最大值:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "块大小",
                Min = 3,
                Max = 31,
                DefaultValue = 11,
                DisplayFormat = "块:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "C偏移",
                Min = -20,
                Max = 20,
                DefaultValue = 2,
                DisplayFormat = "C:{0}",
                ForceOdd = false
            },
            ..RoiRegion.ParamDescs(),
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";

            // 算子级 ROI：只在框选区域做自适应阈值，区域外保持原图不变
            if (RoiRegion.TryGet(srcMat, paramValues, out Rect roi))
            {
                Mat dst = srcMat.Clone();
                using Mat sub = new Mat(srcMat, roi);            // ROI 子视图
                using Mat gray = VisionHelper.ToGray(sub);
                using Mat bin = new Mat();
                // 高斯加权邻域均值作局部阈值；C 为从均值减去的常量偏移（越大越容易判黑）
                Cv2.AdaptiveThreshold(gray, bin, paramValues[0], AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, paramValues[1], paramValues[2]);
                RoiRegion.PasteBinarized(dst, bin, roi);
                int total = roi.Width * roi.Height;
                int white = Cv2.CountNonZero(bin);
                LastSummary = $"自适应阈值(框选 {roi.Width}x{roi.Height}): 白 {100.0 * white / total:F1}% 黑 {100.0 * (total - white) / total:F1}%";
                return dst;
            }

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat dst = new();
                // 高斯加权邻域均值作局部阈值；C 为从均值减去的常量偏移（越大越容易判黑）
                Cv2.AdaptiveThreshold(gray, dst, paramValues[0], AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, paramValues[1], paramValues[2]);

                // 黑白占比反馈
                int total = dst.Rows * dst.Cols;
                int white = Cv2.CountNonZero(dst);
                LastSummary = $"自适应阈值: 白 {100.0 * white / total:F1}% 黑 {100.0 * (total - white) / total:F1}%";
                return dst;
            }
        }
    }
}