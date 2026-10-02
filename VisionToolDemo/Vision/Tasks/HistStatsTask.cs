using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 直方图统计：灰度直方图（0~255）叠加画在结果图下半部（白线），
    /// 摘要输出灰度 min/max/均值/标准差。
    /// 用于曝光/灰度分布分析，无几何检出。
    /// 参数：无。
    /// </summary>
    public class HistStatsTask : IVisionTask, IResultReporter
    {
        public string TaskName => "直方图统计";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new TaskParamDesc[0];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat hist = new())
            {
                Cv2.CalcHist(new[] { gray }, new[] { 0 }, null, hist, 1, new[] { 256 },
                    new[] { new Rangef(0, 256) });

                // 灰度 min/max 直接从直方图推得（首个/末个非零 bin）
                int mn = -1, mx = -1;
                double hmax = 0;
                for (int i = 0; i < 256; i++)
                {
                    float c = hist.Get<float>(i);
                    if (c > 0)
                    {
                        if (mn < 0) mn = i;
                        mx = i;
                    }
                    if (c > hmax) hmax = c;
                }

                Cv2.MeanStdDev(gray, out Scalar mean, out Scalar std);
                if (mn < 0)
                    mn = mx = 0;
                LastSummary = string.Format("直方图: min={0} max={1} 均值={2:F1} 标准差={3:F1}",
                    mn, mx, mean.Val0, std.Val0);

                // 直方图曲线画在下半部（先涂黑衬底）
                if (hmax <= 0 || dst.Cols < 16)
                    return dst;
                int w = dst.Cols;
                int plotH = Math.Max(40, dst.Rows * 2 / 5);
                using (Mat shade = new(dst, new Rect(0, dst.Rows - plotH, w, plotH)))
                    Cv2.Rectangle(shade, new Rect(0, 0, w, plotH), new Scalar(0, 0, 0), -1);

                Point[] poly = new Point[256];
                for (int i = 0; i < 256; i++)
                {
                    int x = i * (w - 1) / 255;
                    int y = dst.Rows - (int)(hist.Get<float>(i) / hmax * (plotH - 4));
                    poly[i] = new Point(x, y);
                }
                Cv2.Polylines(dst, new[] { poly }, false, Scalar.White, 1, LineTypes.AntiAlias);
                return dst;
            }
        }
    }
}
