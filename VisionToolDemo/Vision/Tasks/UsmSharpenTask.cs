using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// USM 锐化（Unsharp Mask）：原图 + 强度 × (原图 − 高斯模糊)。
    /// 比「模糊锐化」里的 USM 多了**阈值**参数：只对超过阈值的边缘差增强，
    /// 阈值越大越不会放大噪点（工业图低阈值易出噪）。
    /// </summary>
    public class UsmSharpenTask : IVisionTask
    {
        public string TaskName => "USM锐化";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "半径",
                Min = 1,
                Max = 20,
                DefaultValue = 3,
                DisplayFormat = "半径:{0}",
                ForceOdd = true,
                Tip = "高斯模糊核半径（自动取奇数），大半径影响粗边缘"
            },
            new TaskParamDesc
            {
                ParamName = "强度",
                Min = 0,
                Max = 200,
                DefaultValue = 100,
                DisplayFormat = "强度:{0}%",
                Tip = "边缘差叠加比例，100=常规；过大出现白边"
            },
            new TaskParamDesc
            {
                ParamName = "阈值",
                Min = 0,
                Max = 100,
                DefaultValue = 0,
                DisplayFormat = "阈值:{0}",
                Tip = "只增强差值大于阈值的边缘（0=全部增强）；越大越抑制噪点"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int radius = Math.Max(1, paramValues[0]);
            double amount = paramValues[1] / 100.0;
            double threshold = paramValues[2];

            using Mat blur = new();
            Cv2.GaussianBlur(srcMat, blur, new OpenCvSharp.Size(radius * 2 + 1, radius * 2 + 1), 0);

            // 差 = 原图 − 模糊
            using Mat diff = new();
            Cv2.Subtract(srcMat, blur, diff);

            // 阈值：差值小于阈值的置 0（不增强）
            if (threshold > 0)
                Cv2.Threshold(diff, diff, threshold, 0, ThresholdTypes.Tozero);

            // 原图 + 强度 × 差
            Mat dst = new();
            Cv2.AddWeighted(srcMat, 1.0, diff, amount, 0.0, dst);
            return dst;
        }
    }
}
