using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Blob 分析（连通域统计）：二值化后做连通域标记，按面积筛选，
    /// 结果图上框出每个 Blob 并标注序号，质心画十字；
    /// 摘要输出候选总数与合格数（粒子计数、缺料/孔位有无判断）。
    /// 参数：阈值、前景极性（0=亮为前景 1=暗为前景）、最小面积。
    /// </summary>
    public class BlobTask : IVisionTask, IResultReporter
    {
        public string TaskName => "Blob分析";

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
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 1,
                Max = 20000,
                DefaultValue = 100,
                DisplayFormat = "MinArea:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int thresh = paramValues[0];
            int polarity = paramValues[1] % 2;
            int minArea = Math.Max(1, paramValues[2]);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat bin = new())
            using (Mat labels = new())
            using (Mat stats = new())
            using (Mat centroids = new())
            {
                ThresholdTypes type = polarity == 0 ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;
                Cv2.Threshold(gray, bin, thresh, 255, type);

                int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);
                // 标签 0 为背景；stats 列: 0=Left 1=Top 2=Width 3=Height 4=Area
                int maxArea = (int)(srcMat.Cols * (long)srcMat.Rows * 0.9);
                int kept = 0;
                for (int i = 1; i < n; i++)
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < minArea || area > maxArea)
                        continue;
                    kept++;

                    int x = stats.Get<int>(i, (int)ConnectedComponentsTypes.Left);
                    int y = stats.Get<int>(i, (int)ConnectedComponentsTypes.Top);
                    int w = stats.Get<int>(i, (int)ConnectedComponentsTypes.Width);
                    int h = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);
                    Cv2.Rectangle(dst, new Rect(x, y, w, h), Scalar.LimeGreen, 2, LineTypes.AntiAlias);

                    double ccx = centroids.Get<double>(i, 0);
                    double ccy = centroids.Get<double>(i, 1);
                    DrawCross(dst, (int)Math.Round(ccx), (int)Math.Round(ccy), 5, Scalar.Red);
                    Cv2.PutText(dst, kept.ToString(), new Point(x, Math.Max(12, y - 4)),
                        HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                }

                LastSummary = "Blob分析: 连通域 " + (n - 1) + " 个, 合格 " + kept + " 个 (最小面积 " + minArea + ")";
                return dst;
            }
        }

        private static void DrawCross(Mat dst, int cx, int cy, int r, Scalar color)
        {
            Cv2.Line(dst, cx - r, cy, cx + r, cy, color, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, cx, cy - r, cx, cy + r, color, 1, LineTypes.AntiAlias);
        }
    }
}
