using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 彩色直方图分析算子：HSV 空间统计色相分布，输出平均色/主色、
    /// 颜色数量，以及可视化直方图。用于颜色一致性检测、色偏分析。
    /// 参数：色相分箱（8~64）、饱和度下限、输出模式。
    /// </summary>
    public class ColorHistTask : IVisionTask, IResultReporter
    {
        public string TaskName => "彩色直方图";

        public string LastSummary { get; private set; } = "";

        public Scalar AverageHsv { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "色相分箱",
                Min = 8, Max = 64, DefaultValue = 32,
                DisplayFormat = "分箱:{0}",
                Tip = "色相直方图分箱数"
            },
            new TaskParamDesc
            {
                ParamName = "饱和度下限",
                Min = 0, Max = 255, DefaultValue = 40,
                DisplayFormat = "Smin:{0}",
                Tip = "饱和度低于此值的像素视为灰（不计入色相统计）"
            },
            new TaskParamDesc
            {
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "模式:{0}",
                Tip = "0=原图不变（只输出统计）；1=色相直方图可视化"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int bins = Math.Max(8, Math.Min(64, paramValues[0]));
            int sMin = paramValues[1];
            int mode = paramValues[2];

            using (Mat hsv = new())
            {
                Cv2.CvtColor(srcMat, hsv, ColorConversionCodes.BGR2HSV);
                Mat[] planes;
                Cv2.Split(hsv, out planes);

                // 统计色相直方图（加权：忽略低饱和与低明度）
                double[] hist = new double[bins];
                long count = 0;
                double sumH = 0, sumS = 0, sumV = 0;
                for (int y = 0; y < hsv.Rows; y++)
                {
                    for (int x = 0; x < hsv.Cols; x++)
                    {
                        int s = planes[1].At<byte>(y, x);
                        int v = planes[2].At<byte>(y, x);
                        if (v < 20) continue;
                        sumV += v; sumS += s;
                        if (s >= sMin)
                        {
                            int h = planes[0].At<byte>(y, x);
                            hist[Math.Min(bins - 1, h * bins / 180)]++;
                            sumH += h;
                            count++;
                        }
                    }
                }
                foreach (var p in planes) p.Dispose();

                if (mode == 0)
                {
                    string avg = count > 0
                        ? $"H={sumH / count:F0} S={sumS / (double)(hsv.Rows * hsv.Cols):F0} V={sumV / (double)(hsv.Rows * hsv.Cols):F0}"
                        : "无有效彩色像素";
                    LastSummary = $"彩色直方图: {avg}";
                    return srcMat.Clone();
                }

                // 直方图可视化
                Mat vis = new Mat(200, 640, MatType.CV_8UC3, new Scalar(20, 20, 20));
                double maxH = 1;
                for (int i = 0; i < bins; i++) maxH = Math.Max(maxH, hist[i]);
                int barW = Math.Max(4, 600 / bins);
                for (int i = 0; i < bins; i++)
                {
                    int h = (int)(hist[i] / maxH * 150);
                    // 用色相颜色画条
                    Scalar color = HsvToBgr(i * 180.0 / bins);
                    Cv2.Rectangle(vis, new Point(20 + i * barW, 170 - h),
                        new Point(20 + (i + 1) * barW - 2, 170), color, -1);
                }
                Cv2.Line(vis, new Point(20, 170), new Point(620, 170), new Scalar(255, 255, 255), 1);
                Cv2.PutText(vis, "Hue histogram (0-180)", new Point(20, 20),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);

                // 主色
                int best = 0;
                for (int i = 1; i < bins; i++)
                    if (hist[i] > hist[best]) best = i;
                LastSummary = $"彩色直方图: 主色 H={best * 180.0 / bins:F0}（{hist[best] / Math.Max(1, count) * 100:F0}%）";
                return vis;
            }
        }

        /// <summary>HSV 色相 → BGR（用于直方图着色）</summary>
        private static Scalar HsvToBgr(double hue)
        {
            double h = hue / 60.0;
            double s = 0.9, v = 0.9;
            int i = (int)h % 6;
            double f = h - Math.Floor(h);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return new Scalar(b * 255, g * 255, r * 255);
        }
    }
}
