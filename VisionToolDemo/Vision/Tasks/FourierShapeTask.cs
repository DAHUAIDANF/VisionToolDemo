using System;
using System.Numerics;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 轮廓傅里叶描述子算子：把轮廓点序列做 DFT，取前 N 个低频系数作为形状描述，
    /// 输出描述子谱图（前 N 系数的幅度直方图）。形状分类/相似度分析用。
    /// 参数：二值化阈值、描述子数量（4~64）、输出模式。
    /// </summary>
    public class FourierShapeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "轮廓傅里叶描述子";

        public string LastSummary { get; private set; } = "";

        public double[] Descriptors { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "二值化阈值",
                Min = 0, Max = 255, DefaultValue = 128,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度转二值的前景阈值"
            },
            new TaskParamDesc
            {
                ParamName = "描述子数量",
                Min = 4, Max = 64, DefaultValue = 16,
                DisplayFormat = "系数:{0}",
                Tip = "保留的低频傅里叶系数个数（越多越精细）"
            },
            new TaskParamDesc
            {
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "模式:{0}",
                Tip = "0=原图叠加轮廓；1=描述子谱图"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Descriptors = Array.Empty<double>();
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int thresh = Math.Max(0, Math.Min(255, paramValues[0]));
            int nCoef = Math.Max(4, Math.Min(64, paramValues[1]));
            int mode = paramValues[2];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat bin = new();
                Cv2.Threshold(gray, bin, thresh, 255, ThresholdTypes.Binary);
                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy,
                    RetrievalModes.External, ContourApproximationModes.ApproxNone);
                bin.Dispose();

                Point[] best = null;
                foreach (var c in contours)
                    if (c.Length >= 16 && (best == null || c.Length > best.Length))
                        best = c;

                if (best == null)
                {
                    LastSummary = "轮廓傅里叶描述子: 未找到轮廓";
                    return srcMat.Clone();
                }

                // 复坐标：z = x + iy，做 DFT
                int N = best.Length;
                Complex[] z = new Complex[N];
                for (int i = 0; i < N; i++)
                    z[i] = new Complex(best[i].X, best[i].Y);

                Complex[] F = Dft(z);
                int kMax = Math.Min(nCoef, N / 2);
                Descriptors = new double[kMax];
                for (int k = 0; k < kMax; k++)
                    Descriptors[k] = F[k].Magnitude;

                // 归一化：除以 DC 幅度（尺度不变）
                double dc = Math.Max(1e-6, F[0].Magnitude);
                for (int k = 0; k < kMax; k++)
                    Descriptors[k] /= dc;

                if (mode == 0)
                {
                    // 原图叠加轮廓
                    Mat dst = srcMat.Channels() == 1
                        ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                        : srcMat.Clone();
                    Cv2.Polylines(dst, new[] { best }, true, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
                    LastSummary = $"轮廓傅里叶描述子: 轮廓 {N} 点，前 {kMax} 系数";
                    return dst;
                }

                // 描述子谱图
                Mat spec = new Mat(200, 640, MatType.CV_8UC3, new Scalar(20, 20, 20));
                double maxV = 1;
                for (int k = 0; k < kMax; k++) maxV = Math.Max(maxV, Descriptors[k]);
                int barW = Math.Max(4, 600 / Math.Max(1, kMax));
                for (int k = 0; k < kMax; k++)
                {
                    int h = (int)(Descriptors[k] / maxV * 160);
                    Cv2.Rectangle(spec, new Point(20 + k * barW, 180 - h),
                        new Point(20 + (k + 1) * barW - 2, 180), new Scalar(0, 200, 255), -1);
                }
                Cv2.Line(spec, new Point(20, 180), new Point(620, 180), new Scalar(255, 255, 255), 1);
                Cv2.PutText(spec, "Fourier descriptors (normalized)", new Point(20, 20),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);

                LastSummary = $"轮廓傅里叶描述子: 轮廓 {N} 点，前 {kMax} 系数";
                return spec;
            }
        }

        /// <summary>一维 DFT（朴素实现，N 一般几百点够用）</summary>
        private static Complex[] Dft(Complex[] x)
        {
            int N = x.Length;
            Complex[] X = new Complex[N];
            for (int k = 0; k < N; k++)
            {
                Complex sum = Complex.Zero;
                for (int n = 0; n < N; n++)
                {
                    double ang = -2 * Math.PI * k * n / N;
                    sum += x[n] * new Complex(Math.Cos(ang), Math.Sin(ang));
                }
                X[k] = sum;
            }
            return X;
        }
    }
}
