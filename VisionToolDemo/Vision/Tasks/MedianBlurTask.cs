using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>中值滤波算子：用核内像素中值替换中心像素，擅长去除椒盐噪声。
/// 参数：核大小（必须为奇数，越大越平滑）。</summary>
public class MedianBlurTask : IVisionTask
    {
        public string TaskName => "中值滤波";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "核大小",
                Min = 1,
                Max = 31,
                DefaultValue = 5,
                DisplayFormat = "核:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = new();
            int ksize = paramValues[0];
            // 核大小必须是奇数（界面参数已用 ForceOdd 约束）
            Cv2.MedianBlur(srcMat, dst, ksize);
            return dst;
        }
    }
}