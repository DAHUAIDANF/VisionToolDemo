using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度图读取：把单通道 16U 深度图（如结构光/ToF 相机输出）归一化为 8U 灰度可视化图，
    /// 并统计高度信息（最小/最大/平均，可换算为毫米等真实单位）。
    /// 输入非 16U 深度图时提示并原样返回。
    /// </summary>
    public class DepthReadTask : IVisionTask, IResultReporter
    {
        public string TaskName => "深度图读取";

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
                Tip = "深度值 × 比例 = 真实单位（如 100=1.0 时深度值即毫米）；只影响统计数值，不影响显示"
            },
            new TaskParamDesc
            {
                ParamName = "归一化",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "归一化:{0}",
                Tip = "0=自动 MinMax 拉伸（深色=低、亮色=高）；1=按固定上限截断（上限=下一参数）"
            },
            new TaskParamDesc
            {
                ParamName = "上限",
                Min = 100,
                Max = 65535,
                DefaultValue = 5000,
                DisplayFormat = "上限:{0}",
                Tip = "仅「归一化=1」时用：深度超过此值截断为白色，适合深度范围已知的场景"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_16UC1)
            {
                LastSummary = "深度图读取: 跳过 —— 输入不是 16U 深度图（当前 " + srcMat.Type().ToString() + "）";
                return srcMat.Clone();
            }
            double scale = paramValues[0] / 100.0;
            int norm = paramValues[1];
            int cap = Math.Max(1, paramValues[2]);

            // 统计原始深度（在 ROI 区域？逐像素统计全图即可，简单可靠）
            Cv2.MinMaxLoc(srcMat, out double mn, out double mx, out _, out _);
            using Mat gray = new();
            if (norm == 0)
            {
                double span = mx - mn;
                if (span < 1) span = 1;
                srcMat.ConvertTo(gray, MatType.CV_8U, 255.0 / span, -mn * 255.0 / span);
            }
            else
            {
                srcMat.ConvertTo(gray, MatType.CV_8U, 255.0 / cap);
            }

            double mean = srcMat.Mean().Val0;
            LastSummary = string.Format("深度图: 最小 {0:F1} 最大 {1:F1} 平均 {2:F1}（×{3:F2}={4:F1}~{5:F1}{6}）",
                mn, mx, mean, scale, mn * scale, mx * scale, scale != 1 ? "单位" : "像素");
            return gray;
        }
    }
}
