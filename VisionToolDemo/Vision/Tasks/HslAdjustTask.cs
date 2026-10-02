using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 色相/饱和度算子（PS「色相饱和度」）：在 HSV 空间调整色相偏移、
    /// 饱和度倍率与明度偏移。参数：色相偏移（-180~180，正值顺向偏移）、
    /// 饱和度（0~200%，100=原样，0=去色成灰度感）、明度（-100~100）。
    /// </summary>
    public class HslAdjustTask : IVisionTask, IResultReporter
    {
        public string LastSummary { get; private set; } = "";

        public string TaskName => "色相饱和度";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc { SupportRoi = true, ParamName = "色相", Min = -180, Max = 180, DefaultValue = 0, DisplayFormat = "色相:{0}", Tip = "色相旋转偏移（度）：0=不动，正值颜色整体顺向偏移" },
            new TaskParamDesc { ParamName = "饱和度", Min = 0, Max = 200, DefaultValue = 100, DisplayFormat = "饱和度:{0}%", Tip = "100=原样；0=去色；>100 更鲜艳" },
            new TaskParamDesc { ParamName = "明度", Min = -100, Max = 100, DefaultValue = 0, DisplayFormat = "明度:{0}", Tip = "亮度偏移：正值变亮，负值变暗" }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int hOff = paramValues[0];
            int sScale = paramValues[1];
            int vOff = paramValues[2];

            // 灰度图（1 通道）先转成 BGR，HSV 空间需要 3 通道
            Mat work = srcMat;
            using Mat rgb = new();
            if (srcMat.Channels() == 1)
            {
                Cv2.CvtColor(srcMat, rgb, ColorConversionCodes.GRAY2BGR);
                work = rgb;
            }
            using Mat hsv = new();
            Cv2.CvtColor(work, hsv, ColorConversionCodes.BGR2HSV);
            Mat[] ch = Cv2.Split(hsv);
            using Mat h = ch[0], s = ch[1], v = ch[2];

            // 色相偏移：H 范围 0~180；正偏移后超过 180 的回绕（减 180）
            int delta = ((hOff % 180) + 180) % 180;
            Mat h2 = new();
            Cv2.Add(h, new Scalar(delta), h2);
            Mat hw = new();
            Cv2.Subtract(h2, new Scalar(180), hw);   // 8U 减法饱和在 0，>180 的像素变成"原值-180"实现回绕
            // 回绕只在原 h+delta>=180 时发生；OpenCV 8U 饱和减法对 <180 的像素为 0，
            // 需区分：先用比较掩码取正确的值
            using Mat mask = new();
            Cv2.Threshold(h2, mask, 179, 255, ThresholdTypes.Binary); // h2>=180 → 255
            Mat hFinal = new();
            Cv2.BitwiseOr(hw, h2, hFinal, mask);     // 仅对回绕像素取 hw，其余保留 h2

            // 饱和度倍率（0~200%，100=1.0；8U 乘法饱和到 255）
            Mat s2 = new();
            Cv2.Multiply(s, sScale / 100.0, s2);

            // 明度偏移（-100~100 → -255~255，8U 自动饱和）
            Mat v2 = new();
            Cv2.Add(v, new Scalar(vOff * 255 / 100), v2);

            Mat merged = new();
            Mat[] newCh = { hFinal, s2, v2 };
            Cv2.Merge(newCh, merged);
            Mat dst = new();
            Cv2.CvtColor(merged, dst, ColorConversionCodes.HSV2BGR);
            LastSummary = string.Format("色相饱和度: H{0}° S{1}% V{2}", hOff, sScale, vOff);
            return dst;
        }
    }
}
