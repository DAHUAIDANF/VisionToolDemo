using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 环形 ROI 展开算子：把圆环区域按极坐标展开成矩形（WarpPolar 线性/对数极坐标）。
    /// 适合圆形工件圆周检测、环形区域缺陷展开、旋转不变分析。
    /// 参数：中心 X/Y 偏移（%）、内/外半径（%）、对数极坐标开关。
    /// </summary>
    public class RingUnrollTask : IVisionTask, IResultReporter
    {
        public string TaskName => "环形展开";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "中心X偏移",
                Min = -50, Max = 50, DefaultValue = 0,
                DisplayFormat = "CX:{0}%",
                Tip = "圆心 X 相对图像中心偏移（百分数，正=右）"
            },
            new TaskParamDesc
            {
                ParamName = "中心Y偏移",
                Min = -50, Max = 50, DefaultValue = 0,
                DisplayFormat = "CY:{0}%",
                Tip = "圆心 Y 相对图像中心偏移（百分数，正=下）"
            },
            new TaskParamDesc
            {
                ParamName = "内半径%",
                Min = 0, Max = 90, DefaultValue = 20,
                DisplayFormat = "内径:{0}%",
                Tip = "展开起始半径（相对图像短边一半的百分数）"
            },
            new TaskParamDesc
            {
                ParamName = "外半径%",
                Min = 10, Max = 100, DefaultValue = 90,
                DisplayFormat = "外径:{0}%",
                Tip = "展开结束半径（相对图像短边一半的百分数）"
            },
            new TaskParamDesc
            {
                ParamName = "对数极坐标",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "对数:{0}",
                Tip = "1=对数极坐标（旋转/缩放不变分析）；0=线性极坐标"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int cxOff = paramValues[0];
            int cyOff = paramValues[1];
            int innerPct = paramValues[2];
            int outerPct = Math.Max(paramValues[3], innerPct + 1);
            bool logPolar = paramValues[4] != 0;

            // 圆心与半径（相对短边一半）
            double half = Math.Min(srcMat.Cols, srcMat.Rows) / 2.0;
            Point2f center = new(
                (float)(srcMat.Cols / 2.0 + cxOff * half / 100.0),
                (float)(srcMat.Rows / 2.0 + cyOff * half / 100.0));
            double rInner = half * innerPct / 100.0;
            double rOuter = half * outerPct / 100.0;
            double maxRadius = half;   // WarpPolar 的最大半径（映射到展开图高度）

            // 展开尺寸：宽 = 2π * 中径，高 = 内外半径差
            double rMid = (rInner + rOuter) / 2.0;
            int dstW = Math.Max(64, (int)(2 * Math.PI * rMid));
            int dstH = Math.Max(32, (int)(rOuter - rInner));

            Mat dst = new();
            try
            {
                var mode = logPolar
                    ? WarpPolarMode.Log
                    : WarpPolarMode.Linear;
                Cv2.WarpPolar(srcMat, dst, new Size(dstW, dstH), center, maxRadius, InterpolationFlags.Linear, mode);
            }
            catch (Exception ex)
            {
                LastSummary = $"环形展开: 失败（{ex.Message}）";
                return srcMat.Clone();
            }

            LastSummary = $"环形展开: {dst.Width}×{dst.Height}（内径{innerPct}% 外径{outerPct}%{(logPolar ? " 对数" : "")}）";
            return dst;
        }
    }
}
