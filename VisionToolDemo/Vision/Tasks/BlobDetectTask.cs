using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 斑点检测算子（SimpleBlobDetector）：检测高斯拉普拉斯意义上的圆斑（亮点/暗点），
    /// 输出原图叠加斑点圆圈。用于圆点阵列、细胞、焊点定位。
    /// 参数：最小半径、最大半径、灰度极性、检测阈值。
    /// </summary>
    public class BlobDetectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "斑点检测";

        public string LastSummary { get; private set; } = "";

        public int BlobCount { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "最小半径",
                Min = 1, Max = 50, DefaultValue = 2,
                DisplayFormat = "Rmin:{0}",
                Tip = "斑点最小半径（像素）"
            },
            new TaskParamDesc
            {
                ParamName = "最大半径",
                Min = 5, Max = 200, DefaultValue = 30,
                DisplayFormat = "Rmax:{0}",
                Tip = "斑点最大半径（像素）"
            },
            new TaskParamDesc
            {
                ParamName = "灰度极性",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "极性:{0}",
                Tip = "0=亮斑 1=暗斑 2=都检"
            },
            new TaskParamDesc
            {
                ParamName = "检测阈值",
                Min = 1, Max = 250, DefaultValue = 10,
                DisplayFormat = "阈值:{0}",
                Tip = "斑点对比度阈值（越小越灵敏）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            BlobCount = 0;
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int rMin = Math.Max(1, paramValues[0]);
            int rMax = Math.Max(paramValues[1], rMin + 1);
            int polarity = paramValues[2];
            int thresh = Math.Max(1, Math.Min(250, paramValues[3]));

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 构建 SimpleBlobDetector 参数
                SimpleBlobDetector.Params p = new()
                {
                    MinThreshold = Math.Max(1, 128 - thresh),
                    MaxThreshold = Math.Min(255, 128 + thresh),
                    ThresholdStep = 5,
                    FilterByArea = true,
                    MinArea = (float)(Math.PI * rMin * rMin),
                    MaxArea = (float)(Math.PI * rMax * rMax),
                    FilterByCircularity = true,
                    MinCircularity = 0.6f,
                    FilterByConvexity = false,
                    FilterByInertia = false,
                };
                if (polarity == 0) { p.FilterByColor = true; p.BlobColor = 255; }
                else if (polarity == 1) { p.FilterByColor = true; p.BlobColor = 0; }

                using (var detector = SimpleBlobDetector.Create(p))
                {
                    KeyPoint[] keypoints = detector.Detect(gray);
                    BlobCount = keypoints.Length;

                    Mat dst = srcMat.Channels() == 1
                        ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                        : srcMat.Clone();
                    foreach (var kp in keypoints)
                        Cv2.Circle(dst, (int)kp.Pt.X, (int)kp.Pt.Y, (int)kp.Size / 2,
                            new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);

                    LastSummary = $"斑点检测: 检出 {BlobCount} 个斑点（R{rMin}~{rMax}）";
                    return dst;
                }
            }
        }
    }
}
