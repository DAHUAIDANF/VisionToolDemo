using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>锐化算子：原图减去模糊图（unsharp mask），增强边缘对比。
    /// 参数：模糊核（奇数）、锐化强度×10。</summary>
    public class SharpenTask : IVisionTask
    {
        public string TaskName => "锐化";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "模糊核",
                Min = 1,
                Max = 21,
                DefaultValue = 5,
                DisplayFormat = "核:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "锐化强度x10",
                Min = 5,
                Max = 30,
                DefaultValue = 15,
                DisplayFormat = "强度:{0:F1}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int ksize = paramValues[0];
            float amount = paramValues[1] / 10.0f;   // 锐化强度系数

            Mat dst = new();
            using (Mat blurred = new())
            {
                // 先高斯模糊得到"钝化"版本
                Cv2.GaussianBlur(srcMat, blurred, new OpenCvSharp.Size(ksize, ksize), 0);
                // dst = src * (1+amount) - blurred*amount：细节（原图-模糊图）被放大
                Cv2.AddWeighted(srcMat, 1 + amount, blurred, -amount, 0, dst);
            }
            return dst;
        }
    }
}
