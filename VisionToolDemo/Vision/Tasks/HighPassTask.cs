using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 高反差保留（PS 滤镜）：原图 − 高斯模糊 + 128。
    /// 结果保留高频细节（边缘、纹理），大块均匀区域呈中灰。
    /// 常与「混合模式」类算子配合做细节叠加，或单独查看纹理/划痕。
    /// </summary>
    public class HighPassTask : IVisionTask
    {
        public string TaskName => "高反差保留";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "半径",
                Min = 1,
                Max = 100,
                DefaultValue = 10,
                DisplayFormat = "半径:{0}",
                ForceOdd = true,
                Tip = "模糊半径：越小保留越细的纹理，越大保留越粗的边缘"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int radius = Math.Max(1, paramValues[0]);

            using Mat blur = new();
            Cv2.GaussianBlur(srcMat, blur, new OpenCvSharp.Size(radius * 2 + 1, radius * 2 + 1), 0);
            using Mat diff = new();
            Cv2.Subtract(srcMat, blur, diff);
            Mat dst = new();
            // +128：高频差居中到中灰（8U 饱和截断）
            Cv2.Add(diff, new Scalar(128, 128, 128), dst);
            return dst;
        }
    }
}
