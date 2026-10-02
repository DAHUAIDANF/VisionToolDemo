using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>图像旋转算子：绕图像中心旋转指定角度（度数），输出尺寸与原图相同。
/// 参数：旋转角度（-180~180°）。角度接近 0 时直接返回原图副本。</summary>
public class RotateTask : IVisionTask
    {
        public string TaskName => "图像旋转";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "旋转角度",
                Min = -180,
                Max = 180,
                DefaultValue = 0,
                DisplayFormat = "角度:{0}°",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            double angle = paramValues[0];
            // 角度几乎为 0：没有旋转意义，直接返回副本省掉一次重采样
            if (Math.Abs(angle) < 0.5)
                return srcMat.Clone();

            Mat dst = new();
            // 旋转矩阵：绕图像中心、角度 angle、缩放 1.0
            using (Mat rotMat = Cv2.GetRotationMatrix2D(
                new Point2f(srcMat.Cols / 2f, srcMat.Rows / 2f), angle, 1.0))
            {
                // 仿射变换应用到整图，输出尺寸与原图一致（角落会被裁掉）
                Cv2.WarpAffine(srcMat, dst, rotMat, srcMat.Size());
            }
            return dst;
        }
    }
}
