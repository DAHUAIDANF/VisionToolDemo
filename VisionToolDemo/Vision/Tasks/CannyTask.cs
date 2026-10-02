using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>Canny 边缘检测算子：内部按 (0.4×阈值, 阈值) 作双阈值，汇报边缘像素占比。
    /// 参数：阈值（0~255）。边缘断断续续就调低，噪点太多就调高。</summary>
    public class CannyTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Canny边缘检测";

        /// <summary>最近一次 Execute 的结果摘要（边缘点数量）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "Canny阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}",
                Tip = "内部按 (0.4×阈值, 阈值) 作为 Canny 的低/高阈值。边缘断断续续就调低，" +
                      "噪点太多就调高。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int threshVal = paramValues[0];
            LastSummary = "";
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat dst = new();
                // 低阈值=threshVal，高阈值=threshVal×2（常见 1:2 比例）
                Cv2.Canny(gray, dst, threshVal, threshVal * 2);

                // 统计边缘点占比：全黑说明阈值过高，提示可调低
                int total = dst.Rows * dst.Cols;
                int edgePx = Cv2.CountNonZero(dst);
                LastSummary = edgePx > 0
                    ? $"Canny: 边缘 {edgePx} 点 ({100.0 * edgePx / total:F1}%)"
                    : "Canny: 无边缘（可调低阈值）";
                return dst;
            }
        }
    }
}
