using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// Retinex 增强（单尺度 SSR）：在 HSV 的 V（亮度）通道上做
    /// log(V) − log(高斯模糊 V) 光照补偿，再把结果归一化拉伸。
    /// 暗光/逆光/照度不均图：压暗部提亮、亮部细节保留，颜色不偏。
    /// 强度=100 全量替换为 Retinex 结果，越小越接近原图。
    /// </summary>
    public class RetinexTask : IVisionTask
    {
        public string TaskName => "Retinex增强";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "尺度",
                Min = 5,
                Max = 300,
                DefaultValue = 120,
                DisplayFormat = "尺度:{0}",
                ForceOdd = true,
                Tip = "高斯模糊核半径：小尺度增强局部细节，大尺度补偿整体光照"
            },
            new TaskParamDesc
            {
                ParamName = "强度",
                Min = 0,
                Max = 200,
                DefaultValue = 100,
                DisplayFormat = "强度:{0}%",
                Tip = "Retinex 结果与原始亮度的混合比例，100=全量增强"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int scale = Math.Max(1, paramValues[0]);
            double mix = paramValues[1] / 100.0;

            // 灰度图：直接 SSR
            if (srcMat.Channels() == 1)
            {
                using Mat v = srcMat.Clone();
                Mat dst = new();
                RetinexChannel(v, scale, mix, dst);
                return dst;
            }

            // 彩色：HSV 只增强 V 通道，保留 H/S → 颜色不偏
            using Mat hsv = new();
            Cv2.CvtColor(srcMat, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Split(hsv, out var chs);
            using Mat vCh = chs[2].Clone();
            RetinexChannel(vCh, scale, mix, vCh);
            Mat merged = new();
            Cv2.Merge(new[] { chs[0], chs[1], vCh }, merged);
            foreach (var m in chs) m.Dispose();
            using Mat bgr = new();
            Cv2.CvtColor(merged, bgr, ColorConversionCodes.HSV2BGR);
            merged.Dispose();
            return bgr;
        }

        /// <summary>单通道 SSR：log(I+1) − log(blur(I)+1)，MinMax 拉伸后按 mix 与原始混合</summary>
        private static void RetinexChannel(Mat ch, int scale, double mix, Mat dst)
        {
            using Mat blur = new();
            Cv2.GaussianBlur(ch, blur, new OpenCvSharp.Size(scale * 2 + 1, scale * 2 + 1), 0);
            using Mat srcF = new();
            using Mat blurF = new();
            ch.ConvertTo(srcF, MatType.CV_32F, 1.0 / 255.0);
            blur.ConvertTo(blurF, MatType.CV_32F, 1.0 / 255.0);
            // log 域：ln(I+1)，再用原图减模糊（去除光照分量）
            Cv2.Add(srcF, 0.001, srcF);
            Cv2.Log(srcF, srcF);
            Cv2.Add(blurF, 0.001, blurF);
            Cv2.Log(blurF, blurF);
            using Mat diff = new();
            Cv2.Subtract(srcF, blurF, diff);
            // 拉伸到 0~255
            Cv2.MinMaxLoc(diff, out double mn, out double mx, out _, out _);
            double span = mx - mn;
            if (span < 1e-6) span = 1e-6;
            using Mat r = new();
            Cv2.Subtract(diff, mn, r);
            Cv2.Multiply(r, 255.0 / span, r);
            r.ConvertTo(r, MatType.CV_8U);
            // 按强度混合：dst = (1-mix)*原 + mix*Retinex
            Cv2.AddWeighted(ch, 1.0 - mix, r, mix, 0.0, dst);
        }
    }
}
