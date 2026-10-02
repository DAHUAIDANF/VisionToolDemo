using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 椭圆拟合：把轮廓或亚像素边缘点拟合成椭圆，输出中心、长短半轴、长轴方向、
    /// 偏心率、轴比、面积与周长，并可做**几何精修**降低偏差。
    ///
    /// 与"圆拟合"的区别：圆拟合用 FitEllipse 得到的是**外接椭圆**（长短轴差异
    /// 被当成圆度误差报出来）；本算子直接输出椭圆的几何参数，适合量测本来
    /// 就是椭圆的目标（斜看圆孔、瓶口、镜筒、焊点、丝印椭圆等）。
    ///
    /// 两种取样方式：
    ///   · 轮廓   —— 阈值→找轮廓→逐轮廓拟合，**支持一张图里多个椭圆**；
    ///   · 亚像素 —— 灰度梯度抛物线拟合，精度到 0.1px，适合单目标精密量测
    ///               （椭圆的边缘朝向各个方向，所以同时沿行、沿列扫描再合并，
    ///                比只扫一个方向的点更完整）。
    ///
    /// 精度关键在"拟合方法"：代数最小二乘（Fitzgibbon）最小化的是隐式方程值
    /// 而不是真实距离，对**点分布不均**（只取到半圈、采样疏密不均）有系统性偏差；
    /// 几何精修把残差换成 Sampson 距离（F/|∇F|，真实垂距的一阶近似）并用
    /// Levenberg–Marquardt 迭代，基本消除该偏差。
    /// </summary>
    public class EllipseFitTask : GdtTaskBase
    {
        public override string TaskName => "椭圆拟合";

        /// <summary>一次拟合结果</summary>
        public readonly struct EllipseResult
        {
            public readonly GeometryFit.Ellipse2 Ellipse;
            public readonly double Rms;         // Sampson 距离 RMS（≈ 几何 RMSE）
            public readonly int PointCount;
            public readonly double ContourArea; // 参与拟合轮廓的面积（仅轮廓模式）
            public EllipseResult(GeometryFit.Ellipse2 e, double rms, int n, double area)
            { Ellipse = e; Rms = rms; PointCount = n; ContourArea = area; }
        }

        /// <summary>本次检出的全部椭圆（按面积降序）</summary>
        public List<EllipseResult> Results { get; } = new();

        public int EllipseCount { get; private set; }

        // —— 最佳（面积最大）椭圆的常用量，UI 直接读这些 ——
        public GeometryFit.Ellipse2 Best { get; private set; }
        public double BestRms { get; private set; } = double.NaN;
        public int BestPoints { get; private set; }

        /// <summary>最近一次拟合的迭代次数（代数模式恒为 0）</summary>
        public int LastIterations { get; private set; }

        public override TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "取样方式 0轮廓1亚像素", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "取样:{0}", Group = "取样",
                Tip = "0轮廓：阈值化后取轮廓，逐轮廓拟合，**一张图里多个椭圆都能出**，" +
                "8MP 上约 20ms（最快）。\n" +
                "1亚像素：灰度梯度 + 抛物线拟合定位边缘，只扫目标包围盒，" +
                "8MP 上约 70ms，精度到 0.01px 量级（单目标精密量测用）。\n" +
                "\n" +
                "两者的量测位置不同，按需要选：\n" +
                "  · 轮廓取的是**外沿**。对线宽 3px 的圆环实测半轴偏大 2.2px" +
                "（约半个线宽），量外形/外廓时用它；\n" +
                "  · 亚像素取的是**真实边缘/中心线**。同一圆环误差仅 0.03px。" +
                "量线宽中心、量配合尺寸时用它。\n" +
                "  （线较粗时亚像素会同时取到内外两条边，拟合结果即中心线，" +
                "此时 RMS 约等于半个线宽，属正常。）" },
            new TaskParamDesc { ParamName = "阈值", Min = 1, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "取样" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "取样",
                Tip = "轮廓模式：0 亮前景（亮椭圆/白块），1 暗前景（暗孔/黑块）。\n" +
                "亚像素模式：0 任意边缘，1 暗→亮（上升沿），2 亮→暗（下降沿）。" },
            new TaskParamDesc { ParamName = "扫描步长", Min = 1, Max = 50, DefaultValue = 1,
                DisplayFormat = "步长:{0}px", Group = "取样",
                Tip = "仅亚像素模式：每隔多少像素扫一条线。调大可显著加速，" +
                "但会减少参与拟合的点数（椭圆至少需要几十个点才稳）。" },
            new TaskParamDesc { ParamName = "最小面积%", Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "面积≥{0}%", Group = "筛选",
                Tip = "过滤小于图面此比例的轮廓，用于排除噪点小块。" },
            new TaskParamDesc { ParamName = "最大椭圆数", Min = 1, Max = 50, DefaultValue = 1,
                DisplayFormat = "最多:{0}", Group = "筛选",
                Tip = "最多输出几个椭圆（按面积从大到小取）。设 1 = 只量最大的那个。" },
            new TaskParamDesc { ParamName = "拟合方法 0代数1几何精修", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "拟合:{0}", Group = "拟合",
                Tip = "0代数：Fitzgibbon 直接最小二乘。它是**无偏**的代数拟合 —— " +
                "实测对解析点集误差 < 0.01%（119.994 vs 120），也最快。\n" +
                "1几何精修：在代数初值上做 Sampson 距离的 Levenberg–Marquardt 迭代。\n" +
                "  说明：Fitzgibbon 初值本身已接近几何最优，实测多数场合精修结果" +
                "与代数解**完全一致**（通常 1 次迭代即收敛退出），代价约 25% 耗时。\n" +
                "  保留它是为了点分布极端不均时多一层保险；要更快可以选 0。\n" +
                "注意：两者的误差都不来自拟合算法，而来自**取样点集** —— " +
                "轮廓模式取的是像素边界（对粗线条会偏到外沿），要更高精度请用亚像素模式。" },
            new TaskParamDesc { ParamName = "精修迭代", Min = 1, Max = 200, DefaultValue = 30,
                DisplayFormat = "迭代≤{0}", Group = "拟合",
                Tip = "几何精修的最大迭代次数。通常 3~8 次就已收敛（收敛后自动提前退出），" +
                "所以调大基本不增加耗时。" },
            new TaskParamDesc { ParamName = "边缘点上限", Min = 50, Max = 200000, DefaultValue = 4000,
                DisplayFormat = "点数≤{0}", Group = "性能",
                Tip = "参与拟合的最大点数，超出时按等间隔抽稀。\n" +
                "拟合本身很便宜（8MP 上约 0.5ms），但点数过多会增加取样与转换开销；\n" +
                "4000 点对椭圆拟合已远超需要（几十个点就足以定形）。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算",
                Tip = "每个像素代表多少微米。设 0 只输出像素单位；设了则同时输出 mm。" },
        ];

        /// <summary>是否已由用户在图上手动点过点（≥5）；true 时 Execute 直接用手动点拟合椭圆</summary>
        public bool HasManualPoints { get; private set; }
        public List<GeometryFit.P2> ManualPoints { get; } = new();
        public void SetManualPoints(IEnumerable<GeometryFit.P2> pts)
        { ManualPoints.Clear(); ManualPoints.AddRange(pts); HasManualPoints = true; }
        public void ClearManualPoints() { HasManualPoints = false; ManualPoints.Clear(); }

        public override Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Results.Clear();
            EllipseCount = 0;
            Best = default;
            BestRms = double.NaN;
            BestPoints = 0;
            LastIterations = 0;
            LastPointCount = 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            // —— 手动模式：用户在图上点的点直接最小二乘拟合椭圆（自动取样对不上时用）——
            if (HasManualPoints)
            {
                bool geom = paramValues.Length > 6 && paramValues[6] == 1;
                int itMax = paramValues.Length > 7 ? Math.Max(1, paramValues[7]) : 30;
                if (ManualPoints.Count < 5)
                {
                    LastSummary = "椭圆拟合(手动): 点数不足 5";
                    return dst;
                }
                if (!GeometryFit.FitEllipse(ManualPoints, geom, itMax,
                        out GeometryFit.Ellipse2 me, out double mrms, out int miters))
                {
                    LastSummary = "椭圆拟合(手动): 点集退化，无法拟合椭圆";
                    return dst;
                }
                Results.Clear();
                Results.Add(new EllipseResult(me, mrms, ManualPoints.Count, 0));
                Best = me; BestRms = mrms; BestPoints = ManualPoints.Count;
                EllipseCount = 1; LastIterations = miters; LastPointCount = ManualPoints.Count;
                MmPerPixel = paramValues.Length > 9 ? paramValues[9] / 1000.0 : 0;

                Cv2.Ellipse(dst, new Point((int)Math.Round(me.Cx), (int)Math.Round(me.Cy)),
                    new Size(Math.Max(1, (int)Math.Round(me.A)), Math.Max(1, (int)Math.Round(me.B))),
                    me.ThetaDeg, 0, 360, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                double ct = Math.Cos(me.Theta), st = Math.Sin(me.Theta);
                var mc = new Point((int)Math.Round(me.Cx), (int)Math.Round(me.Cy));
                Cv2.Line(dst, mc, new Point((int)Math.Round(me.Cx + me.A * ct), (int)Math.Round(me.Cy + me.A * st)),
                    Scalar.Yellow, 1, LineTypes.AntiAlias);
                Cv2.Line(dst, mc, new Point((int)Math.Round(me.Cx - me.B * st), (int)Math.Round(me.Cy + me.B * ct)),
                    Scalar.Orange, 1, LineTypes.AntiAlias);
                DrawCross(dst, me.Cx, me.Cy, Scalar.Red, 8);
                DrawText(dst, string.Format("{0}x{1}px {2:F1}deg RMS {3:F2}",
                    (me.A * 2), (me.B * 2), me.ThetaDeg, mrms), me.Cx + 10, me.Cy - 12, Scalar.LimeGreen);
                LastSummary = string.Format(
                    "椭圆拟合(手动): 中心({0:F1},{1:F1}) 半轴 {2} x {3} 长轴方向 {4:F2}deg " +
                    "(偏心率 {5:F3}, 轴比 {6:F3}, 周长 {7}, 面积 {8:F0}px², 点数 {9}, RMS {10:F3}px)",
                    me.Cx, me.Cy, Unit(me.A), Unit(me.B), me.ThetaDeg,
                    me.Eccentricity, me.AxisRatio, Unit(me.Perimeter), me.Area,
                    ManualPoints.Count, mrms);
                return dst;
            }

            int mode = Math.Clamp(paramValues[0], 0, 1);
            int threshold = Math.Clamp(paramValues[1], 1, 255);
            int polarity = Math.Clamp(paramValues[2], 0, 2);
            int step = Math.Max(1, paramValues[3]);
            double minAreaPct = paramValues[4];
            int maxEllipses = Math.Max(1, paramValues[5]);
            bool geometric = paramValues[6] == 1;
            int maxIter = Math.Max(1, paramValues[7]);
            int maxPoints = Math.Clamp(paramValues[8], 50, 200000);
            MmPerPixel = paramValues[9] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);

            if (mode == 0) FitFromContours(gray, threshold, polarity, maxEllipses,
                                            minAreaPct, geometric, maxIter, maxPoints, srcMat.Size());
            else FitFromEdges(gray, threshold, polarity, step, minAreaPct,
                              geometric, maxIter, maxPoints, srcMat.Size());

            if (Results.Count == 0)
            {
                LastSummary = string.Format("椭圆拟合: 未找到可拟合的椭圆（检查阈值/极性/最小面积；点数需 ≥5）");
                return dst;
            }

            // 按面积降序（大目标优先），取前 N
            Results.Sort((a, b) => b.Ellipse.Area.CompareTo(a.Ellipse.Area));
            if (Results.Count > maxEllipses) Results.RemoveRange(maxEllipses, Results.Count - maxEllipses);

            var best = Results[0].Ellipse;
            Best = best;
            BestRms = Results[0].Rms;
            BestPoints = Results[0].PointCount;
            EllipseCount = Results.Count;

            // —— 绘制 ——
            for (int i = 0; i < Results.Count; i++)
            {
                var r = Results[i];
                var e = r.Ellipse;
                Scalar col = i == 0 ? Scalar.LimeGreen : Scalar.Cyan;
                int thick = i == 0 ? 2 : 1;

                Cv2.Ellipse(dst, new Point((int)Math.Round(e.Cx), (int)Math.Round(e.Cy)),
                    new Size(Math.Max(1, (int)Math.Round(e.A)), Math.Max(1, (int)Math.Round(e.B))),
                    e.ThetaDeg, 0, 360, col, thick, LineTypes.AntiAlias);

                // 长短轴方向线：让"角度"看得见
                double ct = Math.Cos(e.Theta), st = Math.Sin(e.Theta);
                var c = new Point((int)Math.Round(e.Cx), (int)Math.Round(e.Cy));
                Cv2.Line(dst, c, new Point((int)Math.Round(e.Cx + e.A * ct), (int)Math.Round(e.Cy + e.A * st)),
                    Scalar.Yellow, 1, LineTypes.AntiAlias);
                Cv2.Line(dst, c, new Point((int)Math.Round(e.Cx - e.B * st), (int)Math.Round(e.Cy + e.B * ct)),
                    Scalar.Orange, 1, LineTypes.AntiAlias);
                DrawCross(dst, e.Cx, e.Cy, Scalar.Red, 8);

                string tag = Results.Count > 1 ? "#" + (i + 1) + " " : "";
                DrawText(dst, string.Format("{0}{1}x{2}px {3:F1}deg RMS {4:F2}",
                    tag, (e.A * 2), (e.B * 2), e.ThetaDeg, r.Rms),
                    e.Cx + 10, e.Cy - 12, col);
            }

            DrawText(dst, string.Format("椭圆 {0} 个  轴比 {1:F3}  偏心率 {2:F3}",
                EllipseCount, best.AxisRatio, best.Eccentricity), 6, 20, Scalar.LimeGreen);

            LastSummary = string.Format(
                "椭圆拟合: {0} 个  最佳 中心({1:F1},{2:F1}) 半轴 {3} x {4} 长轴方向 {5:F2}deg " +
                "(偏心率 {6:F3}, 轴比 {7:F3}, 周长 {8}, 面积 {9:F0}px², 点数 {10}, RMS {11:F3}px{12})",
                EllipseCount, best.Cx, best.Cy,
                Unit(best.A), Unit(best.B), best.ThetaDeg,
                best.Eccentricity, best.AxisRatio,
                Unit(best.Perimeter), best.Area, BestPoints, BestRms,
                LastIterations > 0 ? string.Format(", 迭代 {0}", LastIterations) : "");
            return dst;
        }

        /// <summary>轮廓模式：逐轮廓拟合椭圆，支持多目标</summary>
        private void FitFromContours(Mat gray, int threshold, int polarity, int maxEllipses,
            double minAreaPct, bool geometric, int maxIter, int maxPoints, Size imgSize)
        {
            using Mat bin = new Mat();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);

            Cv2.FindContours(bin, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            double minArea = imgSize.Width * (double)imgSize.Height * minAreaPct / 100.0;
            double maxSane = Math.Max(imgSize.Width, imgSize.Height) * 20.0;   // 拒绝荒谬结果

            // 只拟合面积最大的前若干个轮廓：小噪块既无意义又费时
            var cand = new List<(double area, Point[] pts)>();
            foreach (Point[] c in contours)
            {
                double a = Cv2.ContourArea(c);
                if (a >= minArea && c.Length >= 5) cand.Add((a, c));
            }
            if (cand.Count == 0) return;
            cand.Sort((x, y) => y.area.CompareTo(x.area));

            var buf = new List<GeometryFit.P2>();
            int limit = Math.Min(cand.Count, Math.Max(maxEllipses * 4, maxEllipses));
            for (int i = 0; i < limit && Results.Count < maxEllipses; i++)
            {
                Point[] c = cand[i].pts;
                // 等间隔抽稀：点数远多于拟合需要，抽稀不损精度却能省下转换开销
                int stride = Math.Max(1, c.Length / maxPoints);
                buf.Clear();
                for (int k = 0; k < c.Length; k += stride)
                    buf.Add(new GeometryFit.P2(c[k].X, c[k].Y));
                if (buf.Count < 5) continue;

                if (!GeometryFit.FitEllipse(buf, geometric, maxIter,
                        out GeometryFit.Ellipse2 e, out double rms, out int iters))
                    continue;
                if (e.A > maxSane || e.B > maxSane || e.A / Math.Max(1e-9, e.B) > 1000) continue;

                Results.Add(new EllipseResult(e, rms, buf.Count, cand[i].area));
                LastIterations = iters;
            }
        }

        /// <summary>
        /// 亚像素模式：梯度抛物线定位边缘，再做一轮内点筛选后精拟合。
        /// 椭圆边缘朝向各个方向，所以沿行、沿列各扫一遍再合并 —— 只扫一个方向
        /// 会在与扫描线平行的弧段上取不到点（那部分边缘的点会严重缺失，
        /// 导致拟合被"半圈点"带偏）。
        /// </summary>
        private void FitFromEdges(Mat gray, int threshold, int polarity, int step,
            double minAreaPct, bool geometric, int maxIter, int maxPoints, Size imgSize)
        {
            // 先用轮廓定位"最大的目标"，把边缘点限制在它的包围盒里，
            // 否则整图的纹理/噪点都会混进同一个椭圆拟合里。
            using Mat bin = new Mat();
            Cv2.Threshold(gray, bin, threshold, 255,
                polarity == 2 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
            Cv2.FindContours(bin, out Point[][] cs, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            double minArea = imgSize.Width * (double)imgSize.Height * minAreaPct / 100.0;
            Rect box = new Rect();
            double bestArea = 0;
            foreach (Point[] c in cs)
            {
                double a = Cv2.ContourArea(c);
                if (a >= minArea && a > bestArea) { bestArea = a; box = Cv2.BoundingRect(c); }
            }
            if (bestArea <= 0)
            {
                box = new Rect(0, 0, imgSize.Width, imgSize.Height);   // 没找到明显目标就全图
            }
            // 留一圈余量，避免边缘刚好压在包围盒上被切掉
            box = new Rect(Math.Max(0, box.X - 4), Math.Max(0, box.Y - 4),
                           Math.Min(imgSize.Width - Math.Max(0, box.X - 4), box.Width + 8),
                           Math.Min(imgSize.Height - Math.Max(0, box.Y - 4), box.Height + 8));

            // 沿行、沿列都扫：椭圆各方向都有边缘，单方向会漏掉与扫描线平行的弧段。
            //
            // 只扫描目标包围盒而不是整图：亚像素扫描是**逐像素的托管循环**，
            // 8MP 上两个方向要跑 1600 万次，实测 1230ms。而一个典型目标只占
            // 图面百分之几，限制到包围盒后扫描量降到几十万次，耗时降到几十毫秒。
            // （Mat(roi) 是父图视图，At() 读到的仍是整图对应像素，梯度正确；
            //   得到的点再统一加回 box 偏移即可。）
            var inBox = new List<GeometryFit.P2>();
            double edgeThr = Math.Max(4, threshold * 0.3);   // 梯度阈值按灰度阈值折算

            // 包围盒必须夹进图像范围：上面把 box 向外扩了 4px 做余量，
            // 小图（例如 1x1、8x8）上扩完就超出图像尺寸了，
            // 直接 new Mat(gray, rect) 会抛 "0 <= roi.x && ..." ROI 越界。
            int bx = Math.Clamp(box.X, 0, Math.Max(0, gray.Cols - 1));
            int by = Math.Clamp(box.Y, 0, Math.Max(0, gray.Rows - 1));
            int bw = Math.Min(Math.Max(2, box.Width), gray.Cols - bx);
            int bh = Math.Min(Math.Max(2, box.Height), gray.Rows - by);
            if (bw < 3 || bh < 3) return;   // 区域太小，梯度扫描没有意义

            using (Mat roi = new Mat(gray, new Rect(bx, by, bw, bh)))
            {
                var part = GeometryFit.SubPixelEdgePoints(roi, true, step, edgeThr, polarity);
                foreach (var p in part) inBox.Add(new GeometryFit.P2(p.X + bx, p.Y + by));
                part = GeometryFit.SubPixelEdgePoints(roi, false, step, edgeThr, polarity);
                foreach (var p in part) inBox.Add(new GeometryFit.P2(p.X + bx, p.Y + by));
            }

            if (inBox.Count < 5) return;

            // 抽稀到上限
            var use = new List<GeometryFit.P2>();
            int stride2 = Math.Max(1, inBox.Count / maxPoints);
            for (int i = 0; i < inBox.Count; i += stride2) use.Add(inBox[i]);

            if (!GeometryFit.FitEllipse(use, geometric, maxIter,
                    out GeometryFit.Ellipse2 e1, out double rms1, out int it1))
                return;

            double maxSane = Math.Max(imgSize.Width, imgSize.Height) * 20.0;
            if (e1.A > maxSane || e1.B > maxSane || e1.A / Math.Max(1e-9, e1.B) > 1000) return;

            // —— 内点筛选后重拟合 ——
            // 包围盒里仍可能有别的纹理；按"到初步椭圆的 Sampson 距离"剔除外点，
            // 再用内点重拟合一次，能把杂点的影响基本消掉。
            double tol = Math.Max(2.0, 3.0 * rms1);
            var inl = new List<GeometryFit.P2>(use.Count);
            foreach (var p in use)
                if (Math.Abs(e1.SampsonDistance(p.X, p.Y)) <= tol) inl.Add(p);

            GeometryFit.Ellipse2 final = e1;
            double finalRms = rms1;
            int finalIt = it1;
            if (inl.Count >= 5 && inl.Count < use.Count)
            {
                if (GeometryFit.FitEllipse(inl, geometric, maxIter,
                        out GeometryFit.Ellipse2 e2, out double rms2, out int it2))
                {
                    if (e2.A <= maxSane && e2.B <= maxSane && e2.A / Math.Max(1e-9, e2.B) <= 1000)
                    { final = e2; finalRms = rms2; finalIt = it2; }
                }
            }

            Results.Add(new EllipseResult(final, finalRms, use.Count, bestArea));
            LastIterations = finalIt;
            LastPointCount = use.Count;
        }
    }
}
