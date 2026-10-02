using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 平面度（矩形拟合）：对取样点做最小面积外接矩形，输出四边长度、对角线与倾角；
    /// 平面度取**四边直线度的最大值**（ISO 1101 简化模型）。
    ///
    /// 与"最小旋转外接矩形"的区别：那个只给几何框，不做拟合评价。
    /// 本算子额外做：
    ///   · 亚像素取样（可选）
    ///   · 每条边的独立直线度
    ///   · 对边平行度 / 邻边垂直度
    /// 用于判断矩形工件是否"方、正、平"。
    /// </summary>
    public class FlatnessTask : GdtTaskBase
    {
        public override string TaskName => "平面度";

        /// <summary>平面度（四边直线度最大值，px）</summary>
        public double FlatnessValue { get; private set; } = double.NaN;

        /// <summary>四条边的直线度</summary>
        public double[] EdgeStraightness { get; private set; } = new double[4];

        /// <summary>四边长度（px），顺序：上/右/下/左</summary>
        public double[] EdgeLength { get; private set; } = new double[4];

        public double Width { get; private set; } = double.NaN;
        public double Height { get; private set; } = double.NaN;

        /// <summary>矩形倾角（度）</summary>
        public double AngleDeg { get; private set; } = double.NaN;

        /// <summary>对角线长度</summary>
        public double Diagonal { get; private set; } = double.NaN;

        /// <summary>对边平行度偏差（上-下 与 左-右 的夹角，取较大者）</summary>
        public double ParallelismDeviation { get; private set; } = double.NaN;

        /// <summary>邻边垂直度偏差（与 90° 的偏差，取四个角最大值）</summary>
        public double PerpendicularityDeviation { get; private set; } = double.NaN;

        /// <summary>矩形度 = 轮廓面积 / 外接矩形面积（1.0 = 完美矩形）</summary>
        public double Rectangularity { get; private set; } = double.NaN;

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
            FlatnessValue = Width = Height = AngleDeg = Diagonal = double.NaN;
            ParallelismDeviation = PerpendicularityDeviation = Rectangularity = double.NaN;
            EdgeStraightness = new double[4];
            EdgeLength = new double[4];
            for (int i = 0; i < 4; i++) { EdgeStraightness[i] = double.NaN; EdgeLength[i] = double.NaN; }
            LastPointCount = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            MmPerPixel = paramValues[6] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            List<GeometryFit.P2> pts = SamplePoints(gray, paramValues[0], paramValues[1],
                paramValues[2], paramValues[3], paramValues[4], paramValues[5]);
            LastPointCount = pts.Count;

            if (pts.Count < 4)
            {
                LastSummary = "平面度: 有效点不足（" + pts.Count + " 个）";
                return dst;
            }

            // 最小面积外接矩形：OpenCV 只接受 Point[]，先转过去
            var ipts = new Point[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                ipts[i] = new Point((int)Math.Round(pts[i].X), (int)Math.Round(pts[i].Y));
            RotatedRect rr = Cv2.MinAreaRect(ipts);
            Width = rr.Size.Width;
            Height = rr.Size.Height;
            AngleDeg = rr.Angle;
            Diagonal = Math.Sqrt((Width * Width) + (Height * Height));

            // 矩形四角
            Point2f[] corners = rr.Points();   // 顺序为 左下/左上/右上/右下（与 OpenCV 一致）
            var cc = new GeometryFit.P2[4];
            for (int i = 0; i < 4; i++) cc[i] = new GeometryFit.P2(corners[i].X, corners[i].Y);

            // 把每个采样点归给最近的边，然后逐边做直线拟合求直线度。
            // 用"点到线段距离"而不是"点到直线距离"，否则远处的点会被错分到对边。
            var edgePts = new List<GeometryFit.P2>[4];
            for (int i = 0; i < 4; i++) edgePts[i] = new List<GeometryFit.P2>();
            for (int i = 0; i < pts.Count; i++)
            {
                int bestE = 0; double bestD = double.MaxValue;
                for (int e = 0; e < 4; e++)
                {
                    double d = DistToSegment(pts[i], cc[e], cc[(e + 1) % 4]);
                    if (d < bestD) { bestD = d; bestE = e; }
                }
                edgePts[bestE].Add(pts[i]);
            }

            var edgeLines = new GeometryFit.Line2[4];
            bool[] edgeOk = new bool[4];
            double flat = 0;
            for (int e = 0; e < 4; e++)
            {
                EdgeLength[e] = cc[e].DistanceTo(cc[(e + 1) % 4]);
                if (edgePts[e].Count < 2) continue;
                if (!GeometryFit.FitLine(edgePts[e], out GeometryFit.Line2 le)) continue;
                edgeLines[e] = le;
                edgeOk[e] = true;
                EdgeStraightness[e] = GeometryFit.Straightness(edgePts[e], le);
                if (EdgeStraightness[e] > flat) flat = EdgeStraightness[e];
            }
            FlatnessValue = edgeOk[0] || edgeOk[1] || edgeOk[2] || edgeOk[3] ? flat : double.NaN;

            // 对边平行度：0-2 与 1-3
            double par = 0; bool parOk = false;
            if (edgeOk[0] && edgeOk[2])
            { par = Math.Max(par, GeometryFit.AngleBetween(edgeLines[0], edgeLines[2])); parOk = true; }
            if (edgeOk[1] && edgeOk[3])
            { par = Math.Max(par, GeometryFit.AngleBetween(edgeLines[1], edgeLines[3])); parOk = true; }
            if (parOk) ParallelismDeviation = par;

            // 邻边垂直度：四个角各自与 90° 的偏差
            double perp = 0; bool perpOk = false;
            for (int e = 0; e < 4; e++)
            {
                int f = (e + 1) % 4;
                if (!edgeOk[e] || !edgeOk[f]) continue;
                double dev = Math.Abs(GeometryFit.AngleBetween(edgeLines[e], edgeLines[f]) - 90.0);
                if (dev > perp) perp = dev;
                perpOk = true;
            }
            if (perpOk) PerpendicularityDeviation = perp;

            // 矩形度：用外接矩形面积与点数估算的凸包面积比
            double hullArea = Cv2.ContourArea(Cv2.ConvexHull(ipts));
            double rectArea = Width * Height;
            if (rectArea > 1e-9) Rectangularity = hullArea / rectArea;

            // 绘制
            var cornerPts = new Point[4];
            for (int i = 0; i < 4; i++)
                cornerPts[i] = new Point((int)Math.Round(corners[i].X), (int)Math.Round(corners[i].Y));
            Cv2.Polylines(dst, new[] { cornerPts }, true, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            DrawPoints(dst, pts, new Scalar(80, 80, 80));
            for (int e = 0; e < 4; e++)
            {
                if (!edgeOk[e] || edgePts[e].Count == 0) continue;
                // 在最差边的中点标注该边直线度
                var m = new GeometryFit.P2(
                    (cc[e].X + cc[(e + 1) % 4].X) / 2,
                    (cc[e].Y + cc[(e + 1) % 4].Y) / 2);
                DrawText(dst, string.Format("{0:F2}", EdgeStraightness[e]), m.X - 12, m.Y, Scalar.Yellow);
            }
            DrawCross(dst, rr.Center.X, rr.Center.Y, Scalar.Red, 8);
            DrawText(dst, string.Format("平面度 {0}", Unit(FlatnessValue)), 6, 20, Scalar.Yellow);
            DrawText(dst, string.Format("W{0:F1} H{1:F1} 倾角{2:F2} 矩形度{3:F3}",
                Width, Height, AngleDeg, Rectangularity), 6, 40, Scalar.LimeGreen);
            DrawText(dst, string.Format("对边平行偏差{0:F3} 邻边垂直偏差{1:F3}",
                ParallelismDeviation, PerpendicularityDeviation), 6, 60, Scalar.Orange);

            LastSummary = string.Format(
                "平面度: {0}  (矩形 {1:F1}x{2:F1}px 倾角 {3:F2}deg, 对角线 {4:F1}px, 矩形度 {5:F3}, " +
                "对边平行偏差 {6:F3}deg, 邻边垂直偏差 {7:F3}deg)",
                Unit(FlatnessValue), Width, Height, AngleDeg, Diagonal, Rectangularity,
                ParallelismDeviation, PerpendicularityDeviation);
            return dst;
        }

        private static double DistToSegment(GeometryFit.P2 p, GeometryFit.P2 a, GeometryFit.P2 b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double len2 = (vx * vx) + (vy * vy);
            if (len2 < 1e-12) return p.DistanceTo(a);
            double t = ((wx * vx) + (wy * vy)) / len2;
            t = Math.Clamp(t, 0.0, 1.0);
            double px = a.X + (t * vx), py = a.Y + (t * vy);
            double dx = p.X - px, dy = p.Y - py;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }
}
