using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// LCD 会聚 / 色差（分通道边缘位移）：分别定位 R、G、B 三通道的**同一条边缘**，
    /// 用它们的像素位移换算成角分（arcmin）或毫米偏差。
    ///
    /// 为什么需要它：LCD 面板的 RGB 子像素若没对齐，会出现彩色镶边（convergence error），
    /// 人眼在文字边缘特别敏感。这是面板/投影类产品的必测项。
    ///
    /// 算法：拆 R/G/B 三通道 → 各自找边缘的亚像素位置 → 以 G 为基准算 ΔR、ΔB →
    /// 按像素物理尺寸换算成角分（或直接输出像素/毫米偏差）。
    /// </summary>
    public class LcdConvergenceTask : IVisionTask, IResultReporter
    {
        public string TaskName => "LCD会聚色偏";

        public string LastSummary { get; private set; } = "";

        /// <summary>R 相对 G 的水平位移（亚像素，正=往右）</summary>
        public double ShiftR { get; private set; } = double.NaN;
        /// <summary>B 相对 G 的水平位移</summary>
        public double ShiftB { get; private set; } = double.NaN;
        /// <summary>总会聚误差（角分）- 取 R/B 位移绝对值最大者换算</summary>
        public double ErrorArcmin { get; private set; } = double.NaN;
        /// <summary>总会聚误差（像素）</summary>
        public double ErrorPixel { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "扫描行位置%",
                Min = 0, Max = 100, DefaultValue = 50,
                DisplayFormat = "row:{0}%",
                Group = "测量",
                Tip = "在画面这个高度取一条扫描行来分析（边缘应垂直穿过该行）。"
            },
            new TaskParamDesc
            {
                ParamName = "扫描行数",
                Min = 1, Max = 200, DefaultValue = 20,
                DisplayFormat = "rows:{0}",
                Group = "测量",
                Tip = "在该位置上下共取多少行求平均，抑噪用。调大更稳但要求边缘更竖直。"
            },
            new TaskParamDesc
            {
                ParamName = "边缘搜索范围%",
                Min = 1, Max = 100, DefaultValue = 60,
                DisplayFormat = "span:{0}%",
                Group = "测量",
                Tip = "沿扫描行搜索边缘的横向范围（相对图像宽度）。太大可能锁到别的边缘，太小可能找不到。"
            },
            new TaskParamDesc
            {
                // 参数框架只支持 int，故像素尺寸以"微米"为单位取整（0.1um 级精度对本项无意义）
                ParamName = "像素尺寸um",
                Min = 1, Max = 1000, DefaultValue = 100,
                DisplayFormat = "px:{0}um",
                Group = "换算",
                Tip = "一个像素对应的物理尺寸（微米）。仅用于把偏差换算成毫米；不影响像素/角分结果。"
            },
            new TaskParamDesc
            {
                ParamName = "观测距离mm",
                Min = 1, Max = 100000, DefaultValue = 500,
                DisplayFormat = "d:{0}mm",
                Group = "换算",
                Tip = "人眼到屏幕的观测距离。用于把像素偏差换算成角分（arcmin，衡量肉眼可见度）。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0剖面曲线1原图标注",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 画 R/G/B 三条边缘剖面曲线（看亚像素对齐最直观）；1 = 在原图上标注各通道边缘位置。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ShiftR = ShiftB = ErrorArcmin = ErrorPixel = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "LCD会聚: 输入为空";
                return srcMat?.Clone();
            }

            int rowPct = Math.Clamp(paramValues[0], 0, 100);
            int rowCount = Math.Max(1, paramValues[1]);
            int spanPct = Math.Clamp(paramValues[2], 1, 100);
            double pxUm = Math.Max(1, paramValues[3]);
            double distMm = Math.Max(1, paramValues[4]);
            int outMode = paramValues[5];

            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);
            Cv2.Split(bgr, out Mat[] ch);
            using Mat chB = ch[0], chG = ch[1], chR = ch[2];

            int w = bgr.Cols, h = bgr.Rows;
            int cy = Math.Clamp(h * rowPct / 100, 0, h - 1);
            int r0 = Math.Clamp(cy - rowCount / 2, 0, h - 1);
            int r1 = Math.Clamp(r0 + rowCount - 1, 0, h - 1);

            int span = Math.Max(8, w * spanPct / 100);
            int x0 = Math.Clamp(w / 2 - span / 2, 1, w - 2);
            int x1 = Math.Clamp(w / 2 + span / 2, x0 + 4, w - 2);

            // —— 三通道在同一搜索范围内找最强的竖直边缘（沿 x 的梯度极值） ——
            double eR = FindVerticalEdge(chR, r0, r1, x0, x1, out double ampR);
            double eG = FindVerticalEdge(chG, r0, r1, x0, x1, out double ampG);
            double eB = FindVerticalEdge(chB, r0, r1, x0, x1, out double ampB);

            if (double.IsNaN(eG) || ampG < 3)
            {
                LastSummary = "LCD会聚: 未找到有效边缘（该扫描行上对比度不足）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            // 位移以 G 为基准；正 = 该通道边缘在 G 右侧
            ShiftR = double.IsNaN(eR) ? double.NaN : eR - eG;
            ShiftB = double.IsNaN(eB) ? double.NaN : eB - eG;

            double worst = 0;
            if (!double.IsNaN(ShiftR) && Math.Abs(ShiftR) > Math.Abs(worst)) worst = ShiftR;
            if (!double.IsNaN(ShiftB) && Math.Abs(ShiftB) > Math.Abs(worst)) worst = ShiftB;
            ErrorPixel = worst;

            // 角分换算：1 像素对应角度 = atan(pxSize/dist)；1 度 = 60 arcmin
            double pxMm = pxUm / 1000.0;
            double angRad = Math.Atan2(pxMm, distMm);
            ErrorArcmin = Math.Abs(worst) * angRad * 180 / Math.PI * 60;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (outMode == 1)
            {
                // 三条边缘位置各画一条竖线（各通道用对应颜色）
                foreach (var (e, col, name) in new (double, Scalar, string)[]
                    { (eR, new Scalar(0, 0, 255), "R"), (eG, new Scalar(0, 255, 0), "G"), (eB, new Scalar(255, 0, 0), "B") })
                {
                    if (double.IsNaN(e)) continue;
                    int x = (int)Math.Round(e);
                    Cv2.Line(dst, new Point(x, r0), new Point(x, r1), col, 1);
                    Cv2.PutText(dst, name, new Point(x + 2, r0 - 4), HersheyFonts.HersheySimplex, 0.45, col, 1);
                }
                Cv2.PutText(dst, string.Format("dR={0:F2}px dB={1:F2}px  max={2:F2}px / {3:F2}arcmin",
                        ShiftR, ShiftB, ErrorPixel, ErrorArcmin),
                    new Point(8, 22), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            }
            else
            {
                // 三条通道的横向剖面曲线（叠加显示，看亚像素对齐）
                var canvas = new Mat(Math.Min(360, h), Math.Min(640, w), MatType.CV_8UC3, Scalar.Black);
                DrawProfiles(canvas, chR, chG, chB, (r0 + r1) / 2, x0, x1);
                dst.Dispose();
                dst = canvas;
            }

            LastSummary = string.Format("LCD会聚: ΔR={0:F2}px ΔB={1:F2}px 最大={2:F2}px ({3:F2} arcmin, {4:F3}mm)",
                ShiftR, ShiftB, ErrorPixel, ErrorArcmin, Math.Abs(ErrorPixel) * pxUm / 1000.0);
            return dst;
        }

        /// <summary>
        /// 在指定行区间内沿 x 找最陡的竖直边缘，返回亚像素位置（行间取中位数抗噪）。
        /// 用抛物线拟合梯度极值实现亚像素。
        /// </summary>
        private static double FindVerticalEdge(Mat ch, int r0, int r1, int x0, int x1, out double amp)
        {
            amp = 0;
            var positions = new System.Collections.Generic.List<double>();
            for (int y = r0; y <= r1; y++)
            {
                double best = 0; int bx = -1; bool bestRising = true;
                for (int x = x0; x < x1; x++)
                {
                    double g = ch.At<byte>(y, x + 1) - ch.At<byte>(y, x);
                    if (Math.Abs(g) > best) { best = Math.Abs(g); bx = x; bestRising = g > 0; }
                }
                if (bx < x0 || best < 3) continue;

                // 抛物线亚像素
                double g0 = ch.At<byte>(y, bx) - ch.At<byte>(y, bx - 1);
                double g1 = ch.At<byte>(y, bx + 1) - ch.At<byte>(y, bx);
                double g2 = ch.At<byte>(y, bx + 2) - ch.At<byte>(y, bx + 1);
                double denom = g0 - (2 * g1) + g2;
                double sub = Math.Abs(denom) > 1e-9 ? 0.5 * (g0 - g2) / denom : 0;
                if (sub < -1) sub = -1;
                if (sub > 1) sub = 1;
                positions.Add(bx + sub);
                if (best > amp) amp = best;
            }

            if (positions.Count == 0) return double.NaN;
            positions.Sort();
            return positions[positions.Count / 2];   // 中位数
        }

        /// <summary>画 R/G/B 三条横向剖面（同一行），直观展示亚像素对齐情况</summary>
        private static void DrawProfiles(Mat canvas, Mat chR, Mat chG, Mat chB, int row, int x0, int x1)
        {
            int pad = 30;
            int cw = canvas.Cols - 2 * pad, chh = canvas.Rows - 2 * pad;
            if (cw < 20 || chh < 20) return;

            void Plot(Mat ch, Scalar col)
            {
                Point prev = new(-1, -1);
                for (int x = x0; x < x1; x++)
                {
                    double v = ch.At<byte>(row, x) / 255.0;
                    int px = pad + (int)((x - x0) / (double)(x1 - x0) * cw);
                    int py = canvas.Rows - pad - (int)(v * chh);
                    var p = new Point(px, py);
                    if (prev.X >= 0) Cv2.Line(canvas, prev, p, col, 1);
                    prev = p;
                }
            }

            Cv2.Rectangle(canvas, new Rect(pad, pad, cw, chh), Scalar.Gray, 1);
            Plot(chR, new Scalar(0, 0, 255));
            Plot(chG, new Scalar(0, 255, 0));
            Plot(chB, new Scalar(255, 0, 0));
            MatDraw.DrawText(canvas, "R/G/B 剖面（横轴位置，纵轴灰度）", pad, pad - 10, Scalar.White, 11);
        }
    }
}
