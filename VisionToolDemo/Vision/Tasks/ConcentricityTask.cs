using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 同心度：找出内外两个圆并各自做最小二乘拟合，输出两圆心的偏移量（同心度误差）
    /// 与径向壁厚均匀性（最大/最小壁厚差）。
    ///
    /// 内外圆的区分靠**面积排序**：面积最大的轮廓是外圆，次大的若是内孔（面积明显小）
    /// 则视为内圆。这样不依赖"内圆比外圆亮还是暗"——实际工件两种都有
    /// （镜筒内壁反光 vs 内孔发黑），靠灰度极性判断会翻车。
    /// </summary>
    public class ConcentricityTask : GdtTaskBase
    {
        public override string TaskName => "同心度";

        /// <summary>同心度误差（两圆心距离，px）</summary>
        public double Offset { get; private set; } = double.NaN;

        /// <summary>偏移方向（度）</summary>
        public double OffsetAngle { get; private set; } = double.NaN;

        public GeometryFit.P2 CenterOuter { get; private set; }
        public GeometryFit.P2 CenterInner { get; private set; }

        public double RadiusOuter { get; private set; } = double.NaN;
        public double RadiusInner { get; private set; } = double.NaN;

        /// <summary>最大/最小壁厚（px）：沿内圆逐点量到外圆的径向距离</summary>
        public double WallMax { get; private set; } = double.NaN;
        public double WallMin { get; private set; } = double.NaN;

        /// <summary>壁厚差（最大−最小，px）：反映壁厚不均/椭圆化</summary>
        public double WallVariation { get; private set; } = double.NaN;

        /// <summary>同心度 / 外半径（‰）</summary>
        public double OffsetRatio { get; private set; } = double.NaN;

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "极性:{0}", Group = "取样", Tip = "工件整体是亮还是暗。" +
                "内外圆的明暗关系**不影响**结果，只看外轮廓。" },
            new TaskParamDesc { ParamName = "内圆判定比%", Min = 5, Max = 95, DefaultValue = 90,
                DisplayFormat = "内圆<{0}%", Group = "取样", Tip = "次大轮廓面积小于最大轮廓的此比例时，" +
                "才认作内圆。默认 90%：同心环的内圆面积（外径²−内径²）本来就更小。" +
                "如果工件是实心圆没有内孔，本算子会报“未找到内圆”。" },
            new TaskParamDesc { ParamName = "壁厚采样数", Min = 8, Max = 720, DefaultValue = 180,
                DisplayFormat = "采样:{0}", Group = "取样", Tip = "沿内圆周取多少个方向量壁厚。" +
                "调大更细但更慢，180 对应每 2 度一个采样。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        /// <summary>是否已由用户在图上手动指定两个圆（外圆 + 内圆）；true 时 Execute 直接用手动圆</summary>
        public bool HasManualCircles { get; private set; }
        public GeometryFit.Circle2 ManualOuter { get; private set; }
        public GeometryFit.Circle2 ManualInner { get; private set; }
        public void SetManualCircles(GeometryFit.Circle2 outer, GeometryFit.Circle2 inner)
        { ManualOuter = outer; ManualInner = inner; HasManualCircles = true; }
        public void ClearManualCircles() { HasManualCircles = false; }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Offset = OffsetAngle = RadiusOuter = RadiusInner = double.NaN;
            WallMax = WallMin = WallVariation = OffsetRatio = double.NaN;
            CenterOuter = CenterInner = default;
            LastPointCount = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            MmPerPixel = paramValues[4] / 1000.0;

            // —— 手动模式：用户分别指定了外圆和内圆（圆心+半径），直接评估同心度 ——
            if (HasManualCircles)
            {
                RadiusOuter = ManualOuter.R;
                RadiusInner = ManualInner.R;
                CenterOuter = new GeometryFit.P2(ManualOuter.Cx, ManualOuter.Cy);
                CenterInner = new GeometryFit.P2(ManualInner.Cx, ManualInner.Cy);
                double mox = ManualInner.Cx - ManualOuter.Cx, moy = ManualInner.Cy - ManualOuter.Cy;
                Offset = Math.Sqrt((mox * mox) + (moy * moy));
                OffsetAngle = Math.Atan2(moy, mox) * 180.0 / Math.PI;
                OffsetRatio = ManualOuter.R > 1e-9 ? 1000.0 * Offset / ManualOuter.R : double.NaN;

                int mwallSamples = Math.Max(8, paramValues.Length > 3 ? paramValues[3] : 180);
                WallMax = double.MinValue; WallMin = double.MaxValue;
                for (int s = 0; s < mwallSamples; s++)
                {
                    double ang = 2 * Math.PI * s / mwallSamples;
                    double ux = Math.Cos(ang), uy = Math.Sin(ang);
                    double dx = ManualInner.Cx - ManualOuter.Cx, dy = ManualInner.Cy - ManualOuter.Cy;
                    double b = 2 * ((ux * dx) + (uy * dy));
                    double cc = (dx * dx) + (dy * dy) - (ManualOuter.R * ManualOuter.R);
                    double disc = (b * b) - (4 * cc);
                    if (disc < 0) continue;
                    double t = (-b + Math.Sqrt(disc)) / 2.0;
                    if (t <= 0) continue;
                    double wall = t - ManualInner.R;
                    if (wall > WallMax) WallMax = wall;
                    if (wall < WallMin) WallMin = wall;
                }
                if (WallMax < WallMin) WallMax = WallMin = WallVariation = double.NaN;
                else WallVariation = WallMax - WallMin;
                LastPointCount = 2;

                Cv2.Circle(dst, new Point((int)Math.Round(ManualOuter.Cx), (int)Math.Round(ManualOuter.Cy)),
                    (int)Math.Round(ManualOuter.R), Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                Cv2.Circle(dst, new Point((int)Math.Round(ManualInner.Cx), (int)Math.Round(ManualInner.Cy)),
                    (int)Math.Round(ManualInner.R), Scalar.Cyan, 1, LineTypes.AntiAlias);
                DrawCross(dst, ManualOuter.Cx, ManualOuter.Cy, Scalar.Red, 9);
                DrawCross(dst, ManualInner.Cx, ManualInner.Cy, Scalar.Magenta, 9);
                if (Offset > 1.0)
                    Cv2.ArrowedLine(dst,
                        new Point((int)Math.Round(ManualOuter.Cx), (int)Math.Round(ManualOuter.Cy)),
                        new Point((int)Math.Round(ManualInner.Cx), (int)Math.Round(ManualInner.Cy)),
                        Scalar.Yellow, 1, LineTypes.AntiAlias);
                DrawText(dst, string.Format("同心度 {0}", Unit(Offset)),
                    (ManualOuter.Cx + ManualInner.Cx) / 2 + 8, (ManualOuter.Cy + ManualInner.Cy) / 2 - 6, Scalar.Yellow);
                DrawText(dst, string.Format("外R {0}  内R {1}", Unit(RadiusOuter), Unit(RadiusInner)),
                    6, 20, Scalar.LimeGreen);
                LastSummary = string.Format(
                    "同心度(手动): {0}  (外R {1}, 内R {2}, 方向 {3:F2}deg, 比值 {4:F2}‰, 壁厚 {5}~{6})",
                    Unit(Offset), Unit(RadiusOuter), Unit(RadiusInner),
                    OffsetAngle, OffsetRatio, Unit(WallMin), Unit(WallMax));
                return dst;
            }
            int threshold = paramValues[0], polarity = paramValues[1];
            double innerRatio = paramValues[2] / 100.0;
            int wallSamples = Math.Max(8, paramValues[3]);

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            // 用 RETR_CCOMP 拿内外两层：外层是外轮廓，内层是孔
            Cv2.FindContours(bin, out Point[][] contours, out HierarchyIndex[] hier,
                RetrievalModes.CComp, ContourApproximationModes.ApproxNone);
            if (contours.Length == 0)
            {
                LastSummary = "同心度: 未找到轮廓（检查阈值/极性）";
                return dst;
            }

            int outerIdx = -1;
            double outerArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                // 只要最外层（parent = -1）
                if (hier[i].Parent >= 0) continue;
                double a = Cv2.ContourArea(contours[i]);
                if (a > outerArea) { outerArea = a; outerIdx = i; }
            }
            if (outerIdx < 0 || contours[outerIdx].Length < 5)
            {
                LastSummary = "同心度: 外轮廓点不足，无法拟合圆";
                return dst;
            }

            if (!FitContour(contours[outerIdx], out GeometryFit.Circle2 outer))
            {
                LastSummary = "同心度: 外圆拟合失败";
                return dst;
            }

            // 内圆：外轮廓的所有子轮廓里最大的（且面积小于最大轮廓的 innerRatio）
            GeometryFit.Circle2 inner = default;
            bool hasInner = false;
            double bestChildArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                if (hier[i].Parent != outerIdx) continue;
                double a = Cv2.ContourArea(contours[i]);
                if (a < bestChildArea) continue;
                if (a > outerArea * innerRatio) continue;
                if (contours[i].Length < 5) continue;
                if (!FitContour(contours[i], out GeometryFit.Circle2 ci)) continue;
                bestChildArea = a; inner = ci; hasInner = true;
            }

            if (!hasInner)
            {
                LastSummary = "同心度: 未找到内圆（无内孔或内圆面积未达阈值），已改用外圆单独报告";
                RadiusOuter = outer.R;
                CenterOuter = new GeometryFit.P2(outer.Cx, outer.Cy);
                Cv2.Circle(dst, new Point((int)Math.Round(outer.Cx), (int)Math.Round(outer.Cy)),
                    (int)Math.Round(outer.R), Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                DrawCross(dst, outer.Cx, outer.Cy, Scalar.Red, 9);
                return dst;
            }

            RadiusOuter = outer.R;
            RadiusInner = inner.R;
            CenterOuter = new GeometryFit.P2(outer.Cx, outer.Cy);
            CenterInner = new GeometryFit.P2(inner.Cx, inner.Cy);

            double ox = inner.Cx - outer.Cx, oy = inner.Cy - outer.Cy;
            Offset = Math.Sqrt((ox * ox) + (oy * oy));
            OffsetAngle = Math.Atan2(oy, ox) * 180.0 / Math.PI;
            OffsetRatio = outer.R > 1e-9 ? 1000.0 * Offset / outer.R : double.NaN;

            // 壁厚：沿内圆每个方向，从内圆心径向找外圆交点。
            // 外圆心与内圆心不同心，所以用"射线与外圆求交"而不是 2*(R外-R内)。
            WallMax = double.MinValue; WallMin = double.MaxValue;
            for (int s = 0; s < wallSamples; s++)
            {
                double ang = 2 * Math.PI * s / wallSamples;
                double ux = Math.Cos(ang), uy = Math.Sin(ang);
                // 以内圆心为起点沿 u 方向，与外圆（圆心 outer.C，半径 R）的交点：
                // |P + t u - C| = R  =>  t² + 2t(u·D) + |D|² - R² = 0,  D = P - C
                double dx = inner.Cx - outer.Cx, dy = inner.Cy - outer.Cy;
                double b = 2 * ((ux * dx) + (uy * dy));
                double cc = (dx * dx) + (dy * dy) - (outer.R * outer.R);
                double disc = (b * b) - (4 * cc);
                if (disc < 0) continue;
                double t = (-b + Math.Sqrt(disc)) / 2.0;   // 取正向交点（外侧）
                if (t <= 0) continue;
                double wall = t - inner.R;
                if (wall > WallMax) WallMax = wall;
                if (wall < WallMin) WallMin = wall;
            }
            if (WallMax < WallMin)
            {
                WallMax = WallMin = WallVariation = double.NaN;
            }
            else WallVariation = WallMax - WallMin;

            LastPointCount = 2;

            Cv2.Circle(dst, new Point((int)Math.Round(outer.Cx), (int)Math.Round(outer.Cy)),
                (int)Math.Round(outer.R), Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            Cv2.Circle(dst, new Point((int)Math.Round(inner.Cx), (int)Math.Round(inner.Cy)),
                (int)Math.Round(inner.R), Scalar.Cyan, 1, LineTypes.AntiAlias);
            DrawCross(dst, outer.Cx, outer.Cy, Scalar.Red, 9);
            DrawCross(dst, inner.Cx, inner.Cy, Scalar.Magenta, 9);
            if (Offset > 1.0)
                Cv2.ArrowedLine(dst, new Point((int)Math.Round(outer.Cx), (int)Math.Round(outer.Cy)),
                    new Point((int)Math.Round(inner.Cx), (int)Math.Round(inner.Cy)),
                    Scalar.Yellow, 1, LineTypes.AntiAlias, 0, 0.15);
            DrawText(dst, string.Format("同心度 {0}", Unit(Offset)),
                outer.Cx - 40, outer.Cy - outer.R - 10, Scalar.Yellow);
            DrawText(dst, string.Format("R外 {0}  R内 {1}", Unit(RadiusOuter), Unit(RadiusInner)), 6, 20, Scalar.LimeGreen);

            LastSummary = string.Format(
                "同心度: {0}  (R外 {1}, R内 {2}, 方向 {3:F1}deg, 壁厚 {4:F2}~{5:F2}px 差 {6:F2}, 比值 {7:F2}‰)",
                Unit(Offset), Unit(RadiusOuter), Unit(RadiusInner), OffsetAngle,
                WallMin, WallMax, WallVariation, OffsetRatio);
            return dst;
        }

        private static bool FitContour(Point[] c, out GeometryFit.Circle2 circle)
        {
            var list = new List<GeometryFit.P2>(c.Length);
            foreach (Point p in c) list.Add(new GeometryFit.P2(p.X, p.Y));
            return GeometryFit.FitCircle(list, out circle);
        }
    }
}
