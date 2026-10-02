using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// RI / Shading（相对照度与亮度均匀性）：测量画面中心到边缘的亮度衰减，拟合 cos⁴ 律，
    /// 并生成 LSC（镜头阴影校正）增益图。
    ///
    /// 为什么需要它：镜头暗角会让**同一物体在画面不同位置亮度不同** —— 边缘偏暗会导致
    /// 阈值判定、缺陷检出、颜色判定系统性偏移。RI 是镜头/模组的必测项；得到 gain map 后
    /// 可反向补偿，让全画面响应一致。
    ///
    /// 输出：
    ///   · RI 曲线：按"像高"分 bin 的归一化亮度（中心=100%）
    ///   · 角部 RI（画面四角相对中心的比例，行业常看这个值）
    ///   · cos⁴ 拟合：实测衰减与理想 cos⁴ 的偏离，判断是几何暗角还是额外遮挡
    ///   · LSC gain map：逐像素增益（= 目标亮度 / 实测亮度），可直接用于校正
    /// </summary>
    public class RiShadingTask : IVisionTask, IResultReporter
    {
        public string TaskName => "RI照度均匀性";

        public string LastSummary { get; private set; } = "";

        /// <summary>中心归一化亮度（通常取中心区均值为 100%）</summary>
        public double CenterLevel { get; private set; } = double.NaN;
        /// <summary>四角平均 RI（相对中心，%）</summary>
        public double CornerRi { get; private set; } = double.NaN;
        /// <summary>四边中点平均 RI（%）</summary>
        public double EdgeRi { get; private set; } = double.NaN;
        /// <summary>实测衰减与 cos⁴ 理论的最大偏离（%）</summary>
        public double Cos4Deviation { get; private set; } = double.NaN;

        /// <summary>LSC 增益图（CV_32FC1），供外部保存/复用</summary>
        public Mat GainMap { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "分环数",
                Min = 2, Max = 64, DefaultValue = 16,
                DisplayFormat = "bins:{0}",
                Group = "分析",
                Tip = "把画面按'像高'（到中心的距离）分成多少个环来统计。环数越多曲线越细，但每环样本越少、噪声越大。"
            },
            new TaskParamDesc
            {
                ParamName = "大核平滑",
                Min = 1, Max = 301, DefaultValue = 51,
                DisplayFormat = "blur:{0}",
                ForceOdd = true,
                Group = "分析",
                Tip = "统计前先大核模糊，抹掉被测物纹理，只留照明/暗角趋势（这是关键：不平滑会把图案当成照度）。"
            },
            new TaskParamDesc
            {
                ParamName = "中心区半径%",
                Min = 1, Max = 50, DefaultValue = 10,
                DisplayFormat = "c:{0}%",
                Group = "分析",
                Tip = "把该比例半径内的平均亮度作为 100% 基准（归一化参考）。"
            },
            new TaskParamDesc
            {
                ParamName = "FOV度",
                Min = 5, Max = 180, DefaultValue = 60,
                DisplayFormat = "fov:{0}°",
                Group = "分析",
                Tip = "镜头视场角，用于 cos⁴ 理论拟合。可先用「畸变FOV标定」算子测出再填。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0曲线1增益图2校正后",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = RI 曲线（含 cos⁴ 理论对比）；1 = LSC 增益热力图；2 = 用增益图校正后的图像。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            CenterLevel = CornerRi = EdgeRi = Cos4Deviation = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "RI照度: 输入为空";
                return srcMat?.Clone();
            }

            int bins = Math.Clamp(paramValues[0], 2, 64);
            int blurK = Math.Max(1, paramValues[1]);
            int centerPct = Math.Clamp(paramValues[2], 1, 50);
            double fovDeg = Math.Clamp(paramValues[3], 5, 180);
            int outMode = paramValues[4];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat work = new Mat();
            gray.ConvertTo(work, MatType.CV_32FC1);

            if (blurK >= 3)
            {
                int k = blurK % 2 == 0 ? blurK + 1 : blurK;
                Cv2.Blur(work, work, new Size(k, k));
            }

            int w = work.Cols, h = work.Rows;
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
            double maxR = Math.Sqrt((cx * cx) + (cy * cy));    // 最远角点像高

            // —— 1. 按像高分环统计平均亮度 ——
            var sum = new double[bins];
            var cnt = new long[bins];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double r = Math.Sqrt((dx * dx) + (dy * dy)) / maxR;   // 归一化像高 0..1
                    int b = (int)(r * bins);
                    if (b >= bins) b = bins - 1;
                    sum[b] += work.At<float>(y, x);
                    cnt[b]++;
                }

            var ri = new double[bins];
            for (int i = 0; i < bins; i++)
                ri[i] = cnt[i] > 0 ? sum[i] / cnt[i] : double.NaN;

            // —— 2. 中心基准 ——
            double centerR = centerPct / 100.0;
            double cSum = 0; long cCnt = 0;
            for (int i = 0; i < bins; i++)
            {
                double rMid = (i + 0.5) / bins;
                if (rMid > centerR) break;
                if (cnt[i] > 0) { cSum += sum[i]; cCnt += cnt[i]; }
            }
            CenterLevel = cCnt > 0 ? cSum / cCnt : (ri[0]);
            if (CenterLevel < 1e-6)
            {
                LastSummary = "RI照度: 图像过暗（中心亮度≈0），无法归一化";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            // —— 3. RI 曲线（%）与四角/四边 ——
            for (int i = 0; i < bins; i++)
                if (!double.IsNaN(ri[i])) ri[i] = ri[i] / CenterLevel * 100.0;

            CornerRi = SampleAt(ri, bins, 1.0);          // 归一化像高 1.0 = 角
            EdgeRi = SampleAt(ri, bins, 0.85);

            // —— 4. cos⁴ 理论拟合 ——
            // 半视场角、归一化像高 r 处的入射角 θ 满足 tanθ = r * tan(半FOV)
            double halfFov = fovDeg / 2 * Math.PI / 180.0;
            double tanHalf = Math.Tan(halfFov);
            double maxDev = 0;
            for (int i = 0; i < bins; i++)
            {
                if (double.IsNaN(ri[i])) continue;
                double r = (i + 0.5) / bins;
                double theta = Math.Atan(r * tanHalf);
                double cos4 = Math.Pow(Math.Cos(theta), 4) * 100.0;
                double dev = ri[i] - cos4;
                if (Math.Abs(dev) > Math.Abs(maxDev)) maxDev = dev;
            }
            Cos4Deviation = maxDev;

            // —— 5. 输出 ——
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (outMode == 0)
            {
                var canvas = new Mat(Math.Min(360, h), Math.Min(560, w), MatType.CV_8UC3, Scalar.Black);
                DrawRiCurve(canvas, ri, bins, fovDeg, tanHalf);
                dst.Dispose();
                dst = canvas;
            }
            else
            {
                // 生成 LSC gain map：gain = 中心亮度 / 当前像素亮度（模糊场，避免逐像素噪声）
                using Mat field = new Mat();
                if (blurK >= 3)
                {
                    int k = blurK % 2 == 0 ? blurK + 1 : blurK;
                    Cv2.Blur(work, field, new Size(k, k));
                }
                else work.CopyTo(field);

                Mat gain = new Mat(field.Size(), MatType.CV_32FC1);
                for (int y = 0; y < field.Rows; y++)
                    for (int x = 0; x < field.Cols; x++)
                    {
                        float v = field.At<float>(y, x);
                        gain.Set(y, x, v > 1e-3f ? (float)(CenterLevel / v) : 1f);
                    }
                // 限制最大增益，避免暗区把噪声放大成雪花
                Cv2.Min(gain, new Scalar(4.0), gain);

                GainMap?.Dispose();
                GainMap = gain.Clone();

                if (outMode == 2)
                {
                    // 用增益图校正原图；gain 是单通道，必须扩成 3 通道再乘，
                    // 否则 Cv2.Multiply 抛 "array op array 尺寸/通道不匹配"（选"校正后"即崩）
                    using Mat srcF = new Mat();
                    srcMat.ConvertTo(srcF, MatType.CV_32FC3);
                    using Mat gain3 = new Mat();
                    Cv2.CvtColor(gain, gain3, ColorConversionCodes.GRAY2BGR);
                    using Mat dstF = new Mat();
                    Cv2.Multiply(srcF, gain3, dstF);
                    dst.Dispose();
                    dst = new Mat();
                    dstF.ConvertTo(dst, MatType.CV_8UC3);
                }
                else
                {
                    // 增益热力图（1.0 处绿，越大越红）
                    using Mat norm = new Mat();
                    Cv2.Normalize(gain, norm, 0, 255, NormTypes.MinMax);
                    using Mat g8 = new Mat();
                    norm.ConvertTo(g8, MatType.CV_8UC1);
                    dst.Dispose();
                    dst = new Mat();
                    Cv2.ApplyColorMap(g8, dst, ColormapTypes.Turbo);
                    Cv2.PutText(dst, string.Format("LSC gain 1.0..{0:F2}x", gain.At<float>(
                        Math.Min(field.Rows - 1, field.Rows / 2), Math.Min(field.Cols - 1, field.Cols / 2)) ),
                        new Point(8, 24), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
                }
            }

            LastSummary = string.Format("RI照度: 角部RI={0:F1}% 边缘RI={1:F1}% 中心={2:F0} cos⁴偏离={3:F1}%",
                CornerRi, EdgeRi, CenterLevel, Cos4Deviation);
            return dst;
        }

        /// <summary>按归一化像高取 RI 曲线的插值值</summary>
        private static double SampleAt(double[] ri, int bins, double rNorm)
        {
            double pos = rNorm * bins - 0.5;
            int i0 = (int)Math.Floor(pos);
            int i1 = i0 + 1;
            if (i0 < 0) i0 = 0;
            if (i1 >= bins) i1 = bins - 1;
            if (double.IsNaN(ri[i0])) return double.NaN;
            if (double.IsNaN(ri[i1])) return ri[i0];
            double t = pos - Math.Floor(pos);
            return ri[i0] * (1 - t) + ri[i1] * t;
        }

        /// <summary>画 RI 曲线 + cos⁴ 理论曲线</summary>
        private static void DrawRiCurve(Mat canvas, double[] ri, int bins, double fovDeg, double tanHalf)
        {
            int pad = 40;
            int w = canvas.Cols, h = canvas.Rows;
            Cv2.Rectangle(canvas, new Rect(pad, pad, w - 2 * pad, h - 2 * pad), Scalar.Gray, 1);

            // 纵轴 0..110%
            double yMax = 110;
            Point prev = new(-1, -1), prevCos = new(-1, -1);
            for (int i = 0; i < bins; i++)
            {
                if (double.IsNaN(ri[i])) continue;
                double r = (i + 0.5) / bins;
                int px = pad + (int)(r * (w - 2 * pad));
                int py = h - pad - (int)(Math.Clamp(ri[i], 0, yMax) / yMax * (h - 2 * pad));
                var p = new Point(px, py);
                if (prev.X >= 0) Cv2.Line(canvas, prev, p, Scalar.Lime, 2);
                prev = p;

                double theta = Math.Atan(r * tanHalf);
                double cos4 = Math.Pow(Math.Cos(theta), 4) * 100.0;
                int py2 = h - pad - (int)(Math.Clamp(cos4, 0, yMax) / yMax * (h - 2 * pad));
                var p2 = new Point(px, py2);
                if (prevCos.X >= 0) Cv2.Line(canvas, prevCos, p2, Scalar.Orange, 1);
                prevCos = p2;
            }

            for (int pctVal = 0; pctVal <= 100; pctVal += 25)
            {
                int py = h - pad - (int)(pctVal / yMax * (h - 2 * pad));
                Cv2.Line(canvas, new Point(pad, py), new Point(w - pad, py), new Scalar(60, 60, 60), 1);
                Cv2.PutText(canvas, pctVal + "%", new Point(6, py + 4), HersheyFonts.HersheySimplex, 0.35, Scalar.Gray, 1);
            }
            MatDraw.DrawText(canvas, "绿=实测RI  橙=cos^4理论  横轴=归一化像高",
                pad, pad - 12, Scalar.White, 11);
            Cv2.PutText(canvas, "0", new Point(pad - 6, h - pad + 16), HersheyFonts.HersheySimplex, 0.4, Scalar.Gray, 1);
            Cv2.PutText(canvas, "1 (corner)", new Point(w - pad - 62, h - pad + 16),
                HersheyFonts.HersheySimplex, 0.4, Scalar.Gray, 1);
        }
    }
}
