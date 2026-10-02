using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 区域填充算子：在图上（或算子级 ROI 框选区域内）识别封闭区域
    /// （灰度阈值白区 / Canny 边缘闭合区），把每个封闭区域整体填充为指定灰度或彩色，
    /// 区域外保持原图。用途：敏感区打码、区域标色、把识别出的连通域统一染色。
    /// 支持算子级 ROI：勾选"启用ROI"后只填充框内识别出的区域，框外原样。
    /// </summary>
    public class RegionFillTask : IVisionTask, IResultReporter
    {
        public string TaskName => "区域填充";

        /// <summary>最近一次 Execute 的结果摘要（填充区域数量）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "填充类型 0灰度1彩色",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "填充:{0}",
                Group = "填充色",
                Tip = "0=把封闭区域填充为指定灰度值；1=填充为指定的 RGB 彩色"
            },
            new TaskParamDesc
            {
                ParamName = "灰度值",
                Min = 0, Max = 255, DefaultValue = 128,
                DisplayFormat = "灰度:{0}",
                Group = "填充色",
                Tip = "填充类型=0 时使用：把识别出的封闭区域整体填充为这个灰度"
            },
            new TaskParamDesc { ParamName = "红色", Min = 0, Max = 255, DefaultValue = 255, DisplayFormat = "R:{0}", Group = "填充色", Tip = "填充类型=1 时使用：红色分量" },
            new TaskParamDesc { ParamName = "绿色", Min = 0, Max = 255, DefaultValue = 0, DisplayFormat = "G:{0}", Group = "填充色", Tip = "填充类型=1 时使用：绿色分量" },
            new TaskParamDesc { ParamName = "蓝色", Min = 0, Max = 255, DefaultValue = 0, DisplayFormat = "B:{0}", Group = "填充色", Tip = "填充类型=1 时使用：蓝色分量" },
            new TaskParamDesc
            {
                ParamName = "封闭区来源 0阈值1边缘",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "来源:{0}",
                Tip = "0=灰度阈值提取白区（区域阈值=0 时自动 Otsu）；1=Canny 边缘闭合区（自动膨胀闭合）"
            },
            new TaskParamDesc
            {
                ParamName = "区域阈值",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "阈值:{0}",
                Tip = "封闭区来源=0：大于此值的像素构成区域；0=自动 Otsu。识别不出区域时调低它"
            },
            ..RoiRegion.ParamDescs(),   // 算子级 ROI：框选后只填充框内识别出的区域
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty()) return new Mat();
            int fillType = paramValues[0];   // 0灰度 1彩色
            int grayV = paramValues[1];
            int rr = paramValues[2], gg = paramValues[3], bb = paramValues[4];
            int srcMode = paramValues[5];    // 0阈值 1边缘
            int th = paramValues[6];

            // 输出恒为彩色图（灰度输入也转 BGR，通道一致下游好接）
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat fg = new Mat();
            if (srcMode == 1)
            {
                // 边缘来源：Canny 提边 → 膨胀让边缘闭合 → 找外轮廓
                Cv2.Canny(gray, fg, 50, 150);
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
                Cv2.Dilate(fg, fg, kernel);
            }
            else
            {
                // 阈值来源：固定阈值或 Otsu 自动；大于阈值=白区（构成区域）
                if (th > 0) Cv2.Threshold(gray, fg, th, 255, ThresholdTypes.Binary);
                else Cv2.Threshold(gray, fg, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            }

            Cv2.FindContours(fg, out Point[][] contours, out HierarchyIndex[] _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Scalar fillColor = fillType == 1 ? new Scalar(bb, gg, rr) : new Scalar(grayV, grayV, grayV);
            int filled = 0;
            foreach (Point[] c in contours)
            {
                if (c.Length < 3) continue;                    // 点数不足不是封闭区域
                Cv2.FillPoly(dst, new[] { c }, fillColor);     // 整体填充指定色
                filled++;
            }

            LastSummary = filled > 0
                ? $"区域填充: 识别并填充 {filled} 个封闭区域"
                : "区域填充: 没识别到封闭区域（试试调低区域阈值，或改用边缘来源）";
            return dst;
        }
    }
}
