using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 颜色替换算子：在 HSV 空间按目标色相 ± 容差圈选颜色区间，
    /// 替换为指定色相的颜色（S/V 可保留或重设）。
    /// 用于产品换色、区域填色、背景去色。
    /// 参数：目标色相 H（0~180）、容差、输出色相 H、输出饱和度 S、输出明度 V（S/V=0 表示保留原值）。
    /// </summary>
    public class ColorReplaceTask : IVisionTask, IResultReporter
    {
        public string TaskName => "颜色替换";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "目标色相",
                Min = 0, Max = 180, DefaultValue = 30,
                DisplayFormat = "目标H:{0}",
                Tip = "要替换的颜色色相（HSV，OpenCV 范围 0~180；30≈橙红 60≈黄 90≈绿 120≈青 150≈蓝）"
            },
            new TaskParamDesc
            {
                ParamName = "容差",
                Min = 1, Max = 40, DefaultValue = 12,
                DisplayFormat = "容差:{0}",
                Tip = "色相容差（±），容差内视为目标色"
            },
            new TaskParamDesc
            {
                ParamName = "输出色相",
                Min = 0, Max = 180, DefaultValue = 150,
                DisplayFormat = "输出H:{0}",
                Tip = "替换后的色相（0~180）"
            },
            new TaskParamDesc
            {
                ParamName = "输出饱和度",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "输出S:{0}",
                Tip = "替换后的饱和度；0=保留原饱和度"
            },
            new TaskParamDesc
            {
                ParamName = "输出明度",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "输出V:{0}",
                Tip = "替换后的明度；0=保留原明度"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int targetH = Math.Max(0, Math.Min(180, paramValues[0]));
            int tol = Math.Max(1, paramValues[1]);
            int outH = Math.Max(0, Math.Min(180, paramValues[2]));
            int outS = paramValues[3];
            int outV = paramValues[4];

            using (Mat hsv = new())
            {
                Cv2.CvtColor(srcMat, hsv, ColorConversionCodes.BGR2HSV);
                using (Mat mask = new Mat(hsv.Rows, hsv.Cols, MatType.CV_8U, Scalar.All(0)))
                {
                    // 色相是环形（0 与 180 相邻），分段处理
                    int lo1 = targetH - tol, hi1 = targetH + tol;
                    if (lo1 < 0)
                    {
                        using (Mat m1 = new())
                        {
                            Cv2.InRange(hsv, new Scalar(0, 60, 60), new Scalar(hi1, 255, 255), m1);
                            mask.SetTo(Scalar.All(255), m1);
                        }
                        using (Mat m2 = new())
                        {
                            Cv2.InRange(hsv, new Scalar(180 + lo1, 60, 60), new Scalar(180, 255, 255), m2);
                            mask.SetTo(Scalar.All(255), m2);
                        }
                    }
                    else if (hi1 > 180)
                    {
                        using (Mat m1 = new())
                        {
                            Cv2.InRange(hsv, new Scalar(lo1, 60, 60), new Scalar(180, 255, 255), m1);
                            mask.SetTo(Scalar.All(255), m1);
                        }
                        using (Mat m2 = new())
                        {
                            Cv2.InRange(hsv, new Scalar(0, 60, 60), new Scalar(hi1 - 180, 255, 255), m2);
                            mask.SetTo(Scalar.All(255), m2);
                        }
                    }
                    else
                    {
                        Cv2.InRange(hsv, new Scalar(lo1, 60, 60), new Scalar(hi1, 255, 255), mask);
                    }

                    // 输出图：目标区改 H（与 S/V 按设置），非目标区保持
                    Mat dst = srcMat.Clone();
                    using (Mat outHsv = new())
                    {
                        Cv2.CvtColor(dst, outHsv, ColorConversionCodes.BGR2HSV);
                        Mat[] planes = new Mat[3];
                        Cv2.Split(outHsv, out planes);
                        try
                        {
                            planes[0].SetTo(outH, mask);
                            if (outS > 0) planes[1].SetTo(outS, mask);
                            if (outV > 0) planes[2].SetTo(outV, mask);
                            Cv2.Merge(planes, outHsv);
                            Cv2.CvtColor(outHsv, dst, ColorConversionCodes.HSV2BGR);
                        }
                        finally
                        {
                            foreach (var p in planes) p?.Dispose();
                        }
                    }

                    double cnt = Cv2.CountNonZero(mask);
                    LastSummary = $"颜色替换: 命中 {cnt} 像素（H{targetH}±{tol} → H{outH}）";
                    return dst;
                }
            }
        }
    }
}
