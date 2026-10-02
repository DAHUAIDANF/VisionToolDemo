using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>二值化算子：固定阈值把灰度图切为黑白，并汇报黑白像素占比。
    /// 参数：二值阈值（0~255）。光照不均时建议改用「自动二值化」（Otsu）。</summary>
    public class ThresholdTask : IVisionTask, IResultReporter
    {
        public string TaskName => "二值化";

        /// <summary>最近一次 Execute 的结果摘要（黑白像素占比）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}",
                Tip = "固定阈值：亮于此值的像素判白。光照不均时固定值很容易失效，" +
                      "建议改用「自动二值化」算子（Otsu 会跟着光照走）。"
            },
            ..RoiRegion.ParamDescs(),
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";

            // 算子级 ROI：只在框选区域做二值化，区域外保持原图不变
            if (RoiRegion.TryGet(srcMat, paramValues, out Rect roi))
            {
                Mat dst = srcMat.Clone();
                using Mat sub = new Mat(srcMat, roi);            // ROI 子视图（不拷贝像素）
                using Mat gray = VisionHelper.ToGray(sub);
                using Mat bin = new Mat();
                // 大于阈值=255（白），否则=0（黑）；阈值按框内像素统计
                Cv2.Threshold(gray, bin, paramValues[0], 255, ThresholdTypes.Binary);
                RoiRegion.PasteBinarized(dst, bin, roi);
                int total = roi.Width * roi.Height;
                int white = Cv2.CountNonZero(bin);
                LastSummary = $"二值化(框选 {roi.Width}x{roi.Height}): 白 {100.0 * white / total:F1}% 黑 {100.0 * (total - white) / total:F1}%";
                return dst;
            }

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat dst = new();
                // 大于阈值=255（白），否则=0（黑）
                Cv2.Threshold(gray, dst, paramValues[0], 255, ThresholdTypes.Binary);

                // 统计黑白占比，作为识别效果的直观反馈
                int total = dst.Rows * dst.Cols;
                int white = Cv2.CountNonZero(dst);
                LastSummary = $"二值化: 白 {100.0 * white / total:F1}% 黑 {100.0 * (total - white) / total:F1}%";
                return dst;
            }
        }
    }
}
