using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像边框算子：为图片添加边框（实线框 / 白色相纸边 / 渐变暗角）。
    /// 用于输出排版、拼接预览、报告配图。
    /// 参数：边框宽度（%）、边框颜色、边框样式。
    /// </summary>
    public class ImageFrameTask : IVisionTask, IResultReporter
    {
        public string TaskName => "图像边框";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "边框宽度",
                Min = 1, Max = 30, DefaultValue = 5,
                DisplayFormat = "宽度:{0}%",
                Tip = "边框宽度（图像最小边的百分数）"
            },
            new TaskParamDesc
            {
                ParamName = "边框颜色",
                Min = 0, Max = 3, DefaultValue = 1,
                DisplayFormat = "颜色:{0}",
                Tip = "0=黑 1=白 2=红 3=蓝"
            },
            new TaskParamDesc
            {
                ParamName = "边框样式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "样式:{0}",
                Tip = "0=实线框 1=白相纸边（整体扩边）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int widthPct = Math.Max(1, Math.Min(30, paramValues[0]));
            int colorIdx = paramValues[1];
            int style = paramValues[2];

            Scalar color = colorIdx switch
            {
                0 => new Scalar(0, 0, 0),
                1 => new Scalar(255, 255, 255),
                2 => new Scalar(0, 0, 255),
                _ => new Scalar(255, 0, 0)
            };

            int bw = Math.Max(2, Math.Min(srcMat.Rows, srcMat.Cols) * widthPct / 100);

            if (style == 1)
            {
                // 白相纸边：整体扩边
                using (Mat dst = new())
                {
                    Cv2.CopyMakeBorder(srcMat, dst, bw, bw, bw, bw, BorderTypes.Constant, color);
                    LastSummary = $"图像边框: 白相纸边 {bw}px";
                    return dst.Clone();
                }
            }

            // 实线框
            Mat outMat = srcMat.Channels() == 1
                ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                : srcMat.Clone();
            Cv2.Rectangle(outMat, new Rect(0, 0, outMat.Cols, outMat.Rows), color, bw);
            LastSummary = $"图像边框: 实线框 {bw}px（颜色{colorIdx}）";
            return outMat;
        }
    }
}
