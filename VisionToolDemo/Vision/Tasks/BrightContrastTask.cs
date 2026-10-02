using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>亮度对比度算子：线性调整图像 g(x)=α·x+β。
/// 参数：对比度 α（0~300%，100=原样）、亮度 β（-100~100）。</summary>
public class BrightContrastTask : IVisionTask
    {
        public string TaskName => "亮度对比度";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "对比度",
                Min = 0,
                Max = 300,
                DefaultValue = 100,
                DisplayFormat = "对比度:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "亮度",
                Min = -100,
                Max = 100,
                DefaultValue = 0,
                DisplayFormat = "亮度:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = new();
            // α=对比度/100（100→1.0 原样）；β=亮度偏移（正值变亮）
            double alpha = paramValues[0] / 100.0;
            int beta = paramValues[1];
            // ConvertTo 做逐像素线性变换并自动饱和截断到 8 位
            srcMat.ConvertTo(dst, MatType.CV_8UC3, alpha, beta);
            return dst;
        }
    }
}