using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 三点角度：在图像的三个区域（左 / 右上 / 右下）各取一个特征点，以中间那个为顶点算夹角。
    ///
    /// 与"两线夹角"的区别：两线夹角要先拟合两条直线，只适合有长直边的目标；
    /// 本算子只需要三个特征点，适合 V 形槽、弯折件、指针等**折线/尖角**类目标。
    /// 三点按"分割位置%"自动分区（左半 / 右上半 / 右下半），保证换图后仍能自动重算。
    /// </summary>
    public class Angle3PointTask : GdtTaskBase
    {
        public override string TaskName => "三点角度";

        /// <summary>夹角（度，0~180）</summary>
        public double AngleDeg { get; private set; } = double.NaN;

        /// <summary>顶点到两端的臂长</summary>
        public double ArmA { get; private set; } = double.NaN;
        public double ArmB { get; private set; } = double.NaN;

        /// <summary>与 90° 的偏差（正=钝角，负=锐角）</summary>
        public double DeviationFrom90 { get; private set; } = double.NaN;

        public GeometryFit.P2 Vertex { get; private set; }
        public GeometryFit.P2 EndA { get; private set; }
        public GeometryFit.P2 EndB { get; private set; }

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "极性:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "特征点 0质心1圆心2外接圆心", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "特征:{0}", Group = "取样", Tip = "用目标的哪个点代表它。圆孔用 1 圆心。" },
            new TaskParamDesc { ParamName = "分割位置%", Min = 1, Max = 99, DefaultValue = 50,
                DisplayFormat = "分割:{0}%", Group = "取样", Tip = "横向分割线的位置百分比。" +
                "顶点取左半区目标，两条边各取右半区的上/下目标 —— 即目标应是" +
                "左1右2 的 V 形/折线布局。" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积:{0}%", Group = "取样" },
        ];

        /// <summary>是否已由用户在图上手动指定三点（顶点 + 两臂端点）；true 时 Execute 优先用手动点</summary>
        public bool HasManualPoints { get; private set; }
        private GeometryFit.P2[] _manual = new GeometryFit.P2[3];
        public GeometryFit.P2[] ManualPoints => _manual;

        /// <summary>手动指定角的三点：顶点、臂A端点、臂B端点（图/输入图像素坐标）</summary>
        public void SetManualPoints(GeometryFit.P2 vertex, GeometryFit.P2 endA, GeometryFit.P2 endB)
        {
            _manual[0] = vertex; _manual[1] = endA; _manual[2] = endB;
            HasManualPoints = true;
        }

        public void ClearManualPoints() { HasManualPoints = false; _manual = new GeometryFit.P2[3]; }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            AngleDeg = ArmA = ArmB = DeviationFrom90 = double.NaN;
            Vertex = EndA = EndB = default;
            LastPointCount = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            // —— 手动模式：用户在图上点过三个点就直接按手动点测角（自动取点对不上真实目标时用） ——
            if (HasManualPoints)
            {
                Vertex = _manual[0]; EndA = _manual[1]; EndB = _manual[2];
                double mux = EndA.X - Vertex.X, muy = EndA.Y - Vertex.Y;
                double mwx = EndB.X - Vertex.X, mwy = EndB.Y - Vertex.Y;
                ArmA = Math.Sqrt((mux * mux) + (muy * muy));
                ArmB = Math.Sqrt((mwx * mwx) + (mwy * mwy));
                if (ArmA < 1e-6 || ArmB < 1e-6)
                {
                    LastSummary = "三点角度(手动): 两点重合，无法定角";
                    return dst;
                }
                double mcos = ((mux * mwx) + (muy * mwy)) / (ArmA * ArmB);
                AngleDeg = Math.Acos(Math.Clamp(mcos, -1.0, 1.0)) * 180.0 / Math.PI;
                DeviationFrom90 = AngleDeg - 90.0;
                LastPointCount = 3;
                DrawCross(dst, Vertex.X, Vertex.Y, Scalar.Red, 10);
                DrawCross(dst, EndA.X, EndA.Y, Scalar.LimeGreen, 9);
                DrawCross(dst, EndB.X, EndB.Y, Scalar.LimeGreen, 9);
                Cv2.Line(dst, new Point((int)Math.Round(Vertex.X), (int)Math.Round(Vertex.Y)),
                    new Point((int)Math.Round(EndA.X), (int)Math.Round(EndA.Y)), Scalar.Yellow, 1, LineTypes.AntiAlias);
                Cv2.Line(dst, new Point((int)Math.Round(Vertex.X), (int)Math.Round(Vertex.Y)),
                    new Point((int)Math.Round(EndB.X), (int)Math.Round(EndB.Y)), Scalar.Yellow, 1, LineTypes.AntiAlias);
                DrawText(dst, string.Format("{0:F2}deg", AngleDeg), Vertex.X - 30, Vertex.Y + 22, Scalar.Yellow);
                DrawText(dst, string.Format("臂长 {0:F1} / {1:F1}px  与90偏差 {2:F2}deg",
                    ArmA, ArmB, DeviationFrom90), 6, 20, Scalar.LimeGreen);
                LastSummary = string.Format("三点角度(手动): {0:F3}deg  (顶点({1:F1},{2:F1}), 臂 {3:F1}/{4:F1}px, 与90偏差 {5:F2}deg)",
                    AngleDeg, Vertex.X, Vertex.Y, ArmA, ArmB, DeviationFrom90);
                return dst;
            }

            MmPerPixel = 0;
            int threshold = paramValues[0], polarity = paramValues[1], feature = paramValues[2];
            double splitPct = paramValues[3], minAreaPct = paramValues[4];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            double minArea = bin.Cols * (double)bin.Rows * minAreaPct / 100.0;
            int splitX = (int)Math.Round(bin.Cols * splitPct / 100.0);
            int splitY = bin.Rows / 2;

            double bestL = 0, bestU = 0, bestD = 0;
            GeometryFit.P2 vA = default, vU = default, vD = default;
            bool okL = false, okU = false, okD = false;

            foreach (Point[] c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < minArea || c.Length < 3) continue;
                Moments m = Cv2.Moments(c);
                if (Math.Abs(m.M00) < 1e-9) continue;
                double mx = m.M10 / m.M00, my = m.M01 / m.M00;

                GeometryFit.P2 fp;
                if (feature == 0) fp = new GeometryFit.P2(mx, my);
                else if (feature == 1)
                {
                    var list = new List<GeometryFit.P2>(c.Length);
                    foreach (Point p in c) list.Add(new GeometryFit.P2(p.X, p.Y));
                    if (!GeometryFit.FitCircle(list, out GeometryFit.Circle2 cc)) continue;
                    fp = new GeometryFit.P2(cc.Cx, cc.Cy);
                }
                else
                {
                    Cv2.MinEnclosingCircle(c, out Point2f mc, out _);
                    fp = new GeometryFit.P2(mc.X, mc.Y);
                }

                if (mx < splitX) { if (area > bestL) { bestL = area; vA = fp; okL = true; } }
                else if (my < splitY) { if (area > bestU) { bestU = area; vU = fp; okU = true; } }
                else { if (area > bestD) { bestD = area; vD = fp; okD = true; } }
            }

            if (!okL || !okU || !okD)
            {
                LastSummary = string.Format("三点角度: 三点未凑齐（左={0} 右上={1} 右下={2}）",
                    okL ? "有" : "无", okU ? "有" : "无", okD ? "有" : "无");
                return dst;
            }

            Vertex = vA; EndA = vU; EndB = vD;
            double ux = vU.X - vA.X, uy = vU.Y - vA.Y;
            double wx = vD.X - vA.X, wy = vD.Y - vA.Y;
            ArmA = Math.Sqrt((ux * ux) + (uy * uy));
            ArmB = Math.Sqrt((wx * wx) + (wy * wy));
            if (ArmA < 1e-6 || ArmB < 1e-6)
            {
                LastSummary = "三点角度: 两点重合，无法定角";
                return dst;
            }
            double cos = ((ux * wx) + (uy * wy)) / (ArmA * ArmB);
            AngleDeg = Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
            DeviationFrom90 = AngleDeg - 90.0;
            LastPointCount = 3;

            DrawCross(dst, vA.X, vA.Y, Scalar.Red, 10);
            DrawCross(dst, vU.X, vU.Y, Scalar.LimeGreen, 9);
            DrawCross(dst, vD.X, vD.Y, Scalar.LimeGreen, 9);
            var pv = new Point((int)Math.Round(vA.X), (int)Math.Round(vA.Y));
            Cv2.Line(dst, pv, new Point((int)Math.Round(vU.X), (int)Math.Round(vU.Y)),
                Scalar.Yellow, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, pv, new Point((int)Math.Round(vD.X), (int)Math.Round(vD.Y)),
                Scalar.Yellow, 1, LineTypes.AntiAlias);
            DrawText(dst, string.Format("{0:F2}deg", AngleDeg), vA.X - 30, vA.Y + 22, Scalar.Yellow);
            DrawText(dst, string.Format("臂长 {0:F1} / {1:F1}px  与90偏差 {2:F2}deg",
                ArmA, ArmB, DeviationFrom90), 6, 20, Scalar.LimeGreen);

            LastSummary = string.Format("三点角度: {0:F3}deg  (顶点({1:F1},{2:F1}), 臂 {3:F1}/{4:F1}px, 与90偏差 {5:F2}deg)",
                AngleDeg, vA.X, vA.Y, ArmA, ArmB, DeviationFrom90);
            return dst;
        }
    }
}
