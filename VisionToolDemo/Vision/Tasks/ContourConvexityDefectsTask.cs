using OpenCvSharp;
using System.Linq;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>凸缺陷检测算子：找轮廓相对其凸包的凹陷（缺陷），青色连线 + 红点标最远点。
    /// 参数：二值阈值、最小轮廓面积、缺陷最小深度。常用于找缺口/咬边类缺陷。</summary>
    public class ContourConvexityDefectsTask : IVisionTask, IResultReporter
    {
        public string TaskName => "凸缺陷检测";

        /// <summary>最近一次 Execute 的结果摘要（缺陷数量与最大深度）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "thresh:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最小轮廓面积",
                Min = 5,
                Max = 5000,
                DefaultValue = 30,
                DisplayFormat = "minArea:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "缺陷最小深度",
                Min = 1,
                Max = 500,
                DefaultValue = 20,
                DisplayFormat = "minDepth:{0}",
                ForceOdd = false,
                Tip = "深度=凹陷最深处到凸包边的距离（像素），小于该值的缺陷不显示"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            int minArea = paramValues[1];
            int minDepth = paramValues[2];
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Mat bin = new();
                Cv2.Threshold(gray, bin, paramValues[0], 255, ThresholdTypes.Binary);

                Mat dst = VisionHelper.ToBgrCopy(srcMat);

                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                int kept = 0;
                int defectTotal = 0;
                double maxDepth = 0;
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    if (area < minArea)
                        continue;

                    kept++;
                    // 用凸包顶点索引算缺陷；每个缺陷含 起点/终点/最远点/深度
                    int[] hullIndices = Cv2.ConvexHullIndices(cnt);
                    Vec4i[] defects = Cv2.ConvexityDefects(cnt, hullIndices);

                    // 凸包轮廓线（细线，与缺陷标记区分）
                    Point[] hull = hullIndices.Select(i => cnt[i]).ToArray();
                    Cv2.Polylines(dst, new[] { hull }, true, Scalar.OrangeRed, 1);

                    foreach (Vec4i d in defects)
                    {
                        // Item3 为定点数深度（真实值 ×256）
                        double depth = d.Item3 / 256.0;
                        if (depth < minDepth)
                            continue;

                        Point start = cnt[d.Item0];
                        Point end = cnt[d.Item1];
                        Point far = cnt[d.Item2];

                        Cv2.Line(dst, far, start, Scalar.Cyan, 1);
                        Cv2.Line(dst, far, end, Scalar.Cyan, 1);
                        Cv2.Circle(dst, start, 2, Scalar.LimeGreen, -1);
                        Cv2.Circle(dst, end, 2, Scalar.LimeGreen, -1);
                        Cv2.Circle(dst, far, 3, Scalar.Red, -1);

                        defectTotal++;
                        if (depth > maxDepth)
                            maxDepth = depth;
                    }
                }

                if (kept == 0)
                    LastSummary = "凸缺陷: 未检出轮廓（可调低阈值或最小面积）";
                else if (defectTotal == 0)
                    LastSummary = $"凸缺陷: 无（{kept} 个轮廓均为凸形）";
                else
                    LastSummary = $"凸缺陷: {defectTotal} 个, 最大深度 {maxDepth:F1}px";

                bin.Dispose();
                return dst;
            }
        }
    }
}
