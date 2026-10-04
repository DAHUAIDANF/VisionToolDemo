using System;
using OpenCvSharp;
using ZXing;
using ZXing.QrCode;
using VisionToolDemo.Vision.Automation;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 二维码生成：把文本/网址/序列号等内容编码为 QR 码图像（ZXing 编码，零新增依赖）。
    ///
    /// 内容通过文本参数输入（参数面板的文本框，默认 https://example.com），
    /// 支持 {全局变量} 引用（AutomationContext.ExpandVariables 展开）；
    /// 输出为生成的二维码图（BGR，黑码白底），不依赖输入图内容。
    /// </summary>
    public class QrCodeGenTask : IVisionTask, IStringParamTask
    {
        public string TaskName => "二维码生成";

        /// <summary>二维码内容（文本参数注入；自动化节点/算子链共用）</summary>
        public string NodeText { get; set; } = "https://example.com";
        public string NodeKey { get; set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "内容",
                TextDefault = "https://example.com",
                Tip = "二维码内容：网址/文本/序列号等，支持 {变量} 引用（如 {sn}）"
            },
            new TaskParamDesc
            {
                ParamName = "尺寸",
                Min = 100,
                Max = 1200,
                DefaultValue = 300,
                DisplayFormat = "尺寸:{0}px",
                Tip = "生成二维码的边长（含边距），像素"
            },
            new TaskParamDesc
            {
                ParamName = "纠错级别",
                Min = 0,
                Max = 3,
                DefaultValue = 1,
                DisplayFormat = "纠错:{0}",
                Tip = "0=L 1=M 2=Q 3=H；级别越高越耐污损/遮挡，图案越密"
            },
            new TaskParamDesc
            {
                ParamName = "边距",
                Min = 0,
                Max = 20,
                DefaultValue = 4,
                DisplayFormat = "边距:{0}",
                Tip = "二维码四周的空白边距（模块数），越大越易被识别"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 内容：空时用默认值；支持 {变量} 展开
            string content = string.IsNullOrWhiteSpace(NodeText)
                ? "https://example.com"
                : AutomationContext.ExpandVariables(NodeText);
            int size = Math.Max(100, Math.Min(1200, paramValues.Length > 1 ? paramValues[1] : 300));
            int ec = paramValues.Length > 2 ? paramValues[2] : 1;
            int margin = Math.Max(0, Math.Min(20, paramValues.Length > 3 ? paramValues[3] : 4));

            try
            {
                var writer = new BarcodeWriterGeneric
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new QrCodeEncodingOptions
                    {
                        Width = size,
                        Height = size,
                        Margin = margin,
                        ErrorCorrection = ec switch
                        {
                            0 => ErrorCorrectionLevel.L,
                            2 => ErrorCorrectionLevel.Q,
                            3 => ErrorCorrectionLevel.H,
                            _ => ErrorCorrectionLevel.M,
                        },
                        CharacterSet = "UTF-8",   // 中文内容不乱码
                    },
                };
                var matrix = writer.Encode(content);
                if (matrix == null)
                    return new Mat();

                // BitMatrix → Mat 灰度（黑=码元 0，白=背景 255）
                int w = matrix.Width, h = matrix.Height;
                Mat gray = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (matrix[x, y])
                            gray.At<byte>(y, x) = 0;

                // 统一输出 BGR，与其它算子链衔接一致
                Mat dst = new();
                Cv2.CvtColor(gray, dst, ColorConversionCodes.GRAY2BGR);
                gray.Dispose();
                return dst;
            }
            catch
            {
                // 内容过长/字符集不支持等异常：返回空图，由链层给出失败摘要，不炸链
                return new Mat();
            }
        }
    }
}
