using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    public class EqualizeHistTask : IVisionTask
    {
        public string TaskName => "直方图均衡化";
        public TaskParamDesc[] ParamDescriptions => System.Array.Empty<TaskParamDesc>();

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat dst = new();
                Cv2.EqualizeHist(gray, dst);
                return dst;
            }
        }
    }
}
