using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>高斯模糊算子：按高斯核加权平均平滑图像，去噪同时比均值模糊更保留细节。
/// 参数：核大小（奇数，越大越模糊）。</summary>
public class GaussianBlurTask : IVisionTask
    {
        public string TaskName => "高斯模糊";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "Kernel",
                Min = 1,
                Max = 21,
                DefaultValue = 5,
                DisplayFormat = "核大小：{0}x{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int kernelSize = paramValues[0];
            // 偶数核自动 +1 修正为奇数（OpenCV 高斯核要求奇数）
            if (kernelSize % 2 == 0) kernelSize++;
            Mat dst = new();
            // sigmaX=0 表示由核大小自动推算标准差
            Cv2.GaussianBlur(srcMat, dst, new Size(kernelSize, kernelSize), 0);
            return dst;
        }
    }
}