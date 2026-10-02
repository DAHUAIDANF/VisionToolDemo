using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 直线拟合：Canny 边缘后把所有边缘点（最多采样 5000 个）用 FitLine(L2)
    /// 拟合为一条直线，红色画出全幅延长线，摘要输出直线角度（-90°~90°，0=水平）。
    /// 适合标定针、传送带/工件边缘等单一直线场景，配合 ROI 框选使用。
    /// 参数：Canny 低/高阈值。
    /// </summary>
    public class LineFitTask : IVisionTask, IResultReporter
    {
        public string TaskName => "直线拟合";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
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
            }
        ];

        /// <summary>是否已由用户在图上手动点过点（≥2）；true 时 Execute 直接用手动点拟合直线</summary>
        public bool HasManualPoints { get; private set; }
        public List<GeometryFit.P2> ManualPoints { get; } = new();
        public void SetManualPoints(IEnumerable<GeometryFit.P2> pts)
        { ManualPoints.Clear(); ManualPoints.AddRange(pts); HasManualPoints = true; }
        public void ClearManualPoints() { HasManualPoints = false; ManualPoints.Clear(); }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat?.Empty() != false)
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 手动模式：用户在图上点的点直接最小二乘拟合直线（边缘自动拟合对不上时用）——
            if (HasManualPoints)
            {
                if (!GeometryFit.FitLine(ManualPoints, out GeometryFit.Line2 ml))
                {
                    LastSummary = "直线拟合(手动): 点数不足 2 或点集退化";
                    return dst;
                }
                double mlen = Math.Max(dst.Cols, dst.Rows) * 2.0;
                Cv2.Line(dst,
                    new Point((int)Math.Round(ml.Px - ml.Dx * mlen), (int)Math.Round(ml.Py - ml.Dy * mlen)),
                    new Point((int)Math.Round(ml.Px + ml.Dx * mlen), (int)Math.Round(ml.Py + ml.Dy * mlen)),
                    Scalar.Red, 2, LineTypes.AntiAlias);
                double mang = ml.AngleDeg;
                while (mang > 90) mang -= 180;
                while (mang < -90) mang += 180;
                LastSummary = string.Format("直线拟合(手动): 角度 {0:F2}° (0=水平, 点数 {1})", -mang, ManualPoints.Count);
                return dst;
            }

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat edges = new();
            using Mat nz = new();
            Cv2.Canny(gray, edges, paramValues[0], paramValues[1]);
            Cv2.FindNonZero(edges, nz);
            int total = nz.Rows;
            if (total < 10)
            {
                LastSummary = "直线拟合: 边缘点过少 (" + total + ")";
                return dst;
            }

            // 边缘点过多时均匀采样，控制拟合耗时
            int want = Math.Min(total, 5000);
            Point2f[] pts = new Point2f[want];
            double step = total / (double)want;
            for (int i = 0; i < want; i++)
            {
                Vec2i p = nz.Get<Vec2i>((int)(i * step), 0);
                pts[i] = new Point2f(p.Item0, p.Item1);
            }

            Line2D line = Cv2.FitLine(pts, DistanceTypes.L2, 0, 0.01, 0.01);
            double vx = line.Vx, vy = line.Vy, x0 = line.X1, y0 = line.Y1;
            double len = Math.Max(dst.Cols, dst.Rows) * 2.0;
            Cv2.Line(dst,
                new Point((int)Math.Round(x0 - (vx * len)), (int)Math.Round(y0 - (vy * len))),
                new Point((int)Math.Round(x0 + (vx * len)), (int)Math.Round(y0 + (vy * len))),
                Scalar.Red, 2, LineTypes.AntiAlias);

            double ang = Math.Atan2(vy, vx) * 180.0 / Math.PI;
            if (ang > 90) ang -= 180;
            if (ang < -90) ang += 180;
            LastSummary = string.Format("直线拟合: 角度 {0:F2}° (0=水平, 边缘点 {1}, 采样 {2})", -ang, total, want);
            return dst;
        }
    }
}