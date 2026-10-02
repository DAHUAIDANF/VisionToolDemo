using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 形位公差算子的共用基类：把"取点"这一段统一掉。
    ///
    /// 7 个公差算子（直线度/圆度/同心度/平行度/垂直度/平面度/点到点）共用同一套取样逻辑，
    /// 差别只在"用拟合出的基元算什么"。把它们各自写一遍会出现取样参数不一致、
    /// 同一个轮廓在不同算子里取到不同点集的坑。
    ///
    /// 取样有两条路径，由"取样方式"参数选择：
    ///   0 = 二值轮廓（阈值 + 找轮廓）—— 适合形状干净、能整块二值化的目标
    ///   1 = 亚像素边缘（灰度梯度 + 抛物线拟合）—— 适合尺寸量测，精度到 0.1px 级
    /// </summary>
    public abstract class GdtTaskBase : IVisionTask, IResultReporter
    {
        public abstract string TaskName { get; }
        public string LastSummary { get; protected set; } = "";

        /// <summary>最近一次拟合用的有效点数</summary>
        public int LastPointCount { get; protected set; }

        /// <summary>像素当量（mm/px）。≤0 表示只输出像素单位。</summary>
        public double MmPerPixel { get; protected set; } = 0;

        public abstract TaskParamDesc[] ParamDescriptions { get; }

        public abstract Mat Execute(Mat srcMat, int[] paramValues);

        /// <summary>把像素量换算成显示字符串：设了 mm/px 就双单位输出</summary>
        protected string Unit(double px)
        {
            if (MmPerPixel > 0)
                return string.Format("{0:F3}px/{1:F5}mm", px, px * MmPerPixel);
            return string.Format("{0:F3}px", px);
        }

        protected string UnitPct(double px, double reference)
        {
            string s = Unit(px);
            if (reference > 1e-9)
                s += string.Format("({0:F3}%)", 100.0 * px / reference);
            return s;
        }

        /// <summary>
        /// 统一取样。参数下标约定（各算子把自己的参数拼成这个数组后调用）：
        ///   mode       取样方式 0二值轮廓 1亚像素边缘
        ///   threshold  二值阈值 / 边缘梯度阈值
        ///   polarity   极性 0亮 1暗（二值）/ 0任意 1暗→亮 2亮→暗（边缘）
        ///   step       扫描步长
        ///   maxContours 二值路径取前 N 大轮廓
        ///   minArea%   二值路径最小面积百分比
        /// </summary>
        protected List<GeometryFit.P2> SamplePoints(Mat gray, int mode, int threshold,
            int polarity, int step, int maxContours, double minAreaPercent)
        {
            if (mode == 1)
            {
                // 亚像素路径：先判断该沿行还是沿列扫。
                // 用 Sobel 分别算两个方向的梯度总能量，谁大沿谁扫（边缘更锐）。
                double gx = 0, gy = 0;
                using (Mat sx = new()) using (Mat sy = new())
                {
                    Cv2.Sobel(gray, sx, MatType.CV_16S, 1, 0, 3);
                    Cv2.Sobel(gray, sy, MatType.CV_16S, 0, 1, 3);
                    using Mat ax = sx.Abs(); using Mat ay = sy.Abs();
                    gx = Cv2.Mean(ax).Val0;
                    gy = Cv2.Mean(ay).Val0;
                }
                // 水平边缘（gy 强，灰度沿 y 变化）→ 需要**逐列**扫描（scanRows=false）；
                // 竖直边缘（gx 强，灰度沿 x 变化）→ 需要**逐行**扫描（scanRows=true）。
                // 判据写反会让采样点数为 0，整条亚像素路径直接失效。
                bool scanRows = gx > gy;
                return GeometryFit.SubPixelEdgePoints(gray, scanRows, step, threshold, polarity);
            }

            using Mat bin = new();
            ThresholdTypes tt = polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            Cv2.Threshold(gray, bin, threshold, 255, tt);
            // 阈值拉到极值会让二值图几乎全白/全黑（没有有效几何），
            // 后续 MinAreaRect/拟合会 native abort —— 这里直接返回空点集，上层会提示"有效点不足"
            long fg = Cv2.CountNonZero(bin);
            double fgRatio = fg / (double)(bin.Cols * (long)bin.Rows);
            if (fgRatio > 0.999 || fgRatio < 0.001)
                return new List<GeometryFit.P2>();
            return GeometryFit.ContourPoints(bin, maxContours, minAreaPercent);
        }

        protected static string PolarityTip =>
            "取样方式=0二值轮廓时：0亮前景(亮物/白块)，1暗前景(暗孔/黑块)。" +
            "取样方式=1亚像素边缘时：0任意边缘，1暗→亮(上升沿)，2亮→暗(下降沿)。";

        // ------------------------------------------------------------------ 绘制工具

        protected static void DrawCross(Mat dst, double x, double y, Scalar color, int arm = 7)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            Cv2.Line(dst, ix - arm, iy, ix + arm, iy, color, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, ix, iy - arm, ix, iy + arm, color, 1, LineTypes.AntiAlias);
        }

        protected static void DrawText(Mat dst, string text, double x, double y, Scalar color)
        {
            // 中文安全绘制（GDI+）：Cv2.PutText 的 Hershey 字体画不了中文
            MatDraw.DrawText(dst, text, Math.Max(2, (int)Math.Round(x)), Math.Max(14, (int)Math.Round(y)), color, 12);
        }

        /// <summary>把点拟合出的直线画到图幅两端</summary>
        protected static void DrawFittedLine(Mat dst, GeometryFit.Line2 line, Scalar color)
        {
            double t = Math.Sqrt((dst.Cols * (double)dst.Cols) + (dst.Rows * (double)dst.Rows));
            var p1 = new Point((int)Math.Round(line.Px - (t * line.Dx)), (int)Math.Round(line.Py - (t * line.Dy)));
            var p2 = new Point((int)Math.Round(line.Px + (t * line.Dx)), (int)Math.Round(line.Py + (t * line.Dy)));
            Cv2.Line(dst, p1, p2, color, 1, LineTypes.AntiAlias);
        }

        /// <summary>
        /// 把参与拟合的点画成小十字。点太多时抽稀，否则整幅糊成一片看不出形状。
        /// </summary>
        protected static void DrawPoints(Mat dst, IReadOnlyList<GeometryFit.P2> pts, Scalar color)
        {
            if (pts == null || pts.Count == 0) return;
            int stride = Math.Max(1, pts.Count / 600);
            for (int i = 0; i < pts.Count; i += stride)
            {
                int ix = (int)Math.Round(pts[i].X), iy = (int)Math.Round(pts[i].Y);
                if (ix < 0 || iy < 0 || ix >= dst.Cols || iy >= dst.Rows) continue;
                dst.Set(iy, ix, color);
            }
        }
    }
}
