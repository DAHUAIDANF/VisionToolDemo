using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// CLAHE 自适应直方图均衡：分块限制对比度后均衡，比全局均衡更不易过曝，
    /// 用于低对比度图像增强（弱纹理、暗场工件）。
    /// 参数：对比度限制、网格大小。
    /// </summary>
    public class ClaheTask : IVisionTask, IResultReporter
    {
        public string TaskName => "CLAHE均衡";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "对比度限制",
                Min = 1,
                Max = 40,
                DefaultValue = 4,
                DisplayFormat = "clip:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "网格大小",
                Min = 2,
                Max = 32,
                DefaultValue = 8,
                DisplayFormat = "网格:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            // 参数防御：clip 至少 0.5，网格至少 2（OpenCV 约束）
            double clip = Math.Max(0.5, paramValues[0]);
            int grid = Math.Max(2, paramValues[1]);

            // 灰度 CLAHE 后转 BGR（本算子输出三通道，便于后续链直接接彩色算子）
            Mat dst = new(srcMat.Size(), MatType.CV_8UC3);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat eq = new())
            using (CLAHE clahe = CLAHE.Create(clip, new OpenCvSharp.Size(grid, grid)))
            {
                clahe.Apply(gray, eq);
                Cv2.CvtColor(eq, dst, ColorConversionCodes.GRAY2BGR);
            }

            LastSummary = "CLAHE: clip=" + paramValues[0] + " 网格=" + grid + "x" + grid;
            return dst;
        }
    }
}
