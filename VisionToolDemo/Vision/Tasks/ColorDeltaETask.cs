using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 颜色分割 / 色差检测 (ΔE)：按 CIEDE2000 感知色差分割颜色区域，比 HSV 阈值更贴近人眼。
    ///
    /// 为什么需要它：现有 HSV颜色提取 用 H/S/V 三轴阈值框选颜色，问题有两个——
    ///   · H 是循环量，红色跨 0/180 边界要拆两段，用户很容易框漏；
    ///   · HSV 三轴同时卡阈值时，颜色空间的"立方体"与人眼感知的"相似"不一致，
    ///     调参时经常出现"明暗一变就漏检"，因为它对亮度 V 的变化不做归一。
    /// CIELAB + ΔE 直接回答"这个像素和目标色差多少"，单参数、有物理含义（ΔE≈2.3 为
    /// 刚可察觉差异），且对亮度变化更鲁棒。
    ///
    /// 两种用法：
    ///   · 参考色模式 —— 取图上某个矩形区域的平均色作参考，分割全图相似区域；
    ///   · 色差图模式 —— 输出逐像素 ΔE 热图，用来定位色偏/污点，不做二值分割。
    /// </summary>
    public class ColorDeltaETask : IVisionTask, IResultReporter
    {
        public string TaskName => "色差ΔE分割";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次分割出的像素占比</summary>
        public double MatchFraction { get; private set; }

        /// <summary>最近一次参考色（Lab）</summary>
        public Vec3b LastReferenceLab { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                // 参考色取样区的中心与大小（相对图像百分比，避免写死像素）
                ParamName = "取样中心X%",
                Min = 0,
                Max = 100,
                DefaultValue = 50,
                DisplayFormat = "cx:{0}%",
                Group = "取样"
            },
            new TaskParamDesc
            {
                ParamName = "取样中心Y%",
                Min = 0,
                Max = 100,
                DefaultValue = 50,
                DisplayFormat = "cy:{0}%",
                Group = "取样"
            },
            new TaskParamDesc
            {
                ParamName = "取样边长%",
                Min = 2,
                Max = 100,
                DefaultValue = 10,
                DisplayFormat = "size:{0}%",
                Group = "取样"
            },
            new TaskParamDesc
            {
                // CIEDE2000 色差阈值：≈2.3 刚可察觉，5~10 为"明显不同色"
                ParamName = "ΔE阈值x10",
                Min = 5,
                Max = 500,
                DefaultValue = 80,
                DisplayFormat = "ΔE≤{0}",
                Group = "色差"
            },
            new TaskParamDesc
            {
                // 0=二值掩膜（相似=白） 1=原图彩色叠加 2=ΔE热图
                ParamName = "输出 0掩膜1叠加2热图",
                Min = 0,
                Max = 2,
                DefaultValue = 1,
                DisplayFormat = "out:{0}",
                Group = "色差"
            },
            new TaskParamDesc
            {
                // 分割前的高斯平滑核（0=不平滑），抑制传感器彩色噪声
                ParamName = "去噪核",
                Min = 0,
                Max = 15,
                DefaultValue = 3,
                DisplayFormat = "denoise:{0}",
                ForceOdd = true,
                Group = "色差"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            MatchFraction = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "色差分割: 输入为空";
                return srcMat?.Clone();
            }

            int cxPct = paramValues[0];
            int cyPct = paramValues[1];
            int sizePct = paramValues[2];
            double dEThresh = paramValues[3] / 10.0;
            int outMode = paramValues[4];
            int denoise = paramValues[5];

            // 统一转 3 通道 BGR（灰度图也能用：三通道相同，等于按亮度差分割）
            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);
            using Mat work = new Mat();
            if (denoise >= 3)
            {
                int k = denoise % 2 == 0 ? denoise + 1 : denoise;
                Cv2.GaussianBlur(bgr, work, new Size(k, k), 0);
            }
            else bgr.CopyTo(work);

            using Mat lab = new Mat();
            Cv2.CvtColor(work, lab, ColorConversionCodes.BGR2Lab);

            // 取样区（按百分比定位，图像尺寸变化时不必重调）
            int bw = Math.Max(2, work.Cols * sizePct / 100);
            int bh = Math.Max(2, work.Rows * sizePct / 100);
            int bx = Math.Clamp(work.Cols * cxPct / 100 - bw / 2, 0, Math.Max(0, work.Cols - bw));
            int by = Math.Clamp(work.Rows * cyPct / 100 - bh / 2, 0, Math.Max(0, work.Rows - bh));
            var refRect = new Rect(bx, by, bw, bh);

            using Mat refRoi = new Mat(lab, refRect);
            Scalar refMean = Cv2.Mean(refRoi);
            // OpenCV 的 Lab 是 8U 编码：L 0..255, a/b 偏移 128
            byte rl = (byte)Math.Clamp(refMean.Val0, 0, 255);
            byte ra = (byte)Math.Clamp(refMean.Val1, 0, 255);
            byte rb = (byte)Math.Clamp(refMean.Val2, 0, 255);
            LastReferenceLab = new Vec3b(rl, ra, rb);

            // 逐像素 CIEDE2000
            using Mat dEMap = new Mat(lab.Rows, lab.Cols, MatType.CV_32FC1);
            unsafe
            {
                byte* p = (byte*)lab.Data;
                float* d = (float*)dEMap.Data;
                long step = lab.Step();
                long dstep = dEMap.Step() / 4;
                for (int y = 0; y < lab.Rows; y++)
                {
                    byte* row = p + (y * step);
                    float* drow = (float*)((byte*)d + (y * dstep * 4));
                    for (int x = 0; x < lab.Cols; x++)
                    {
                        double l1 = row[(x * 3) + 0] * 100.0 / 255.0;
                        double a1 = row[(x * 3) + 1] - 128.0;
                        double b1 = row[(x * 3) + 2] - 128.0;
                        double l2 = rl * 100.0 / 255.0;
                        double a2 = ra - 128.0;
                        double b2 = rb - 128.0;
                        drow[x] = (float)DeltaE2000(l1, a1, b1, l2, a2, b2);
                    }
                }
            }

            // 阈值内 = 相似
            using Mat mask = new Mat();
            Cv2.Compare(dEMap, dEThresh, mask, CmpTypes.LE);
            MatchFraction = Cv2.CountNonZero(mask) * 100.0 / (dEMap.Rows * dEMap.Cols);

            Mat dst;
            if (outMode == 0)
            {
                dst = mask.Clone();
            }
            else if (outMode == 2)
            {
                using Mat norm = new Mat();
                using Mat scaled = new Mat();
                Cv2.Normalize(dEMap, norm, 0, 255, NormTypes.MinMax);
                norm.ConvertTo(scaled, MatType.CV_8UC1);
                dst = new Mat();
                Cv2.ApplyColorMap(scaled, dst, ColormapTypes.Turbo);
                // 把取样框画在热图上，便于确认参考色取对了
                Cv2.Rectangle(dst, refRect, Scalar.White, 2);
            }
            else
            {
                dst = VisionHelper.ToBgrCopy(srcMat);
                using Mat dim = new Mat();
                dst.ConvertTo(dim, dst.Type(), 0.3, 0);
                dim.CopyTo(dst, mask);   // 相似区域压暗，差异区域保持原色
                Cv2.Rectangle(dst, refRect, Scalar.White, 2);
            }

            LastSummary = string.Format("色差ΔE分割: 参考 Lab({0},{1},{2})  ΔE≤{3:F1}  匹配 {4:F1}%",
                rl, ra, rb, dEThresh, MatchFraction);
            return dst;
        }

        /// <summary>
        /// CIEDE2000 色差（Sharma 实现，含 all fixes 的标准公式）。
        /// 用标准公式而非欧氏 Lab 距离：欧氏距离在饱和色区域与实际感知偏差可达数倍。
        /// 设为 public：ColorShading 等算子共用同一实现，避免两份拷贝各自演化导致
        /// 不同算子测出的 ΔE 不可比。
        /// </summary>
        public static double DeltaE2000(double L1, double a1, double b1,
                                        double L2, double a2, double b2)
        {
            const double kL = 1, kC = 1, kH = 1;
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cbar = (C1 + C2) / 2.0;
            double Cbar7 = Math.Pow(Cbar, 7);
            double G = 0.5 * (1 - Math.Sqrt(Cbar7 / (Cbar7 + 6103515625.0))); // 25^7 = 6103515625

            double a1p = (1 + G) * a1;
            double a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);

            double h1p = HueAngleDeg(b1, a1p);
            double h2p = HueAngleDeg(b2, a2p);

            double dLp = L2 - L1;
            double dCp = C2p - C1p;

            double dhp;
            if (C1p * C2p == 0) dhp = 0;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180) dhp -= 360;
                else if (dhp < -180) dhp += 360;
            }
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(Deg2Rad(dhp / 2.0));

            double Lbp = (L1 + L2) / 2.0;
            double Cbp = (C1p + C2p) / 2.0;

            double hbp;
            if (C1p * C2p == 0) hbp = h1p + h2p;
            else
            {
                double sum = h1p + h2p;
                double diff = Math.Abs(h1p - h2p);
                if (diff <= 180) hbp = sum / 2.0;
                else if (sum < 360) hbp = (sum + 360) / 2.0;
                else hbp = (sum - 360) / 2.0;
            }

            double T = 1
                - (0.17 * Math.Cos(Deg2Rad(hbp - 30)))
                + (0.24 * Math.Cos(Deg2Rad(2 * hbp)))
                + (0.32 * Math.Cos(Deg2Rad((3 * hbp) + 6)))
                - (0.20 * Math.Cos(Deg2Rad((4 * hbp) - 63)));

            double dTheta = 30 * Math.Exp(-Math.Pow((hbp - 275) / 25.0, 2));
            double Cbp7 = Math.Pow(Cbp, 7);
            double Rc = 2 * Math.Sqrt(Cbp7 / (Cbp7 + 6103515625.0));
            double Lm50 = (Lbp - 50) * (Lbp - 50);
            double Sl = 1 + ((0.015 * Lm50) / Math.Sqrt(20 + Lm50));
            double Sc = 1 + (0.045 * Cbp);
            double Sh = 1 + (0.015 * Cbp * T);
            double Rt = -Math.Sin(Deg2Rad(2 * dTheta)) * Rc;

            double tL = dLp / (kL * Sl);
            double tC = dCp / (kC * Sc);
            double tH = dHp / (kH * Sh);

            return Math.Sqrt((tL * tL) + (tC * tC) + (tH * tH) + (Rt * tC * tH));
        }

        private static double HueAngleDeg(double b, double ap)
        {
            if (b == 0 && ap == 0) return 0;
            double deg = Math.Atan2(b, ap) * 180.0 / Math.PI;
            return deg >= 0 ? deg : deg + 360;
        }

        private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    }
}
