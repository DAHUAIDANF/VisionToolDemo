using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 圆拟合：二值化后取最大轮廓用 FitEllipse 做最小二乘椭圆拟合，
    /// 橙色画出椭圆、红色十字标出中心，摘要输出圆心、长短轴与倾角。
    /// 适合圆孔/圆销/瓶口定位测量，配合 ROI 框选使用。
    /// 参数：阈值、前景极性（0=亮为前景 1=暗为前景）。
    /// </summary>
    public class CircleFitTask : IVisionTask, IResultReporter
    {
        public string TaskName => "圆拟合";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮/1暗",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "前景:{0}"
            }
        };

        /// <summary>是否已由用户在图上手动点过点（≥3）；true 时 Execute 直接用手动点拟合圆</summary>
        public bool HasManualPoints { get; private set; }
        public List<GeometryFit.P2> ManualPoints { get; } = new();
        public void SetManualPoints(IEnumerable<GeometryFit.P2> pts)
        { ManualPoints.Clear(); ManualPoints.AddRange(pts); HasManualPoints = true; }
        public void ClearManualPoints() { HasManualPoints = false; ManualPoints.Clear(); }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 手动模式：用户在图上点的点直接最小二乘拟合圆（自动选最大轮廓对不上时用）——
            if (HasManualPoints)
            {
                if (!GeometryFit.FitCircle(ManualPoints, out GeometryFit.Circle2 mc))
                {
                    LastSummary = "圆拟合(手动): 点数不足 3 或点集退化";
                    return dst;
                }
                Cv2.Circle(dst, new Point((int)Math.Round(mc.Cx), (int)Math.Round(mc.Cy)),
                    (int)Math.Round(mc.R), Scalar.Orange, 2, LineTypes.AntiAlias);
                var mc2 = new Point((int)Math.Round(mc.Cx), (int)Math.Round(mc.Cy));
                Cv2.Line(dst, mc2.X - 6, mc2.Y, mc2.X + 6, mc2.Y, Scalar.Red, 1, LineTypes.AntiAlias);
                Cv2.Line(dst, mc2.X, mc2.Y - 6, mc2.X, mc2.Y + 6, Scalar.Red, 1, LineTypes.AntiAlias);
                LastSummary = string.Format("圆拟合(手动): 圆心({0:F1},{1:F1}) 半径{2:F1} 点数{3}",
                    mc.Cx, mc.Cy, mc.R, ManualPoints.Count);
                return dst;
            }

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bin = new())
            {
                ThresholdTypes type = paramValues[1] % 2 == 0 ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;
                Cv2.Threshold(gray, bin, paramValues[0], 255, type);

                Point[][] contours;
                HierarchyIndex[] hier;
                Cv2.FindContours(bin, out contours, out hier, RetrievalModes.External,
                    ContourApproximationModes.ApproxSimple);

                // 取面积最大的轮廓（过滤面积不足 0.5% 图面的噪声）
                double minArea = srcMat.Cols * (double)srcMat.Rows * 0.005;
                double bestArea = 0;
                Point[] best = null;
                foreach (Point[] c in contours)
                {
                    double area = Cv2.ContourArea(c);
                    if (area > bestArea && area >= minArea)
                    {
                        bestArea = area;
                        best = c;
                    }
                }

                if (best == null || best.Length < 5)
                {
                    LastSummary = "圆拟合: 未找到足够大的轮廓";
                    return dst;
                }

                RotatedRect ell = Cv2.FitEllipse(best);
                Cv2.Ellipse(dst, ell, Scalar.Orange, 2, LineTypes.AntiAlias);
                int cx = (int)Math.Round(ell.Center.X), cy = (int)Math.Round(ell.Center.Y);
                Cv2.Line(dst, cx - 6, cy, cx + 6, cy, Scalar.Red, 1, LineTypes.AntiAlias);
                Cv2.Line(dst, cx, cy - 6, cx, cy + 6, Scalar.Red, 1, LineTypes.AntiAlias);

                LastSummary = string.Format(
                    "圆拟合: 圆心({0:F1},{1:F1}) 长轴{2:F1} 短轴{3:F1} 倾角{4:F1}°",
                    ell.Center.X, ell.Center.Y, ell.Size.Width, ell.Size.Height, ell.Angle);
                return dst;
            }
        }
    }
}
