using System;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 直线到直线距离算子：UI 层依次画两条直线（两点定线，无限长直线）；
    /// 计算第 1 条线中点到第 2 条直线的垂直距离并标注。
    /// 两线近似平行时该距离即为两条平行线的间距；不平行时同时给出两线夹角。
    /// 显示分工：两条直线由 UI 层叠加在原图上；本任务在结果图上绘制距离/角度标注。
    /// </summary>
    public class LineLineDistanceTask : IVisionTask, IResultReporter, IStatefulTask
    {
        /// <summary>近似平行判定：|sinθ| 低于该值视为平行（约 0.57°）</summary>
        private const double ParallelSinThreshold = 0.01;

        public string TaskName => "直线到直线距离";

        public Point Line1P1 { get; private set; }
        public Point Line1P2 { get; private set; }
        public Point Line2P1 { get; private set; }
        public Point Line2P2 { get; private set; }
        public bool HasLine1 { get; private set; }
        public bool HasLine2 { get; private set; }

        /// <summary>最近一次计算的距离（像素）与两线夹角（度），未测量时为 -1</summary>
        public double LastDistance { get; private set; } = -1;
        public double LastAngle { get; private set; }
        public bool IsParallel { get; private set; }

        /// <summary>最近一次 Execute 的结果摘要（完成测量时为距离/夹角，否则为空）</summary>
        public string LastSummary { get; private set; } = "";

        public void SetLine1(Point p1, Point p2)
        {
            Line1P1 = p1;
            Line1P2 = p2;
            HasLine1 = true;
        }

        public void SetLine2(Point p1, Point p2)
        {
            Line2P1 = p1;
            Line2P2 = p2;
            HasLine2 = true;
        }

        public void ClearGeometry()
        {
            HasLine1 = false;
            HasLine2 = false;
            LastDistance = -1;
        }

        /// <summary>状态 = [线1x1,y1,x2,y2] 或 [线1 4个 + 线2 4个]</summary>
        public string SaveState()
        {
            if (!HasLine1) return null;
            int[] p = HasLine2
                ? new[] { Line1P1.X, Line1P1.Y, Line1P2.X, Line1P2.Y, Line2P1.X, Line2P1.Y, Line2P2.X, Line2P2.Y }
                : new[] { Line1P1.X, Line1P1.Y, Line1P2.X, Line1P2.Y };
            return JsonConvert.SerializeObject(p);
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p is { Length: 4 })
                    SetLine1(new Point(p[0], p[1]), new Point(p[2], p[3]));
                else if (p is { Length: 8 })
                {
                    SetLine1(new Point(p[0], p[1]), new Point(p[2], p[3]));
                    SetLine2(new Point(p[4], p[5]), new Point(p[6], p[7]));
                }
            }
            catch
            {
                // 状态内容损坏时忽略
            }
        }

        public TaskParamDesc[] ParamDescriptions => new TaskParamDesc[0];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            LastDistance = -1;
            LastSummary = "";
            if (!HasLine1 || !HasLine2)
                return dst;

            double d1x = Line1P2.X - Line1P1.X, d1y = Line1P2.Y - Line1P1.Y;
            double d2x = Line2P2.X - Line2P1.X, d2y = Line2P2.Y - Line2P1.Y;
            double len1 = Math.Sqrt((d1x * d1x) + (d1y * d1y));
            double len2 = Math.Sqrt((d2x * d2x) + (d2y * d2y));
            if (len1 < 1e-6 || len2 < 1e-6)
                return dst; // 线退化成点，无法测量

            double u1x = d1x / len1, u1y = d1y / len1;
            double u2x = d2x / len2, u2y = d2y / len2;

            // 两线夹角 [0°, 90°]，|sinθ| 判定平行
            double cross = (u1x * u2y) - (u1y * u2x);
            double dot = (u1x * u2x) + (u1y * u2y);
            double angle = Math.Atan2(Math.Abs(cross), Math.Abs(dot)) * 180.0 / Math.PI;
            IsParallel = Math.Abs(cross) < ParallelSinThreshold;

            // 第 1 条线中点到第 2 条无限直线的垂直距离（平行时即两线间距）
            double mx = (Line1P1.X + Line1P2.X) / 2.0;
            double my = (Line1P1.Y + Line1P2.Y) / 2.0;
            double dist = Math.Abs(((mx - Line2P1.X) * u2y) - ((my - Line2P1.Y) * u2x));
            LastDistance = dist;
            LastAngle = angle;
            LastSummary = IsParallel
                ? $"直线间距(平行): {dist:F1}px"
                : $"线1中点到线2: {dist:F1}px  夹角 {angle:F1}°";

            // 垂足 = P2 + ((M-P2)·u2)·u2
            double s = ((mx - Line2P1.X) * u2x) + ((my - Line2P1.Y) * u2y);
            int fx = (int)Math.Round(Line2P1.X + (u2x * s));
            int fy = (int)Math.Round(Line2P1.Y + (u2y * s));
            int px = (int)Math.Round(mx);
            int py = (int)Math.Round(my);

            // —— 绘制：两条线（青/黄）、中点到第2线的垂线与垂足（绿）、距离文本（白字黑边） ——
            Cv2.Line(dst, Line1P1, Line1P2, Scalar.Cyan, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, Line2P1, Line2P2, Scalar.Yellow, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, new Point(px, py), new Point(fx, fy), Scalar.LimeGreen, 2, LineTypes.AntiAlias);
            Cv2.Circle(dst, fx, fy, 3, Scalar.LimeGreen, -1, LineTypes.AntiAlias);

            string text = IsParallel ? $"d={dist:F1}px" : $"d={dist:F1}px ang={angle:F1}";
            Point tp = new(((px + fx) / 2) + 8, ((py + fy) / 2) - 8);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.Black, 3, LineTypes.AntiAlias);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.White, 1, LineTypes.AntiAlias);
            return dst;
        }
    }
}
