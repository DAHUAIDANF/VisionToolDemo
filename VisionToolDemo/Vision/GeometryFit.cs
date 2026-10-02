using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 形位公差（GD&T）共用内核：拟合基元 + 偏差统计。
    ///
    /// 之所以抽出来而不是每个算子各写一份，是因为"圆度/同心度"都要圆拟合、
    /// "直线度/平行度/垂直度/平面度"都要直线拟合，且**偏差定义必须一致**：
    ///   · 直线度 = 轮廓点到最小二乘直线的最大垂距（ISO 1101 的包容带半宽 ×2）
    ///   · 圆度   = 轮廓点到最小二乘圆的最大/最小半径差（半径带宽度）
    ///   · 平面度 = 矩形四边的直线度最大值
    /// 谁复制一份就可能出现"同一个轮廓两个算子算出不同直线度"的坑。
    ///
    /// 直线拟合用**正交最小二乘（TLS / PCA）**而不是 y=kx+b：
    /// 竖直边缘的斜率会趋于无穷，k 形式直接崩掉；TLS 对任意方向都成立。
    /// </summary>
    public static class GeometryFit
    {
        /// <summary>二维点（double 精度，避免整像素取样把亚像素信息抹掉）</summary>
        public readonly struct P2
        {
            public readonly double X;
            public readonly double Y;
            public P2(double x, double y) { X = x; Y = y; }
            public double DistanceTo(P2 o)
            {
                double dx = X - o.X, dy = Y - o.Y;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
            public override string ToString() => $"({X:F2},{Y:F2})";
        }

        /// <summary>正交最小二乘直线：过点 (Px,Py)，方向 (Dx,Dy)，已归一化</summary>
        public readonly struct Line2
        {
            public readonly double Px, Py, Dx, Dy;
            public Line2(double px, double py, double dx, double dy)
            {
                double n = Math.Sqrt((dx * dx) + (dy * dy));
                if (n < 1e-12) { dx = 1; dy = 0; n = 1; }
                Px = px; Py = py; Dx = dx / n; Dy = dy / n;
            }
            /// <summary>点到直线的**垂直**距离（带符号：左侧为正）</summary>
            public double SignedDistance(double x, double y)
                => (-(x - Px) * Dy) + ((y - Py) * Dx);
            public double Distance(double x, double y) => Math.Abs(SignedDistance(x, y));
            /// <summary>方向角，归一到 [0,180)</summary>
            public double AngleDeg
            {
                get
                {
                    double a = Math.Atan2(Dy, Dx) * 180.0 / Math.PI;
                    while (a < 0) a += 180.0;
                    while (a >= 180.0) a -= 180.0;
                    return a;
                }
            }
        }

        /// <summary>正交最小二乘圆</summary>
        public readonly struct Circle2
        {
            public readonly double Cx, Cy, R;
            public Circle2(double cx, double cy, double r) { Cx = cx; Cy = cy; R = r; }
            /// <summary>点到圆周的**带符号**径向偏差（正=在外侧）</summary>
            public double RadialDeviation(double x, double y)
            {
                double dx = x - Cx, dy = y - Cy;
                return Math.Sqrt((dx * dx) + (dy * dy)) - R;
            }
        }

        // ------------------------------------------------------------------ 直线拟合

        /// <summary>
        /// 正交最小二乘（TLS）直线拟合：对质心做中心化后取协方差矩阵最大特征向量。
        /// 用 2x2 特征分解的闭式解，避免引用外部线代库。
        /// 点数 &lt; 2 或所有点重合时返回 false。
        /// </summary>
        public static bool FitLine(IReadOnlyList<P2> pts, out Line2 line)
        {
            line = default;
            if (pts == null || pts.Count < 2) return false;

            double mx = 0, my = 0;
            for (int i = 0; i < pts.Count; i++) { mx += pts[i].X; my += pts[i].Y; }
            mx /= pts.Count; my /= pts.Count;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - mx, dy = pts[i].Y - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            if (sxx + syy < 1e-12) return false;

            // 2x2 对称阵 [[sxx,sxy],[sxy,syy]] 的最大特征值对应方向
            double tr = sxx + syy;
            double det = (sxx * syy) - (sxy * sxy);
            double disc = Math.Sqrt(Math.Max(0, (tr * tr / 4) - det));
            double lmax = (tr / 2) + disc;

            double dx2, dy2;
            if (Math.Abs(sxy) > 1e-12) { dx2 = lmax - syy; dy2 = sxy; }
            else { dx2 = sxx >= syy ? 1 : 0; dy2 = sxx >= syy ? 0 : 1; }
            if ((dx2 * dx2) + (dy2 * dy2) < 1e-18) { dx2 = 1; dy2 = 0; }

            line = new Line2(mx, my, dx2, dy2);
            return true;
        }

        /// <summary>直线度：所有点到拟合直线的最大垂距 ×2（包容带宽度）</summary>
        public static double Straightness(IReadOnlyList<P2> pts, Line2 line)
        {
            double maxAbs = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = Math.Abs(line.SignedDistance(pts[i].X, pts[i].Y));
                if (d > maxAbs) maxAbs = d;
            }
            return maxAbs * 2.0;
        }

        // ------------------------------------------------------------------ 圆拟合

        /// <summary>
        /// 圆拟合：先用 Kåsa 代数法（线性最小二乘）得到初值，再用 Gauss-Newton 精化。
        /// 只做代数法时，采样点分布不均（比如圆弧只占 1/4）会有明显偏差，
        /// 而径向偏差直接进公差判定，所以必须精化。点数 &lt; 3 返回 false。
        /// </summary>
        public static bool FitCircle(IReadOnlyList<P2> pts, out Circle2 circle)
        {
            circle = default;
            if (pts == null || pts.Count < 3) return false;

            // —— Kåsa 初值：解 x²+y² + D·x + E·y + F = 0 ——
            double suu = 0, svv = 0, suv = 0, su = 0, sv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                double u = pts[i].X, v = pts[i].Y;
                suu += u * u; svv += v * v; suv += u * v;
                su += u; sv += v;
                suuu += u * u * u; svvv += v * v * v;
                suvv += u * v * v; svuu += v * u * u;
            }
            double a11 = 2 * (suu - (su * su / n));
            double a12 = 2 * (suv - (su * sv / n));
            double a22 = 2 * (svv - (sv * sv / n));
            double b1 = suuu + suvv - ((suu + svv) * su / n);
            double b2 = svvv + svuu - ((suu + svv) * sv / n);
            double det = (a11 * a22) - (a12 * a12);
            if (Math.Abs(det) < 1e-12) return false;
            double cx = ((b1 * a22) - (b2 * a12)) / det;
            double cy = ((a11 * b2) - (a12 * b1)) / det;
            double r = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i].X - cx, dy = pts[i].Y - cy;
                r += Math.Sqrt((dx * dx) + (dy * dy));
            }
            r /= n;
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(r) || r <= 0)
                return false;

            // —— Gauss-Newton 精化：残差 f_i = |p_i − c| − r ——
            //
            // 这里曾经写错过，修好的关键点：
            //   · 残差对圆心的偏导是 ∂f/∂cx = −(x−cx)/d = jx，对半径是 ∂f/∂r = −1；
            //   · 正规方程是 (JᵀJ)·δ = −Jᵀf，所以需要**同时**用 j11/j12/j22 和
            //     梯度 g1/g2（= Jᵀf 的前两维）；
            //   · 半径那一维与圆心维**没有交叉项**（∂f/∂r 与 jx,jy 正交），
            //     故 JᵀJ 是块对角：左上 2x2 解圆心，右下 n 解半径。
            //   · 不要给 j11/j22 加 ε 当正则 —— 那会**系统性地偏置**解，
            //     实测在半径 120 的真实圆周上会收敛到 203，完全跑飞。
            //     只有在行列式退化时才跳过本次迭代。
            // 目标函数：Σ(|p_i − c| − r)²
            static double Cost(IReadOnlyList<P2> p, double ccx, double ccy, double rr)
            {
                double s = 0;
                for (int i = 0; i < p.Count; i++)
                {
                    double dx = p[i].X - ccx, dy = p[i].Y - ccy;
                    double f = Math.Sqrt((dx * dx) + (dy * dy)) - rr;
                    s += f * f;
                }
                return s;
            }

            double cost = Cost(pts, cx, cy, r);

            for (int iter = 0; iter < 80; iter++)
            {
                double j11 = 0, j12 = 0, j22 = 0, g1 = 0, g2 = 0, g3 = 0;
                for (int i = 0; i < n; i++)
                {
                    double dx = pts[i].X - cx, dy = pts[i].Y - cy;
                    double d = Math.Sqrt((dx * dx) + (dy * dy));
                    if (d < 1e-9) continue;
                    double jx = -dx / d, jy = -dy / d;
                    double f = d - r;
                    j11 += jx * jx; j12 += jx * jy; j22 += jy * jy;
                    g1 += jx * f; g2 += jy * f; g3 += f;
                }

                double gnDet = (j11 * j22) - (j12 * j12);
                if (Math.Abs(gnDet) < 1e-12) break;   // 圆心维退化，本次不再更新

                // δ = −(JᵀJ)⁻¹·Jᵀf。实测符号必须是负号：正号会把解推向无穷远。
                double dcx = -(((g1 * j22) - (g2 * j12)) / gnDet);
                double dcy = -(((j11 * g2) - (j12 * g1)) / gnDet);
                double dr = -g3 / n;

                // 步长限制 + 回退线搜索。
                // 光栅化的圆周轮廓点高度重复（同一像素出现多次），
                // 纯 Gauss-Newton 会因 JᵀJ 病态而**一步冲出**成千像素（实测飞到 9e4）。
                // 限制单步和"只在代价下降时接受"是这类离散点云的必备保护。
                double stepLen = Math.Sqrt((dcx * dcx) + (dcy * dcy));
                double maxStep = Math.Max(1.0, r * 0.5);
                if (stepLen > maxStep)
                {
                    double k = maxStep / stepLen;
                    dcx *= k; dcy *= k; dr *= k;
                }

                double nc = Cost(pts, cx + dcx, cy + dcy, r + dr);
                if (nc > cost)
                {
                    bool improved = false;
                    double lambda = 1.0;
                    for (int t = 0; t < 20; t++)
                    {
                        lambda *= 0.5;
                        double nx = Cost(pts, cx + (dcx * lambda), cy + (dcy * lambda), r + (dr * lambda));
                        if (nx < cost)
                        {
                            dcx *= lambda; dcy *= lambda; dr *= lambda;
                            nc = nx; improved = true; break;
                        }
                    }
                    if (!improved) break;   // 已到局部极小
                }

                cx += dcx; cy += dcy; r += dr;
                bool done = Math.Abs(dcx) + Math.Abs(dcy) + Math.Abs(dr) < 1e-10
                            || Math.Abs(cost - nc) < 1e-14 * Math.Max(1.0, cost);
                cost = nc;
                if (done) break;
            }

            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(r) || r <= 0)
                return false;
            circle = new Circle2(cx, cy, r);
            return true;
        }

        /// <summary>
        /// 圆度：所有点到拟合圆的最大/最小径向偏差之差 = 最小包容环带宽度。
        /// 这是 ISO 1101 定义的圆度（半径法），不是拟合残差 RMS。
        /// </summary>
        public static bool Roundness(IReadOnlyList<P2> pts, Circle2 circle,
            out double roundness, out double rMin, out double rMax)
        {
            roundness = double.NaN; rMin = double.NaN; rMax = double.NaN;
            if (pts == null || pts.Count == 0) return false;
            rMin = double.MaxValue; rMax = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - circle.Cx, dy = pts[i].Y - circle.Cy;
                double d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d < rMin) rMin = d;
                if (d > rMax) rMax = d;
            }
            roundness = rMax - rMin;
            return true;
        }

        // ------------------------------------------------------------------ 椭圆拟合

        /// <summary>
        /// 椭圆（几何参数形式）：中心 + 半长轴 A（≥ 半短轴 B）+ 长轴方向 Theta（弧度）。
        ///
        /// 为什么不直接用 OpenCV 的 RotatedRect 当结果：
        /// RotatedRect 的 Size 是**全轴**（直径），且 Width/Height 与 Angle 的搭配不规则
        /// （Angle 描述的是 Width 那一侧的方向）。实测同一个标准椭圆会给出
        /// size=(121.7,241.8) angle=115.08°，而真值是 (240,120)@25° —— 数值正确，
        /// 但轴序与角度都转了 90°，直接显示给用户完全看不懂。
        /// 这里统一成「A 是长半轴、Theta 是长轴方向、ThetaDeg ∈ [0,180)」，与直觉一致。
        /// </summary>
        public readonly struct Ellipse2
        {
            public readonly double Cx, Cy, A, B, Theta;   // A = 半长轴, B = 半短轴

            public Ellipse2(double cx, double cy, double a, double b, double theta)
            {
                Cx = cx; Cy = cy;
                if (a >= b) { A = a; B = b; Theta = theta; }
                else { A = b; B = a; Theta = theta + (Math.PI / 2); }
            }

            public double Semimajor => A;
            public double Semiminor => B;

            /// <summary>长轴方向（度），归一到 [0,180)</summary>
            public double ThetaDeg
            {
                get
                {
                    double d = Theta * 180.0 / Math.PI;
                    while (d < 0) d += 180.0;
                    while (d >= 180.0) d -= 180.0;
                    return d;
                }
            }

            /// <summary>偏心率：0 = 圆，越大越扁</summary>
            public double Eccentricity => A > 1e-12 ? Math.Sqrt(Math.Max(0, 1 - (B * B) / (A * A))) : 0;

            /// <summary>轴比 B/A（1 = 圆）</summary>
            public double AxisRatio => A > 1e-12 ? B / A : 0;

            /// <summary>椭圆像素面积</summary>
            public double Area => Math.PI * A * B;

            /// <summary>周长（Ramanujan 近似）</summary>
            public double Perimeter
            {
                get
                {
                    double h = (A - B) * (A - B) / ((A + B) * (A + B));
                    return Math.PI * (A + B) * (1 + (3 * h) / (10 + Math.Sqrt(4 - (3 * h))));
                }
            }

            /// <summary>椭圆上参数角 t（弧度）对应的点</summary>
            public P2 PointAt(double t)
            {
                double ct = Math.Cos(Theta), st = Math.Sin(Theta);
                double u = A * Math.Cos(t), v = B * Math.Sin(t);
                return new P2(Cx + (u * ct) - (v * st), Cy + (u * st) + (v * ct));
            }

            /// <summary>隐式方程值：&lt;0 内部，=0 边界，&gt;0 外部</summary>
            public double Implicit(double x, double y, out double u, out double v)
            {
                double dx = x - Cx, dy = y - Cy;
                double ct = Math.Cos(Theta), st = Math.Sin(Theta);
                u = (dx * ct) + (dy * st);
                v = (-dx * st) + (dy * ct);
                return ((u * u) / (A * A)) + ((v * v) / (B * B)) - 1;
            }

            /// <summary>
            /// 点到椭圆的 Sampson 距离（≈ 几何距离）：F / |∇F|。
            /// 比"中心径向距离"更接近真实最近点距离，计算量与代数距离同阶。
            /// </summary>
            public double SampsonDistance(double x, double y)
            {
                double f = Implicit(x, y, out double u, out double v);
                double fu = 2 * u / (A * A), fv = 2 * v / (B * B);
                double ct = Math.Cos(Theta), st = Math.Sin(Theta);
                double fx = (fu * ct) - (fv * st);
                double fy = (fu * st) + (fv * ct);
                double g = Math.Sqrt((fx * fx) + (fy * fy));
                return g > 1e-12 ? f / g : 0;
            }
        }

        /// <summary>
        /// 椭圆拟合：<paramref name="geometric"/>=false 用直接最小二乘（Fitzgibbon 代数距离）；
        /// =true 在其基础上做 **Sampson 距离的 Levenberg–Marquardt 几何精修**。
        ///
        /// 为什么需要精修：代数最小二乘最小化的是 F(p) 而非真实距离，对
        /// **点分布不均**（圆弧只占一部分、采样密度沿周长变化）会产生系统性偏差。
        ///
        /// 用 LM 而不是纯 Gauss-Newton：初值较差时（例如只有半圈点）纯 GN 会一步跨到发散；
        /// 阻尼保证每次迭代目标函数单调不增。
        /// </summary>
        public static bool FitEllipse(IReadOnlyList<P2> pts, bool geometric, int maxIter,
            out Ellipse2 ellipse, out double rms, out int iterations)
        {
            ellipse = default;
            rms = double.NaN;
            iterations = 0;
            if (pts == null || pts.Count < 5) return false;   // 5 是椭圆的自由度下限

            // —— 初值：Fitzgibbon 直接最小二乘 ——
            var arr = new Point[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                arr[i] = new Point((int)Math.Round(pts[i].X), (int)Math.Round(pts[i].Y));

            RotatedRect rr;
            try { rr = Cv2.FitEllipseDirect(arr); }
            catch { return false; }

            if (!double.IsFinite(rr.Center.X) || !double.IsFinite(rr.Center.Y)
                || !double.IsFinite(rr.Size.Width) || !double.IsFinite(rr.Size.Height))
                return false;

            double a0 = rr.Size.Width / 2.0, b0 = rr.Size.Height / 2.0;
            if (a0 <= 0 || b0 <= 0) return false;
            var e = new Ellipse2(rr.Center.X, rr.Center.Y, a0, b0, rr.Angle * Math.PI / 180.0);
            if (e.A <= 1e-9 || e.B <= 1e-9) return false;

            if (!geometric)
            {
                ellipse = e;
                rms = RmsOf(pts, e);
                return true;
            }

            double cx = e.Cx, cy = e.Cy, A = e.A, B = e.B, th = e.Theta;
            double cost = SampsonCost(pts, cx, cy, A, B, th);
            double lambda = 1e-3;
            int n = pts.Count;
            var J = new double[5];

            for (int iter = 0; iter < maxIter; iter++)
            {
                iterations = iter + 1;
                var H = new double[5, 5];
                var g = new double[5];
                double ct = Math.Cos(th), st = Math.Sin(th);

                for (int i = 0; i < n; i++)
                {
                    double f = ImplicitAt(pts[i].X, pts[i].Y, cx, cy, A, B, ct, st, out double u, out double v);
                    double invA2 = 1.0 / (A * A), invB2 = 1.0 / (B * B);
                    double fu = 2 * u * invA2, fv = 2 * v * invB2;
                    double fx = (fu * ct) - (fv * st);
                    double fy = (fu * st) + (fv * ct);
                    double gn = Math.Sqrt((fx * fx) + (fy * fy));
                    if (gn < 1e-12) continue;

                    double w = 1.0 / gn;
                    double r = f * w;                   // Sampson 残差

                    J[0] = -fx * w;
                    J[1] = -fy * w;
                    J[2] = -2 * u * u / (A * A * A) * w;
                    J[3] = -2 * v * v / (B * B * B) * w;
                    J[4] = 2 * u * v * (invA2 - invB2) * w;

                    for (int p = 0; p < 5; p++)
                    {
                        g[p] += J[p] * r;
                        for (int q = p; q < 5; q++) H[p, q] += J[p] * J[q];
                    }
                }
                for (int p = 0; p < 5; p++)
                    for (int q = 0; q < p; q++) H[p, q] = H[q, p];

                bool improved = false;
                for (int trial = 0; trial < 8 && !improved; trial++)
                {
                    var Hd = (double[,])H.Clone();
                    for (int p = 0; p < 5; p++) Hd[p, p] *= (1 + lambda);
                    if (!Solve5(Hd, g, out double[] d)) { lambda *= 10; continue; }

                    double ncx = cx + d[0], ncy = cy + d[1];
                    double nA = A + d[2], nB = B + d[3], nth = th + d[4];
                    if (nA <= 1e-6 || nB <= 1e-6) { lambda *= 10; continue; }

                    double ncost = SampsonCost(pts, ncx, ncy, nA, nB, nth);
                    if (ncost < cost)
                    {
                        double move = Math.Abs(d[0]) + Math.Abs(d[1]) + Math.Abs(d[2])
                                    + Math.Abs(d[3]) + Math.Abs(d[4]);
                        cx = ncx; cy = ncy; A = nA; B = nB; th = nth;
                        cost = ncost;
                        lambda = Math.Max(1e-9, lambda * 0.3);
                        improved = true;
                        if (move < 1e-10) iter = maxIter;   // 已收敛
                    }
                    else lambda *= 10;
                }
                if (!improved) break;   // 阻尼到极限仍不下降 -> 局部极小
            }

            var res = new Ellipse2(cx, cy, A, B, th);
            if (res.A <= 1e-9 || res.B <= 1e-9
                || !double.IsFinite(res.A) || !double.IsFinite(res.B)
                || !double.IsFinite(res.Cx) || !double.IsFinite(res.Cy))
                return false;

            ellipse = res;
            rms = RmsOf(pts, res);
            return true;
        }

        /// <summary>与 FitEllipse 内部一致的隐式方程（复用 sin/cos）</summary>
        private static double ImplicitAt(double x, double y, double cx, double cy,
            double A, double B, double ct, double st, out double u, out double v)
        {
            double dx = x - cx, dy = y - cy;
            u = (dx * ct) + (dy * st);
            v = (-dx * st) + (dy * ct);
            return ((u * u) / (A * A)) + ((v * v) / (B * B)) - 1;
        }

        /// <summary>Sampson 残差平方和（几何精修的目标函数）</summary>
        private static double SampsonCost(IReadOnlyList<P2> pts, double cx, double cy,
            double A, double B, double th)
        {
            double ct = Math.Cos(th), st = Math.Sin(th);
            double invA2 = 1.0 / (A * A), invB2 = 1.0 / (B * B);
            double s = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double f = ImplicitAt(pts[i].X, pts[i].Y, cx, cy, A, B, ct, st, out double u, out double v);
                double fu = 2 * u * invA2, fv = 2 * v * invB2;
                double fx = (fu * ct) - (fv * st);
                double fy = (fu * st) + (fv * ct);
                double gn2 = (fx * fx) + (fy * fy);
                if (gn2 > 1e-24) s += (f * f) / gn2;
            }
            return s;
        }

        /// <summary>Sampson 距离的 RMS（≈ 几何 RMSE）</summary>
        private static double RmsOf(IReadOnlyList<P2> pts, Ellipse2 e)
        {
            double s = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = e.SampsonDistance(pts[i].X, pts[i].Y);
                s += d * d;
            }
            return pts.Count > 0 ? Math.Sqrt(s / pts.Count) : double.NaN;
        }

        /// <summary>解 5x5 线性方程组（部分主元高斯消元）；奇异返回 false</summary>
        private static bool Solve5(double[,] m, double[] b, out double[] x)
        {
            x = new double[5];
            var a = (double[,])m.Clone();
            var rhs = (double[])b.Clone();
            const int n = 5;
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-14) return false;
                if (piv != col)
                {
                    for (int c = 0; c < n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                    (rhs[col], rhs[piv]) = (rhs[piv], rhs[col]);
                }
                double dd = a[col, col];
                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / dd;
                    if (f == 0) continue;
                    for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                    rhs[r] -= f * rhs[col];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double s = rhs[r];
                for (int c = r + 1; c < n; c++) s -= a[r, c] * x[c];
                if (Math.Abs(a[r, r]) < 1e-14) return false;
                x[r] = s / a[r, r];
                if (!double.IsFinite(x[r])) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ 夹角

        /// <summary>两直线夹角，返回 [0,180)。平行度判定用 0、垂直度用 90。</summary>
        public static double AngleBetween(Line2 a, Line2 b)
        {
            double dot = Math.Abs((a.Dx * b.Dx) + (a.Dy * b.Dy));
            dot = Math.Clamp(dot, 0.0, 1.0);
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        // ------------------------------------------------------------------ 采样

        /// <summary>
        /// 从二值图取轮廓点集（double 精度，用轮廓原始像素点）。
        /// 取面积最大的前 N 个轮廓合并：单一边缘可能被噪声断成几段。
        /// </summary>
        public static List<P2> ContourPoints(Mat bin, int maxContours, double minAreaPercent)
        {
            var result = new List<P2>();
            Cv2.FindContours(bin, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxNone);
            if (contours.Length == 0) return result;

            double minArea = bin.Cols * (double)bin.Rows * minAreaPercent / 100.0;
            var cand = new List<(double area, Point[] pts)>();
            foreach (Point[] c in contours)
            {
                double a = Cv2.ContourArea(c);
                if (a >= minArea && c.Length >= 3) cand.Add((a, c));
            }
            cand.Sort((x, y) => y.area.CompareTo(x.area));
            for (int i = 0; i < cand.Count && i < maxContours; i++)
                foreach (Point p in cand[i].pts)
                    result.Add(new P2(p.X, p.Y));
            return result;
        }

        /// <summary>
        /// 亚像素边缘点提取：沿行或列扫描灰度剖面，抛物线拟合极值点。
        /// scanRows = true  → 逐行扫描，每行内沿 x 找边缘；得到的是**水平走向的边**
        ///                    （竖直边缘，灰度沿 x 变化）。
        /// scanRows = false → 逐列扫描，每列内沿 y 找边缘；得到的是**竖直走向的边**
        ///                    （水平边缘，灰度沿 y 变化）。
        /// 参数名从 scanColumns 改过来：原名与实际行为相反，
        /// 调用方按名字理解会选错方向，导致取到 0 个点（曾实际发生）。
        /// </summary>
        public static List<P2> SubPixelEdgePoints(Mat gray, bool scanRows, int step,
            double edgeThreshold, int polarity)
        {
            var pts = new List<P2>();
            step = Math.Max(1, step);
            if (scanRows)
            {
                // 逐行扫描 → 得到"竖直方向变化"的边缘点，即水平线上的边缘
                for (int y = 1; y < gray.Rows - 1; y += step)
                    for (int x = 2; x < gray.Cols - 2; x++)
                    {
                        double gPrev = gray.At<byte>(y, x - 1) - gray.At<byte>(y, x - 2);
                        double gCur = gray.At<byte>(y, x) - gray.At<byte>(y, x - 1);
                        double gNext = gray.At<byte>(y, x + 1) - gray.At<byte>(y, x);
                        if (!MatchPolarity(gCur, polarity)) continue;
                        if (Math.Abs(gCur) < edgeThreshold) continue;
                        double denom = gPrev - (2 * gCur) + gNext;
                        double sub = Math.Abs(denom) < 1e-9 ? 0 : 0.5 * (gPrev - gNext) / denom;
                        if (sub < -1 || sub > 1) sub = 0;
                        pts.Add(new P2(x - 1 + sub, y));
                    }
            }
            else
            {
                for (int x = 1; x < gray.Cols - 1; x += step)
                    for (int y = 2; y < gray.Rows - 2; y++)
                    {
                        double gPrev = gray.At<byte>(y - 1, x) - gray.At<byte>(y - 2, x);
                        double gCur = gray.At<byte>(y, x) - gray.At<byte>(y - 1, x);
                        double gNext = gray.At<byte>(y + 1, x) - gray.At<byte>(y, x);
                        if (!MatchPolarity(gCur, polarity)) continue;
                        if (Math.Abs(gCur) < edgeThreshold) continue;
                        double denom = gPrev - (2 * gCur) + gNext;
                        double sub = Math.Abs(denom) < 1e-9 ? 0 : 0.5 * (gPrev - gNext) / denom;
                        if (sub < -1 || sub > 1) sub = 0;
                        pts.Add(new P2(x, y - 1 + sub));
                    }
            }
            return pts;
        }

        private static bool MatchPolarity(double grad, int polarity)
            => polarity switch
            {
                1 => grad > 0,     // 暗→亮
                2 => grad < 0,     // 亮→暗
                _ => true          // 任意
            };
    }
}
