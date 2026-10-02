using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 倾斜校正：Otsu 二值化后取目标最小外接旋转矩形的角度，
    /// 将图像反向旋转回水平（同尺寸画布、边缘复制填充），输出校正后的整图。
    /// 用于读码/OCR/测量前的水平基准校正。
    /// 参数：前景极性（0=亮 1=暗）、倾角上限(°)（超过则不校正）。
    /// </summary>
    public class DeskewTask : IVisionTask, IResultReporter
    {
        public string TaskName => "倾斜校正";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "极性 0亮/1暗",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "前景:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "倾角上限°",
                Min = 1,
                Max = 45,
                DefaultValue = 30,
                DisplayFormat = "上限:{0}°"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int polarity = paramValues[0] % 2;
            int maxAngle = Math.Max(1, paramValues[1]);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bin = new())
            {
                ThresholdTypes type = polarity == 0
                    ? ThresholdTypes.Binary | ThresholdTypes.Otsu
                    : ThresholdTypes.BinaryInv | ThresholdTypes.Otsu;
                Cv2.Threshold(gray, bin, 0, 255, type);

                using (Mat nz = new())
                {
                    Cv2.FindNonZero(bin, nz);
                    if (nz.Rows == 0)
                    {
                        LastSummary = "倾斜校正: 未找到目标";
                        return VisionHelper.ToBgrCopy(srcMat);
                    }

                    // 最多取 20000 个点估算最小外接矩形
                    int total = nz.Rows;
                    int step = Math.Max(1, total / 20000);
                    List<Point2f> pts = new((total / step) + 1);
                    for (int i = 0; i < total; i += step)
                    {
                        Vec2i p = nz.Get<Vec2i>(i, 0);
                        pts.Add(new Point2f(p.Item0, p.Item1));
                    }

                    RotatedRect rr = Cv2.MinAreaRect(pts);
                    double angle = rr.Angle;           // OpenCV 4.5+: [0, 90)
                    if (angle > 45) angle -= 90;       // 归一化到 (-45, 45]
                    else if (angle <= -45) angle += 90;

                    if (Math.Abs(angle) < 0.05)
                    {
                        LastSummary = "倾斜校正: 已水平 (0.0°)";
                        return VisionHelper.ToBgrCopy(srcMat);
                    }
                    if (Math.Abs(angle) > maxAngle)
                    {
                        LastSummary = string.Format("倾斜校正: 倾角 {0:F1}° 超过上限 {1}°, 未校正", angle, maxAngle);
                        return VisionHelper.ToBgrCopy(srcMat);
                    }

                    // 同尺寸旋转，边缘复制填充；旋转路径直接输出结果，不再拷贝整图 BGR
                    using (Mat m = Cv2.GetRotationMatrix2D(
                        new Point2f(srcMat.Cols / 2f, srcMat.Rows / 2f), angle, 1.0))
                    {
                        Mat rot = new();
                        Cv2.WarpAffine(srcMat, rot, m, srcMat.Size(),
                            InterpolationFlags.Linear, BorderTypes.Replicate);
                        LastSummary = string.Format("倾斜校正: 目标倾角 {0:F1}°, 已旋转回水平", angle);
                        return rot;
                    }
                }
            }
        }
    }
}
