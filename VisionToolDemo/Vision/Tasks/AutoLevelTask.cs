using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 自动色阶：统计直方图，裁剪两端指定百分位的像素后，把剩余灰度范围线性拉伸到 0~255。
    /// 低对比度图的一键增强：自动去掉极暗/极亮噪声，展开中间灰度带。
    /// 彩色图逐通道独立拉伸（与 PS 自动色阶一致，会增强色彩对比）。
    /// </summary>
    public class AutoLevelTask : IVisionTask
    {
        public string TaskName => "自动色阶";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "裁剪百分比x10",
                Min = 0,
                Max = 100,
                DefaultValue = 10,          // 1.0%：两端各裁 1%
                DisplayFormat = "裁剪:{0:F1}%",
                Tip = "两端裁剪的像素百分比（×10）：0=不裁剪，10=1%，50=5%；低对比图可加大裁剪量"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            double clipFrac = paramValues[0] / 1000.0;   // 0~100 → 0~10%
            int channels = srcMat.Channels();
            using var src = srcMat.Channels() == 1 ? srcMat.Clone() : srcMat;

            var chs = new Mat[channels];
            Cv2.Split(src, out var split);
            var outs = new Mat[channels];
            try
            {
                for (int c = 0; c < channels; c++)
                {
                    Mat ch = split[c];
                    byte[] lut = BuildLut(ch, clipFrac);
                    outs[c] = new Mat();
                    Cv2.LUT(ch, lut, outs[c]);
                }
                Mat dst = new();
                Cv2.Merge(outs, dst);
                return dst;
            }
            finally
            {
                foreach (var m in split) m.Dispose();
                foreach (var m in outs) m?.Dispose();
            }
        }

        /// <summary>单通道直方图两端裁剪后线性拉伸的查找表</summary>
        private static byte[] BuildLut(Mat ch, double clipFrac)
        {
            int[] hist = new int[256];
            int rows = ch.Rows, cols = ch.Cols;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                    hist[ch.At<byte>(y, x)]++;
            }
            int total = rows * cols;
            int cut = (int)(total * clipFrac);
            int acc = 0, lo = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc > cut) { lo = i; break; }
            }
            acc = 0;
            int hi = 255;
            for (int i = 255; i >= 0; i--)
            {
                acc += hist[i];
                if (acc > cut) { hi = i; break; }
            }
            byte[] lut = new byte[256];
            if (hi <= lo)   // 裁剪过度：原样
            {
                for (int i = 0; i < 256; i++) lut[i] = (byte)i;
                return lut;
            }
            double scale = 255.0 / (hi - lo);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)Math.Round((i - lo) * scale);
                lut[i] = (byte)Math.Max(0, Math.Min(255, v));
            }
            return lut;
        }
    }
}
