using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 光照校正（背景均衡/平场校正）：用大核高斯模糊估计不均匀光照背景，
    /// 原图除以背景放大 255 倍，消除亮度渐变、暗角，仅保留纹理与缺陷，
    /// 显著改善后续阈值/检测的稳定性。
    /// 参数：背景核（越大背景越平滑）、增益(%)。
    /// </summary>
    public class IlluminationTask : IVisionTask, IResultReporter
    {
        public string TaskName => "光照校正";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "背景核",
                Min = 3,
                Max = 301,
                DefaultValue = 51,
                DisplayFormat = "核:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "增益%",
                Min = 50,
                Max = 300,
                DefaultValue = 100,
                DisplayFormat = "增益:{0}%"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int ksize = Math.Max(3, paramValues[0] | 1);
            double gain = Math.Max(0.1, paramValues[1] / 100.0);

            Mat dst = new(srcMat.Size(), MatType.CV_8UC3);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bg = new())
            {
                Cv2.GaussianBlur(gray, bg, new OpenCvSharp.Size(ksize, ksize), 0);
                using (Mat f32 = new())
                using (Mat bg32 = new())
                {
                    gray.ConvertTo(f32, MatType.CV_32F);
                    bg.ConvertTo(bg32, MatType.CV_32F);
                    Cv2.Divide(f32, bg32, f32, 255.0 * gain);
                    f32.ConvertTo(f32, MatType.CV_8U);
                    Cv2.CvtColor(f32, dst, ColorConversionCodes.GRAY2BGR);

                    double min, max;
                    Cv2.MinMaxLoc(f32, out min, out max, out Point pmin, out Point pmax);
                    LastSummary = string.Format("光照校正: 背景核 {0}, 校正后范围 {1}~{2}",
                        paramValues[0], (int)min, (int)max);
                }
            }
            return dst;
        }
    }
}
