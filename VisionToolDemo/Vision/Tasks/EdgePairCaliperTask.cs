using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 亚像素边缘对卡尺：沿一条扫描线找"一对边缘"并给出**亚像素**间距（宽度/缝隙/毛刺）。
    ///
    /// 与直线卡尺的区别：直线卡尺只报告最强的一个上升沿和一个下降沿，位置取到整像素，
    /// 且不保证这两个沿属于同一个目标。做宽度量测时这不够——需要：
    ///   · 按极性配对（亮带=升沿+降沿，暗带=降沿+升沿），而不是各找各的最强；
    ///   · 亚像素定位（抛物线拟合差分极值），整像素定位在 0.5px 量级上就顶死了；
    ///   · 沿扫描线做多次重复测量并取中位数，剔除单点噪声/毛刺造成的跳变。
    ///
    /// 量测线由 UI 层画线注入（真实像素坐标），与直线卡尺同一套交互。
    /// </summary>
    public class EdgePairCaliperTask : IVisionTask, IResultReporter, IStatefulTask, ILineCaliper
    {
        public string TaskName => "边缘对卡尺";

        public Point CaliperStart { get; private set; }
        public Point CaliperEnd { get; set; }
        public bool HasCaliper { get; private set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次测得的亚像素宽度（px）；未测到为 NaN</summary>
        public double LastWidth { get; private set; } = double.NaN;

        /// <summary>最近一次两侧亚像素边缘位置（沿扫描线，相对带中心，px）</summary>
        public double LastEdgeA { get; private set; } = double.NaN;

        public double LastEdgeB { get; private set; } = double.NaN;

        /// <summary>参与中位数的有效重复测量次数</summary>
        public int LastSamples { get; private set; }

        public void SetCaliper(Point start, Point end)
        {
            CaliperStart = start;
            CaliperEnd = end;
            HasCaliper = true;
        }

        public void ClearCaliper()
        {
            CaliperStart = new Point();
            CaliperEnd = new Point();
            HasCaliper = false;
        }

        public string SaveState()
        {
            return HasCaliper
                ? JsonConvert.SerializeObject(new[] { CaliperStart.X, CaliperStart.Y, CaliperEnd.X, CaliperEnd.Y })
                : null;
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p is { Length: 4 })
                    SetCaliper(new Point(p[0], p[1]), new Point(p[2], p[3]));
            }
            catch { /* 损坏状态忽略，保持未画卡尺 */ }
        }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "边缘阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 20,
                DisplayFormat = "阈值:{0}",
                Group = "卡尺"
            },
            new TaskParamDesc
            {
                // 垂直于扫描线方向取均值的半宽：越大越抗噪，但会糊掉细结构
                ParamName = "平滑半宽",
                Min = 0,
                Max = 300,
                DefaultValue = 30,
                DisplayFormat = "平滑:{0}px",
                Group = "卡尺"
            },
            new TaskParamDesc
            {
                // 目标极性：1 = 找亮带(升沿→降沿)，2 = 找暗带(降沿→升沿)，0 = 自动取对比最强的一对
                ParamName = "极性 0自动1亮带2暗带",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "pol:{0}",
                Group = "卡尺",
                Tip = "要测哪种带。0自动：取对比最强的一对边缘，不确定时先用它。" +
                      "1亮带：亮条/白线宽度。2暗带：暗线/缝隙/划痕宽度。"
            },
            new TaskParamDesc
            {
                // 沿扫描线方向做多少次重复测量（每条偏移一个像素），取中位数抗毛刺
                ParamName = "重复次数",
                Min = 1,
                Max = 51,
                DefaultValue = 7,
                DisplayFormat = "rep:{0}",
                ForceOdd = true,
                Group = "卡尺",
                Tip = "沿垂直方向重复测多少次取中位数。边缘毛糙/有毛刺时调大（7~15）更稳；" +
                      "目标很细时调小，避免重复线跨出目标。"
            }
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            LastWidth = double.NaN;
            LastEdgeA = double.NaN;
            LastEdgeB = double.NaN;
            LastSamples = 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (!HasCaliper)
                return dst;

            int edgeThresh = paramValues[0];
            int smoothHalf = paramValues[1];
            int polarity = paramValues[2];
            int repeats = Math.Max(1, paramValues[3]);
            if (repeats % 2 == 0) repeats++;

            int cx = (CaliperStart.X + CaliperEnd.X) / 2;
            int cy = (CaliperStart.Y + CaliperEnd.Y) / 2;
            double dx = CaliperEnd.X - CaliperStart.X;
            double dy = CaliperEnd.Y - CaliperStart.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 4)
            {
                LastSummary = "边缘对卡尺: 量测线太短（至少 4px）";
                return dst;
            }

            int halfLength = Math.Max(2, (int)(length / 2));
            double dirX = dx / length;
            double dirY = dy / length;
            double nX = -dirY, nY = dirX;   // 垂直于扫描线的方向

            using Mat gray = VisionHelper.ToGray(srcMat);

            // 沿"垂直方向"平移扫描线做重复测量：每条线独立找边缘对，最后取中位数
            var widths = new List<double>();
            var eA = new List<double>();
            var eB = new List<double>();
            int repHalf = repeats / 2;
            for (int r = -repHalf; r <= repHalf; r++)
            {
                if (TryMeasurePair(gray, cx, cy, dirX, dirY, nX, nY, halfLength, r,
                        smoothHalf, edgeThresh, polarity, out double a, out double b))
                {
                    widths.Add(b - a);
                    eA.Add(a);
                    eB.Add(b);
                }
            }

            if (widths.Count == 0)
            {
                LastSummary = "边缘对卡尺: 未检出成对边缘（调低阈值或改极性）";
                return dst;
            }

            widths.Sort();
            double medW = Median(widths);
            eA.Sort(); eB.Sort();
            double medA = Median(eA), medB = Median(eB);

            LastWidth = medW;
            LastEdgeA = medA;
            LastEdgeB = medB;
            LastSamples = widths.Count;

            // 结果图：画出两条亚像素边缘线 + 带中心连线
            DrawEdgeLine(dst, cx, cy, dirX, dirY, nX, nY, medA, Scalar.LimeGreen);
            DrawEdgeLine(dst, cx, cy, dirX, dirY, nX, nY, medB, Scalar.LimeGreen);
            Point pa = Offset(cx, cy, dirX, dirY, medA);
            Point pb = Offset(cx, cy, dirX, dirY, medB);
            Cv2.Line(dst, pa, pb, Scalar.Yellow, 1);
            Cv2.PutText(dst, medW.ToString("F2") + "px",
                new Point(Math.Min(pa.X, pb.X), Math.Min(pa.Y, pb.Y) - 6),
                HersheyFonts.HersheySimplex, 0.45, Scalar.Yellow, 1);

            double spread = widths[^1] - widths[0];
            LastSummary = string.Format("边缘对卡尺: 宽度 {0:F3}px  (重复 {1} 次, 极差 {2:F2}, 边缘 {3:F2}/{4:F2})",
                medW, LastSamples, spread, medA, medB);
            return dst;
        }

        /// <summary>
        /// 在一条（可沿垂直方向偏移 offset 像素的）扫描线上找一对边缘。
        /// 返回沿扫描线方向的亚像素位置 a/b（相对带中心）。
        /// </summary>
        private static bool TryMeasurePair(Mat gray, int cx, int cy, double dirX, double dirY,
            double nX, double nY, int halfLength, double offset, int smoothHalf, int edgeThresh,
            int polarity, out double a, out double b)
        {
            a = b = 0;

            double ox = cx + (offset * nX);
            double oy = cy + (offset * nY);

            int sampleCount = (halfLength * 2) + 1;
            double[] prof = new double[sampleCount];
            bool[] ok = new bool[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int t = i - halfLength;
                double px = ox + (t * dirX);
                double py = oy + (t * dirY);
                if (px < 1 || py < 1 || px >= gray.Cols - 2 || py >= gray.Rows - 2)
                    continue;

                double sum = 0;
                int hits = 0;
                for (int w = -smoothHalf; w <= smoothHalf; w++)
                {
                    double x = px + (w * nX);
                    double y = py + (w * nY);
                    if (x < 1 || y < 1 || x >= gray.Cols - 2 || y >= gray.Rows - 2)
                        continue;
                    sum += SampleBilinear(gray, x, y);
                    hits++;
                }
                if (hits > 0) { prof[i] = sum / hits; ok[i] = true; }
            }

            // 一阶差分 + 抛物线拟合求亚像素极值位置
            // grad[i] 代表 prof[i-1] -> prof[i] 之间的变化，其极值点位于 i-1 与 i 之间
            var rises = new List<(double pos, double mag)>();
            var falls = new List<(double pos, double mag)>();
            for (int i = 2; i < sampleCount - 1; i++)
            {
                if (!ok[i] || !ok[i - 1] || !ok[i + 1]) continue;
                double gPrev = prof[i - 1] - prof[i - 2];
                double gCur = prof[i] - prof[i - 1];
                double gNext = prof[i + 1] - prof[i];

                // 抛物线顶点偏移：sub = 0.5*(gPrev-gNext)/(gPrev-2gCur+gNext)
                double denom = gPrev - (2 * gCur) + gNext;
                if (Math.Abs(denom) < 1e-9) continue;
                double sub = 0.5 * (gPrev - gNext) / denom;
                if (sub < -1.0 || sub > 1.0) sub = 0;   // 非真正的极值，退回整像素

                double pos = (i - 1) + sub;              // 相对样本下标
                double peak = gCur - (0.25 * (gPrev - gNext) * sub);

                if (peak > 0) rises.Add((pos, peak));
                else if (peak < 0) falls.Add((pos, -peak));
            }

            if (rises.Count == 0 && falls.Count == 0)
                return false;

            // 按极性配对：亮带 = 升沿在前、降沿在后；暗带 = 降沿在前、升沿在后
            double bestScore = -1, bestA = 0, bestB = 0;
            void Consider(List<(double pos, double mag)> first, List<(double pos, double mag)> second)
            {
                foreach ((double p1, double m1) in first)
                {
                    if (m1 < edgeThresh) continue;
                    foreach ((double p2, double m2) in second)
                    {
                        if (m2 < edgeThresh) continue;
                        if (p2 <= p1) continue;
                        double score = Math.Min(m1, m2);   // 以较弱一侧为准，避免强边配弱边
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestA = p1;
                            bestB = p2;
                        }
                    }
                }
            }

            if (polarity == 1) Consider(rises, falls);          // 亮带
            else if (polarity == 2) Consider(falls, rises);     // 暗带
            else { Consider(rises, falls); Consider(falls, rises); }

            if (bestScore < 0)
                return false;

            a = bestA - halfLength;
            b = bestB - halfLength;
            return true;
        }

        private static double Median(List<double> sorted)
        {
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        private static Point Offset(int cx, int cy, double dirX, double dirY, double t)
            => new((int)Math.Round(cx + (t * dirX)), (int)Math.Round(cy + (t * dirY)));

        private static void DrawEdgeLine(Mat dst, int cx, int cy, double dirX, double dirY,
            double nX, double nY, double t, Scalar color)
        {
            double px = cx + (t * dirX);
            double py = cy + (t * dirY);
            int half = 12;
            var p1 = new Point((int)Math.Round(px - (half * nX)), (int)Math.Round(py - (half * nY)));
            var p2 = new Point((int)Math.Round(px + (half * nX)), (int)Math.Round(py + (half * nY)));
            Cv2.Line(dst, p1, p2, color, 1);
        }

        private static double SampleBilinear(Mat gray, double x, double y)
        {
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            double v00 = gray.Get<byte>(y0, x0);
            double v10 = gray.Get<byte>(y0, x0 + 1);
            double v01 = gray.Get<byte>(y0 + 1, x0);
            double v11 = gray.Get<byte>(y0 + 1, x0 + 1);
            return (((v00 * (1 - fx)) + (v10 * fx)) * (1 - fy))
                 + (((v01 * (1 - fx)) + (v11 * fx)) * fy);
        }
    }
}