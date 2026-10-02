using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>Laplacian 边缘算子：二阶导数算子，检测灰度剧烈变化处（边缘），对噪声较敏感。
/// 参数：核大小（奇数）。输出为转成 8 位后的边缘强度图。</summary>
public class LaplacianTask : IVisionTask
    {
        public string TaskName => "Laplacian边缘";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "核大小",
                Min = 1,
                Max = 7,
                DefaultValue = 3,
                DisplayFormat = "ksize:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            // Laplacian 输出为 16 位有符号（可能有负值），先转灰度再算
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat lap16 = new())
            {
                Cv2.Laplacian(gray, lap16, MatType.CV_16S, paramValues[0]);
                Mat dst = new();
                // 取绝对值并缩放到 8 位，负边缘才不会被丢掉
                Cv2.ConvertScaleAbs(lap16, dst);
                return dst;
            }
        }
    }
}
