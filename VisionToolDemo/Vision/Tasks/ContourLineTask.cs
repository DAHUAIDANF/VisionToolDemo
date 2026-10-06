using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 描边/轮廓线输出算子：提取边缘或轮廓，在黑底上输出白色线条图。
    /// 用于边缘分析、图形输出、二次处理输入。
    /// 参数：模式（0=Canny 边缘 1=二值轮廓 2=外部轮廓）、阈值、粗细。
    /// </summary>
    public class ContourLineTask : IVisionTask, IResultReporter
    {
        public string TaskName => "描边";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "模式",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=Canny 边缘 1=二值轮廓（含孔） 2=外部轮廓（只最外层）"
            },
            new TaskParamDesc
            {
                ParamName = "阈值",
                Min = 10, Max = 255, DefaultValue = 100,
                DisplayFormat = "阈值:{0}",
                Tip = "边缘/二值化灵敏度（越小线越多越杂）"
            },
            new TaskParamDesc
            {
                ParamName = "线宽",
                Min = 1, Max = 8, DefaultValue = 2,
                DisplayFormat = "线宽:{0}",
                Tip = "描边线条粗细（像素）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int mode = paramValues[0];
            int thresh = Math.Max(10, Math.Min(255, paramValues[1]));
            int thickness = Math.Max(1, Math.Min(8, paramValues[2]));

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat lineImg = new Mat(gray.Rows, gray.Cols, MatType.CV_8U, Scalar.All(0));

                if (mode == 0)
                {
                    // Canny 边缘
                    Cv2.Canny(gray, lineImg, thresh / 2, thresh, 3, true);
                }
                else
                {
                    // 二值化
                    Mat bin = new();
                    Cv2.Threshold(gray, bin, thresh, 255, ThresholdTypes.Binary);
                    Point[][] contours;
                    HierarchyIndex[] hierarchy;
                    Cv2.FindContours(bin, out contours, out hierarchy,
                        mode == 2 ? RetrievalModes.External : RetrievalModes.List,
                        ContourApproximationModes.ApproxNone);
                    foreach (var c in contours)
                        if (c.Length >= 2)
                            Cv2.Polylines(lineImg, new[] { c }, true, Scalar.All(255), thickness, LineTypes.AntiAlias);
                    bin.Dispose();
                }

                string[] names = { "Canny 边缘", "二值轮廓", "外部轮廓" };
                LastSummary = $"描边: {names[mode]}（阈值{thresh} 线宽{thickness}）";
                return lineImg;
            }
        }
    }
}
