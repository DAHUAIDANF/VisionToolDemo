using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 对称度（Symmetry）：轮廓关于给定对称轴镜像后与自身的最大偏差 ×2。
    ///
    /// 对称度 = 2 × 最大偏差，其中偏差 = 轮廓点镜像后到原轮廓的最近距离
    /// （机械加工对称公差带定义：被测要素必须位于以基准对称面为中面的两平行面内）。
    ///
    /// 对称轴选择：垂直中线 / 水平中线 / 任意角度轴（角度 0°=水平轴，90°=垂直轴，
    /// 其余角度为过图像中心的斜轴）。输出：原图叠加对称轴、镜像轮廓与偏差线。
    /// </summary>
    public class SymmetryTask : IVisionTask, IResultReporter
    {
        public string TaskName => "对称度";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次最大偏差（像素）</summary>
        public double MaxDeviation { get; private set; }

        /// <summary>最近一次对称度（2×最大偏差）</summary>
        public double Symmetry { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "对称轴 0垂直1水平2角度",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "轴:{0}",
                Tip = "0=垂直中线（左右对称）1=水平中线（上下对称）2=自定义角度（下一参数给角度）"
            },
            new TaskParamDesc
            {
                ParamName = "轴角度°",
                Min = -90,
                Max = 90,
                DefaultValue = 0,
                DisplayFormat = "角:{0}°",
                Tip = "轴类型=2 时的对称轴角度：0°=水平，90°=垂直，正角=顺时针倾斜（过图像中心）"
            },
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 1,
                Max = 254,
                DefaultValue = 127,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度 > 阈值 视为目标（白色），取最大连通域做对称评价"
            },
            new TaskParamDesc
            {
                ParamName = "比例尺 mm/px",
                Min = 0,
                Max = 1000,
                DefaultValue = 0,
                DisplayFormat = "标定:{0}mm/px",
                Tip = "0=输出像素；填比例尺则同时输出毫米值"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int axis = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 0, 0, 2);
            double angleDeg = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 0, -90, 90);
            int thr = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 127, 1, 254);
            double scale = Math.Clamp(paramValues.Length > 3 ? paramValues[3] : 0, 0, 1000) / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            Cv2.Threshold(gray, bin, thr, 255, ThresholdTypes.Binary);
            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            int cx = srcMat.Cols / 2, cy = srcMat.Rows / 2;

            // 对称轴方向（单位向量）
            double ax, ay;
            if (axis == 1) { ax = 0; ay = 1; }           // 水平中线
            else if (axis == 2) { double r = angleDeg * Math.PI / 180; ax = Math.Cos(r); ay = Math.Sin(r); }
            else { ax = 1; ay = 0; }                     // 垂直中线

            // 画对称轴
            double len = Math.Max(srcMat.Cols, srcMat.Rows);
            Point p1 = new((int)(cx - ax * len), (int)(cy - ay * len));
            Point p2 = new((int)(cx + ax * len), (int)(cy + ay * len));
            Cv2.Line(dst, p1, p2, Scalar.LimeGreen, 1, LineTypes.AntiAlias);

            if (contours == null || contours.Length == 0)
            {
                LastSummary = "对称度: 二值图中没有找到目标（试试调整阈值/先做二值化）";
                return dst;
            }

            int best = 0;
            double bestArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area > bestArea) { bestArea = area; best = i; }
            }
            Point[] c = contours[best];

            // 轮廓镜像 + 找最大最近距离
            double maxDev = 0;
            foreach (Point p in c)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                double proj = dx * ax + dy * ay;
                double mx = p.X - 2 * (dx - proj * ax);   // 关于轴镜像
                double my = p.Y - 2 * (dy - proj * ay);
                double near = double.MaxValue;
                foreach (Point q in c)
                {
                    double qx = q.X - mx, qy = q.Y - my;
                    double d = qx * qx + qy * qy;
                    if (d < near) near = d;
                }
                double dev = Math.Sqrt(near);
                if (dev > maxDev)
                {
                    maxDev = dev;
                    Cv2.Circle(dst, p, 1, Scalar.Orange, 1);
                }
            }

            double sym = 2 * maxDev;
            MaxDeviation = maxDev;
            Symmetry = sym;

            string axisName = axis == 0 ? "垂直中线" : axis == 1 ? "水平中线" : "角度轴 " + angleDeg + "°";
            if (scale > 1e-6)
            {
                LastSummary = string.Format(
                    "对称度: 轴={0} 最大偏差={1:F2}px({2:F3}mm) 对称度={3:F2}px({4:F3}mm)",
                    axisName, maxDev, maxDev * scale, sym, sym * scale);
            }
            else
            {
                LastSummary = string.Format(
                    "对称度: 轴={0} 最大偏差={1:F2}px 对称度={2:F2}px",
                    axisName, maxDev, sym);
            }
            return dst;
        }
    }
}
