using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 去雾（暗通道先验简化版）：暗通道 = 各通道最小值做最小值滤波（erode）；
    /// 大气光取暗通道最亮值；透射率 t = 1 − 强度×暗通道/大气光（下限 0.1）；
    /// 恢复 J = (I − A)/t + A。雾天/灰蒙图增强对比与清晰度。
    /// 强度=100 全量去雾，越小越接近原图。
    /// </summary>
    public class DehazeTask : IVisionTask
    {
        public string TaskName => "去雾";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "强度",
                Min = 0,
                Max = 100,
                DefaultValue = 50,
                DisplayFormat = "强度:{0}%",
                Tip = "去雾强度：0=原样，50=中等，100=全量去雾"
            },
            new TaskParamDesc
            {
                ParamName = "窗口",
                Min = 3,
                Max = 20,
                DefaultValue = 7,
                DisplayFormat = "窗口:{0}",
                ForceOdd = true,
                Tip = "暗通道最小值滤波窗口半径（自动取奇数）：越大去雾越平滑，细节损失越多"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Channels() != 3)
                return srcMat.Clone();
            double w = paramValues[0] / 100.0;
            if (w <= 0)
                return srcMat.Clone();
            int r = Math.Max(1, paramValues[1]);

            // 1) 暗通道：min(B,G,R) 的最小值滤波（erode）
            Cv2.Split(srcMat, out var chs);
            using Mat minT = new();
            Cv2.Min(chs[1], chs[0], minT);       // min(B,G)
            using Mat minCh = new();
            Cv2.Min(chs[2], minT, minCh);        // min(R, min(B,G))
            using Mat dark = new();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * r + 1, 2 * r + 1));
            Cv2.Erode(minCh, dark, kernel);

            // 2) 大气光 A：暗通道最亮值（简化取全局）
            Cv2.MinMaxLoc(dark, out _, out double maxV, out _, out _);
            float A = (float)Math.Max(1.0, maxV);          // 0~255
            float Af = A / 255f;

            // 3) 透射率 t = 1 − w·dark/A，下限 0.1
            using Mat darkF = new();
            dark.ConvertTo(darkF, MatType.CV_32F, 1.0 / 255.0);
            using Mat ones = new Mat(darkF.Rows, darkF.Cols, MatType.CV_32F, Scalar.All(1.0));
            using Mat t = new();
            Cv2.Subtract(ones, w * darkF, t);              // 1 − w·dark/A
            Cv2.Max(t, 0.1, t);                            // 下限保护

            // 4) 恢复 J = (I − A)/t + A，逐通道
            var outs = new Mat[3];
            for (int c = 0; c < 3; c++)
            {
                using Mat f = new();
                chs[c].ConvertTo(f, MatType.CV_32F, 1.0 / 255.0);
                Cv2.Subtract(f, Af, f);
                Cv2.Divide(f, t, f);
                Cv2.Add(f, Af, f);
                outs[c] = new Mat();
                f.ConvertTo(outs[c], MatType.CV_8U, 255.0);
            }
            using Mat j = new();
            Cv2.Merge(outs, j);

            // 5) 按强度与原图混合
            Mat dst = new();
            Cv2.AddWeighted(srcMat, 1.0 - w, j, w, 0.0, dst);
            foreach (var m in chs) m.Dispose();
            foreach (var m in outs) m.Dispose();
            return dst;
        }
    }
}
