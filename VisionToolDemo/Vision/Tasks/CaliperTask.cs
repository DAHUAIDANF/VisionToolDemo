using System;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 卡尺工具：沿一条带方向的扫描线提取灰度剖面，检测剖面中的最强边缘并标注。
    /// 卡尺的位置/角度/长度由 UI 层在原图上画线注入（真实像素坐标）；
    /// 可调参数：边缘阈值、检测带宽（垂直于扫描方向的采样范围）。
    /// 显示分工：扫描带矩形（黄）+ 方向箭头（青）由 UI 层叠加在原图上；
    /// 本任务在结果图上只标注找到的边缘线（绿升/橙降）。
    /// </summary>
    public class CaliperTask : IVisionTask, IResultReporter, IStatefulTask, ILineCaliper
    {
        public string TaskName => "直线卡尺";

        /// <summary>卡尺线段真实像素坐标（UI层画线后赋值），OpenCvSharp 坐标系</summary>
        public Point CaliperStart { get; private set; }

        public Point CaliperEnd { get; set; }
        public bool HasCaliper { get; private set; }

        /// <summary>最近一次 Execute 的结果摘要（找到的边缘位置，未画卡尺时为空）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>UI层画线后调用，同时设置起止点并标记卡尺有效</summary>
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

        /// <summary>状态 = 卡尺线段坐标 [sx, sy, ex, ey]（未画卡尺时不保存）</summary>
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
            catch
            {
                // 状态内容损坏时忽略，保持未画卡尺
            }
        }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "边缘阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 30,
                DisplayFormat = "阈值:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "检测带宽",
                Min = 1,
                Max = 600,
                DefaultValue = 100,
                DisplayFormat = "带宽:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // VisionMaster 卡尺的"边缘极性"：只找指定方向的跳变，避免亮线暗线上都出沿
                ParamName = "边缘极性 0任意1亮到暗2暗到亮",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "极性:{0}",
                Tip = "亮到暗=灰度从亮变暗的边（白→黑）；暗到亮=灰度从暗变亮的边（黑→白）。" +
                      "0任意：两条都找；测亮线上边缘用1，测暗线边缘用2。"
            }
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int edgeThresh = paramValues[0];
            int bandHalf = Math.Max(1, paramValues[1] / 2);
            // 边缘极性（老链 Values 可能没有第 3 个参数，缺省=任意）
            int polar = paramValues.Length > 2 ? paramValues[2] : 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            LastSummary = "";
            if (!HasCaliper)
                return dst; // 未画卡尺时只返回原图

            int cx = (CaliperStart.X + CaliperEnd.X) / 2;
            int cy = (CaliperStart.Y + CaliperEnd.Y) / 2;
            double dx = CaliperEnd.X - CaliperStart.X;
            double dy = CaliperEnd.Y - CaliperStart.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            int halfLength = Math.Max(1, (int)(length / 2));
            double dirX = dx / length;
            double dirY = dy / length;
            // 垂直方向（扫描带的宽度方向）
            double nX = -dirY;
            double nY = dirX;

            // 沿扫描带采样：长度方向步进 + 带宽方向取均值，抑制噪声（双线性插值取亚像素）
            int sampleCount = (int)length;
            double[] profile = new double[sampleCount];
            bool[] valid = new bool[sampleCount];
            using Mat gray = VisionHelper.ToGray(srcMat);
            for (int i = 0; i < sampleCount; i++)
            {
                int t = i - halfLength;
                double px = cx + (t * dirX);
                double py = cy + (t * dirY);
                if (px < 0 || py < 0 || px >= gray.Cols - 1 || py >= gray.Rows - 1)
                    continue;

                double sum = 0;
                int hits = 0;
                for (int w = -bandHalf; w <= bandHalf; w++)
                {
                    double x = px + (w * nX);
                    double y = py + (w * nY);
                    if (x < 0 || y < 0 || x >= gray.Cols - 1 || y >= gray.Rows - 1)
                        continue;
                    sum += SampleBilinear(gray, x, y);
                    hits++;
                }
                if (hits > 0)
                {
                    profile[i] = sum / hits;
                    valid[i] = true;
                }
            }

            // 一阶差分求梯度，只在连续有效段内找最强上升/下降沿
            // （VisionMaster 卡尺逻辑：按"边缘极性"只收指定方向的沿；任意=两条都找）
            int bestRise = -1, bestFall = -1;
            double maxGrad = 0, minGrad = 0;
            for (int i = 1; i < sampleCount; i++)
            {
                if (!valid[i] || !valid[i - 1]) continue;
                double grad = profile[i] - profile[i - 1];
                if (polar == 2 && grad <= 0) continue;   // 只要暗到亮：忽略下降
                if (polar == 1 && grad >= 0) continue;   // 只要亮到暗：忽略上升
                if (grad > maxGrad) { maxGrad = grad; bestRise = i; }
                if (grad < minGrad) { minGrad = grad; bestFall = i; }
            }

            bool hasRise = polar != 1 && bestRise > 0 && maxGrad >= edgeThresh;
            bool hasFall = polar != 2 && bestFall > 0 && -minGrad >= edgeThresh;

            // 亚像素定位：对最强梯度峰做抛物线内插（与圆卡尺同一套公式），
            // 边缘位置精度从整像素提升到约 0.1px，量测才有意义。
            double risePos = hasRise ? Subpixel(profile, sampleCount, bestRise, maxGrad) : 0;
            double fallPos = hasFall ? Subpixel(profile, sampleCount, bestFall, minGrad) : 0;

            // —— 绘制：只在结果图上标注找到的边缘线 ——
            if (hasRise)
                DrawEdgeLine(dst, cx, cy, dirX, dirY, nX, nY, halfLength, bandHalf, risePos, Scalar.LimeGreen);
            if (hasFall)
                DrawEdgeLine(dst, cx, cy, dirX, dirY, nX, nY, halfLength, bandHalf, fallPos, Scalar.Orange);

            // 摘要：边缘位置相对带中心的偏移（px，亚像素一位小数）
            if (hasRise || hasFall)
            {
                string riseTxt = hasRise ? $"{risePos - halfLength:F1}px" : "—";
                string fallTxt = hasFall ? $"{fallPos - halfLength:F1}px" : "—";
                string polarTxt = polar == 1 ? " 亮到暗" : polar == 2 ? " 暗到亮" : "";
                LastSummary = $"直线卡尺{polarTxt}: 上升沿 {riseTxt} 下降沿 {fallTxt}";
            }
            else
            {
                LastSummary = "直线卡尺: 未检出边缘（可调低阈值或加大带宽）";
            }

            return dst;
        }

        /// <summary>在结果图上画找到的边缘线：垂直于扫描方向、横贯扫描带的线段（位置可亚像素）</summary>
        private static void DrawEdgeLine(Mat dst, int cx, int cy, double dirX, double dirY, double nX, double nY, int halfLength, int bandHalf, double idx, Scalar color)
        {
            double t = idx - halfLength;
            double ex = cx + (t * dirX);
            double ey = cy + (t * dirY);
            Point p1 = new((int)(ex + (nX * bandHalf)), (int)(ey + (nY * bandHalf)));
            Point p2 = new((int)(ex - (nX * bandHalf)), (int)(ey - (nY * bandHalf)));
            Cv2.Line(dst, p1, p2, color, 2, LineTypes.AntiAlias);
        }

        /// <summary>梯度峰抛物线内插求亚像素位置（差分域三点的抛物顶点）</summary>
        private static double Subpixel(double[] profile, int count, int idx, double peakGrad)
        {
            double g0 = idx - 1 >= 1 ? profile[idx - 1] - profile[idx - 2] : peakGrad;
            double g2 = idx + 1 < count ? profile[idx + 1] - profile[idx] : peakGrad;
            double denom = g2 - (2 * peakGrad) + g0;
            double delta = Math.Abs(denom) > 1e-9 ? 0.5 + ((peakGrad - g2) / denom) : 0;
            if (delta < -0.5) delta = -0.5;
            if (delta > 0.5) delta = 0.5;
            return idx + delta - 0.5;
        }

        /// <summary>双线性插值采样，获得亚像素灰度值</summary>
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