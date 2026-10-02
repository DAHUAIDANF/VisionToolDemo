using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 圆度：对取样点做最小二乘圆拟合，输出**最小包容环带宽度**（ISO 1101 半径法）：
    /// 所有点到圆心距离的最大值 − 最小值。同时给出半径、圆度/半径比、RMS 与偏心。
    ///
    /// 圆度用"半径带宽度"而不是拟合残差 RMS：RMS 会把单向的椭圆化误差
    /// 摊到整个圆周上，读数虚低；公差判定必须用实际包容带。
    /// </summary>
    public class RoundnessTask : GdtTaskBase
    {
        public override string TaskName => "圆度";

        /// <summary>圆度（最小包容环带宽度，px）</summary>
        public double RoundnessValue { get; private set; } = double.NaN;

        /// <summary>拟合半径（px）</summary>
        public double Radius { get; private set; } = double.NaN;

        public double RadiusMin { get; private set; } = double.NaN;
        public double RadiusMax { get; private set; } = double.NaN;
        public double Rms { get; private set; } = double.NaN;

        /// <summary>圆心</summary>
        public GeometryFit.P2 Center { get; private set; }

        /// <summary>圆度 / 半径（‰）：归一化后才能横向比较不同直径的圆</summary>
        public double RoundnessRatio { get; private set; } = double.NaN;

        /// <summary>是否已由用户在图上手动点过点（≥3）；true 时 Execute 直接用手动点评估圆度</summary>
        public bool HasManualPoints { get; private set; }
        public List<GeometryFit.P2> ManualPoints { get; } = new();
        public void SetManualPoints(IEnumerable<GeometryFit.P2> pts)
        { ManualPoints.Clear(); ManualPoints.AddRange(pts); HasManualPoints = true; }
        public void ClearManualPoints() { HasManualPoints = false; ManualPoints.Clear(); }

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "取样方式 0轮廓1边缘", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "取样:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "阈值", Min = 1, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "取样", Tip = PolarityTip },
            new TaskParamDesc { ParamName = "扫描步长", Min = 1, Max = 50, DefaultValue = 1,
                DisplayFormat = "步长:{0}px", Group = "取样" },
            new TaskParamDesc { ParamName = "最大轮廓数", Min = 1, Max = 20, DefaultValue = 1,
                DisplayFormat = "轮廓:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积:{0}%", Group = "取样" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            RoundnessValue = Radius = RadiusMin = RadiusMax = Rms = RoundnessRatio = double.NaN;
            LastPointCount = 0;
            Center = default;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            MmPerPixel = paramValues[6] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            // 手动模式：直接用用户在图上点的点评估圆度（自动取样对不上真实轮廓时用）
            List<GeometryFit.P2> pts = HasManualPoints
                ? new List<GeometryFit.P2>(ManualPoints)
                : SamplePoints(gray, paramValues[0], paramValues[1],
                    paramValues[2], paramValues[3], paramValues[4], paramValues[5]);
            LastPointCount = pts.Count;

            if (pts.Count < 3)
            {
                LastSummary = "圆度: 有效点不足（" + pts.Count + " 个，至少 3 个）";
                return dst;
            }
            if (!GeometryFit.FitCircle(pts, out GeometryFit.Circle2 c))
            {
                LastSummary = "圆度: 点集退化，无法拟合圆";
                return dst;
            }
            if (!GeometryFit.Roundness(pts, c, out double rnd, out double rMin, out double rMax))
            {
                LastSummary = "圆度: 包容带计算失败";
                return dst;
            }

            Radius = c.R;
            RadiusMin = rMin;
            RadiusMax = rMax;
            RoundnessValue = rnd;
            Center = new GeometryFit.P2(c.Cx, c.Cy);
            RoundnessRatio = c.R > 1e-9 ? 1000.0 * rnd / c.R : double.NaN;

            double sumSq = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = c.RadialDeviation(pts[i].X, pts[i].Y);
                sumSq += d * d;
            }
            Rms = Math.Sqrt(sumSq / pts.Count);

            // 结果图：拟合圆（绿）、最小/最大包容圆（青/品红）、圆心十字
            var cen = new Point((int)Math.Round(c.Cx), (int)Math.Round(c.Cy));
            Cv2.Circle(dst, cen, (int)Math.Round(c.R), Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            Cv2.Circle(dst, cen, (int)Math.Round(rMin), Scalar.Yellow, 1, LineTypes.AntiAlias);
            Cv2.Circle(dst, cen, (int)Math.Round(rMax), Scalar.Magenta, 1, LineTypes.AntiAlias);
            DrawCross(dst, c.Cx, c.Cy, Scalar.Red, 8);
            DrawPoints(dst, pts, new Scalar(90, 90, 90));
            DrawText(dst, string.Format("圆度 {0}", Unit(RoundnessValue)), c.Cx + 0, c.Cy - c.R - 10, Scalar.Yellow);
            DrawText(dst, string.Format("R {0}", Unit(Radius)), 6, 20, Scalar.LimeGreen);

            LastSummary = string.Format(
                "{0}: {1}  (R {2}, 半径带 {3:F2}~{4:F2}, 比值 {5:F2}‰, RMS {6:F3}px, 点数 {7})",
                HasManualPoints ? "圆度(手动)" : "圆度",
                Unit(RoundnessValue), Unit(Radius), rMin, rMax, RoundnessRatio, Rms, pts.Count);
            return dst;
        }
    }
}
