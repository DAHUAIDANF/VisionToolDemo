using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度图滤波：对 16U 深度图去噪/平滑（保持 16U 输出，可继续接其它深度算子）。
    /// 中值/双边能保边缘（台阶不被磨平），高斯平滑更彻底。
    /// </summary>
    public class DepthFilterTask : IVisionTask
    {
        public string TaskName => "深度图滤波";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "类型",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=中值 1=双边 2=高斯；台阶场景用中值/双边保边缘"
            },
            new TaskParamDesc
            {
                ParamName = "半径",
                Min = 1,
                Max = 15,
                DefaultValue = 3,
                DisplayFormat = "半径:{0}",
                ForceOdd = true,
                Tip = "滤波核半径（自动取奇数）；越大越平滑，边缘越模糊"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_16UC1)
                return srcMat.Clone();
            int type = paramValues[0];
            int r = Math.Max(1, paramValues[1]);
            int k = r * 2 + 1;

            Mat dst = new();
            switch (type)
            {
                case 0:   // 中值：保边缘去椒盐噪声
                    Cv2.MedianBlur(srcMat, dst, k);
                    break;
                case 1:   // 双边：保边缘平滑
                    Cv2.BilateralFilter(srcMat, dst, k, r * 20, r * 20);
                    break;
                default:  // 高斯：整体平滑
                    Cv2.GaussianBlur(srcMat, dst, new OpenCvSharp.Size(k, k), 0);
                    break;
            }
            return dst;
        }
    }
}
