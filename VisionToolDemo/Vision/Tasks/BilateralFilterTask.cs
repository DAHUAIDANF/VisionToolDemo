using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>双边滤波算子：去噪的同时保留边缘（同时考虑空间距离和像素值差）。
    /// 参数：d 直径、sigmaColor（颜色差权重大小）、sigmaSpace（空间距离权重）。</summary>
    public class BilateralFilterTask : IVisionTask
    {
        public string TaskName => "双边滤波";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "d直径",
                Min = 1,
                Max = 15,
                DefaultValue = 5,
                DisplayFormat = "d:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "sigmaColor",
                Min = 1,
                Max = 150,
                DefaultValue = 75,
                DisplayFormat = "sigmaC:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "sigmaSpace",
                Min = 1,
                Max = 150,
                DefaultValue = 75,
                DisplayFormat = "sigmaS:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int d = paramValues[0];
            int sigmaC = paramValues[1];
            int sigmaS = paramValues[2];

            // 双边滤波只支持 8U / 32F。16 位 RAW 直接进来会抛异常，
            // 先统一归一化到 8U（见 VisionHelper.ToU8 的位深说明）。
            using Mat src8 = VisionHelper.ToU8(srcMat);
            Mat dst = new();
            Cv2.BilateralFilter(src8, dst, d, sigmaC, sigmaS);
            return dst;
        }
    }
}