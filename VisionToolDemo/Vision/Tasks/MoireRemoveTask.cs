using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像去摩尔纹算子：频域环形陷波（带阻）滤除周期性网纹/摩尔纹，
    /// 再与中值滤波结果融合保持细节。用于屏幕拍摄、印刷品网纹去除。
    /// 参数：陷波半径（%）、带宽（%）、融合比例。
    /// </summary>
    public class MoireRemoveTask : IVisionTask, IResultReporter
    {
        public string TaskName => "去摩尔纹";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "陷波半径",
                Min = 1, Max = 50, DefaultValue = 15,
                DisplayFormat = "半径:{0}",
                Tip = "滤除的频带中心半径（图像最小边 50% 的百分数）"
            },
            new TaskParamDesc
            {
                ParamName = "带宽",
                Min = 1, Max = 40, DefaultValue = 8,
                DisplayFormat = "带宽:{0}",
                Tip = "陷波带宽（中心半径±带宽的频带被衰减）"
            },
            new TaskParamDesc
            {
                ParamName = "融合比例",
                Min = 0, Max = 100, DefaultValue = 70,
                DisplayFormat = "融合:{0}%",
                Tip = "陷波结果与中值滤波结果的融合权重（越大细节保留越多）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int radiusPct = Math.Max(1, paramValues[0]);
            int bandPct = Math.Max(1, paramValues[1]);
            int blendPct = Math.Max(0, Math.Min(100, paramValues[2]));

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 1) 频域环形陷波（带阻）
                int rows = Cv2.GetOptimalDFTSize(gray.Rows);
                int cols = Cv2.GetOptimalDFTSize(gray.Cols);
                Mat padded = new();
                Cv2.CopyMakeBorder(gray, padded, 0, rows - gray.Rows, 0, cols - gray.Cols,
                    BorderTypes.Constant, Scalar.All(0));
                padded.ConvertTo(padded, MatType.CV_32F);

                using (Mat zeros = new Mat(rows, cols, MatType.CV_32F, Scalar.All(0)))
                {
                    Mat[] planes = { padded, zeros };
                    Mat complex = new();
                    Cv2.Merge(planes, complex);
                    Mat dft = new();
                    Cv2.Dft(complex, dft, DftFlags.ComplexOutput);
                    FftShift(dft);

                    // 陷波掩膜：环形带内为 0.15（强衰减），其余 1
                    float half = Math.Min(cols, rows) / 2f;
                    float rC = half * radiusPct / 100f;
                    float rB = half * bandPct / 100f;
                    int cx = cols / 2, cy = rows / 2;
                    using (Mat mask = new Mat(rows, cols, MatType.CV_32F, Scalar.All(1f)))
                    {
                        // 环形带内设为 0.15
                        for (int y = 0; y < rows; y++)
                        {
                            for (int x = 0; x < cols; x++)
                            {
                                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                                if (d >= rC - rB && d <= rC + rB)
                                    mask.At<float>(y, x) = 0.15f;
                            }
                        }

                        // 乘掩膜
                        Mat[] maskPlanes = { mask, zeros };
                        Mat maskComplex = new();
                        Cv2.Merge(maskPlanes, maskComplex);
                        using (maskComplex)
                            Cv2.MulSpectrums(dft, maskComplex, dft, 0);
                    }

                    FftShift(dft);
                    Mat filtered = new();
                    Cv2.Idft(dft, filtered, DftFlags.Inverse | DftFlags.Scale | DftFlags.RealOutput);
                    filtered.ConvertTo(filtered, MatType.CV_8U);
                    Mat notch = new Mat(filtered, new Rect(0, 0, srcMat.Cols, srcMat.Rows)).Clone();
                    filtered.Dispose();

                    // 2) 中值滤波（去随机网纹点）
                    Mat median = new();
                    Cv2.MedianBlur(gray, median, 3);

                    // 3) 融合
                    Mat dst = new();
                    Cv2.AddWeighted(notch, blendPct / 100.0, median, 1 - blendPct / 100.0, 0, dst);
                    notch.Dispose();

                    LastSummary = $"去摩尔纹: 陷波半径{radiusPct}% 带宽{bandPct}%（融合{blendPct}%）";
                    return dst;
                }
            }
        }

        private static void FftShift(Mat m)
        {
            int cx = m.Cols / 2, cy = m.Rows / 2;
            using (Mat q0 = new(m, new Rect(0, 0, cx, cy)))
            using (Mat q1 = new(m, new Rect(cx, 0, m.Cols - cx, cy)))
            using (Mat q2 = new(m, new Rect(0, cy, cx, m.Rows - cy)))
            using (Mat q3 = new(m, new Rect(cx, cy, m.Cols - cx, m.Rows - cy)))
            using (Mat tmp = new())
            {
                q0.CopyTo(tmp); q3.CopyTo(q0); tmp.CopyTo(q3);
                q1.CopyTo(tmp); q2.CopyTo(q1); tmp.CopyTo(q2);
            }
        }
    }
}
