using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 点到点距离：在两个 ROI 半区里各取一个特征点（质心 / 最小外接圆圆心 / 轮廓拟合圆心），
    /// 输出两点距离、水平/垂直分量与连线角度。
    ///
    /// 用"左右半区各找一个特征点"而不是让用户点两个点，是为了能进流水线：
    /// 特征点由图像内容决定，换一张图仍然自动重算，不需要重新标注。
    /// 左右分区由"分割位置%"控制。
    /// </summary>
    public class PointToPointTask : GdtTaskBase
    {
        public override string TaskName => "点到点距离";

        /// <summary>两点距离（px）</summary>
        public double Distance { get; private set; } = double.NaN;

        public double DeltaX { get; private set; } = double.NaN;
        public double DeltaY { get; private set; } = double.NaN;

        /// <summary>连线角度（度，相对水平，[-180,180)）</summary>
        public double AngleDeg { get; private set; } = double.NaN;

        public GeometryFit.P2 PointA { get; private set; }
        public GeometryFit.P2 PointB { get; private set; }

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "极性:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "特征点 0质心1圆心2外接圆心", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "特征:{0}", Group = "取样", Tip = "用目标的哪个点代表它。" +
                "0质心：面积均匀时最快最稳。1圆心：最小二乘圆拟合圆心，圆孔/圆柱用。" +
                "2外接圆心：最小外接圆圆心，形状不规则但外轮廓清楚时用。" },
            new TaskParamDesc { ParamName = "分割位置%", Min = 1, Max = 99, DefaultValue = 50,
                DisplayFormat = "分割:{0}%", Group = "取样", Tip = "在图的左/右两侧各找一个目标。" +
                "此处是分割线的横向位置百分比。目标不在左右分布时改用 ROI 框选。" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积:{0}%", Group = "取样", Tip = "滤掉小于图面此比例的噪点块。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        /// <summary>是否已由用户在图上手动指定两点；true 时 Execute 优先用手动点测距离</summary>
        public bool HasManualPoints { get; private set; }
        private GeometryFit.P2[] _manual = new GeometryFit.P2[2];
        public GeometryFit.P2[] ManualPoints => _manual;

        /// <summary>手动指定测距两点（图/输入图像素坐标）</summary>
        public void SetManualPoints(GeometryFit.P2 a, GeometryFit.P2 b)
        {
            _manual[0] = a; _manual[1] = b;
            HasManualPoints = true;
        }

        public void ClearManualPoints() { HasManualPoints = false; _manual = new GeometryFit.P2[2]; }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Distance = DeltaX = DeltaY = AngleDeg = double.NaN;
            PointA = PointB = default;
            LastPointCount = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            MmPerPixel = paramValues[5] / 1000.0;

            // —— 手动模式：用户在图上点过两个点就直接按手动点测距（自动取点对不上真实目标时用） ——
            if (HasManualPoints)
            {
                PointA = _manual[0]; PointB = _manual[1];
                DeltaX = PointB.X - PointA.X;
                DeltaY = PointB.Y - PointA.Y;
                Distance = Math.Sqrt((DeltaX * DeltaX) + (DeltaY * DeltaY));
                AngleDeg = Math.Atan2(DeltaY, DeltaX) * 180.0 / Math.PI;
                LastPointCount = 2;
                DrawCross(dst, PointA.X, PointA.Y, Scalar.LimeGreen, 9);
                DrawCross(dst, PointB.X, PointB.Y, Scalar.LimeGreen, 9);
                Cv2.Line(dst, new Point((int)Math.Round(PointA.X), (int)Math.Round(PointA.Y)),
                    new Point((int)Math.Round(PointB.X), (int)Math.Round(PointB.Y)), Scalar.Yellow, 1, LineTypes.AntiAlias);
                DrawText(dst, string.Format("d={0}", Unit(Distance)),
                    (PointA.X + PointB.X) / 2 - 40, (PointA.Y + PointB.Y) / 2 - 8, Scalar.Yellow);
                DrawText(dst, string.Format("dx {0:F2}  dy {1:F2}  ang {2:F2}deg", DeltaX, DeltaY, AngleDeg),
                    6, 20, Scalar.LimeGreen);
                LastSummary = string.Format("点到点距离(手动): {0}  (dx {1:F3}px, dy {2:F3}px, 角度 {3:F2}deg)",
                    Unit(Distance), DeltaX, DeltaY, AngleDeg);
                return dst;
            }
            int threshold = paramValues[0], polarity = paramValues[1], feature = paramValues[2];
            double splitPct = paramValues[3];
            double minAreaPct = paramValues[4];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            double minArea = bin.Cols * (double)bin.Rows * minAreaPct / 100.0;

            int splitX = (int)Math.Round(bin.Cols * splitPct / 100.0);
            double bestLA = 0, bestRA = 0;
            GeometryFit.P2 pA = default, pB = default;
            bool okA = false, okB = false;

            foreach (Point[] c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < minArea || c.Length < 3) continue;

                Moments m = Cv2.Moments(c);
                if (Math.Abs(m.M00) < 1e-9) continue;
                double mx = m.M10 / m.M00, my = m.M01 / m.M00;
                bool left = mx < splitX;

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

                if (left)
                {
                    if (area > bestLA) { bestLA = area; pA = fp; okA = true; }
                }
                else
                {
                    if (area > bestRA) { bestRA = area; pB = fp; okB = true; }
                }
            }

            if (!okA || !okB)
            {
                LastSummary = string.Format("点到点距离: 一侧未找到目标（左={0} 右={1}），调低阈值或改分割位置",
                    okA ? "有" : "无", okB ? "有" : "无");
                return dst;
            }

            PointA = pA; PointB = pB;
            DeltaX = pB.X - pA.X;
            DeltaY = pB.Y - pA.Y;
            Distance = Math.Sqrt((DeltaX * DeltaX) + (DeltaY * DeltaY));
            AngleDeg = Math.Atan2(DeltaY, DeltaX) * 180.0 / Math.PI;
            LastPointCount = 2;

            DrawCross(dst, pA.X, pA.Y, Scalar.LimeGreen, 9);
            DrawCross(dst, pB.X, pB.Y, Scalar.LimeGreen, 9);
            Cv2.Line(dst, new Point((int)Math.Round(pA.X), (int)Math.Round(pA.Y)),
                new Point((int)Math.Round(pB.X), (int)Math.Round(pB.Y)), Scalar.Yellow, 1, LineTypes.AntiAlias);
            DrawText(dst, string.Format("d={0}", Unit(Distance)),
                (pA.X + pB.X) / 2 - 40, (pA.Y + pB.Y) / 2 - 8, Scalar.Yellow);
            DrawText(dst, string.Format("dx {0:F2}  dy {1:F2}  ang {2:F2}deg", DeltaX, DeltaY, AngleDeg),
                6, 20, Scalar.LimeGreen);

            LastSummary = string.Format("点到点距离: {0}  (dx {1:F3}px, dy {2:F3}px, 角度 {3:F2}deg)",
                Unit(Distance), DeltaX, DeltaY, AngleDeg);
            return dst;
        }
    }
}
