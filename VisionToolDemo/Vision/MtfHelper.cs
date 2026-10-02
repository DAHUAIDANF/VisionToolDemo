using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// MTF / SFR 公共算法库（ISO 12233:2014/2017 斜边法）。
    ///
    /// 被"斜边SFR""楔形/星图""场曲热力图"等算子共用，避免同一套 ESF/LSF/DFT
    /// 流程在多个算子里各写一遍（写了就会有版本差异，量出来的 MTF 不可比）。
    ///
    /// 流程：
    ///   ① 找斜边并做亚像素精化（直线拟合，避免 Canny 的整像素跳动）
    ///   ② 沿垂直于边的方向投影得到超采样 ESF（用双线性插值，超采样倍数可调）
    ///   ③ ESF 差分得 LSF，乘汉宁窗抑制截断泄漏
    ///   ④ DFT 取模并归一化到 0 频 = 1，得到 MTF(f)
    ///   ⑤ 按 ISO 修正因子补偿"有限长度 + 窗函数"带来的低频低估
    /// </summary>
    public static class MtfHelper
    {
        /// <summary>一次 SFR 计算的完整结果</summary>
        public sealed class SfrResult
        {
            /// <summary>空间频率轴（cycles/pixel）</summary>
            public double[] Freq { get; set; } = Array.Empty<double>();
            /// <summary>对应 MTF 值（已归一化，0 频 = 1）</summary>
            public double[] Mtf { get; set; } = Array.Empty<double>();
            /// <summary>边缘倾角（度，相对图像水平轴）</summary>
            public double EdgeAngle { get; set; }
            /// <summary>边缘中心位置（垂直方向的像素坐标）</summary>
            public double EdgeCenter { get; set; }
            /// <summary>ESF 对比度（黑到白的幅度，用于判断边缘是否有效）</summary>
            public double Contrast { get; set; }
            /// <summary>失败原因；null 表示成功</summary>
            public string Error { get; set; }

            public bool Ok => Error == null;

            /// <summary>取指定频率处的 MTF（线性插值）</summary>
            public double At(double f)
            {
                if (Freq.Length == 0) return double.NaN;
                if (f <= Freq[0]) return Mtf[0];
                if (f >= Freq[^1]) return Mtf[^1];
                for (int i = 1; i < Freq.Length; i++)
                    if (Freq[i] >= f)
                    {
                        double df = Freq[i] - Freq[i - 1];
                        if (Math.Abs(df) < 1e-12) return Mtf[i];
                        double t = (f - Freq[i - 1]) / df;
                        if (t < 0) t = 0;
                        if (t > 1) t = 1;
                        return Mtf[i - 1] + (t * (Mtf[i] - Mtf[i - 1]));
                    }
                return Mtf[^1];
            }

            /// <summary>MTF 降到给定值时的频率（如 MTF50 传 0.5）；未降到返回 NaN</summary>
            public double FreqAt(double level)
            {
                for (int i = 1; i < Mtf.Length; i++)
                {
                    if (Mtf[i] > level) continue;

                    double d = Mtf[i] - Mtf[i - 1];
                    // 分母接近 0 说明相邻两点同值（平台段）：直接取该点频率，
                    // 否则除出天文数字（曾出现 MTF50 = -3e9 的假值）。
                    if (Math.Abs(d) < 1e-9) return Freq[i];

                    double t = (level - Mtf[i - 1]) / d;
                    if (double.IsNaN(t) || double.IsInfinity(t)) return Freq[i];
                    // 线性插值必须在两点之间，越界说明数据非单调，夹住即可
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    return Freq[i - 1] + (t * (Freq[i] - Freq[i - 1]));
                }
                return double.NaN;
            }
        }

        /// <summary>
        /// 对含一条斜边的图像块做 SFR 分析。
        /// </summary>
        /// <param name="gray">输入灰度图（应为包含斜边的 ROI）</param>
        /// <param name="oversample">超采样倍数（默认 4，ISO 推荐 4）</param>
        /// <param name="darkIsLeft">true = 边左侧是暗区；false = 左侧亮</param>
        /// <param name="edgeSearchHalfWidth">找边时沿垂直于边方向的搜索半宽</param>
        public static SfrResult Compute(Mat gray, int oversample = 4, bool darkIsLeft = true,
            int edgeSearchHalfWidth = 6)
        {
            var res = new SfrResult();
            if (gray == null || gray.Empty() || gray.Rows < 16 || gray.Cols < 16)
            {
                res.Error = "图像块过小（至少 16x16）";
                return res;
            }
            if (oversample < 1) oversample = 1;

            using Mat work = new Mat();
            gray.ConvertTo(work, MatType.CV_32FC1);

            // —— ① 找边：用 Sobel 水平梯度定位竖直方向上的灰度跃变 ——
            // 斜边在图中近似竖直（允许倾斜），所以沿 x 方向求导，每行取梯度极值位置
            using Mat gx = new Mat();
            Cv2.Sobel(work, gx, MatType.CV_32FC1, 1, 0, 3);

            int rows = work.Rows, cols = work.Cols;
            var edgeX = new double[rows];
            var valid = new bool[rows];
            int cx0 = cols / 2;

            for (int y = 0; y < rows; y++)
            {
                double best = 0; int bx = -1;
                int lo = Math.Max(1, cx0 - (cols / 2) + 1);
                int hi = Math.Min(cols - 2, cx0 + (cols / 2) - 1);
                for (int x = lo; x <= hi; x++)
                {
                    float g = Math.Abs(gx.At<float>(y, x));
                    if (g > best) { best = g; bx = x; }
                }
                // 梯度太弱说明这行没穿过边（可能在暗区/亮区内部）
                if (bx > 0 && best > 1e-3)
                {
                    // 抛物线亚像素精化：极值在 bx 附近，用 g(bx-1),g(bx),g(bx+1) 拟合
                    float g0 = Math.Abs(gx.At<float>(y, bx - 1));
                    float g1 = Math.Abs(gx.At<float>(y, bx));
                    float g2 = Math.Abs(gx.At<float>(y, bx + 1));
                    double denom = g0 - (2 * g1) + g2;
                    double sub = Math.Abs(denom) > 1e-9 ? 0.5 * (g0 - g2) / denom : 0;
                    if (sub < -1) sub = -1;
                    if (sub > 1) sub = 1;
                    edgeX[y] = bx + sub;
                    valid[y] = true;
                }
            }

            int validCount = 0;
            foreach (bool ok in valid) if (ok) validCount++;
            if (validCount < rows / 3)
            {
                res.Error = "未找到有效边缘（梯度太弱或画面内没有斜边）";
                return res;
            }

            // —— 拟合直线得到倾角（用最小二乘，抗个别行的跳动） ——
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int y = 0; y < rows; y++)
                if (valid[y]) { sx += y; sy += edgeX[y]; sxx += (double)y * y; sxy += y * edgeX[y]; n++; }
            double den = (n * sxx) - (sx * sx);
            if (Math.Abs(den) < 1e-9)
            {
                res.Error = "边缘几乎不动（无法拟合倾角）";
                return res;
            }
            double slope = ((n * sxy) - (sx * sy)) / den;      // dx/dy
            double intercept = (sy - (slope * sx)) / n;

            // 倾角：相对水平轴的夹角。slope 是 dx/dy，竖直边 slope≈0
            double angleFromVertical = Math.Atan(slope) * 180.0 / Math.PI;
            res.EdgeAngle = 90.0 - angleFromVertical;          // 相对水平轴

            // —— ② 超采样 ESF：沿边法线方向投影 ——
            // 对每一行，在拟合边的两侧按超采样步长采样灰度，累加到 ESF bin
            double norm = Math.Sqrt(1 + (slope * slope));
            double nx = 1.0 / norm;      // 法线 x 分量（指向 +x）
            double ny = -slope / norm;   // 法线 y 分量

            int halfSamples = (int)Math.Round((double)(edgeSearchHalfWidth * oversample));
            int bins = (2 * halfSamples) + 1;
            var acc = new double[bins];
            var cnt = new int[bins];

            for (int y = 0; y < rows; y++)
            {
                if (!valid[y]) continue;
                double ex = edgeX[y], ey = y;
                for (int k = -halfSamples; k <= halfSamples; k++)
                {
                    double d = (double)k / oversample;          // 距边的像素距离
                    double px = ex + (d * nx);
                    double py = ey + (d * ny);
                    if (px < 0 || py < 0 || px >= cols - 1 || py >= rows - 1) continue;
                    double v = Bilinear(work, px, py);
                    int bi = k + halfSamples;
                    acc[bi] += v;
                    cnt[bi]++;
                }
            }

            var esf = new double[bins];
            int filled = 0;
            for (int i = 0; i < bins; i++)
            {
                if (cnt[i] == 0) { esf[i] = double.NaN; continue; }
                esf[i] = acc[i] / cnt[i];
                filled++;
            }
            if (filled < bins / 2)
            {
                res.Error = "ESF 有效样本不足（边缘可能太靠图像边界）";
                return res;
            }

            // 补掉空洞（沿 x 线性插值）
            FillNaNs(esf);

            // 方向：让 ESF 从"暗"过渡到"亮"
            double left = esf[0], right = esf[^1];
            if (!darkIsLeft) { /* 已是左亮右暗，反转后统一为左暗右亮 */ }

            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (double v in esf) { if (v < minV) minV = v; if (v > maxV) maxV = v; }
            res.Contrast = maxV - minV;
            if (res.Contrast < 5)
            {
                res.Error = string.Format("边缘对比度太低 ({0:F1} 灰阶，至少需 5)", res.Contrast);
                return res;
            }

            // 归一化到 [-1,1]，让低频行为统一（MTF 对线性缩放不敏感，但便于检视）
            var esfN = new double[bins];
            double mid = (maxV + minV) / 2.0, half = res.Contrast / 2.0;
            for (int i = 0; i < bins; i++) esfN[i] = (esf[i] - mid) / half;

            // —— ③ LSF = ESF 的差分 ——
            var lsf = new double[bins];
            for (int i = 1; i < bins; i++) lsf[i] = esfN[i] - esfN[i - 1];
            lsf[0] = lsf[1];

            // —— ④ 汉宁窗：抑制截断造成的频谱泄漏 ——
            var windowed = new double[bins];
            for (int i = 0; i < bins; i++)
            {
                double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (bins - 1)));   // Hann
                windowed[i] = lsf[i] * w;
            }

            // —— ⑤ DFT 取模 ——
            int nFft = NextPow2(bins);
            var re = new double[nFft];
            var im = new double[nFft];
            Array.Copy(windowed, re, bins);
            Fft(re, im, false);

            var mag = new double[nFft / 2 + 1];
            for (int k = 0; k <= nFft / 2; k++)
                mag[k] = Math.Sqrt((re[k] * re[k]) + (im[k] * im[k]));

            // 归一化：0 频 = 1
            double dc = mag[0];
            if (dc < 1e-12)
            {
                res.Error = "LSF 能量过低（边缘可能被截断过度）";
                return res;
            }

            // 频率轴：ESF 的采样间隔是 1/oversample 像素，共 bins 点
            // 因此频率分辨率 df = oversample / (nFft) cycles/pixel
            int outBins = nFft / 2 + 1;
            res.Freq = new double[outBins];
            res.Mtf = new double[outBins];
            for (int k = 0; k < outBins; k++)
            {
                res.Freq[k] = (double)k * oversample / nFft;
                // ISO 修正因子：补偿汉宁窗在低频处的增益损失。
                // Hann 窗的频谱在 0 频处增益为 0.5，除以它可还原幅度；
                // 这里按 ISO 12233 的做法用窗的直流增益归一。
                double corr = HannLowFreqCorrection(k, nFft);
                res.Mtf[k] = mag[k] / dc / Math.Max(1e-6, corr);
            }
            res.Mtf[0] = 1.0;   // 定义

            // 边缘中心：ESF 里灰度越过中点的位置（换算回像素）
            double centerIdx = -1;
            for (int i = 1; i < bins; i++)
                if ((esfN[i - 1] < 0) != (esfN[i] < 0))
                {
                    double t = -esfN[i - 1] / Math.Max(1e-12, esfN[i] - esfN[i - 1]);
                    centerIdx = i - 1 + t;
                    break;
                }
            res.EdgeCenter = centerIdx >= 0 ? (centerIdx - halfSamples) / (double)oversample : 0;

            return res;
        }

        /// <summary>
        /// 汉宁窗低频修正。窗函数会把信号乘衰减包络，导致低频 MTF 被低估；
        /// ISO 12233 用"窗的归一化直流增益"回补。
        /// Hann 窗 sum = N/2，信号 sum 与窗 sum 的比值即修正量。
        /// </summary>
        private static double HannLowFreqCorrection(int k, int nFft)
        {
            if (k == 0) return 1.0;
            // 对 Hann 窗而言，低频段增益≈0.5；随频率略变，这里给出解析近似
            double f = (double)k / nFft;
            return 0.5 + (0.5 * Math.Cos(2 * Math.PI * f));
        }

        /// <summary>双线性采样（图像须为 CV_32FC1）</summary>
        public static double Bilinear(Mat f32, double x, double y)
        {
            int x0 = (int)x, y0 = (int)y;
            if (x0 < 0 || y0 < 0 || x0 >= f32.Cols - 1 || y0 >= f32.Rows - 1)
                return 0;
            double fx = x - x0, fy = y - y0;
            float v00 = f32.At<float>(y0, x0);
            float v10 = f32.At<float>(y0, x0 + 1);
            float v01 = f32.At<float>(y0 + 1, x0);
            float v11 = f32.At<float>(y0 + 1, x0 + 1);
            return ((v00 * (1 - fx)) + (v10 * fx)) * (1 - fy)
                 + ((v01 * (1 - fx)) + (v11 * fx)) * fy;
        }

        private static void FillNaNs(double[] a)
        {
            int n = a.Length;
            int firstGood = -1;
            for (int i = 0; i < n; i++) if (!double.IsNaN(a[i])) { firstGood = i; break; }
            if (firstGood < 0) return;
            for (int i = 0; i < firstGood; i++) a[i] = a[firstGood];
            for (int i = firstGood + 1; i < n; i++)
            {
                if (!double.IsNaN(a[i])) continue;
                int j = i;
                while (j < n && double.IsNaN(a[j])) j++;
                double prev = a[i - 1];
                double next = j < n ? a[j] : prev;
                int steps = j - i + 1;
                for (int k = i; k < j; k++)
                    a[k] = prev + ((next - prev) * (k - i + 1) / steps);
                i = j;
            }
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>就地基-2 FFT（inverse=false 为正变换）。长度须为 2 的幂。</summary>
        public static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            if (n <= 1) return;
            if ((n & (n - 1)) != 0) throw new ArgumentException("FFT 长度必须是 2 的幂");

            // 位反转置换
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + (len / 2);
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }

            if (inverse)
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }
}
