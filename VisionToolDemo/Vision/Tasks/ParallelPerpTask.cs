using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 平行度 / 垂直度：在图像的两个区域（上 / 下，或左 / 右）各拟合一条直线，
    /// 计算实际夹角与理想夹角（平行 0° / 垂直 90°）的偏差，并给出**包容带宽度**。
    ///
    /// 包容带宽度取两线间垂距的极差：把短线上的每个采样点投影到长线的法向，
    /// 取最大值−最小值。这正是 ISO 1101 平行度/垂直度的定义 —— 只报角度偏差
    /// 而不报带宽度是不够的，两条线可以角度几乎一致但仍然歪着错开。
    ///
    /// 两条线的分区由"分割轴"决定：0 = 上下分（找近水平的线），
    /// 1 = 左右分（找近竖直的线）。
    /// </summary>
    public class ParallelPerpTask : GdtTaskBase
    {
        public override string TaskName => "平行垂直度";

        /// <summary>两直线的实际夹角（0~90）</summary>
        public double AngleDeg { get; private set; } = double.NaN;

        /// <summary>判定模式：0 平行，1 垂直</summary>
        public int Mode { get; private set; }

        /// <summary>理想夹角</summary>
        public double NominalAngle { get; private set; } = double.NaN;

        /// <summary>与理想夹角的偏差（，正负号表示往哪个方向偏）</summary>
        public double AngleDeviation { get; private set; } = double.NaN;

        /// <summary>包容带宽度（两线法向距离极差，px）</summary>
        public double ToleranceWidth { get; private set; } = double.NaN;

        /// <summary>两线平均间距（px）</summary>
        public double MeanGap { get; private set; } = double.NaN;

        public GeometryFit.Line2 LineA { get; private set; }
        public GeometryFit.Line2 LineB { get; private set; }

        public double StraightnessA { get; private set; } = double.NaN;
        public double StraightnessB { get; private set; } = double.NaN;

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "判定 0平行1垂直", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "判定:{0}", Group = "判定", Tip = "0平行：理想夹角 0°。" +
                "1垂直：理想夹角 90°。偏差 = 实测夹角 − 理想夹角。" },
            new TaskParamDesc { ParamName = "取样方式 0轮廓1边缘", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "取样:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "阈值", Min = 1, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "取样", Tip = PolarityTip },
            new TaskParamDesc { ParamName = "扫描步长", Min = 1, Max = 50, DefaultValue = 1,
                DisplayFormat = "步长:{0}px", Group = "取样" },
            new TaskParamDesc { ParamName = "最大轮廓数", Min = 2, Max = 20, DefaultValue = 2,
                DisplayFormat = "轮廓:{0}", Group = "取样", Tip = "至少 2：两条边各需要一个轮廓。" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积:{0}%", Group = "取样" },
            new TaskParamDesc { ParamName = "区域分割轴 0上下1左右", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "分割:{0}", Group = "取样", Tip = "0上下分：适合近水平的线对。" +
                "1左右分：适合近竖直的线对。分错会让两条线被切成同一组。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        /// <summary>
        /// 把点集按空间连通性切成若干块（网格哈希 + 并查集式合并的简化实现）。
        /// 取样路径可能给回一整张轮廓的所有点，必须先把不同目标分开。
        /// 用"格点邻域内是否有已有点"判断连通：只连接相邻格子，
        /// 避免 O(n²) 的全对比较。
        /// </summary>
        private static List<List<GeometryFit.P2>> SplitComponents(
            List<GeometryFit.P2> pts, int maxComponents)
        {
            var result = new List<List<GeometryFit.P2>>();
            if (pts == null || pts.Count == 0) return result;

            // 估算一个特征尺度作为格边长：用点间距的中位数不可靠，
            // 这里用包围盒对角线的 1/12，对"两根条分居图两侧"这类布局足够。
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            double span = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
            double cell = Math.Max(2.0, span / 12.0);

            var grid = new Dictionary<(int, int), List<int>>();
            var owner = new int[pts.Count];
            for (int i = 0; i < pts.Count; i++) owner[i] = -1;

            for (int i = 0; i < pts.Count; i++)
            {
                int gx = (int)Math.Floor(pts[i].X / cell);
                int gy = (int)Math.Floor(pts[i].Y / cell);
                int found = -1;
                // 查 3x3 邻域里是否已有归属
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dx = -1; dx <= 1 && found < 0; dx++)
                    {
                        if (grid.TryGetValue((gx + dx, gy + dy), out var list))
                            foreach (int j in list)
                                if (owner[j] >= 0) { found = owner[j]; break; }
                    }

                if (found < 0)
                {
                    found = result.Count;
                    result.Add(new List<GeometryFit.P2>());
                }
                owner[i] = found;
                result[found].Add(pts[i]);
                if (!grid.TryGetValue((gx, gy), out var cellList))
                {
                    cellList = new List<int>();
                    grid[(gx, gy)] = cellList;
                }
                cellList.Add(i);
            }

            // 块数过多说明格边长估小了（把一根条切碎了）——合并成一块，
            // 让上层按"分组不平衡"给出可操作的提示，而不是静默地给出错的结果。
            if (result.Count > maxComponents * 4)
                return [new List<GeometryFit.P2>(pts)];
            return result;
        }

        /// <summary>是否已由用户在图上手动指定两条线；true 时 Execute 直接用手动线判定平行/垂直</summary>
        public bool HasManualLines { get; private set; }
        public GeometryFit.P2 ManualLine1A { get; private set; }
        public GeometryFit.P2 ManualLine1B { get; private set; }
        public GeometryFit.P2 ManualLine2A { get; private set; }
        public GeometryFit.P2 ManualLine2B { get; private set; }
        public void SetManualLines(GeometryFit.P2 l1a, GeometryFit.P2 l1b, GeometryFit.P2 l2a, GeometryFit.P2 l2b)
        {
            ManualLine1A = l1a; ManualLine1B = l1b; ManualLine2A = l2a; ManualLine2B = l2b;
            HasManualLines = true;
        }
        public void ClearManualLines() { HasManualLines = false; }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            AngleDeg = NominalAngle = AngleDeviation = ToleranceWidth = MeanGap = double.NaN;
            LineA = LineB = default;
            StraightnessA = StraightnessB = double.NaN;
            LastPointCount = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            // —— 手动模式：用户分别画了两条线，直接判定平行/垂直 ——
            if (HasManualLines)
            {
                var ml1 = new List<GeometryFit.P2> { ManualLine1A, ManualLine1B };
                var ml2 = new List<GeometryFit.P2> { ManualLine2A, ManualLine2B };
                if (!GeometryFit.FitLine(ml1, out GeometryFit.Line2 mla)
                    || !GeometryFit.FitLine(ml2, out GeometryFit.Line2 mlb))
                {
                    LastSummary = "平行垂直度(手动): 线段过短或退化";
                    return dst;
                }
                LineA = mla; LineB = mlb;
                StraightnessA = GeometryFit.Straightness(ml1, mla);
                StraightnessB = GeometryFit.Straightness(ml2, mlb);
                LastPointCount = 4;
                Mode = paramValues[0];
                AngleDeg = GeometryFit.AngleBetween(mla, mlb);
                NominalAngle = Mode == 1 ? 90.0 : 0.0;
                AngleDeviation = AngleDeg - NominalAngle;
                MmPerPixel = paramValues[8] / 1000.0;

                double nlen = Math.Max(dst.Cols, dst.Rows) * 1.5;
                Cv2.Line(dst,
                    new Point((int)Math.Round(mla.Px - mla.Dx * nlen), (int)Math.Round(mla.Py - mla.Dy * nlen)),
                    new Point((int)Math.Round(mla.Px + mla.Dx * nlen), (int)Math.Round(mla.Py + mla.Dy * nlen)),
                    Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                Cv2.Line(dst,
                    new Point((int)Math.Round(mlb.Px - mlb.Dx * nlen), (int)Math.Round(mlb.Py - mlb.Dy * nlen)),
                    new Point((int)Math.Round(mlb.Px + mlb.Dx * nlen), (int)Math.Round(mlb.Py + mlb.Dy * nlen)),
                    Scalar.Orange, 2, LineTypes.AntiAlias);
                DrawText(dst, string.Format("{0}角 {1:F2}deg 偏差 {2:F2}deg",
                    Mode == 1 ? "垂直" : "平行", AngleDeg, AngleDeviation), 6, 20, Scalar.LimeGreen);
                LastSummary = string.Format("平行垂直度(手动): {0}角 {1:F2}deg 偏差 {2:F2}deg (线长 {3:F0}/{4:F0}px)",
                    Mode == 1 ? "垂直" : "平行", AngleDeg, AngleDeviation,
                    ManualLine1A.DistanceTo(ManualLine1B), ManualLine2A.DistanceTo(ManualLine2B));
                return dst;
            }

            Mode = paramValues[0];
            MmPerPixel = paramValues[8] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            List<GeometryFit.P2> pts = SamplePoints(gray, paramValues[1], paramValues[2],
                paramValues[3], paramValues[4], Math.Max(2, paramValues[5]), paramValues[6]);

            if (pts.Count < 4)
            {
                LastSummary = "平行垂直度: 有效点不足（" + pts.Count + " 个），两条边至少各 2 个";
                return dst;
            }

            // 按分割轴把点分到两组
            double splitVal;
            if (paramValues[7] == 0)
            {
                double ymin = double.MaxValue, ymax = double.MinValue;
                foreach (var p in pts) { if (p.Y < ymin) ymin = p.Y; if (p.Y > ymax) ymax = p.Y; }
                splitVal = (ymin + ymax) / 2;
            }
            else
            {
                double xmin = double.MaxValue, xmax = double.MinValue;
                foreach (var p in pts) { if (p.X < xmin) xmin = p.X; if (p.X > xmax) xmax = p.X; }
                splitVal = (xmin + xmax) / 2;
            }

            // —— 分组：必须先按连通块分割，不能直接按坐标切点 ——
            // 直接切点的后果（实测踩过）：一根竖直长条的轮廓同时包含它的**左右两条边**，
            // 按 x 分割会把同一条边的两侧分进两组，拟合出的"两条线"其实是
            // 同一根条的两个侧面，夹角恒为 0、间距恒等于条宽，完全不是用户要的量。
            // 正确做法：每个连通块（=一根条/一条边）算一个质心，按质心分区，
            // 再把整块的点归入该组。
            var comps = SplitComponents(pts, Math.Max(2, paramValues[5]));

            var g1 = new List<GeometryFit.P2>();
            var g2 = new List<GeometryFit.P2>();
            if (comps.Count >= 2)
            {
                foreach (var comp in comps)
                {
                    double cxSum = 0, cySum = 0;
                    foreach (var p in comp) { cxSum += p.X; cySum += p.Y; }
                    double mx = cxSum / comp.Count, my = cySum / comp.Count;
                    double key = paramValues[7] == 0 ? my : mx;
                    if (key <= splitVal) g1.AddRange(comp); else g2.AddRange(comp);
                }
            }

            if (g1.Count < 2 || g2.Count < 2)
            {
                LastSummary = string.Format(
                    "平行垂直度: 分组不平衡（连通块 {0} 个, 点数 {1}/{2}）。" +
                    "本算子需要**两个分离的条状目标**；若目标粘在一起请改区域分割轴或调小最小面积",
                    comps.Count, g1.Count, g2.Count);
                return dst;
            }

            if (!GeometryFit.FitLine(g1, out GeometryFit.Line2 la))
            {
                LastSummary = "平行垂直度: 第一组退化，无法拟合";
                return dst;
            }
            if (!GeometryFit.FitLine(g2, out GeometryFit.Line2 lb))
            {
                LastSummary = "平行垂直度: 第二组退化，无法拟合";
                return dst;
            }

            LineA = la; LineB = lb;
            StraightnessA = GeometryFit.Straightness(g1, la);
            StraightnessB = GeometryFit.Straightness(g2, lb);
            LastPointCount = g1.Count + g2.Count;

            AngleDeg = GeometryFit.AngleBetween(la, lb);
            NominalAngle = Mode == 1 ? 90.0 : 0.0;
            AngleDeviation = AngleDeg - NominalAngle;

            // 包容带宽度：把第二组点投影到第一条线的法向上（法向 n = (-Dy, Dx)）
            // 沿第一条线方向的位置 t 用来配对"同一位置上的两条线间距"
            double nx = -la.Dy, ny = la.Dx;
            var proj = new List<(double t, double s)>();
            foreach (var p in g2)
            {
                double dx = p.X - la.Px, dy = p.Y - la.Py;
                double s = (dx * nx) + (dy * ny);
                double t = (dx * la.Dx) + (dy * la.Dy);
                proj.Add((t, s));
            }
            double smin = double.MaxValue, smax = double.MinValue, ssum = 0;
            foreach (var (t, s) in proj)
            {
                _ = t;
                if (s < smin) smin = s;
                if (s > smax) smax = s;
                ssum += s;
            }
            ToleranceWidth = smax - smin;
            MeanGap = Math.Abs(ssum / proj.Count);

            DrawPoints(dst, g1, new Scalar(80, 80, 80));
            DrawPoints(dst, g2, new Scalar(80, 80, 80));
            DrawFittedLine(dst, la, Scalar.LimeGreen);
            DrawFittedLine(dst, lb, Scalar.Cyan);
            DrawText(dst, string.Format("{0} 夹角 {1:F3}deg 偏差 {2:+0.000;-0.000}deg",
                Mode == 1 ? "垂直度" : "平行度", AngleDeg, AngleDeviation), 6, 20, Scalar.Yellow);
            DrawText(dst, string.Format("包容带 {0}", Unit(ToleranceWidth)), 6, 40, Scalar.Orange);

            LastSummary = string.Format(
                "{0}: 夹角 {1:F3}deg (理想 {2:F0}deg, 偏差 {3:+0.000;-0.000}deg), 包容带 {4}, 平均间距 {5}, 直线度 {6}/{7}",
                Mode == 1 ? "垂直度" : "平行度", AngleDeg, NominalAngle, AngleDeviation,
                Unit(ToleranceWidth), Unit(MeanGap), Unit(StraightnessA), Unit(StraightnessB));
            return dst;
        }
    }
}
