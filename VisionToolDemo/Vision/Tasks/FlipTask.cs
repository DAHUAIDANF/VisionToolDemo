using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>图像翻转算子：把图像按 水平/垂直/双向 镜像翻转。
/// 参数：翻转方式（0=水平 1=垂直 2=双向）。</summary>
public class FlipTask : IVisionTask
    {
        public string TaskName => "图像翻转";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "翻转方式",
                Min = 0,
                Max = 2,
                DefaultValue = 1,
                DisplayFormat = "{0} (0水平 1垂直 2双向)",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护：直接返回空 Mat，避免底层 OpenCV 对空输入抛异常
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = new();
            // FlipMode: 0=水平翻转 1=垂直翻转 2=双向（先水平再垂直）
            Cv2.Flip(srcMat, dst, (FlipMode)paramValues[0]);
            return dst;
        }
    }
}
