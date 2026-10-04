using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 位置度（Position Tolerance）：实测特征中心与理想位置的距离 ×2。
    ///
    /// 位置度 = 2 × 实测偏差（偏差 = 实测中心到理想中心的距离），这是机械加工里
    /// 圆形公差带的经典定义（被测点必须落在以理想点为圆心、位置度/2 为半径的圆内）。
    ///
    /// 实现：二值图上取**最大轮廓**的中心作实测点，与参数给定的理想位置（图像百分比
    /// 坐标）比较。配「标定」比例尺（mm/px）可输出毫米值，未标定时输出像素值。
    /// 输出：原图叠加理想点（绿十字）、实测点（红圈）、偏差连线与数值。
    /// </summary>
    public class PositionToleranceTask : IVisionTask, IResultReporter
    {
        public string TaskName => "位置度";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次偏差（像素）</summary>
        public double DeviationPx { get; private set; }

        /// <summary>最近一次位置度（2×偏差）</summary>
        public double Tolerance { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "理想中心X%",
                Min = 0,
                Max = 100,
                DefaultValue = 50,
                DisplayFormat = "理想X:{0}%",
                Tip = "理想位置 X（图像宽度百分比）。例如目标应在正中央 → 50"
            },
            new TaskParamDesc
            {
                ParamName = "理想中心Y%",
                Min = 0,
                Max = 100,
                DefaultValue = 50,
                DisplayFormat = "理想Y:{0}%",
                Tip = "理想位置 Y（图像高度百分比）"
            },
            new TaskParamDesc
            {
                ParamName = "比例尺 mm/px",
                Min = 0,
                Max = 1000,
                DefaultValue = 0,
                DisplayFormat = "标定:{0}mm/px",
                Tip = "0=输出像素；填标定比例尺（如 0.05 表示 1 像素 = 0.05mm）则同时输出毫米值"
            },
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 1,
                Max = 254,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度 > 阈值 视为目标（白色），取最大连通域的中心"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            double idealX = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 50, 0, 100) / 100.0;
            double idealY = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 50, 0, 100) / 100.0;
            double scale = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 0, 0, 1000) / 1000.0;
            int thr = Math.Clamp(paramValues.Length > 3 ? paramValues[3] : 127, 1, 254);

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, thr, 255, ThresholdTypes.Binary);
            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            Point2f ideal = new((float)(srcMat.Cols * idealX), (float)(srcMat.Rows * idealY));
            Cv2.DrawMarker(dst, new Point((int)ideal.X, (int)ideal.Y), Scalar.LimeGreen, MarkerTypes.Cross, 14, 1, LineTypes.AntiAlias);

            if (contours == null || contours.Length == 0)
            {
                LastSummary = "位置度: 二值图中没有找到目标（试试调整阈值/先做二值化）";
                return dst;
            }

            // 最大轮廓中心
            int best = 0;
            double bestArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area > bestArea) { bestArea = area; best = i; }
            }
            Point2f actual = CenterOfContour(contours[best]);

            double devPx = Math.Sqrt((actual.X - ideal.X) * (actual.X - ideal.X) + (actual.Y - ideal.Y) * (actual.Y - ideal.Y));
            double tolPx = 2 * devPx;
            DeviationPx = devPx;
            Tolerance = tolPx;

            Cv2.Circle(dst, (int)actual.X, (int)actual.Y, 5, Scalar.Red, -1, LineTypes.AntiAlias);
            Cv2.Line(dst, (int)ideal.X, (int)ideal.Y, (int)actual.X, (int)actual.Y, Scalar.Yellow, 1, LineTypes.AntiAlias);
            Cv2.Circle(dst, (int)ideal.X, (int)ideal.Y, (int)Math.Round(tolPx / 2), Scalar.Orange, 1, LineTypes.AntiAlias);

            if (scale > 1e-6)
            {
                double devMm = devPx * scale, tolMm = tolPx * scale;
                LastSummary = string.Format(
                    "位置度: 实测({0:F1},{1:F1}) 理想({2:F1},{3:F1}) 偏差={4:F2}px({5:F3}mm) 位置度={6:F2}px({7:F3}mm)",
                    actual.X, actual.Y, ideal.X, ideal.Y, devPx, devMm, tolPx, tolMm);
            }
            else
            {
                LastSummary = string.Format(
                    "位置度: 实测({0:F1},{1:F1}) 理想({2:F1},{3:F1}) 偏差={4:F2}px 位置度={5:F2}px",
                    actual.X, actual.Y, ideal.X, ideal.Y, devPx, tolPx);
            }
            return dst;
        }

        /// <summary>轮廓中心（面积加权或均值）</summary>
        public static Point2f CenterOfContour(Point[] pts)
        {
            if (pts == null || pts.Length == 0) return default;
            double sx = 0, sy = 0;
            foreach (Point p in pts) { sx += p.X; sy += p.Y; }
            return new Point2f((float)(sx / pts.Length), (float)(sy / pts.Length));
        }
    }
}
