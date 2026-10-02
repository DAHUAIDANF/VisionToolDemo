using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Mura / 周期条纹检测：用二维 FFT 的**径向谱**与**切向谱**分别判定两类缺陷。
    ///
    ///   · 径向谱（同一半径上能量随角度的分布）—— 检出周期性条纹（横纹/纵纹/网格纹/摩尔纹）：
    ///     周期结构会在频谱上产生一对关于原点对称的**亮点**，径向谱里就是尖峰。
    ///   · 低频能量占比 —— 检出 Mura（云斑、色不均）：它没有周期，能量集中在低频，
    ///     表现为"去均值后仍残留大块低频起伏"。
    ///
    /// 两类缺陷的物理机制不同（一个是对称尖峰，一个是低频集中），
    /// 用同一个指标是测不出来的，所以分别给出 PeakRatio 与 LowFreqRatio。
    ///
    /// 与"频域频谱"算子的区别：那个只把频谱画出来给人看，不做数值判定；
    /// 本算子给出可直接进公差的量化指标。
    /// </summary>
    public class MuraDefectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Mura条纹检测";

        public string LastSummary { get; private set; } = "";

        /// <summary>周期条纹强度：径向谱尖峰能量 / 总能量（不含 DC）</summary>
        public double PeakRatio { get; private set; } = double.NaN;

        /// <summary>最强条纹的空间周期（px），NaN = 无显著峰</summary>
        public double PeakPeriod { get; private set; } = double.NaN;

        /// <summary>最强条纹的方向（度，0=水平条纹，90=垂直条纹）</summary>
        public double PeakAngle { get; private set; } = double.NaN;

        /// <summary>低频（Mura）能量占比：低频能量 / 总能量</summary>
        public double LowFreqRatio { get; private set; } = double.NaN;

        /// <summary>整体粗糙度：去均值后标准差</summary>
        public double Roughness { get; private set; } = double.NaN;

        /// <summary>判定结果：0 = OK，1 = 检出条纹，2 = 检出 Mura，3 = 两者都有</summary>
        public int Verdict { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "分析窗口", Min = 64, Max = 4096, DefaultValue = 512,
                DisplayFormat = "窗口:{0}px", Group = "分析", Tip = "FFT 的方形窗口边长（会自动取 2 的幂）。" +
                "越大频率分辨率越高但越慢；512 通常够用，要检很粗的 Mura 时调大到 1024。" },
            new TaskParamDesc { ParamName = "去趋势核", Min = 0, Max = 301, DefaultValue = 31,
                DisplayFormat = "去趋势:{0}", Group = "分析", Tip = "先用大核高斯估计照明本底并减掉。" +
                "不扣除会把照明不均匀误判成 Mura。0 关闭。" },
            new TaskParamDesc { ParamName = "条纹峰门限%", Min = 1, Max = 100, DefaultValue = 8,
                DisplayFormat = "峰门限:{0}%", Group = "判定", Tip = "径向谱尖峰能量占总能量的百分比门限。" +
                "超过即判有周期条纹。干净图通常 <3%，明显条纹 >15%。" },
            new TaskParamDesc { ParamName = "Mura门限%", Min = 1, Max = 100, DefaultValue = 60,
                DisplayFormat = "Mura门限:{0}%", Group = "判定", Tip = "低频能量占总能量的百分比门限。" +
                "超过即判有 Mura。均匀图通常 <40%。" },
            new TaskParamDesc { ParamName = "低频截止%", Min = 1, Max = 100, DefaultValue = 12,
                DisplayFormat = "低频<{0}%", Group = "判定", Tip = "低于 Nyquist 的这个比例算低频。" },
            new TaskParamDesc { ParamName = "显示 0频谱1结果", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "显示:{0}", Group = "输出" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            PeakRatio = PeakPeriod = PeakAngle = LowFreqRatio = Roughness = double.NaN;
            Verdict = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            int win = paramValues[0];
            int detrK = paramValues[1];
            double peakThresh = paramValues[2] / 100.0;
            double muraThresh = paramValues[3] / 100.0;
            double lowCut = paramValues[4] / 100.0;
            int display = paramValues[5];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat g32 = new();
            gray.ConvertTo(g32, MatType.CV_32F);

            // 去趋势：大核高斯 = 照明本底，减掉后只剩缺陷信号
            if (detrK >= 3)
            {
                int k = detrK % 2 == 0 ? detrK + 1 : detrK;
                using Mat bg = new();
                Cv2.GaussianBlur(g32, bg, new Size(k, k), 0);
                Cv2.Subtract(g32, bg, g32);
                Cv2.Add(g32, new Scalar(128), g32);   // 抬回中性灰度，避免负值
            }

            // 取中心窗口：方形 + 2 的幂（FFT 在 2 的幂上最快且尺寸无歧义）。
            //
            // 这里必须用**向下**取整的 2 的幂，不能用 NextPow2 向上取整：
            // NextPow2 会把 240 变成 256，窗口比图还大，
            // 后面的 Rect 直接越界（实测 240x320 的图必崩 "0 <= roi.width && ..."）。
            // FFT 本身不要求 2 的幂，所以向下取整没有任何损失。
            int side = Math.Min(Math.Min(win, gray.Cols), gray.Rows);
            int size = PrevPow2(side);
            if (size < 16)
            {
                LastSummary = "Mura条纹检测: 图像太小（" + gray.Cols + "x" + gray.Rows + "）";
                return dst;
            }
            int ox = Math.Max(0, (gray.Cols - size) / 2);
            int oy = Math.Max(0, (gray.Rows - size) / 2);
            // 再夹一次：即便前面的取值有偏差也不会构造出越界 ROI
            ox = Math.Min(ox, gray.Cols - size);
            oy = Math.Min(oy, gray.Rows - size);
            using Mat roi = new(g32, new Rect(ox, oy, size, size));
            using Mat win32 = new();
            roi.ConvertTo(win32, MatType.CV_32F);

            Cv2.MeanStdDev(win32, out Scalar mean, out Scalar sd);
            Roughness = sd.Val0;

            // **先去均值再加密窗**，顺序不能反：
            //   · 不减均值时 DC 分量巨大（均匀图 DC = 均值 × N²），
            //     经汉宁窗后其旁瓣会把能量摊满整个低频带，
            //     结果连纯均匀图的"低频占比"都能达到 100%，指标彻底失去意义；
            //   · 先减均值，图像就只剩起伏，低频占比才真正反映 Mura 的强弱。
            Cv2.Subtract(win32, new Scalar(mean.Val0), win32);

            // 加汉宁窗抑制边界泄漏（不加重测到的"十字"伪谱）
            using Mat hann = new();
            Cv2.CreateHanningWindow(hann, new Size(size, size), MatType.CV_32F);
            using Mat windowed = new();
            Cv2.Multiply(win32, hann, windowed, 1.0, MatType.CV_32F);

            using Mat spec = new();
            Cv2.Dft(windowed, spec, DftFlags.ComplexOutput);
            // 拆成实虚两通道便于按半径/角度统计
            using Mat planes = new();
            Cv2.Split(spec, out Mat[] ch);
            using Mat re = ch[0], im = ch[1];
            using Mat power = new();
            Cv2.Magnitude(re, im, power);
            Cv2.Multiply(power, power, power);   // 功率谱

            // 用 FFTShift 把 DC 移到中心：这样才能按"到中心的距离"分环、
            // 按"绕中心的角度"分扇区。未 Shift 时 DC 在四角，分环会完全错乱。
            using Mat shifted = new(power.Size(), power.Type());
            FftShift(power, shifted, size);

            double c = size / 2.0;
            double total = 0, lowSum = 0;
            int lowCount = 0;
            double maxBin = 0;
            int peakX = 0, peakY = 0;
            // 环/扇区统计
            int nRings = size / 2;
            var ringSum = new double[nRings + 1];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double p = shifted.At<float>(y, x);
                    double dx = x - c, dy = y - c;
                    double r = Math.Sqrt((dx * dx) + (dy * dy));
                    if (r < 1.0) continue;   // 跳过 DC
                    total += p;
                    int ri = (int)Math.Round(r);
                    if (ri <= nRings) ringSum[ri] += p;
                    if (r < lowCut * (size / 2.0)) { lowSum += p; lowCount++; }
                    // 找峰时排除靠近 DC 的低频（Mura 会主导低频，掩盖条纹峰）
                    if (r > lowCut * (size / 2.0) && p > maxBin)
                    { maxBin = p; peakX = x; peakY = y; }
                }

            if (total <= 1e-9)
            {
                // 完全均匀的图像：没有任何起伏，明确给 0 而不是留下 NaN，
                // 否则下游拿 NaN 做公差比较会静默地全部判成"不超差"。
                PeakRatio = 0;
                LowFreqRatio = 0;
                Verdict = 0;
                MatDraw.DrawText(dst, "完全均匀: 条纹 0.00%  Mura 0.00%", 6, 20, Scalar.LimeGreen, 13);
                Cv2.PutText(dst, "OK", new Point(6, 44),
                    HersheyFonts.HersheySimplex, 0.9, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                LastSummary = "Mura条纹检测: OK (图像完全均匀, 无起伏; 粗糙度 0.00)";
                return dst;
            }

            PeakRatio = maxBin / total;
            LowFreqRatio = lowSum / total;

            // 峰的空间周期：频率 = 半径 / size (cyc/px)，周期 = 1/频率
            double pr = Math.Sqrt(((peakX - c) * (peakX - c)) + ((peakY - c) * (peakY - c)));
            if (pr > 1.0)
            {
                PeakPeriod = size / pr;
                // 角度：频率方向垂直于条纹方向。0 = 水平条纹（频率沿 y）
                // 报告的是**条纹自身的方向**（用户看到的走向），不是频率矢量的方向。
                // 两者正交：频率沿 x 的竖直条纹，走向是竖直(90°)，但频率矢量是 0°。
                // 归一化到 [0,180)，因为周期结构在 ±180° 上是同一件事。
                double freqAngle = Math.Atan2(peakY - c, peakX - c) * 180.0 / Math.PI;
                PeakAngle = freqAngle + 90.0;
                while (PeakAngle < 0) PeakAngle += 180.0;
                while (PeakAngle >= 180.0) PeakAngle -= 180.0;
            }

            bool hasStripe = PeakRatio > peakThresh;
            bool hasMura = LowFreqRatio > muraThresh;
            Verdict = (hasStripe ? 1 : 0) | (hasMura ? 2 : 0);

            // —— 结果绘制 ——
            if (display == 0)
            {
                using Mat vis = new();
                shifted.ConvertTo(vis, MatType.CV_8U, 255.0 / Math.Max(1e-9, maxBin * 2));
                Cv2.ApplyColorMap(vis, vis, ColormapTypes.Jet);
                Cv2.Resize(vis, dst, dst.Size(), 0, 0, InterpolationFlags.Nearest);
                if (double.IsFinite(PeakPeriod))
                {
                    double sc = (double)dst.Cols / size;
                    Cv2.Circle(dst, new Point((int)(peakX * sc), (int)(peakY * sc)), 12, Scalar.White, 2);
                }
            }
            else
            {
                Cv2.Rectangle(dst, new Rect(ox, oy, size, size), Scalar.Gray, 1);
                Scalar col = Verdict == 0 ? Scalar.LimeGreen : Scalar.Red;
                MatDraw.DrawText(dst, string.Format("条纹 {0:F2}% (峰门限 {1:F0}%)", PeakRatio * 100, peakThresh * 100), 6, 20, col, 13);
                MatDraw.DrawText(dst, string.Format("Mura {0:F2}% (门限 {1:F0}%)", LowFreqRatio * 100, muraThresh * 100), 6, 40, col, 13);
                if (double.IsFinite(PeakPeriod))
                    MatDraw.DrawText(dst, string.Format("周期 {0:F1}px 方向 {1:F1}deg", PeakPeriod, PeakAngle), 6, 60, Scalar.Yellow, 13);
                Cv2.PutText(dst, Verdict == 0 ? "OK" : "NG",
                    new Point(6, 84), HersheyFonts.HersheySimplex, 0.9, col, 2, LineTypes.AntiAlias);
            }

            string verdictText = Verdict switch
            {
                0 => "OK",
                1 => "NG-条纹",
                2 => "NG-Mura",
                _ => "NG-条纹+Mura",
            };
            LastSummary = string.Format(
                "Mura条纹检测: {0}  (条纹能量 {1:F2}%, Mura低频 {2:F2}%, 粗糙度 {3:F2}, 周期 {4}, 点数 {5})",
                verdictText, PeakRatio * 100, LowFreqRatio * 100, Roughness,
                double.IsFinite(PeakPeriod) ? string.Format("{0:F1}px/{1:F1}deg", PeakPeriod, PeakAngle) : "无",
                size);
            return dst;
        }

        internal static int NextPow2(int v)
        {
            int p = 1;
            while (p < v && p < 8192) p <<= 1;
            return p;
        }

        /// <summary>不大于 v 的最大 2 的幂（取窗口尺寸用这个，保证不越界）</summary>
        internal static int PrevPow2(int v)
        {
            if (v < 1) return 1;
            int p = 1;
            while (p <= v / 2 && p < 8192) p <<= 1;
            return p;
        }

        /// <summary>
        /// 四个象限对角互换，把 DC 从 (0,0) 移到中心。
        /// 必须保证每个象限的宽高**成对匹配**：奇数尺寸时 cx != rw，
        /// 用错宽度会抛 ROI 越界。
        /// </summary>
        internal static void FftShift(Mat src, Mat dst, int size)
        {
            int cx = size / 2, cy = size / 2;
            int rw = size - cx, rh = size - cy;   // 右/下半个的宽高

            // 用 quadrant 配对而不是硬编码 size/2，否则奇数尺寸会越界
            CopyQuad(src, dst, new Rect(0, 0, cx, cy), new Point(rw, rh));
            CopyQuad(src, dst, new Rect(rw, 0, cx, cy), new Point(0, rh));
            CopyQuad(src, dst, new Rect(0, rh, rw, cy), new Point(cx, 0));
            CopyQuad(src, dst, new Rect(cx, rh, rw, cy), new Point(0, 0));
        }

        private static void CopyQuad(Mat src, Mat dst, Rect r, Point at)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (r.Right > src.Cols || r.Bottom > src.Rows) return;
            if (at.X < 0 || at.Y < 0 || at.X + r.Width > dst.Cols || at.Y + r.Height > dst.Rows) return;
            using Mat s = new(src, r);
            using Mat d = new(dst, new Rect(at.X, at.Y, r.Width, r.Height));
            s.CopyTo(d);
        }
    }
}
