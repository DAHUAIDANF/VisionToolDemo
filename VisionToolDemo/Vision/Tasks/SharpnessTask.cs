using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 清晰度评价（对焦检测）：对灰度图同时计算
    /// Tenengrad 梯度能量（Sobel 平方和超过阈值的均值，越大越清晰）与
    /// Laplacian 方差（越大越清晰），两个指标输出到摘要并在结果图左上角标注。
    /// 参数：Tenengrad 梯度阈值。
    /// </summary>
    public class SharpnessTask : IVisionTask, IResultReporter
    {
        public string TaskName => "清晰度评价";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "梯度阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 15,
                DisplayFormat = "Tenengrad阈值:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int gradThresh = paramValues[0];

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat gx = new())
            using (Mat gy = new())
            using (Mat lap = new())
            {
                Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
                Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);

                int w = gray.Cols, h = gray.Rows;
                long tenengradSum = 0;
                int count = 0;

                unsafe
                {
                    float* px = (float*)gx.Data;
                    float* py = (float*)gy.Data;
                    long t2 = (long)gradThresh * gradThresh;
                    for (int i = 0; i < w * h; i++)
                    {
                        double e = ((double)px[i] * px[i]) + ((double)py[i] * py[i]);
                        if (e > t2)
                        {
                            tenengradSum += (long)e;
                            count++;
                        }
                    }
                }
                double tenengrad = count > 0 ? (double)tenengradSum / count : 0;

                using (Mat lap8 = new())
                {
                    Cv2.Laplacian(gray, lap8, MatType.CV_64F);
                    Cv2.MeanStdDev(lap8, out Scalar mean, out Scalar std);
                    double lapVar = std.Val0 * std.Val0;

                    LastSummary = string.Format("清晰度: Tenengrad={0:F0} LaplacianVar={1:F0} (数值越大越清晰)",
                        tenengrad, lapVar);
                    Cv2.PutText(dst, "T=" + tenengrad.ToString("F0"), new Point(10, 24),
                        HersheyFonts.HersheySimplex, 0.7, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                    return dst;
                }
            }
        }
    }
}
