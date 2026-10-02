using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 场曲热力图：在画面网格上**逐格测清晰度**，再插值成曲面，直观看出哪里离焦。
    ///
    /// 为什么需要它：镜头场曲会让"中心合焦时边缘已经虚了"。单点 SFR 只能回答
    /// "这一点清晰不清晰"，回答不了"全画面清晰度怎么分布" ——
    /// 本算子给出分布图与最差/最好格位置，直接指导对焦调整与镜头选型。
    ///
    /// 度量方式：每格用 Tenengrad（梯度能量）或 SFR 的 MTF50。
    /// SFR 更准确但要求每格都有斜边；Tenengrad 无此要求，适合任意纹理。
    /// </summary>
    public class FieldCurvatureTask : IVisionTask, IResultReporter
    {
        public string TaskName => "场曲热力图";

        public string LastSummary { get; private set; } = "";

        /// <summary>最差格的清晰度（相对最好的比例，%）</summary>
        public double WorstPercent { get; private set; } = double.NaN;
        public double BestScore { get; private set; } = double.NaN;
        public double MeanScore { get; private set; } = double.NaN;
        /// <summary>最差格位置（图像坐标）</summary>
        public Point WorstAt { get; private set; }
        /// <summary>最好格位置</summary>
        public Point BestAt { get; private set; }
        /// <summary>场曲不平度（标准差/均值，%），越大越"不平"</summary>
        public double FieldCurvaturePercent { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "网格列数",
                Min = 2, Max = 20, DefaultValue = 5,
                DisplayFormat = "cols:{0}",
                Group = "网格",
                Tip = "横向切多少格。格数越多空间分辨率越高，但每格样本少、噪声大（建议 5~9）。"
            },
            new TaskParamDesc
            {
                ParamName = "网格行数",
                Min = 2, Max = 20, DefaultValue = 4,
                DisplayFormat = "rows:{0}",
                Group = "网格",
                Tip = "纵向切多少格。应让每个格子都落在有纹理/有斜边的区域。"
            },
            new TaskParamDesc
            {
                ParamName = "度量 0梯度能量1SFR",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "metric:{0}",
                Group = "度量",
                Tip = "0 = Tenengrad 梯度能量（对任意纹理都可用，快）；" +
                      "1 = 逐格 SFR 的 MTF50（更准，但要求每格内都有斜边）。"
            },
            new TaskParamDesc
            {
                ParamName = "格内留边%",
                Min = 0, Max = 40, DefaultValue = 10,
                DisplayFormat = "margin:{0}%",
                Group = "网格",
                Tip = "每格向内收缩的比例，避开相邻格交界处的混叠与边缘效应。"
            },
            new TaskParamDesc
            {
                ParamName = "插值平滑",
                Min = 0, Max = 100, DefaultValue = 40,
                DisplayFormat = "smooth:{0}",
                Group = "输出",
                Tip = "热力图的插值平滑程度。调大更好看但会掩盖局部凹陷，做判读建议调小。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0热力图1数值标注",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 伪彩曲面热力图；1 = 原图上按格标注相对清晰度（%）与格框。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            WorstPercent = BestScore = MeanScore = FieldCurvaturePercent = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "场曲: 输入为空";
                return srcMat?.Clone();
            }

            int cols = Math.Clamp(paramValues[0], 2, 20);
            int rows = Math.Clamp(paramValues[1], 2, 20);
            int metric = paramValues[2];
            int marginPct = Math.Clamp(paramValues[3], 0, 40);
            int smooth = paramValues[4];
            int outMode = paramValues[5];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat grayF = new Mat();
            gray.ConvertTo(grayF, MatType.CV_32FC1);

            // 全局梯度能量图（Tenengrad），供按格统计
            using Mat gx = new Mat(), gy = new Mat();
            Cv2.Sobel(grayF, gx, MatType.CV_32FC1, 1, 0, 3);
            Cv2.Sobel(grayF, gy, MatType.CV_32FC1, 0, 1, 3);
            using Mat energy = new Mat();
            using Mat sx = new Mat(), sy = new Mat();
            Cv2.Multiply(gx, gx, sx);
        Cv2.Multiply(gy, gy, sy);
            Cv2.Add(sx, sy, energy);

            int w = gray.Cols, h = gray.Rows;
            int bw = Math.Max(8, w / cols), bh = Math.Max(8, h / rows);
            int mx = bw * marginPct / 100, my = bh * marginPct / 100;

            var score = new double[cols, rows];
            var valid = new bool[cols, rows];
            double best = -1; int bx = 0, by = 0;
            double worst = double.MaxValue; int wx = 0, wy = 0;
            double sum = 0; int cnt = 0;

            for (int cy = 0; cy < rows; cy++)
                for (int cx = 0; cx < cols; cx++)
                {
                    int x0 = Math.Clamp(cx * bw + mx, 0, w - 4);
                    int y0 = Math.Clamp(cy * bh + my, 0, h - 4);
                    int cw = Math.Min(bw - 2 * mx, w - x0);
                    int ch = Math.Min(bh - 2 * my, h - y0);
                    if (cw < 8 || ch < 8) { valid[cx, cy] = false; continue; }

                    double v;
                    if (metric == 1)
                    {
                        // 逐格 SFR：要求该格内有斜边；失败则退回梯度能量
                        using Mat roi = new Mat(gray, new Rect(x0, y0, cw, ch));
                        var sfr = MtfHelper.Compute(roi, 4, true);
                        // SfrResult 没有 Mtf50 属性，用 FreqAt(0.5) 取 MTF50（单位 cycles/pixel）
                        double m50 = sfr.Ok ? sfr.FreqAt(0.5) : double.NaN;
                        v = (sfr.Ok && !double.IsNaN(m50)) ? m50 : Tenengrad(energy, x0, y0, cw, ch);
                    }
                    else v = Tenengrad(energy, x0, y0, cw, ch);

                    score[cx, cy] = v;
                    valid[cx, cy] = true;
                    sum += v; cnt++;
                    if (v > best) { best = v; bx = cx; by = cy; }
                    if (v < worst) { worst = v; wx = cx; wy = cy; }
                }

            if (cnt == 0)
            {
                LastSummary = "场曲: 网格切分后没有有效格子（网格数过多或图像过小）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            MeanScore = sum / cnt;
            BestScore = best;
            WorstPercent = best > 1e-9 ? worst / best * 100.0 : double.NaN;

            // 场曲不平度 = 标准差/均值
            double var2 = 0;
            for (int cy = 0; cy < rows; cy++)
                for (int cx = 0; cx < cols; cx++)
                    if (valid[cx, cy]) var2 += (score[cx, cy] - MeanScore) * (score[cx, cy] - MeanScore);
            double std = Math.Sqrt(var2 / cnt);
            FieldCurvaturePercent = MeanScore > 1e-9 ? std / MeanScore * 100.0 : double.NaN;

            BestAt = new Point(bx * bw + bw / 2, by * bh + bh / 2);
            WorstAt = new Point(wx * bw + bw / 2, wy * bh + bh / 2);

            Mat dst;
            if (outMode == 0)
            {
                // 归一化到 0..255 后插值放大成曲面
                using Mat mini = new Mat(rows, cols, MatType.CV_8UC1, Scalar.Black);
                for (int cy = 0; cy < rows; cy++)
                    for (int cx = 0; cx < cols; cx++)
                    {
                        double nv = best > 1e-9 ? score[cx, cy] / best : 0;
                        mini.Set(cy, cx, (byte)Math.Clamp(nv * 255, 0, 255));
                    }
                using Mat big = new Mat();
                Cv2.Resize(mini, big, new Size(w, h), 0, 0, InterpolationFlags.Cubic);
                if (smooth > 0)
                {
                    int k = Math.Max(3, smooth / 5 * 2 + 1);
                    Cv2.GaussianBlur(big, big, new Size(0, 0), k);
                }
                dst = new Mat();
                Cv2.ApplyColorMap(big, dst, ColormapTypes.Jet);

                // 叠加网格，便于对应到画面位置
                for (int cx = 1; cx < cols; cx++)
                    Cv2.Line(dst, new Point(cx * bw, 0), new Point(cx * bw, h), new Scalar(60, 60, 60), 1);
                for (int cy = 1; cy < rows; cy++)
                    Cv2.Line(dst, new Point(0, cy * bh), new Point(w, cy * bh), new Scalar(60, 60, 60), 1);

                // 最差格用红框圈出（对焦/选型时最关心的位置）
                var wr = new Rect(wx * bw, wy * bh, Math.Min(bw, w - wx * bw), Math.Min(bh, h - wy * bh));
                Cv2.Rectangle(dst, wr, Scalar.White, 2);
                MatDraw.DrawText(dst, string.Format("最差 {0:F0}%  (均值{1:F0}%)", WorstPercent, 100.0), 8, 22, Scalar.White, 13);
            }
            else
            {
                dst = VisionHelper.ToBgrCopy(srcMat);
                for (int cy = 0; cy < rows; cy++)
                    for (int cx = 0; cx < cols; cx++)
                    {
                        if (!valid[cx, cy]) continue;
                        int x0 = cx * bw, y0 = cy * bh;
                        int cw = Math.Min(bw, w - x0), ch = Math.Min(bh, h - y0);
                        double pct = best > 1e-9 ? score[cx, cy] / best * 100.0 : 0;
                        var col = pct > 80 ? Scalar.Lime : pct > 50 ? Scalar.Yellow : Scalar.Red;
                        Cv2.Rectangle(dst, new Rect(x0, y0, cw, ch), col, 1);
                        Cv2.PutText(dst, pct.ToString("F0") + "%", new Point(x0 + 4, y0 + 18),
                            HersheyFonts.HersheySimplex, 0.45, col, 1);
                    }
                MatDraw.DrawText(dst, string.Format("最差格 {0:F0}%  不平度 {1:F1}%", WorstPercent, FieldCurvaturePercent),
                    8, h - 10, Scalar.Yellow, 13);
            }

            LastSummary = string.Format("场曲: {0}x{1} 格  最差{2:F0}% 最好{3:F0}  不平度{4:F1}%  最差位置({5},{6})",
                cols, rows, WorstPercent, BestScore, FieldCurvaturePercent, WorstAt.X, WorstAt.Y);
            return dst;
        }

        /// <summary>格内 Tenengrad 平均值（梯度能量）</summary>
        private static double Tenengrad(Mat energy, int x0, int y0, int w, int h)
        {
            using Mat roi = new Mat(energy, new Rect(x0, y0, w, h));
            return Cv2.Mean(roi).Val0;
        }
    }
}
