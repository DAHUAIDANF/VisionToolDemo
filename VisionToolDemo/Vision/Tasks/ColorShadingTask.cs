using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Color Shading（色彩阴影/色偏）：测量画面**中心到边缘的颜色漂移**。
    ///
    /// 与"色差ΔE分割"的区别：那个是分割工具（找同色区域），本算子是**量测工具** ——
    /// 回答"同一白色物体在中心和四角拍出来偏多少"，这是模组厂 Color Shading 的必测项。
    ///
    /// 输出：
    ///   · 分块 R/G、B/G 比值图（中心归一化），以及各自的最大偏移
    ///   · 角部与中心的 ΔE（CIELAB 感知色差），比单纯比 R/G 更贴近人眼
    ///   · 最偏色方位与偏移量，便于定位是哪个角的 shading 超标
    /// </summary>
    public class ColorShadingTask : IVisionTask, IResultReporter
    {
        public string TaskName => "ColorShading色偏";

        public string LastSummary { get; private set; } = "";

        /// <summary>角部相对中心的最大 ΔE（CIEDE2000）</summary>
        public double MaxDeltaE { get; private set; } = double.NaN;
        /// <summary>R/G 比值的最大偏移（%），相对中心</summary>
        public double MaxRgShift { get; private set; } = double.NaN;
        /// <summary>B/G 比值的最大偏移（%）</summary>
        public double MaxBgShift { get; private set; } = double.NaN;
        /// <summary>最偏色的方位（度，0=右，逆时针）</summary>
        public double WorstDirection { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "分块数",
                Min = 1, Max = 32, DefaultValue = 8,
                DisplayFormat = "grid:{0}",
                Group = "分析",
                Tip = "把画面切成 NxN 块统计。块数越多越能看出局部色偏，但每块样本少、噪声大。"
            },
            new TaskParamDesc
            {
                ParamName = "中心区比例%",
                Min = 5, Max = 60, DefaultValue = 20,
                DisplayFormat = "center:{0}%",
                Group = "分析",
                Tip = "以画面中心该比例范围内的平均色作为归一化基准。"
            },
            new TaskParamDesc
            {
                ParamName = "大核平滑",
                Min = 1, Max = 201, DefaultValue = 21,
                DisplayFormat = "blur:{0}",
                ForceOdd = true,
                Group = "分析",
                Tip = "统计前平滑，去掉被测物纹理与传感器噪声，只留色彩趋势（建议开）。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0比值图1色偏热图2ΔE标注",
                Min = 0, Max = 2, DefaultValue = 2,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 分块 R/G、B/G 数值；1 = ΔE 伪彩热图；2 = 原图上标注各块 ΔE（最直观）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            MaxDeltaE = MaxRgShift = MaxBgShift = WorstDirection = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "ColorShading: 输入为空";
                return srcMat?.Clone();
            }

            int grid = Math.Clamp(paramValues[0], 1, 32);
            int centerPct = Math.Clamp(paramValues[1], 5, 60);
            int blurK = Math.Max(1, paramValues[2]);
            int outMode = paramValues[3];

            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);
            using Mat work = new Mat();
            if (blurK >= 3)
            {
                int k = blurK % 2 == 0 ? blurK + 1 : blurK;
                Cv2.Blur(bgr, work, new Size(k, k));
            }
            else bgr.CopyTo(work);

            // 转 Lab（8 位 Lab：L 0..255, a/b 偏移 128）
            using Mat lab = new Mat();
            Cv2.CvtColor(work, lab, ColorConversionCodes.BGR2Lab);

            int w = work.Cols, h = work.Rows;
            int bw = Math.Max(1, w / grid), bh = Math.Max(1, h / grid);

            // —— 中心基准：取画面中心 centerPct 区域 ——
            int cw = Math.Max(1, w * centerPct / 100), ch = Math.Max(1, h * centerPct / 100);
            var cRect = new Rect((w - cw) / 2, (h - ch) / 2, cw, ch);
            using (Mat cRoi = new Mat(work, cRect))
            {
                Scalar cm = Cv2.Mean(cRoi);
                _centerB = cm.Val0; _centerG = cm.Val1; _centerR = cm.Val2;
            }
            using (Mat cLab = new Mat(lab, cRect))
            {
                Scalar cl = Cv2.Mean(cLab);
                _centerL = cl.Val0; _centerA = cl.Val1; _centerB2 = cl.Val2;
            }

            // —— 分块统计 ——
            double maxDE = 0, maxRg = 0, maxBg = 0, worstAng = double.NaN;
            var blockDe = new double[grid, grid];
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                {
                    var r = new Rect(gx * bw, gy * bh, Math.Min(bw, w - gx * bw), Math.Min(bh, h - gy * bh));
                    if (r.Width < 2 || r.Height < 2) continue;

                    using Mat roi = new Mat(work, r);
                    Scalar m = Cv2.Mean(roi);
                    using Mat lroi = new Mat(lab, r);
                    Scalar lm = Cv2.Mean(lroi);

                    double de = DeltaE2000(_centerL, _centerA, _centerB2, lm.Val0, lm.Val1, lm.Val2);
                    blockDe[gx, gy] = de;

                    if (_centerR > 1e-6 && _centerG > 1e-6 && _centerB > 1e-6)
                    {
                        double rg = (m.Val2 / m.Val1) / (_centerR / _centerG) - 1.0;
                        double bg = (m.Val0 / m.Val1) / (_centerB / _centerG) - 1.0;
                        // 以画面中心为原点的方位
                        double dx = (gx + 0.5) * bw - w / 2.0;
                        double dy = h / 2.0 - (gy + 0.5) * bh;
                        if (de >= maxDE)
                        {
                            maxDE = de;
                            worstAng = Math.Atan2(dy, dx) * 180 / Math.PI;
                        }
                        if (Math.Abs(rg) * 100 > maxRg) maxRg = Math.Abs(rg) * 100;
                        if (Math.Abs(bg) * 100 > maxBg) maxBg = Math.Abs(bg) * 100;
                    }
                }

            MaxDeltaE = maxDE;
            MaxRgShift = maxRg;
            MaxBgShift = maxBg;
            WorstDirection = worstAng;

            // —— 输出 ——
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (outMode == 0)
            {
                var canvas = new Mat(Math.Min(420, h), Math.Min(560, w), MatType.CV_8UC3, Scalar.Black);
                Cv2.PutText(canvas, "block dE (CIEDE2000 vs center)", new Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1);
                for (int gy = 0; gy < grid; gy++)
                    for (int gx = 0; gx < grid; gx++)
                    {
                        int px = 10 + (gx * (canvas.Cols - 20) / grid);
                        int py = 34 + (gy * (canvas.Rows - 44) / grid);
                        double de = blockDe[gx, gy];
                        var col = de < 2 ? Scalar.Lime : de < 4 ? Scalar.Yellow : Scalar.Red;
                        Cv2.Rectangle(canvas, new Rect(px, py, (canvas.Cols - 20) / grid - 2,
                            (canvas.Rows - 44) / grid - 2), col, -1);
                        Cv2.PutText(canvas, de.ToString("F1"), new Point(px + 2, py + 14),
                            HersheyFonts.HersheySimplex, 0.32, Scalar.Black, 1);
                    }
                dst.Dispose();
                dst = canvas;
            }
            else if (outMode == 1)
            {
                using Mat deMap = new Mat(h, w, MatType.CV_8UC1, Scalar.Black);
                for (int gy = 0; gy < grid; gy++)
                    for (int gx = 0; gx < grid; gx++)
                    {
                        var r = new Rect(gx * bw, gy * bh, Math.Min(bw, w - gx * bw), Math.Min(bh, h - gy * bh));
                        if (r.Width < 1 || r.Height < 1) continue;
                        // ΔE 0..10 映射到 0..255
                        byte v = (byte)Math.Clamp(blockDe[gx, gy] / 10.0 * 255.0, 0, 255);
                        using Mat roi = new Mat(deMap, r);
                        roi.SetTo(new Scalar(v));
                    }
                using Mat blurM = new Mat();
                Cv2.GaussianBlur(deMap, blurM, new Size(0, 0), 8);   // 插值成平滑曲面更好看
                dst.Dispose();
                dst = new Mat();
                Cv2.ApplyColorMap(blurM, dst, ColormapTypes.Turbo);
                Cv2.PutText(dst, string.Format("dE max {0:F2}", MaxDeltaE), new Point(8, 24),
                    HersheyFonts.HersheySimplex, 0.55, Scalar.White, 1);
            }
            else
            {
                for (int gy = 0; gy < grid; gy++)
                    for (int gx = 0; gx < grid; gx++)
                    {
                        var r = new Rect(gx * bw, gy * bh, Math.Min(bw, w - gx * bw), Math.Min(bh, h - gy * bh));
                        if (r.Width < 8 || r.Height < 8) continue;
                        double de = blockDe[gx, gy];
                        var col = de < 2 ? Scalar.Lime : de < 4 ? Scalar.Yellow : Scalar.Red;
                        Cv2.Rectangle(dst, r, col, 1);
                        Cv2.PutText(dst, de.ToString("F1"), new Point(r.X + 3, r.Y + 15),
                            HersheyFonts.HersheySimplex, 0.4, col, 1);
                    }
                Cv2.PutText(dst, string.Format("dE max {0:F2} @ {1:F0}deg  R/G {2:F1}%  B/G {3:F1}%",
                        MaxDeltaE, WorstDirection, MaxRgShift, MaxBgShift),
                    new Point(8, 22), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            }

            LastSummary = string.Format("ColorShading: 最大ΔE={0:F2} @ {1:F0}°  R/G偏移={2:F1}%  B/G偏移={3:F1}%",
                MaxDeltaE, WorstDirection, MaxRgShift, MaxBgShift);
            return dst;
        }

        private double _centerB, _centerG, _centerR;
        private double _centerL, _centerA, _centerB2;

        /// <summary>
        /// CIEDE2000（与色差ΔE分割算子同一实现口径，保证两算子测出的 ΔE 可比）。
        /// 注意：这里的输入是 OpenCV 8 位 Lab 编码，需换算 L 到 0..100、a/b 去偏移。
        /// </summary>
        private static double DeltaE2000(double L1_8, double a1_8, double b1_8,
                                         double L2_8, double a2_8, double b2_8)
        {
            double L1 = L1_8 * 100.0 / 255.0, a1 = a1_8 - 128.0, b1 = b1_8 - 128.0;
            double L2 = L2_8 * 100.0 / 255.0, a2 = a2_8 - 128.0, b2 = b2_8 - 128.0;
            return ColorDeltaETask.DeltaE2000(L1, a1, b1, L2, a2, b2);
        }
    }
}
