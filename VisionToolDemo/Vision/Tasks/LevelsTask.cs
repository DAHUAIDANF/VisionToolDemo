using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 色阶算子（PS「图像 → 调整 → 色阶」）：设置黑场/白场/中间调 gamma，
    /// 把输入动态范围映射到 0~255；「自动色阶」按直方图 1%/99% 自动定黑场白场。
    /// 参数：黑场（0~100，低于此值的像素压到 0）、白场（0~100，高于此值的像素
    /// 拉到 255）、中间调 gamma（10~300%，100=线性）、自动色阶（1=忽略黑场白场
    /// 按直方图自动拉伸）。
    /// </summary>
    public class LevelsTask : IVisionTask, IResultReporter
    {
        public string LastSummary { get; private set; } = "";

        public string TaskName => "色阶";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc { SupportRoi = true, ParamName = "黑场", Min = 0, Max = 100, DefaultValue = 0, DisplayFormat = "黑场:{0}", Tip = "输入下限：低于此值的像素压到 0（暗部更黑）" },
            new TaskParamDesc { ParamName = "白场", Min = 0, Max = 100, DefaultValue = 100, DisplayFormat = "白场:{0}", Tip = "输入上限：高于此值的像素拉到 255（亮部更亮）" },
            new TaskParamDesc { ParamName = "中间调", Min = 10, Max = 300, DefaultValue = 100, DisplayFormat = "gamma:{0}", Tip = "中间调 gamma：100=线性；<100 提亮中间调，>100 压暗中间调" },
            new TaskParamDesc { ParamName = "自动", Min = 0, Max = 1, DefaultValue = 0, DisplayFormat = "自动:{0}", Tip = "1=按直方图 1%/99% 自动确定黑场白场（忽略上面两个手动值）" }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int black = paramValues[0];
            int white = paramValues[1];
            double gamma = paramValues[2] / 100.0;
            bool auto = paramValues[3] == 1;

            // 灰度图（1 通道）直接使用；彩色图转灰度用于算直方图（LUT 仍作用于原通道）
            Mat gray;
            using Mat grayC = new();
            if (srcMat.Channels() >= 3)
            {
                Cv2.CvtColor(srcMat, grayC, ColorConversionCodes.BGR2GRAY);
                gray = grayC;
            }
            else
            {
                gray = srcMat;
            }

            double inLow, inHigh;
            if (auto)
            {
                AutoLevels(gray, out inLow, out inHigh);
            }
            else
            {
                inLow = black / 100.0 * 255.0;
                inHigh = white / 100.0 * 255.0;
            }
            if (inHigh <= inLow) inHigh = inLow + 1.0;

            // 生成 256 项 LUT：out = ((in - low)/(high-low))^(1/gamma) * 255
            var lut = new byte[256];
            double span = inHigh - inLow;
            double gInv = 1.0 / Math.Max(1e-3, gamma);
            for (int i = 0; i < 256; i++)
            {
                double v = (i - inLow) / span;
                v = Math.Clamp(v, 0.0, 1.0);
                double o = Math.Pow(v, gInv) * 255.0;
                lut[i] = (byte)Math.Clamp((int)Math.Round(o), 0, 255);
            }

            using Mat lutMat = Mat.FromPixelData(1, 256, MatType.CV_8UC1, lut);
            Mat dst = new();
            Cv2.LUT(srcMat, lutMat, dst);
            LastSummary = string.Format("色阶: {0} 黑场{1} 白场{2} gamma{3}",
                auto ? "自动" : "手动", (int)inLow, (int)inHigh, paramValues[2]);
            return dst;
        }

        /// <summary>按直方图 1%/99% 百分位求黑场/白场（抗单点噪声）</summary>
        private static void AutoLevels(Mat gray, out double inLow, out double inHigh)
        {
            int[] hist = new int[256];
            int step = (int)gray.Step();
            unsafe
            {
                byte* p = (byte*)gray.Data;
                for (int y = 0; y < gray.Rows; y++)
                    for (int x = 0; x < gray.Cols; x++)
                        hist[p[y * step + x]]++;
            }
            long total = Math.Max(1, gray.Rows * (long)gray.Cols);
            long lo = 0, hi = 0;
            double acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= total * 0.01) { lo = i; break; } }
            acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= total * 0.99) { hi = i; break; } }
            inLow = lo;
            inHigh = hi == lo ? lo + 1 : hi;
        }
    }
}
