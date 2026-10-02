using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 两线夹角：Canny + HoughLinesP 检出线段后按方向聚类（容差 3°，同一粗笔画的
    /// 两侧边缘/同一直线的碎片归为一簇），取总长度最大的两个方向簇，
    /// 绿/橙画出各自最长线段，摘要输出两方向夹角（0°~90°）。
    /// 用于弯角件角度、装配位置检测，配合 ROI 框选角部使用。
    /// 参数：Canny 低/高阈值、最短线段长度。
    /// </summary>
    public class Angle2LinesTask : IVisionTask, IResultReporter
    {
        public string TaskName => "两线夹角";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "Canny低阈值",
                Min = 1,
                Max = 500,
                DefaultValue = 80,
                DisplayFormat = "低:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "Canny高阈值",
                Min = 1,
                Max = 1000,
                DefaultValue = 180,
                DisplayFormat = "高:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "最短线段",
                Min = 10,
                Max = 1000,
                DefaultValue = 50,
                DisplayFormat = "MinLen:{0}"
            }
        };

        private class DirCluster
        {
            public double Angle;    // 代表方向角 (-90, 90]
            public double Total;    // 簇内线段总长
            public LineSegmentPoint Rep;
        }

        /// <summary>是否已由用户在图上手动画两条线；true 时 Execute 优先用手动线测夹角</summary>
        public bool HasManualLines { get; private set; }
        public Point ManualLine1P1 { get; private set; }
        public Point ManualLine1P2 { get; private set; }
        public Point ManualLine2P1 { get; private set; }
        public Point ManualLine2P2 { get; private set; }

        /// <summary>手动指定两条线（图/输入图像素坐标）</summary>
        public void SetManualLines(Point l1p1, Point l1p2, Point l2p1, Point l2p2)
        {
            ManualLine1P1 = l1p1; ManualLine1P2 = l1p2;
            ManualLine2P1 = l2p1; ManualLine2P2 = l2p2;
            HasManualLines = true;
        }

        public void ClearManualLines() { HasManualLines = false; }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (HasManualLines)
            {
                // 手动模式：直接按用户画的两条线测夹角（Hough 自动聚类对不上真实目标时用）
                Cv2.Line(dst, ManualLine1P1, ManualLine1P2, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                Cv2.Line(dst, ManualLine2P1, ManualLine2P2, Scalar.Orange, 2, LineTypes.AntiAlias);
                double a1 = DirAngle(new LineSegmentPoint(ManualLine1P1, ManualLine1P2));
                double a2 = DirAngle(new LineSegmentPoint(ManualLine2P1, ManualLine2P2));
                double deg = AcuteDiff(a1, a2);
                double len1 = SegLen(new LineSegmentPoint(ManualLine1P1, ManualLine1P2));
                double len2 = SegLen(new LineSegmentPoint(ManualLine2P1, ManualLine2P2));
                LastSummary = string.Format("两线夹角(手动): {0:F1}° (线1 {1:F0}px 线2 {2:F0}px)",
                    deg, len1, len2);
                return dst;
            }
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat edges = new())
            {
                Cv2.Canny(gray, edges, paramValues[0], paramValues[1]);
                LineSegmentPoint[] segs = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, 40,
                    paramValues[2], 10);
                if (segs == null || segs.Length < 2)
                {
                    LastSummary = "两线夹角: 检测到的线段不足 2 条 (" + (segs == null ? 0 : segs.Length) + ")";
                    return dst;
                }

                // 按方向聚类（同一粗笔画的两侧边缘、同一长直线的碎片归为一簇）
                List<DirCluster> clusters = [];
                foreach (LineSegmentPoint s in segs.OrderByDescending(SegLen2))
                {
                    double a = DirAngle(s);
                    DirCluster hit = null;
                    foreach (DirCluster c in clusters)
                        if (AcuteDiff(a, c.Angle) <= 3.0)
                        {
                            hit = c;
                            break;
                        }
                    if (hit == null)
                        clusters.Add(new DirCluster { Angle = a, Total = SegLen(s), Rep = s });
                    else
                        hit.Total += SegLen(s);
                }

                if (clusters.Count < 2)
                {
                    LastSummary = "两线夹角: 仅检测到一个方向 (" + segs.Length + " 段, 全部近似共线)";
                    return dst;
                }

                // 按簇内线段总长取前两名（clusters[0] 为最长线段所在簇），代表线段为各簇最长者
                DirCluster c1 = clusters[0];
                DirCluster c2 = clusters
                    .Where(c => c != clusters[0])
                    .OrderByDescending(c => c.Total)
                    .First();

                Cv2.Line(dst, c1.Rep.P1, c1.Rep.P2, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                Cv2.Line(dst, c2.Rep.P1, c2.Rep.P2, Scalar.Orange, 2, LineTypes.AntiAlias);

                double deg = AcuteDiff(c1.Angle, c2.Angle);
                LastSummary = string.Format("两线夹角: {0:F1}° (线1 {1:F0}px 线2 {2:F0}px)",
                    deg, SegLen(c1.Rep), SegLen(c2.Rep));
                return dst;
            }
        }

        /// <summary>线段方向角，归一化到 (-90, 90]（0=水平）</summary>
        private static double DirAngle(LineSegmentPoint s)
        {
            double a = Math.Atan2(s.P2.Y - s.P1.Y, s.P2.X - s.P1.X) * 180.0 / Math.PI;
            if (a > 90) a -= 180;
            if (a <= -90) a += 180;
            return a;
        }

        /// <summary>两方向角的锐角差 (0°~90°)</summary>
        private static double AcuteDiff(double a, double b)
        {
            double d = Math.Abs(a - b);
            if (d > 90) d = 180 - d;
            return d;
        }

        private static double SegLen2(LineSegmentPoint s)
        {
            double dx = s.P2.X - s.P1.X, dy = s.P2.Y - s.P1.Y;
            return (dx * dx) + (dy * dy);
        }

        private static double SegLen(LineSegmentPoint s)
        {
            return Math.Sqrt(SegLen2(s));
        }
    }
}
