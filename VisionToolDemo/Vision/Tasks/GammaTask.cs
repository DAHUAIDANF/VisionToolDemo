using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>Gamma 校正算子：非线性亮度调整，用查找表一次完成。
    /// 参数：Gamma×10（1~40，10=原样；<10 整体变亮，>10 整体变暗）。</summary>
    public class GammaCorrectionTask : IVisionTask
    {
        public string TaskName => "Gamma校正";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "Gamma x10",
                Min = 1,
                Max = 40,
                DefaultValue = 10,
                DisplayFormat = "gamma:{0:F1}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            double gamma = paramValues[0] / 10.0;
            // gamma≈1 时无变换，直接返回副本
            if (Math.Abs(gamma - 1.0) < 0.01)
                return srcMat.Clone();

            // 预计算 256 项查找表，直接传 byte[] 调用 LUT
            byte[] lutData = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                // out = 255 * (i/255)^(1/gamma)
                double v = 255.0 * Math.Pow(i / 255.0, 1.0 / gamma);
                lutData[i] = (byte)Math.Min(255, Math.Max(0, (int)v));
            }

            Mat dst = new();
            Cv2.LUT(srcMat, lutData, dst);
            return dst;
        }
    }
}
