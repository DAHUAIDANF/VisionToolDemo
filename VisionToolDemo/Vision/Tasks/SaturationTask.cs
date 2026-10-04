using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 饱和度调整：BGR → HSV，S 通道乘系数后转回。
    /// 饱和度=100 原样，0=灰度，200=加倍饱和（彩色检测前增强色彩用）。
    /// 灰度图原样返回。
    /// </summary>
    public class SaturationTask : IVisionTask
    {
        public string TaskName => "饱和度";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "饱和度",
                Min = 0,
                Max = 300,
                DefaultValue = 100,
                DisplayFormat = "饱和度:{0}%",
                Tip = "100=原样；0=去饱和变灰；>100 增强；超过 255 自动饱和"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Channels() != 3)
                return srcMat.Clone();
            double sat = paramValues[0] / 100.0;

            using Mat hsv = new();
            Cv2.CvtColor(srcMat, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Split(hsv, out var chs);
            try
            {
                using Mat s = chs[1].Clone();
                // S 通道乘系数（ConvertTo 自动饱和截断）
                s.ConvertTo(s, MatType.CV_8UC1, sat);
                Mat dst = new();
                Cv2.Merge(new[] { chs[0], s, chs[2] }, dst);
                using Mat bgr = new();
                Cv2.CvtColor(dst, bgr, ColorConversionCodes.HSV2BGR);
                dst.Dispose();
                return bgr;
            }
            finally
            {
                foreach (var m in chs) m.Dispose();
            }
        }
    }
}
