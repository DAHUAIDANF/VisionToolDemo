using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 污点修复算子（PS「污点修复画笔」）：根据掩膜指定要修复的区域，用周围像素
    /// 推断填充（Telea / NS 两种算法），适合去瑕疵、去水印、去划痕。
    /// 掩膜复用"模板/参考图"通道：参数面板「导入模板图…」载入掩膜图，
    /// 约定白色像素（>127）= 要修复的区域，黑色 = 保留；掩膜尺寸会自动缩放到与
    /// 图像一致。未导入掩膜时返回原图并给出提示。
    /// 参数：修复半径（越大扩散越远）、算法（0=Telea 1=Navier-Stokes）。
    /// </summary>
    public class InpaintTask : IVisionTask, IResultReporter
    {
        /// <summary>UI 注入的掩膜图（复用模板行）</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        public string TaskName => "污点修复";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc { ParamName = "修复半径", Min = 1, Max = 20, DefaultValue = 3, DisplayFormat = "半径:{0}", Tip = "修复扩散半径：越大填充范围越广" },
            new TaskParamDesc { ParamName = "算法", Min = 0, Max = 1, DefaultValue = 0, DisplayFormat = "算法:{0}", Tip = "0=Telea（速度较快） 1=Navier-Stokes（对细线/结构更稳）" }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "污点修复: 请先导入掩膜图（参数面板「导入模板图…」，白色=要修的区域）";
                return srcMat.Clone();
            }

            // 掩膜缩放到与原图同尺寸（保持二值语义），再按白(>127)转二值单通道
            Mat mask = new();
            if (TemplateMat.Cols != srcMat.Cols || TemplateMat.Rows != srcMat.Rows)
                Cv2.Resize(TemplateMat, mask, new OpenCvSharp.Size(srcMat.Cols, srcMat.Rows), 0, 0, InterpolationFlags.Nearest);
            else
                mask = TemplateMat.Clone();

            using Mat gray = new();
            Cv2.CvtColor(mask, gray, ColorConversionCodes.BGR2GRAY);
            using Mat maskBin = new();
            Cv2.Threshold(gray, maskBin, 127, 255, ThresholdTypes.Binary);

            Mat dst = new();
            Cv2.Inpaint(srcMat, maskBin, dst, paramValues[0], paramValues[1] == 1 ? InpaintTypes.NS : InpaintTypes.Telea);
            LastSummary = string.Format("污点修复: 半径{0} {1}", paramValues[0], paramValues[1] == 1 ? "NS" : "Telea");
            return dst;
        }
    }
}
