using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    public class GrayscaleTask : IVisionTask
    {
        public string TaskName => "灰度转换";
        public TaskParamDesc[] ParamDescriptions => System.Array.Empty<TaskParamDesc>();

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            return VisionHelper.ToGray(srcMat);
        }
    }
}