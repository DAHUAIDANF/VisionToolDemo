using System;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 点到直线距离算子：UI 层第一次拖动画直线（两点定线，无限长直线），
    /// 第二次拖动/点击指定测量点；计算点到直线的垂直距离并标注。
    /// 显示分工：直线与测量点由 UI 层叠加在原图上；
    /// 本任务在结果图上绘制距离标注（青线 + 黄点 + 绿色垂线段 + 距离文本）。
    /// </summary>
    public class PointLineDistanceTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "点到直线距离";

        /// <summary>直线两端点（真实像素坐标），UI 层拖动后赋值</summary>
        public Point LineP1 { get; private set; }

        public Point LineP2 { get; private set; }
        public bool HasLine { get; private set; }

        public Point MeasurePoint { get; private set; }
        public bool HasPoint { get; private set; }

        /// <summary>最近一次计算的垂直距离（像素），未测量时为 -1</summary>
        public double LastDistance { get; private set; } = -1;

        /// <summary>最近一次 Execute 的结果摘要（完成测量时为距离值，否则为空）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>UI层第一次拖动后调用</summary>
        public void SetLine(Point p1, Point p2)
        {
            LineP1 = p1;
            LineP2 = p2;
            HasLine = true;
        }

        /// <summary>UI层第二次拖动/点击后调用</summary>
        public void SetPoint(Point p)
        {
            MeasurePoint = p;
            HasPoint = true;
        }

        public void ClearGeometry()
        {
            HasLine = false;
            HasPoint = false;
            LastDistance = -1;
        }

        /// <summary>状态 = [线x1, y1, x2, y2] 或 [线x1, y1, x2, y2, 点x, 点y]</summary>
        public string SaveState()
        {
            if (!HasLine) return null;
            int[] p = HasPoint
                ? new[] { LineP1.X, LineP1.Y, LineP2.X, LineP2.Y, MeasurePoint.X, MeasurePoint.Y }
                : new[] { LineP1.X, LineP1.Y, LineP2.X, LineP2.Y };
            return JsonConvert.SerializeObject(p);
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p is { Length: 4 })
                    SetLine(new Point(p[0], p[1]), new Point(p[2], p[3]));
                else if (p is { Length: 6 })
                {
                    SetLine(new Point(p[0], p[1]), new Point(p[2], p[3]));
                    SetPoint(new Point(p[4], p[5]));
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
            if (!HasLine || !HasPoint)
                return dst;

            double dx = LineP2.X - LineP1.X;
            double dy = LineP2.Y - LineP1.Y;
            double len2 = (dx * dx) + (dy * dy);
            if (len2 < 1e-6)
                return dst; // 直线退化成点，无法测量

            // 垂足 = P1 + t·d，t = ((Q-P1)·d)/|d|²（对无限直线）
            double t = (((MeasurePoint.X - LineP1.X) * dx) + ((MeasurePoint.Y - LineP1.Y) * dy)) / len2;
            int fx = (int)Math.Round(LineP1.X + (t * dx));
            int fy = (int)Math.Round(LineP1.Y + (t * dy));
            double dist = Math.Sqrt(((MeasurePoint.X - fx) * (MeasurePoint.X - fx))
                                  + ((MeasurePoint.Y - fy) * (MeasurePoint.Y - fy)));
            LastDistance = dist;
            LastSummary = $"点到直线: 距离 {dist:F1}px";

            // —— 绘制：直线（青）、测量点（黄圈）、垂线与垂足（绿）、距离文本（白字黑边） ——
            Cv2.Line(dst, LineP1, LineP2, Scalar.Cyan, 1, LineTypes.AntiAlias);
            Cv2.Circle(dst, MeasurePoint, 4, Scalar.Yellow, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, MeasurePoint, new Point(fx, fy), Scalar.LimeGreen, 2, LineTypes.AntiAlias);
            Cv2.Circle(dst, fx, fy, 3, Scalar.LimeGreen, -1, LineTypes.AntiAlias);

            string text = $"d={dist:F1}px";
            Point tp = new(((MeasurePoint.X + fx) / 2) + 8, ((MeasurePoint.Y + fy) / 2) - 8);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.Black, 3, LineTypes.AntiAlias);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.White, 1, LineTypes.AntiAlias);
            return dst;
        }
    }
}
