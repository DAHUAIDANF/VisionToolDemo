using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 频域滤波算子（DFT → 频谱中心化 → 圆形掩膜 → 逆 DFT）：
    /// 低通（去噪平滑）/ 高通（提取边缘、划痕）/ 带通（周期纹理分离）。
    /// 参数：类型（0=低通 1=高通 2=带通）、截止半径（%）、带宽（%）。
    /// </summary>
    public class FreqFilterTask : IVisionTask, IResultReporter
    {
        public string TaskName => "频域滤波";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "类型",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=低通（去噪平滑） 1=高通（提取边缘/划痕） 2=带通（周期纹理）"
            },
            new TaskParamDesc
            {
                ParamName = "截止半径",
                Min = 1, Max = 50, DefaultValue = 12,
                DisplayFormat = "半径:{0}",
                Tip = "截止频率半径（图像最小边 50% 的百分数）；低通=圆内保留，高通=圆外保留"
            },
            new TaskParamDesc
            {
                ParamName = "带宽",
                Min = 1, Max = 40, DefaultValue = 8,
                DisplayFormat = "带宽:{0}",
                Tip = "仅带通：环形带宽（截止半径±带宽的频带保留）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int type = paramValues[0];
            int radiusPct = Math.Max(1, paramValues[1]);
            int bandPct = Math.Max(1, paramValues[2]);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 1) 扩到 DFT 友好尺寸
                int rows = Cv2.GetOptimalDFTSize(gray.Rows);
                int cols = Cv2.GetOptimalDFTSize(gray.Cols);
                Mat padded = new();
                Cv2.CopyMakeBorder(gray, padded, 0, rows - gray.Rows, 0, cols - gray.Cols,
                    BorderTypes.Constant, Scalar.All(0));
                padded.ConvertTo(padded, MatType.CV_32F);

                // 2) 双通道复数（实部 + 0 虚部）
                using (Mat zeros = new Mat(rows, cols, MatType.CV_32F, Scalar.All(0)))
                {
                    Mat[] planes = { padded, zeros };
                    Mat complex = new();
                    Cv2.Merge(planes, complex);

                    // 3) 前向 DFT
                    Mat dft = new();
                    Cv2.Dft(complex, dft, DftFlags.ComplexOutput);

                    // 4) 频谱中心化（低频移到中心，掩膜才好画圆）
                    FftShift(dft);

                    // 5) 圆形掩膜（低频在中心）
                    float half = Math.Min(cols, rows) / 2f;
                    float rCut = half * radiusPct / 100f;
                    float rBand = half * bandPct / 100f;
                    int cx = cols / 2, cy = rows / 2;
                    using (Mat mask = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0)))
                    {
                        if (type == 0)      // 低通：圆内 1
                        {
                            Cv2.Circle(mask, cx, cy, (int)rCut, new Scalar(255), -1);
                        }
                        else if (type == 1) // 高通：圆外 1
                        {
                            mask.SetTo(Scalar.All(255));
                            Cv2.Circle(mask, cx, cy, (int)rCut, new Scalar(0), -1);
                        }
                        else                // 带通：外圆内亮、内圆挖掉 → 环形
                        {
                            Cv2.Circle(mask, cx, cy, (int)(rCut + rBand), new Scalar(255), -1);
                            Cv2.Circle(mask, cx, cy, Math.Max(1, (int)(rCut - rBand)), new Scalar(0), -1);
                        }

                        // 6) 掩膜转复数并与频谱相乘
                        using (Mat maskF = new())
                        {
                            mask.ConvertTo(maskF, MatType.CV_32F);
                            Mat[] maskPlanes = { maskF, zeros };
                            Mat maskComplex = new();
                            Cv2.Merge(maskPlanes, maskComplex);
                            using (maskComplex)
                                Cv2.MulSpectrums(dft, maskComplex, dft, 0);
                        }

                        // 7) 反中心化 + 逆 DFT → 实部
                        FftShift(dft);
                        Mat filtered = new();
                        Cv2.Idft(dft, filtered, DftFlags.Inverse | DftFlags.Scale | DftFlags.RealOutput);
                        filtered.ConvertTo(filtered, MatType.CV_8U);

                        // 8) 裁剪回原尺寸
                        Mat dst = new Mat(filtered, new Rect(0, 0, srcMat.Cols, srcMat.Rows)).Clone();

                        string[] names = { "低通", "高通", "带通" };
                        LastSummary = $"频域{names[type]}: 半径{radiusPct}%" + (type == 2 ? $" 带宽{bandPct}%" : "");
                        return dst;
                    }
                }
            }
        }

        /// <summary>频谱象限交换（fftshift / ifftshift 同一操作，做两次即还原）</summary>
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
