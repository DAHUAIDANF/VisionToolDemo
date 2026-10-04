using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度图伪彩：16U 深度图 → Jet 伪彩可视化（蓝=近、红=远），
    /// 适合人眼观察高度变化。深度超过上限截断为红色。
    /// </summary>
    public class DepthColorTask : IVisionTask
    {
        public string TaskName => "深度图伪彩";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "上限",
                Min = 100,
                Max = 65535,
                DefaultValue = 5000,
                DisplayFormat = "上限:{0}",
                Tip = "深度超过此值的区域截断为红色；调小可放大近处细节"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_16UC1)
                return srcMat.Clone();
            int cap = Math.Max(1, paramValues[0]);

            using Mat gray = new();
            srcMat.ConvertTo(gray, MatType.CV_8U, 255.0 / cap);
            Mat dst = new();
            Cv2.ApplyColorMap(gray, dst, ColormapTypes.Jet);
            return dst;
        }
    }
}
