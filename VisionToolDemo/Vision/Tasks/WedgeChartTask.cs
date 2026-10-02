using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 楔形 / 星图调制度（MTF 的对比度法测量）。
    ///
    /// 与斜边 SFR 的区别：SFR 只测**一个方向**（斜边法线方向）的 MTF，需要摆正斜边；
    /// 楔形/星图是**径向频率连续变化**的靶标，一次拍摄就能覆盖多个频率；
    /// 星图还能同时测**各方向**的 MTF（看是否有方向性虚化/像散）。
    ///
    /// 算法：定向滤波（filter2D）提取条纹 → 沿垂直于条纹方向取灰度剖面 →
    /// 正弦拟合求振幅 → 调制度 = 振幅/局部均值（即 Michelson 对比度）→ 归一化到低频参考。
    /// </summary>
    public class WedgeChartTask : IVisionTask, IResultReporter
    {
        public string TaskName => "楔形星图调制度";

        public string LastSummary { get; private set; } = "";

        /// <summary>各角度测得的最低调制度（各向 MTF 的最差值，反映最虚的方向）</summary>
        public double WorstModulation { get; private set; } = double.NaN;
        /// <summary>各角度测得调制度的平均值</summary>
        public double MeanModulation { get; private set; } = double.NaN;
        /// <summary>方向性差异（max-min），越大说明越像散</summary>
        public double Anisotropy { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "分析圆心X%",
                Min = 0, Max = 100, DefaultValue = 50,
                DisplayFormat = "cx:{0}%",
                Group = "定位",
                Tip = "星图/楔形图案的中心（按图像百分比给出，避免写死像素）。"
            },
            new TaskParamDesc
            {
                ParamName = "分析圆心Y%",
                Min = 0, Max = 100, DefaultValue = 50,
                DisplayFormat = "cy:{0}%",
                Group = "定位"
            },
            new TaskParamDesc
            {
                ParamName = "分析半径",
                Min = 10, Max = 2000, DefaultValue = 120,
                DisplayFormat = "r:{0}px",
                Group = "定位",
                Tip = "从圆心向外分析的半径。应落在星图有效扇区内，不要越到背景。"
            },
            new TaskParamDesc
            {
                ParamName = "方向数",
                Min = 4, Max = 36, DefaultValue = 12,
                DisplayFormat = "dirs:{0}",
                Group = "测量",
                Tip = "沿多少个角度分别测调制度。选 12 可得每 15° 一条曲线的各向 MTF；" +
                      "只关心单一方向时可设为 4 或更少。"
            },
            new TaskParamDesc
            {
                ParamName = "条纹周期",
                Min = 2, Max = 100, DefaultValue = 12,
                DisplayFormat = "p:{0}px",
                Group = "测量",
                Tip = "靶标在该半径处的条纹周期（像素）。楔形是变周期的，取该半径处的标称值。"
            },
            new TaskParamDesc
            {
                ParamName = "剖面长度",
                Min = 16, Max = 400, DefaultValue = 96,
                DisplayFormat = "len:{0}px",
                Group = "测量",
                Tip = "用于正弦拟合的剖面长度。至少覆盖 2~3 个条纹周期，太短拟合不稳。"
            },
            new TaskParamDesc
            {
                ParamName = "背景去除核",
                Min = 0, Max = 151, DefaultValue = 31,
                DisplayFormat = "bg:{0}",
                ForceOdd = true,
                Group = "测量",
                Tip = "先减去大核背景，消除照明不均对振幅的影响。设 0 表示不去背景。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0原图标注1各向曲线",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 在原图上标出各方向与调制度数值；1 = 画各向调制度极坐标图。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            WorstModulation = MeanModulation = Anisotropy = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "楔形星图: 输入为空";
                return srcMat?.Clone();
            }

            int cxPct = paramValues[0], cyPct = paramValues[1];
            int radius = paramValues[2];
            int dirCount = Math.Clamp(paramValues[3], 4, 36);
            int period = Math.Max(2, paramValues[4]);
            int profLen = Math.Max(16, paramValues[5]);
            int bgK = paramValues[6];
            int outMode = paramValues[7];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat work = new Mat();
            if (bgK >= 3)
            {
                int k = bgK % 2 == 0 ? bgK + 1 : bgK;
                using Mat bg = new Mat();
                Cv2.MedianBlur(gray, bg, k);
                // 原图 - 背景 + 128：去掉照明不均，保留条纹起伏
                Cv2.AddWeighted(gray, 1.0, bg, -1.0, 128, work);
            }
            else gray.CopyTo(work);

            int cx = Math.Clamp(gray.Cols * cxPct / 100, 0, gray.Cols - 1);
            int cy = Math.Clamp(gray.Rows * cyPct / 100, 0, gray.Rows - 1);

            int rInner = Math.Max(4, radius / 4);      // 从离圆心一段距离开始，避开圆心奇点
            var mods = new double[dirCount];
            var angles = new double[dirCount];

            for (int d = 0; d < dirCount; d++)
            {
                double ang = Math.PI * d / dirCount;
                angles[d] = ang * 180 / Math.PI;

                // 沿该方向的径向剖面（取该方向上的条纹，拟合其振幅）
                mods[d] = MeasureAlongDirection(work, cx, cy, ang, rInner, radius, period, profLen);
            }

            // 归一化：以最大调制度为参考（理想靶标低频处调制度接近 1）
            double maxMod = 0, sum = 0; int valid = 0;
            foreach (double m in mods)
                if (!double.IsNaN(m)) { if (m > maxMod) maxMod = m; sum += m; valid++; }

            if (valid == 0)
            {
                LastSummary = "楔形星图: 所有方向均无法拟合（检查圆心/半径/条纹周期）";
                return VisionHelper.ToBgrCopy(srcMat);
            }
            if (maxMod < 1e-6)
            {
                LastSummary = "楔形星图: 调制度过低，未检出条纹（检查条纹周期参数或靶标位置）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            double minMod = double.MaxValue;
            for (int i = 0; i < dirCount; i++)
            {
                if (double.IsNaN(mods[i])) continue;
                mods[i] /= maxMod;                  // 归一化
                if (mods[i] < minMod) minMod = mods[i];
            }
            MeanModulation = sum / maxMod / valid;
            WorstModulation = minMod;
            Anisotropy = 1.0 - minMod;              // 0 = 各向同性

            // —— 绘制 ——
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (outMode == 0)
            {
                Cv2.Circle(dst, new Point(cx, cy), radius, Scalar.Yellow, 1);
                Cv2.Circle(dst, new Point(cx, cy), 3, Scalar.Cyan, -1);
                for (int d = 0; d < dirCount; d++)
                {
                    if (double.IsNaN(mods[d])) continue;
                    double ang = angles[d] * Math.PI / 180;
                    double ex = cx + (radius * Math.Cos(ang));
                    double ey = cy + (radius * Math.Sin(ang));
                    // 调制度越高线越长（直观显示各向差异）
                    double len = radius * Math.Clamp(mods[d], 0, 1);
                    double px = cx + (len * Math.Cos(ang));
                    double py = cy + (len * Math.Sin(ang));
                    var col = mods[d] < 0.5 ? Scalar.Red : Scalar.Lime;
                    Cv2.Line(dst, new Point(cx, cy), new Point((int)px, (int)py), col, 2);
                    Cv2.Line(dst, new Point((int)px, (int)py), new Point((int)ex, (int)ey), new Scalar(80, 80, 80), 1);
                }
                MatDraw.DrawText(dst, string.Format("调制最差{0:F2} 均值{1:F2} 各向差异{2:F2}",
                    WorstModulation, MeanModulation, Anisotropy), 8, 24, Scalar.Yellow, 13);
            }
            else
            {
                DrawPolarPlot(dst, angles, mods);
            }

            LastSummary = string.Format("楔形星图: 最差调制度={0:F3} 均值={1:F3} 各向差异={2:F3} ({3}方向)",
                WorstModulation, MeanModulation, Anisotropy, dirCount);
            return dst;
        }

        /// <summary>
        /// 沿方向 ang 在半径 rInner..rOuter 之间取一段垂直于条纹的剖面，正弦拟合求振幅。
        /// 返回原始调制度（未归一化），失败返回 NaN。
        /// </summary>
        private static double MeasureAlongDirection(Mat work, int cx, int cy, double ang,
            int rInner, int rOuter, int period, int profLen)
        {
            // 径向中点作为采样中心
            double rMid = (rInner + rOuter) / 2.0;
            double px = cx + (rMid * Math.Cos(ang));
            double py = cy + (rMid * Math.Sin(ang));

            // 垂直于径向的方向 = 条纹排列方向（楔形/星图的条纹沿径向呈扇形，
            // 局部近似垂直于半径方向）
            double tx = -Math.Sin(ang);
            double ty = Math.Cos(ang);

            int n = profLen;
            var prof = new double[n];
            var ok = new bool[n];
            for (int i = 0; i < n; i++)
            {
                double t = i - (n - 1) / 2.0;
                double x = px + (t * tx);
                double y = py + (t * ty);
                if (x < 1 || y < 1 || x >= work.Cols - 1 || y >= work.Rows - 1) continue;
                prof[i] = MtfHelper.Bilinear(work, x, y);
                ok[i] = true;
            }

            int cnt = 0; foreach (bool vb in ok) if (vb) cnt++;
            if (cnt < n * 0.7) return double.NaN;

            // 正弦拟合：y = a*sin(2πf t) + b*cos(2πf t) + c
            // 频率 f 由条纹周期给出；用最小二乘解 a,b,c
            double f = 1.0 / period;
            double Sw = 0, Ss = 0, Sc = 0;
            double Sss = 0, Scc = 0, Ssc = 0, Sws = 0, Swc = 0, Sw1 = 0;
            for (int i = 0; i < n; i++)
            {
                if (!ok[i]) continue;
                double t = i - (n - 1) / 2.0;
                double w = 2 * Math.PI * f * t;
                double sn = Math.Sin(w), cs = Math.Cos(w);
                double y = prof[i];
                Sw += y;
                Ss += sn; Sc += cs;
                Sss += sn * sn; Scc += cs * cs; Ssc += sn * cs;
                Sws += y * sn; Swc += y * cs; Sw1 += 1;
            }
            if (Sw1 < 3) return double.NaN;

            // 只在 (a,b) 上解最小二乘，c 由均值给出（去均值后拟合更稳）
            double meanY = Sw / Sw1;
            double a11 = Sss - (Ss * Ss / Sw1);
            double a12 = Ssc - (Ss * Sc / Sw1);
            double a22 = Scc - (Sc * Sc / Sw1);
            double b1 = Sws - (Ss * Sw / Sw1);
            double b2 = Swc - (Sc * Sw / Sw1);
            double det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) < 1e-9) return double.NaN;

            double a = ((b1 * a22) - (b2 * a12)) / det;   // sin 系数
            double b = ((a11 * b2) - (a12 * b1)) / det;   // cos 系数
            double amp = Math.Sqrt((a * a) + (b * b));

            // Michelson 调制度 = 振幅 / 均值
            if (Math.Abs(meanY) < 1e-6) return double.NaN;
            return amp / Math.Abs(meanY);
        }

        /// <summary>各向调制度的极坐标图</summary>
        private static void DrawPolarPlot(Mat dst, double[] angles, double[] mods)
        {
            int cx = dst.Cols / 2, cy = dst.Rows / 2;
            int R = (int)(Math.Min(dst.Cols, dst.Rows) * 0.4);
            if (R < 20) return;

            Cv2.Circle(dst, new Point(cx, cy), R, Scalar.Gray, 1);
            Cv2.Circle(dst, new Point(cx, cy), R / 2, new Scalar(70, 70, 70), 1);
            Cv2.PutText(dst, "1.0", new Point(cx + 4, cy - R + 12), HersheyFonts.HersheySimplex, 0.35, Scalar.Gray, 1);
            Cv2.PutText(dst, "0.5", new Point(cx + 4, cy - R / 2 + 12), HersheyFonts.HersheySimplex, 0.35, Scalar.Gray, 1);

            Point prev = new(-1, -1);
            for (int i = 0; i <= angles.Length; i++)
            {
                int idx = i % angles.Length;
                double m = double.IsNaN(mods[idx]) ? 0 : Math.Clamp(mods[idx], 0, 1);
                double ang = angles[idx] * Math.PI / 180;
                var p = new Point(cx + (int)(R * m * Math.Cos(ang)), cy + (int)(R * m * Math.Sin(ang)));
                if (prev.X >= 0) Cv2.Line(dst, prev, p, Scalar.Lime, 2);
                prev = p;
            }
            MatDraw.DrawText(dst, "各向调制度(归一化)", 8, 22, Scalar.Yellow, 13);
        }
    }
}
