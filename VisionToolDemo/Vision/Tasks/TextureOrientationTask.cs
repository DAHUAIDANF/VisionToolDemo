using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 纹理方向分析算子：Sobel 梯度 → 方向直方图，统计主纹理方向，
    /// 在图上画主方向线（拉丝/刷纹/布纹方向检测）。
    /// 参数：分箱数（4~36）、输出模式（0=主方向线叠加 1=方向玫瑰图）、梯度阈值。
    /// </summary>
    public class TextureOrientationTask : IVisionTask, IResultReporter
    {
        public string TaskName => "纹理方向";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "分箱数",
                Min = 4, Max = 36, DefaultValue = 12,
                DisplayFormat = "分箱:{0}",
                Tip = "方向直方图分箱（12=每 30° 一箱）"
            },
            new TaskParamDesc
            {
                ParamName = "梯度阈值",
                Min = 10, Max = 200, DefaultValue = 50,
                DisplayFormat = "梯度:{0}",
                Tip = "梯度幅度下限，低于此值的像素不算（去平缓区）"
            },
            new TaskParamDesc
            {
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=原图叠主方向线；1=方向玫瑰图（直方图可视化）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int bins = Math.Max(4, Math.Min(36, paramValues[0]));
            int gradThresh = paramValues[1];
            int mode = paramValues[2];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat gx = new(), gy = new())
            {
                // Sobel 梯度
                Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
                Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);

                // 方向直方图（加权：梯度幅度）
                double[] hist = new double[bins];
                double totalW = 0;
                for (int y = 0; y < gray.Rows; y++)
                {
                    for (int x = 0; x < gray.Cols; x++)
                    {
                        float dx = gx.At<float>(y, x), dy = gy.At<float>(y, x);
                        double mag = Math.Sqrt(dx * dx + dy * dy);
                        if (mag < gradThresh) continue;
                        // 方向角 0~180（边缘方向无正负之分）
                        double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        if (ang < 0) ang += 180;
                        if (ang >= 180) ang -= 180;
                        int b = (int)(ang / 180.0 * bins);
                        if (b >= bins) b = bins - 1;
                        hist[b] += mag;
                        totalW += mag;
                    }
                }

                // 主方向（加权平均，取最大箱附近）
                int bestBin = 0;
                for (int i = 1; i < bins; i++)
                    if (hist[i] > hist[bestBin]) bestBin = i;
                double mainAngle = (bestBin + 0.5) * 180.0 / bins;

                // 一致性（主方向占比）
                double ratio = totalW > 0 ? hist[bestBin] / totalW : 0;

                if (mode == 1)
                {
                    // 方向玫瑰图：把直方图画成极坐标扇形
                    Mat rose = new Mat(400, 400, MatType.CV_8UC3, new Scalar(30, 30, 30));
                    Point c = new(200, 200);
                    double max = 1;
                    for (int i = 0; i < bins; i++) max = Math.Max(max, hist[i]);
                    for (int i = 0; i < bins; i++)
                    {
                        double a0 = (i * 180.0 / bins) * Math.PI / 180.0;
                        double a1 = ((i + 1) * 180.0 / bins) * Math.PI / 180.0;
                        double r = 180.0 * hist[i] / max;
                        Point[] pts =
                        {
                            c,
                            new((int)(c.X + Math.Cos(a0) * r), (int)(c.Y - Math.Sin(a0) * r)),
                            new((int)(c.X + Math.Cos(a1) * r), (int)(c.Y - Math.Sin(a1) * r)),
                        };
                        Cv2.FillConvexPoly(rose, pts, new Scalar(0, 180, 255));
                    }
                    // 主方向线
                    double ma = mainAngle * Math.PI / 180.0;
                    Cv2.Line(rose, c,
                        new((int)(c.X + Math.Cos(ma) * 180), (int)(c.Y - Math.Sin(ma) * 180)),
                        new Scalar(0, 0, 255), 2);
                    LastSummary = $"纹理方向: 主方向 {mainAngle:F0}°（一致性 {ratio * 100:F0}%）";
                    return rose;
                }

                // 原图叠加主方向线（穿过中心）
                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();
                Point cc = new(dst.Cols / 2, dst.Rows / 2);
                double ma2 = mainAngle * Math.PI / 180.0;
                double halfLen = Math.Min(dst.Cols, dst.Rows) * 0.4;
                Cv2.Line(dst,
                    new((int)(cc.X - Math.Cos(ma2) * halfLen), (int)(cc.Y + Math.Sin(ma2) * halfLen)),
                    new((int)(cc.X + Math.Cos(ma2) * halfLen), (int)(cc.Y - Math.Sin(ma2) * halfLen)),
                    new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);

                LastSummary = $"纹理方向: 主方向 {mainAngle:F0}°（一致性 {ratio * 100:F0}%）";
                return dst;
            }
        }
    }
}
