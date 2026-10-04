using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>反相：灰度/彩色图取反（255 − 像素值）。黑白胶图、负片、掩膜取反场景。</summary>
    public class InvertTask : IVisionTask
    {
        public string TaskName => "反相";

        public TaskParamDesc[] ParamDescriptions => new TaskParamDesc[0];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = new();
            Cv2.BitwiseNot(srcMat, dst);
            return dst;
        }
    }
}
