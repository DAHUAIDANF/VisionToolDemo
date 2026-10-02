using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 斜边 SFR（ISO 12233:2014/2017）：对含一条斜边的图像块做 MTF 分析，输出
    /// MTF 曲线与 MTF50/MTF30/MTF10 等关键频率。
    ///
    /// 为什么这样做：直接对整图求清晰度只能得一个模糊的"锐度分"；SFR 给出的是
    /// **可追溯的 MTF 曲线** —— MTF50 是镜头/模组的通用清晰度指标，能横向比较不同
    /// 样品与不同视场位置，也能做产线上下限判定。
    ///
    /// 算法链路（与 ISO 一致）：
    ///   Sobel 找边 → 抛物线亚像素精化 → 最小二乘拟合倾角 → 沿法线超采样 ESF →
    ///   差分得 LSF → 汉宁窗 → DFT → 归一化 + 低频修正 → 插值取关键频率
    /// </summary>
    public class SfrEdgeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "斜边SFR(MTF)";

        public string LastSummary { get; private set; } = "";

        public bool Ok { get; private set; }
        /// <summary>MTF50（cycles/pixel）</summary>
        public double Mtf50 { get; private set; } = double.NaN;
        public double Mtf30 { get; private set; } = double.NaN;
        public double Mtf10 { get; private set; } = double.NaN;
        /// <summary>边缘倾角（度）</summary>
        public double EdgeAngle { get; private set; }
        /// <summary>边缘对比度（灰阶）</summary>
        public double Contrast { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "超采样倍数",
                Min = 1, Max = 8, DefaultValue = 4,
                DisplayFormat = "os:{0}x",
                Group = "SFR",
                Tip = "ESF 的超采样倍数，ISO 推荐 4。调大可提高频率轴分辨率，但噪声也更明显。"
            },
            new TaskParamDesc
            {
                ParamName = "暗区在左 0否1是",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "darkLeft:{0}",
                Group = "SFR",
                Tip = "斜边左侧是暗还是亮。判据用于确定 ESF 方向，选反了曲线的对比度符号会倒。"
            },
            new TaskParamDesc
            {
                ParamName = "低频保护%",
                Min = 0, Max = 100, DefaultValue = 10,
                DisplayFormat = "guard:{0}%",
                Group = "SFR",
                Tip = "曲线起始多少比例的低频段不参与 MTF50 判定（窗函数在极低频会引入误差）。"
            },
            new TaskParamDesc
            {
                ParamName = "曲线显示 0关1开",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "plot:{0}",
                Group = "输出",
                Tip = "在结果图上绘制 MTF 曲线与关键频率标记，便于截图留档。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Ok = false;
            Mtf50 = Mtf30 = Mtf10 = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "斜边SFR: 输入为空";
                return srcMat?.Clone();
            }

            int oversample = Math.Clamp(paramValues[0], 1, 8);
            bool darkIsLeft = paramValues[1] == 1;
            int guardPct = paramValues[2];
            bool plot = paramValues[3] == 1;

            using Mat gray = VisionHelper.ToGray(srcMat);
            var r = MtfHelper.Compute(gray, oversample, darkIsLeft);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            if (!r.Ok)
            {
                LastSummary = "斜边SFR: " + r.Error;
                MatDraw.DrawText(dst, "SFR 失败: " + r.Error, 8, 24, Scalar.Red, 13);
                return dst;
            }

            Ok = true;
            EdgeAngle = r.EdgeAngle;
            Contrast = r.Contrast;

            // 关键频率：从低频保护点之后开始找，避免窗函数在 0 频附近的数值噪声
            double startFreq = r.Freq[^1] * Math.Clamp(guardPct, 0, 100) / 100.0;
            Mtf50 = FindFrom(r, startFreq, 0.5);
            Mtf30 = FindFrom(r, startFreq, 0.3);
            Mtf10 = FindFrom(r, startFreq, 0.1);

            if (plot)
            {
                DrawMtfPlot(dst, r);
                MatDraw.DrawText(dst, string.Format("MTF50={0:F3} MTF30={1:F3} 倾角{2:F1}° 对比{3:F0}",
                        Mtf50, Mtf30, r.EdgeAngle, r.Contrast),
                    8, dst.Rows - 10, Scalar.Yellow, 13);
            }

            LastSummary = string.Format("斜边SFR: MTF50={0:F3} MTF30={1:F3} MTF10={2:F3} cyc/px  倾角{3:F1}° 对比度{4:F0}",
                Mtf50, Mtf30, Mtf10, r.EdgeAngle, r.Contrast);
            return dst;
        }

        /// <summary>从 startFreq 起找曲线降到 level 的频率；找不到返回 NaN</summary>
        private static double FindFrom(MtfHelper.SfrResult r, double startFreq, double level)
        {
            for (int i = 1; i < r.Freq.Length; i++)
            {
                if (r.Freq[i] < startFreq) continue;
                if (r.Mtf[i] > level) continue;
                double d = r.Mtf[i] - r.Mtf[i - 1];
                if (Math.Abs(d) < 1e-9) return r.Freq[i];
                double t = (level - r.Mtf[i - 1]) / d;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                return r.Freq[i - 1] + (t * (r.Freq[i] - r.Freq[i - 1]));
            }
            return double.NaN;
        }

        /// <summary>在结果图左下角画 MTF 曲线（横轴频率、纵轴 MTF 0~1）</summary>
        private void DrawMtfPlot(Mat dst, MtfHelper.SfrResult r)
        {
            int w = Math.Min(320, dst.Cols - 20);
            int h = Math.Min(200, dst.Rows - 40);
            if (w < 80 || h < 60) return;
            int x0 = 10, y0 = dst.Rows - 20;

            using Mat overlay = dst.Clone();
            Cv2.Rectangle(overlay, new Rect(x0, y0 - h, w, h), new Scalar(20, 20, 20), -1);
            Cv2.AddWeighted(overlay, 0.75, dst, 0.25, 0, dst);

            Cv2.Rectangle(dst, new Rect(x0, y0 - h, w, h), Scalar.Gray, 1);
            // 0.5 参考线
            int yHalf = y0 - (int)(h * 0.5);
            Cv2.Line(dst, new Point(x0, yHalf), new Point(x0 + w, yHalf), new Scalar(90, 90, 90), 1);
            Cv2.PutText(dst, "0.5", new Point(x0 + w - 24, yHalf - 3), HersheyFonts.HersheySimplex, 0.32, Scalar.Gray, 1);

            double fMax = r.Freq[^1];
            Point prev = new(-1, -1);
            for (int i = 0; i < r.Freq.Length; i++)
            {
                double fx = r.Freq[i] / Math.Max(1e-9, fMax);
                double my = Math.Clamp(r.Mtf[i], 0, 1);
                var p = new Point(x0 + (int)(fx * w), y0 - (int)(my * h));
                if (prev.X >= 0) Cv2.Line(dst, prev, p, Scalar.Lime, 1);
                prev = p;
            }

            // 标出 MTF50
            if (!double.IsNaN(Mtf50))
            {
                var p50 = new Point(x0 + (int)(Mtf50 / Math.Max(1e-9, fMax) * w), yHalf);
                Cv2.Circle(dst, p50, 4, Scalar.Red, -1);
            }
            Cv2.PutText(dst, "MTF", new Point(x0 + 4, y0 - h + 13), HersheyFonts.HersheySimplex, 0.35, Scalar.White, 1);
            Cv2.PutText(dst, "f(cyc/px)", new Point(x0 + w - 70, y0 - 4), HersheyFonts.HersheySimplex, 0.32, Scalar.White, 1);
        }
    }
}
