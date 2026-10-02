using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Dead Leaves 纹理保真度：用**功率谱的径向平均**衡量纹理在不同频率上的保留程度。
    ///
    /// 为什么用功率谱而不是"清晰度分"：Dead Leaves 图是随机堆叠的纹理，没有单一锐边，
    /// SFR/MTF 无从下手；而它的能量按频率分布是已知的。降噪/锐化/编码都会改变这个分布——
    /// 高频被抹掉说明细节丢失（过度降噪），高频异常抬升说明过锐/振铃。
    ///
    /// 输出：径向平均功率谱（log-log 曲线）。若有参考图，则给出
    /// Qt = 高频能量 / 全频能量 的比值及两者之差，即"纹理保留度"。
    /// </summary>
    public class DeadLeavesTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "DeadLeaves纹理";

        /// <summary>作为参考的原始 Dead Leaves 图（可选）</summary>
        public Mat ReferenceMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>当前图的高频能量占比（Qt），越大说明保留的高频细节越多</summary>
        public double QtCurrent { get; private set; } = double.NaN;
        public double QtReference { get; private set; } = double.NaN;
        /// <summary>与参考的比值（1 = 一致；&lt;1 细节丢失；&gt;1 过锐）</summary>
        public double QtRatio { get; private set; } = double.NaN;

        public string SaveState() => VisionHelper.SaveTemplateState(ReferenceMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null) ReferenceMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "分析窗口",
                Min = 64, Max = 1024, DefaultValue = 256,
                DisplayFormat = "win:{0}",
                Group = "分析",
                Tip = "功率谱用的窗口边长（2 的幂最好）：太小频率分辨率差，太大容易跨到不均匀照明。"
            },
            new TaskParamDesc
            {
                ParamName = "高频起始频率%",
                Min = 1, Max = 100, DefaultValue = 30,
                DisplayFormat = "hf≥{0}%",
                Group = "分析",
                Tip = "把高于该奈奎斯特比例的能量算作'高频细节'。细节丢失主要体现在这一段。"
            },
            new TaskParamDesc
            {
                ParamName = "去均值 0关1开",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "demean:{0}",
                Group = "分析",
                Tip = "开：先减去窗口均值，避免直流分量和各向异性照明主导频谱（推荐开）。"
            },
            new TaskParamDesc
            {
                ParamName = "加窗 0无1汉宁",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "win:{0}",
                Group = "分析",
                Tip = "开：乘汉宁窗抑制截断泄漏，得到干净的径向谱（推荐开）。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0谱曲线1原图",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 绘制 log-log 径向功率谱（有参考图时两条曲线对比）；1 = 原图。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            QtCurrent = QtReference = QtRatio = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "DeadLeaves: 输入为空";
                return srcMat?.Clone();
            }

            int win = paramValues[0];
            int hfPct = paramValues[1];
            bool demean = paramValues[2] == 1;
            bool hann = paramValues[3] == 1;
            int outMode = paramValues[4];

            using Mat gray = VisionHelper.ToGray(srcMat);
            var (freqs, ps) = RadialSpectrum(gray, win, demean, hann);
            if (ps.Length == 0)
            {
                LastSummary = "DeadLeaves: 无法计算频谱（图像过小）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            QtCurrent = HighBandRatio(freqs, ps, hfPct);

            double[] refFreqs = null, refPs = null;
            if (ReferenceMat != null && !ReferenceMat.Empty())
            {
                using Mat refGray = VisionHelper.ToGray(ReferenceMat);
                (refFreqs, refPs) = RadialSpectrum(refGray, win, demean, hann);
                if (refPs.Length > 0)
                {
                    QtReference = HighBandRatio(refFreqs, refPs, hfPct);
                    if (QtReference > 1e-12)
                        QtRatio = QtCurrent / QtReference;
                }
            }

            Mat dst;
            if (outMode == 0)
            {
                dst = new Mat(Math.Min(480, gray.Rows), Math.Min(640, gray.Cols), MatType.CV_8UC3, Scalar.Black);
                DrawSpectrum(dst, freqs, ps, refFreqs, refPs);
            }
            else dst = VisionHelper.ToBgrCopy(srcMat);

            string extra = double.IsNaN(QtRatio) ? "" : string.Format("  Qt比={0:F3}", QtRatio);
            LastSummary = string.Format("DeadLeaves: Qt={0:F4}{1}  高频起点{2}% 窗口{3}",
                QtCurrent, extra, hfPct, win);
            return dst;
        }

        /// <summary>
        /// 径向平均功率谱：加窗 → DFT → |F|² → 按半径分箱平均。
        /// 返回 (归一化频率 0~1, 功率)。频率按奈奎斯特归一，1.0 = 奈奎斯特。
        /// </summary>
        private static (double[] freqs, double[] ps) RadialSpectrum(Mat gray, int win, bool demean, bool hann)
        {
            int w = Math.Min(win, Math.Min(gray.Cols, gray.Rows));
            if (w < 16) return (Array.Empty<double>(), Array.Empty<double>());
            // 取偶数边长：FftShift 的象限宽度在偶数下左右对称，避免奇偶导致的分箱偏心
            if (w % 2 != 0) w--;

            // 取中心窗口（纹理分析取中心最能代表画面主体）
            int x0 = (gray.Cols - w) / 2, y0 = (gray.Rows - w) / 2;
            using var roi = new Mat(gray, new Rect(x0, y0, w, w));
            using var f = new Mat();
            roi.ConvertTo(f, MatType.CV_32FC1);

            if (demean)
            {
                Scalar mean = Cv2.Mean(f);
                Cv2.Subtract(f, new Scalar(mean.Val0), f);
            }
            if (hann)
            {
                using Mat hw = new Mat(w, w, MatType.CV_32FC1);
                for (int y = 0; y < w; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double wx = 0.5 * (1 - Math.Cos(2 * Math.PI * x / (w - 1)));
                        double wy = 0.5 * (1 - Math.Cos(2 * Math.PI * y / (w - 1)));
                        hw.Set(y, x, (float)(wx * wy));
                    }
                Cv2.Multiply(f, hw, f);
            }

            // 二维 DFT -> 功率谱
            using Mat spec = new Mat();
            Cv2.Dft(f, spec, DftFlags.ComplexOutput);
            Cv2.Split(spec, out Mat[] planes);
            using Mat power = new Mat();
            Cv2.Magnitude(planes[0], planes[1], power);
            planes[0].Dispose(); planes[1].Dispose();
            Cv2.Multiply(power, power, power);          // |F|²
            // 移到中心，便于按半径分箱。目标必须是**已分配好尺寸**的 Mat，
            // 用 new Mat() 建空矩阵再按 ROI 写入会直接越界报错。
            using Mat centered = new Mat(power.Size(), power.Type());
            FftShift(power, centered);

            int cx = w / 2, cy = w / 2;
            int maxR = w / 2;
            var acc = new double[maxR];
            var cnt = new int[maxR];
            for (int y = 0; y < w; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    int r = (int)Math.Sqrt((dx * dx) + (dy * dy));
                    if (r < 1 || r >= maxR) continue;    // 跳过直流
                    acc[r] += centered.At<float>(y, x);
                    cnt[r]++;
                }

            int used = 0;
            for (int r = 1; r < maxR; r++) if (cnt[r] > 0) used++;
            var freqs = new double[used];
            var ps = new double[used];
            int k = 0;
            for (int r = 1; r < maxR; r++)
            {
                if (cnt[r] == 0) continue;
                freqs[k] = r / (double)maxR;          // 归一化到奈奎斯特
                ps[k] = acc[r] / cnt[r];
                k++;
            }
            return (freqs, ps);
        }

        /// <summary>高频能量占比（把高频段功率求和，除以全频段）</summary>
        private static double HighBandRatio(double[] freqs, double[] ps, int hfPct)
        {
            if (freqs.Length == 0) return double.NaN;
            double start = Math.Clamp(hfPct, 1, 100) / 100.0;
            double hi = 0, all = 0;
            for (int i = 0; i < freqs.Length; i++)
            {
                all += ps[i];
                if (freqs[i] >= start) hi += ps[i];
            }
            return all > 1e-20 ? hi / all : double.NaN;
        }

        /// <summary>
        /// 把 FFT 的零频从左上角移到中心（对角象限互换）。
        /// 每对源/目标 ROI 必须**尺寸完全相同**，否则 OpenCV 会因 ROI 越界报错
        /// （奇偶尺寸下左右两半宽度不同，早期版本把宽度用混了直接崩溃）。
        /// </summary>
        private static void FftShift(Mat src, Mat dst)
        {
            int cx = src.Cols / 2, cy = src.Rows / 2;
            int rw = src.Cols - cx, rh = src.Rows - cy;   // 右半宽 / 下半高（可能是 cx+1）

            // 左上(cx,cy) <-> 右下(rw,rh)
            using (Mat s = new Mat(src, new Rect(0, 0, cx, cy)))
            using (Mat d = new Mat(dst, new Rect(cx, cy, cx, cy)))
                s.CopyTo(d);
            // 右上(rw,cy) <-> 左下(cx,rh)
            using (Mat s = new Mat(src, new Rect(cx, 0, rw, cy)))
            using (Mat d = new Mat(dst, new Rect(0, cy, rw, cy)))
                s.CopyTo(d);
            // 左下(cx,rh) <-> 右上(rw,rh)
            using (Mat s = new Mat(src, new Rect(0, cy, cx, rh)))
            using (Mat d = new Mat(dst, new Rect(cx, 0, cx, rh)))
                s.CopyTo(d);
            // 右下(rw,rh) <-> 左上(rw,rh)
            using (Mat s = new Mat(src, new Rect(cx, cy, rw, rh)))
            using (Mat d = new Mat(dst, new Rect(0, 0, rw, rh)))
                s.CopyTo(d);
        }

        /// <summary>画 log-log 径向谱曲线（有参考图时两条对比）</summary>
        private void DrawSpectrum(Mat dst, double[] freqs, double[] ps, double[] refFreqs, double[] refPs)
        {
            int w = dst.Cols, h = dst.Rows;
            int pad = 40;
            Cv2.Rectangle(dst, new Rect(pad, pad, w - 2 * pad, h - 2 * pad), Scalar.Gray, 1);

            double logMin = -12, logMax = 2;   // log10 功率范围
            void Plot(double[] fr, double[] p, Scalar col, int thick)
            {
                double pMax = 0;
                foreach (double v in p) if (v > pMax) pMax = v;
                if (pMax <= 0) return;
                Point prev = new(-1, -1);
                for (int i = 0; i < fr.Length; i++)
                {
                    double nv = Math.Clamp(p[i] / pMax, 1e-12, 1);
                    double ly = (Math.Log10(nv) - logMin) / (logMax - logMin);
                    ly = Math.Clamp(ly, 0, 1);
                    int px = pad + (int)(fr[i] * (w - 2 * pad));
                    int py = h - pad - (int)(ly * (h - 2 * pad));
                    var pt = new Point(px, py);
                    if (prev.X >= 0) Cv2.Line(dst, prev, pt, col, thick);
                    prev = pt;
                }
            }

            if (refPs != null && refPs.Length > 0) Plot(refFreqs, refPs, Scalar.Gray, 1);
            Plot(freqs, ps, Scalar.Lime, 2);

            Cv2.PutText(dst, "log|F|^2 (normalized)", new Point(pad, pad - 12),
                HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1);
            Cv2.PutText(dst, "0", new Point(pad - 6, h - pad + 18), HersheyFonts.HersheySimplex, 0.4, Scalar.Gray, 1);
            Cv2.PutText(dst, "1 (Nyquist)", new Point(w - pad - 70, h - pad + 18),
                HersheyFonts.HersheySimplex, 0.4, Scalar.Gray, 1);
            Cv2.PutText(dst, string.Format("Qt={0:F4}", QtCurrent), new Point(pad + 6, h - pad - 10),
                HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            if (refPs != null && refPs.Length > 0)
                MatDraw.DrawText(dst, "灰=参考  绿=当前", pad + 6, pad + 16, Scalar.White, 11);
        }
    }
}
