using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>对比度增强算子：CLAHE 分块均衡。彩色图在 LAB 空间只增强 L 通道保留颜色；
    /// 灰度图直接均衡。参数：对比度限制×10、网格大小。</summary>
    public class ContrastEnhanceTask : IVisionTask
    {
        public string TaskName => "对比度增强";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "ClipLimit x10",
                Min = 1,
                Max = 100,
                DefaultValue = 20,
                DisplayFormat = "clip:{0:F1}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "网格大小",
                Min = 2,
                Max = 16,
                DefaultValue = 8,
                DisplayFormat = "tiles:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            double clipLimit = paramValues[0] / 10.0;
            int tileGridSize = paramValues[1];

            // CLAHE 与 BGR2Lab 都只接受 8U。16 位 RAW 直接进来会抛
            // "Unsupported depth of input image"，先统一到 8U。
            using Mat src8 = VisionHelper.ToU8(srcMat);

            // 彩色图转到 LAB 空间只增强 L 通道以保留颜色；灰度图直接增强
            if (src8.Channels() == 3)
            {
                using (Mat lab = new())
                using (Mat enhancedLab = new())
                {
                    Cv2.CvtColor(src8, lab, ColorConversionCodes.BGR2Lab);
                    Cv2.Split(lab, out Mat[] channels);

                    using (CLAHE clahe = Cv2.CreateCLAHE(clipLimit, new OpenCvSharp.Size(tileGridSize, tileGridSize)))
                    {
                        clahe.Apply(channels[0], channels[0]);
                    }

                    Cv2.Merge(channels, enhancedLab);
                    Mat dst = new();
                    Cv2.CvtColor(enhancedLab, dst, ColorConversionCodes.Lab2BGR);

                    foreach (Mat ch in channels)
                        ch.Dispose();

                    return dst;
                }
            }
            else
            {
                Mat dst = new();
                using (CLAHE clahe = Cv2.CreateCLAHE(clipLimit, new OpenCvSharp.Size(tileGridSize, tileGridSize)))
                {
                    clahe.Apply(src8, dst);
                }
                return dst;
            }
        }
    }
}
