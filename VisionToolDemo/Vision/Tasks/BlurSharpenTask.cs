using System;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 模糊/锐化算子（PS「滤镜 → 模糊/锐化」）：高斯模糊、动感模糊、中值模糊、
    /// USM 锐化。参数：类型（0=高斯 1=动感 2=中值 3=USM锐化）、半径（1~40，
    /// 高斯/动感/中值的核半径）、USM 强度（0~300%，100=常规，仅类型 3 用）。
    /// </summary>
    public class BlurSharpenTask : IVisionTask, IResultReporter
    {
        public string LastSummary { get; private set; } = "";

        public string TaskName => "模糊锐化";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc { SupportRoi = true, ParamName = "类型", Min = 0, Max = 3, DefaultValue = 0, DisplayFormat = "类型:{0}", Tip = "0=高斯模糊 1=动感模糊 2=中值模糊 3=USM锐化" },
            new TaskParamDesc { ParamName = "半径", Min = 1, Max = 40, DefaultValue = 3, DisplayFormat = "半径:{0}", ForceOdd = true, Tip = "模糊/锐化的核半径（自动取奇数）" },
            new TaskParamDesc { ParamName = "USM强度", Min = 0, Max = 300, DefaultValue = 100, DisplayFormat = "USM:{0}%", Tip = "仅类型=3：锐化叠加强度，100=常规；过大出现白边" }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int type = paramValues[0];
            int radius = Math.Max(1, paramValues[1]);
            int usm = paramValues[2];
            int k = radius * 2 + 1;

            Mat dst = new();
            switch (type)
            {
                case 0: // 高斯模糊：sigma 随半径
                    Cv2.GaussianBlur(srcMat, dst, new OpenCvSharp.Size(k, k), radius * 0.6);
                    break;
                case 1: // 动感模糊：对角线性核
                    using (Mat kern = MotionKernel(k))
                        Cv2.Filter2D(srcMat, dst, -1, kern);
                    break;
                case 2: // 中值模糊
                    Cv2.MedianBlur(srcMat, dst, k);
                    break;
                default: // 3 USM 锐化：原图 + 强度*(原图-高斯模糊)
                    using (Mat blur = new())
                    {
                        Cv2.GaussianBlur(srcMat, blur, new OpenCvSharp.Size(k, k), radius * 0.6);
                        Mat diff = new();
                        Cv2.Subtract(srcMat, blur, diff);
                        Cv2.AddWeighted(srcMat, 1.0, diff, usm / 100.0, 0, dst);
                    }
                    break;
            }
            string[] names = { "高斯模糊", "动感模糊", "中值模糊", "USM锐化" };
            LastSummary = string.Format("{0}: 半径{1}{2}", names[type], radius, type == 3 ? " USM" + usm + "%" : "");
            return dst;
        }

        /// <summary>动感模糊核：沿 45° 对角方向的长度为 n 的线性核（归一化保持亮度）</summary>
        private static Mat MotionKernel(int n)
        {
            var kern = new float[n * n];
            for (int i = 0; i < n; i++)
                kern[i * n + i] = 1f;              // 对角 1 带
            float sum = kern.Sum();
            if (sum > 0)
                for (int i = 0; i < kern.Length; i++)
                    kern[i] /= sum;
            return Mat.FromPixelData(n, n, MatType.CV_32F, kern);
        }
    }
}
