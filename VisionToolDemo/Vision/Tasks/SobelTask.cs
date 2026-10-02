using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>Sobel 梯度算子：一阶导数算子，提取水平/垂直方向的边缘强度。
/// 参数：dx、dy 阶数（1~2，dx=0且dy=0 时 OpenCV 会抛断言，界面已约束 Min=1）、核大小（奇数）。</summary>
public class SobelTask : IVisionTask
    {
        public string TaskName => "Sobel梯度";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "dx",
                Min = 1,   // dx=0 且 dy=0 时 OpenCV 抛 "dx+dy>0" 断言，Min 从 1 起
                Max = 2,
                DefaultValue = 1,
                DisplayFormat = "dx:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "dy",
                Min = 1,
                Max = 2,
                DefaultValue = 1,
                DisplayFormat = "dy:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "核大小",
                Min = 1,
                Max = 7,
                DefaultValue = 3,
                DisplayFormat = "ksize:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            // 先转灰度；Sobel 输出 16 位有符号（可能有负梯度）
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat sobel16 = new())
            {
                // dx/dy 组合决定提取哪个方向的边缘（如 dx=1,dy=0 提取垂直边缘）
                Cv2.Sobel(gray, sobel16, MatType.CV_16S, paramValues[0], paramValues[1], paramValues[2]);
                Mat dst = new();
                // 绝对值 + 缩放回 8 位
                Cv2.ConvertScaleAbs(sobel16, dst);
                return dst;
            }
        }
    }
}
