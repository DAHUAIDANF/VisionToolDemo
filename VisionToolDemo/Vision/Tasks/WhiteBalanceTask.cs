using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 白平衡（灰度世界法）：统计各通道均值，缩放各通道使均值趋于一致，自动校正色偏。
    /// 强度=100 完全校正；越小保留越多原色偏。灰度图原样返回。
    /// </summary>
    public class WhiteBalanceTask : IVisionTask
    {
        public string TaskName => "白平衡";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "强度",
                Min = 0,
                Max = 200,
                DefaultValue = 100,
                DisplayFormat = "强度:{0}%",
                Tip = "校正比例：100=完全灰度世界校正；50=校正一半；0=原样"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            // 灰度图无色偏，原样返回
            if (srcMat.Channels() != 3)
                return srcMat.Clone();
            double mix = paramValues[0] / 100.0;
            if (mix <= 0)
                return srcMat.Clone();

            Cv2.Split(srcMat, out var chs);
            try
            {
                double[] mean = new double[3];
                double sum = 0;
                for (int c = 0; c < 3; c++)
                {
                    mean[c] = chs[c].Mean().Val0;
                    sum += mean[c];
                }
                double avg = sum / 3.0;
                if (avg < 1e-6) return srcMat.Clone();

                var outs = new Mat[3];
                for (int c = 0; c < 3; c++)
                {
                    // 系数 = 总均值/通道均值，限制在 0.5~2 防止过曝/过暗
                    double coef = avg / Math.Max(1e-6, mean[c]);
                    coef = Math.Max(0.5, Math.Min(2.0, coef));
                    coef = 1.0 + (coef - 1.0) * mix;   // 按强度混合
                    outs[c] = new Mat();
                    chs[c].ConvertTo(outs[c], MatType.CV_8UC1, coef);
                }
                Mat dst = new();
                Cv2.Merge(outs, dst);
                foreach (var m in outs) m.Dispose();
                return dst;
            }
            finally
            {
                foreach (var m in chs) m.Dispose();
            }
        }
    }
}
