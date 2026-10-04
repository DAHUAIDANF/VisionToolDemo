using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 高度统计（3D 测量）：在 ROI 区域内统计深度图的最小/最大/均值/高度差，
    /// 并把结果直接画在可视化图上（文字标注）。台阶判定：左右半区均值差超过阈值判"有台阶"。
    /// 输入 16U 深度图 → 输出 8U 灰度可视化 + 画上统计文字。
    /// </summary>
    public class DepthStatsTask : IVisionTask, IResultReporter
    {
        public string TaskName => "高度统计";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "深度比例x100",
                Min = 1,
                Max = 10000,
                DefaultValue = 100,
                DisplayFormat = "比例:{0:F2}",
                Tip = "深度值 × 比例 = 真实单位（100=1.0 时深度值即毫米）"
            },
            new TaskParamDesc
            {
                ParamName = "台阶阈值",
                Min = 1,
                Max = 10000,
                DefaultValue = 50,
                DisplayFormat = "阈值:{0}",
                Tip = "左右半区平均高度差（原始深度单位）超过此值判定为「有台阶」"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_16UC1)
            {
                LastSummary = "高度统计: 跳过 —— 输入不是 16U 深度图";
                return srcMat.Clone();
            }
            double scale = paramValues[0] / 100.0;
            int stepTh = Math.Max(1, paramValues[1]);

            int W = srcMat.Cols, H = srcMat.Rows;
            Mat roi = srcMat;   // 别名，不拥有、不 Dispose（srcMat 归调用方/链层所有）
            double mn, mx, mean;
            Cv2.MinMaxLoc(roi, out mn, out mx, out _, out _);
            mean = roi.Mean().Val0;

            // 左右半区平均（台阶检测）
            int half = W / 2;
            double leftMean = 0, rightMean = 0;
            if (half >= 4)
            {
                using Mat left = new Mat(roi, new OpenCvSharp.Rect(0, 0, half, H));
                using Mat right = new Mat(roi, new OpenCvSharp.Rect(half, 0, W - half, H));
                leftMean = left.Mean().Val0;
                rightMean = right.Mean().Val0;
            }
            double diff = Math.Abs(leftMean - rightMean);
            bool stepped = diff > stepTh;

            // 可视化：MinMax 归一化 8U
            double span = mx - mn;
            if (span < 1) span = 1;
            using Mat gray = new();
            roi.ConvertTo(gray, MatType.CV_8U, 255.0 / span, -mn * 255.0 / span);
            Mat dst = new();
            Cv2.CvtColor(gray, dst, ColorConversionCodes.GRAY2BGR);

            // 画统计文字
            string[] lines =
            {
                string.Format("Min {0:F1}  Max {1:F1}", mn * scale, mx * scale),
                string.Format("Avg {0:F1}  差 {1:F1}", mean * scale, diff * scale),
                stepped ? "台阶: 有" : "台阶: 无",
            };
            for (int i = 0; i < lines.Length; i++)
                Cv2.PutText(dst, lines[i], new OpenCvSharp.Point(8, 24 + i * 24),
                    HersheyFonts.HersheySimplex, 0.65,
                    stepped && i == 2 ? new Scalar(0, 0, 255) : new Scalar(0, 220, 0), 2);

            LastSummary = string.Format("高度统计: Min {0:F1} Max {1:F1} Avg {2:F1} 台阶差 {3:F1}（{4}）",
                mn * scale, mx * scale, mean * scale, diff * scale, stepped ? "有台阶" : "无台阶");
            return dst;
        }
    }
}
