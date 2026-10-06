using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 文字/Logo 水印算子：在图上叠加 ASCII 文字水印或网格水印。
    /// 模式 0=角落文字；1=网格水印；2=半透明色块角标。
    /// 参数：水印模式、透明度、字号/密度。
    /// </summary>
    public class WatermarkTask : IVisionTask, IResultReporter
    {
        public string TaskName => "图像水印";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "水印模式",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=角落文字 1=网格水印 2=色块角标"
            },
            new TaskParamDesc
            {
                ParamName = "透明度",
                Min = 5, Max = 80, DefaultValue = 35,
                DisplayFormat = "透明:{0}%",
                Tip = "水印透明度（越小越淡）"
            },
            new TaskParamDesc
            {
                ParamName = "尺寸密度",
                Min = 1, Max = 20, DefaultValue = 5,
                DisplayFormat = "密度:{0}",
                Tip = "文字字号或网格间距（越大越疏/越大）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int wmMode = paramValues[0];
            int alphaPct = Math.Max(5, Math.Min(80, paramValues[1]));
            int density = Math.Max(1, Math.Min(20, paramValues[2]));

            Mat dst = srcMat.Channels() == 1
                ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                : srcMat.Clone();

            double alpha = alphaPct / 100.0;
            int w = dst.Cols, h = dst.Rows;

            switch (wmMode)
            {
                case 0:
                {
                    // 角落文字水印
                    string text = "VISION";
                    double fontScale = 0.6 + density * 0.15;
                    int baseline = 0;
                    Size ts = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, 2, out baseline);
                    using (Mat overlay = dst.Clone())
                    {
                        Cv2.Rectangle(overlay, new Point(w - ts.Width - 16, h - ts.Height - 16),
                            new Point(w - 8, h - 8), new Scalar(0, 0, 0), -1);
                        Cv2.PutText(overlay, text, new Point(w - ts.Width - 10, h - 12),
                            HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), 2);
                        Cv2.AddWeighted(overlay, alpha, dst, 1 - alpha, 0, dst);
                    }
                    LastSummary = $"图像水印: 角落文字（透明度{alphaPct}%）";
                    break;
                }
                case 1:
                {
                    // 网格水印：斜线网格
                    int step = Math.Max(20, density * 30);
                    using (Mat overlay = dst.Clone())
                    {
                        for (int x = -h; x < w; x += step)
                            Cv2.Line(overlay, new Point(x, 0), new Point(x + h, h), new Scalar(128, 128, 128), 1);
                        for (int x = -h; x < w; x += step)
                            Cv2.Line(overlay, new Point(x, h), new Point(x + h, 0), new Scalar(128, 128, 128), 1);
                        Cv2.AddWeighted(overlay, alpha, dst, 1 - alpha, 0, dst);
                    }
                    LastSummary = $"图像水印: 网格（间距{step}px 透明度{alphaPct}%）";
                    break;
                }
                default:
                {
                    // 半透明色块角标
                    int bw = Math.Max(40, w / 4), bh = Math.Max(20, h / 12);
                    using (Mat overlay = dst.Clone())
                    {
                        Cv2.Rectangle(overlay, new Rect(0, 0, bw, bh), new Scalar(0, 0, 255), -1);
                        Cv2.PutText(overlay, "DEMO", new Point(10, bh - 8),
                            HersheyFonts.HersheySimplex, 0.8, new Scalar(255, 255, 255), 2);
                        Cv2.AddWeighted(overlay, alpha, dst, 1 - alpha, 0, dst);
                    }
                    LastSummary = $"图像水印: 色块角标（透明度{alphaPct}%）";
                    break;
                }
            }
            return dst;
        }
    }
}
