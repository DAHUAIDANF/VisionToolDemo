using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 直线度：对取样点做正交最小二乘直线拟合，输出**包容带宽度**（ISO 1101 半径法）：
    /// 所有点到拟合直线垂距的最大值 ×2。同时给出 RMS 与最大偏差发生位置。
    ///
    /// 与"直线拟合"算子的区别：直线拟合只给出一条线的参数（角度/截距），
    /// 不做公差评价；本算子回答的是"这条边到底直不直、直多少"。
    /// </summary>
    public class StraightnessTask : GdtTaskBase
    {
        public override string TaskName => "直线度";

        /// <summary>直线度（包容带宽度，px）</summary>
        public double StraightnessValue { get; private set; } = double.NaN;

        /// <summary>残差 RMS（px）</summary>
        public double Rms { get; private set; } = double.NaN;

        /// <summary>拟合直线方向角（度，[0,180)）</summary>
        public double AngleDeg { get; private set; } = double.NaN;

        /// <summary>最大偏差发生位置</summary>
        public GeometryFit.P2 WorstAt { get; private set; }

        /// <summary>单位长度内的直线度（px/mm）：长度归一化后才能横向比较不同长度的边</summary>
        public double StraightnessPerMm { get; private set; } = double.NaN;

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "取样方式 0轮廓1边缘", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "取样:{0}", Group = "取样", Tip = "0二值轮廓：先阈值化再取轮廓，适合干净边界。" +
                "1亚像素边缘：用灰度梯度+抛物线拟合定位，精度到 0.1px，适合尺寸量测。" },
            new TaskParamDesc { ParamName = "阈值", Min = 1, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "取样", Tip = PolarityTip },
            new TaskParamDesc { ParamName = "扫描步长", Min = 1, Max = 50, DefaultValue = 1,
                DisplayFormat = "步长:{0}px", Group = "取样", Tip = "每隔多少像素取一个采样点。" +
                "调大能加速但会漏掉局部波动，边缘干净时用 1~3。" },
            new TaskParamDesc { ParamName = "最大轮廓数", Min = 1, Max = 20, DefaultValue = 1,
                DisplayFormat = "轮廓:{0}", Group = "取样", Tip = "二值路径下取面积最大的前几个轮廓。" +
                "边缘被噪声断成几段时调大（2~5）能把断口接上。" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积:{0}%", Group = "取样", Tip = "过滤小于图面此比例的轮廓，用于排除噪声小块。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算", Tip = "每个像素代表多少微米。" +
                "设为 0 只输出像素单位；设了则同时输出 mm 和 px/mm 归一化直线度。" },
        ];

        /// <summary>是否已由用户在图上手动点过点（≥2）；true 时 Execute 直接用手动点评估直线度</summary>
        public bool HasManualPoints { get; private set; }
        public List<GeometryFit.P2> ManualPoints { get; } = new();
        public void SetManualPoints(IEnumerable<GeometryFit.P2> pts)
        { ManualPoints.Clear(); ManualPoints.AddRange(pts); HasManualPoints = true; }
        public void ClearManualPoints() { HasManualPoints = false; ManualPoints.Clear(); }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            StraightnessValue = Rms = AngleDeg = StraightnessPerMm = double.NaN;
            LastPointCount = 0;
            WorstAt = default;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            MmPerPixel = paramValues[6] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            // 手动模式：直接用用户在图上点的点评估直线度（自动取样对不上真实边时用）
            List<GeometryFit.P2> pts = HasManualPoints
                ? new List<GeometryFit.P2>(ManualPoints)
                : SamplePoints(gray, paramValues[0], paramValues[1],
                    paramValues[2], paramValues[3], paramValues[4], paramValues[5]);
            LastPointCount = pts.Count;

            if (pts.Count < 2)
            {
                LastSummary = "直线度: 有效点不足（" + pts.Count + " 个），调低阈值或改用另一种取样方式";
                return dst;
            }

            if (!GeometryFit.FitLine(pts, out GeometryFit.Line2 line))
            {
                LastSummary = "直线度: 点集退化（所有点重合），无法拟合直线";
                return dst;
            }

            double maxAbs = 0, sumSq = 0;
            double wx = 0, wy = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = line.SignedDistance(pts[i].X, pts[i].Y);
                double ad = Math.Abs(d);
                if (ad > maxAbs) { maxAbs = ad; wx = pts[i].X; wy = pts[i].Y; WorstAt = pts[i]; }
                sumSq += d * d;
            }
            StraightnessValue = maxAbs * 2.0;
            Rms = Math.Sqrt(sumSq / pts.Count);
            AngleDeg = line.AngleDeg;

            // 沿拟合方向的投影长度 -> 归一化直线度
            double tmin = double.MaxValue, tmax = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double t = ((pts[i].X - line.Px) * line.Dx) + ((pts[i].Y - line.Py) * line.Dy);
                if (t < tmin) tmin = t;
                if (t > tmax) tmax = t;
            }
            double spanPx = tmax - tmin;
            if (spanPx > 1e-6)
            {
                StraightnessPerMm = MmPerPixel > 0
                    ? StraightnessValue / (spanPx * MmPerPixel)
                    : StraightnessValue / spanPx;
            }

            DrawPoints(dst, pts, new Scalar(90, 90, 90));
            DrawFittedLine(dst, line, Scalar.Orange);
            DrawCross(dst, wx, wy, Scalar.Red, 8);
            DrawText(dst, string.Format("直线度 {0}", Unit(StraightnessValue)), wx + 10, wy - 8, Scalar.Yellow);
            DrawText(dst, string.Format("长度 {0:F1}px 角度 {1:F2}deg", spanPx, AngleDeg), 6, 20, Scalar.LimeGreen);

            LastSummary = string.Format(
                "{0}: {1}  (RMS {2:F3}px, 角度 {3:F2}deg, 长度 {4:F1}px, 点数 {5}{6})",
                HasManualPoints ? "直线度(手动)" : "直线度",
                Unit(StraightnessValue), Rms, AngleDeg, spanPx, pts.Count,
                MmPerPixel > 0 ? string.Format(", 归一化 {0:F4}px/mm", StraightnessPerMm) : "");
            return dst;
        }
    }
}
