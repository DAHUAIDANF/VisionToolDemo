using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 频域频谱：对灰度图做 DFT，取对数幅度谱并中心化（低频居中），缩放回原尺寸显示；
    /// 摘要输出高频能量占比（周期噪声/摩尔纹会让高频占比明显升高）。
    /// 参数：显示增益(%)。
    /// </summary>
    public class DftSpectrumTask : IVisionTask, IResultReporter
    {
        public string TaskName => "频域频谱";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "显示增益%",
                Min = 10,
                Max = 1000,
                DefaultValue = 100,
                DisplayFormat = "增益:{0}%"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            double gain = Math.Max(0.1, paramValues[0] / 100.0);
            double hfRatio = 0;

            // dst 最终被频谱显示覆盖，无需预先拷贝原图
            Mat dst = new(srcMat.Size(), MatType.CV_8UC3);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat f32 = new())
            {
                gray.ConvertTo(f32, MatType.CV_32F);
                int optR = Cv2.GetOptimalDFTSize(f32.Rows);
                int optC = Cv2.GetOptimalDFTSize(f32.Cols);

                using (Mat padded = new())
                {
                    Cv2.CopyMakeBorder(f32, padded, 0, optR - f32.Rows, 0, optC - f32.Cols,
                        BorderTypes.Constant, Scalar.All(0));

                    using (Mat complex = new())
                    {
                        using (Mat zeros = new(padded.Rows, padded.Cols, MatType.CV_32F, Scalar.All(0)))
                        {
                            Cv2.Merge(new[] { padded, zeros }, complex);
                        }
                        Cv2.Dft(complex, complex, DftFlags.ComplexOutput);

                        Mat[] planes;
                        Cv2.Split(complex, out planes);
                        using (planes[0])
                        using (planes[1])
                        using (Mat mag = new())
                        {
                            Cv2.Magnitude(planes[0], planes[1], mag);
                            using (Mat one = new(mag.Size(), mag.Type(), Scalar.All(1)))
                            {
                                Cv2.Add(mag, one, mag);
                            }
                            Cv2.Log(mag, mag);

                            // 中心化：四象限互换，低频移到中心
                            int cx = mag.Cols / 2, cy = mag.Rows / 2;
                            using (Mat q0 = new(mag, new Rect(0, 0, cx, cy)))
                            using (Mat q1 = new(mag, new Rect(cx, 0, mag.Cols - cx, cy)))
                            using (Mat q2 = new(mag, new Rect(0, cy, cx, mag.Rows - cy)))
                            using (Mat q3 = new(mag, new Rect(cx, cy, mag.Cols - cx, mag.Rows - cy)))
                            using (Mat tmp = new())
                            {
                                q0.CopyTo(tmp); q3.CopyTo(q0); tmp.CopyTo(q3);
                                q1.CopyTo(tmp); q2.CopyTo(q1); tmp.CopyTo(q2);
                            }

                            // 高频能量占比：1 - 中心 1/4 区域能量占比（框须以 DC 点 (cx,cy) 为中心）
                            double hf = 0;
                            double total = Cv2.Sum(mag).Val0;
                            if (total > 1e-9)
                            {
                                int cw = Math.Max(1, cx / 2), ch = Math.Max(1, cy / 2);
                                using (Mat center = new(mag, new Rect(cx - (cw / 2), cy - (ch / 2), cw, ch)))
                                {
                                    hf = 1 - (Cv2.Sum(center).Val0 / total);
                                }
                            }
                            hfRatio = hf;

                            // 归一化显示（增益越大越亮），缩放回原尺寸
                            using (Mat mag8 = new())
                            using (Mat shown = new())
                            {
                                Cv2.Normalize(mag, mag, 0, 255.0 * gain, NormTypes.MinMax);
                                mag.ConvertTo(mag8, MatType.CV_8UC1);
                                Cv2.Resize(mag8, shown, srcMat.Size());
                                Cv2.CvtColor(shown, dst, ColorConversionCodes.GRAY2BGR);
                                Cv2.PutText(dst, "FFT spectrum (log|F|, centered)", new Point(10, 24),
                                    HersheyFonts.HersheySimplex, 0.6, Scalar.Yellow, 1, LineTypes.AntiAlias);
                            }
                        }
                    }
                }
            }
            LastSummary = string.Format("频域频谱: 高频能量 {0:F1}% (增益 {1}%)", hfRatio * 100, paramValues[0]);
            return dst;
        }
    }
}
