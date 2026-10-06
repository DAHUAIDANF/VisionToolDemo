using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 色彩平衡算子：按通道增益调整 RGB（自动保持亮度近似不变），
    /// 用于多相机颜色一致性、白平衡微调。
    /// 参数：R 增益、G 增益、B 增益（%）。
    /// </summary>
    public class ColorBalanceTask : IVisionTask, IResultReporter
    {
        public string TaskName => "色彩平衡";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "R增益",
                Min = 50, Max = 200, DefaultValue = 100,
                DisplayFormat = "R:{0}%",
                Tip = "红色通道增益（100=不变）"
            },
            new TaskParamDesc
            {
                ParamName = "G增益",
                Min = 50, Max = 200, DefaultValue = 100,
                DisplayFormat = "G:{0}%",
                Tip = "绿色通道增益（100=不变）"
            },
            new TaskParamDesc
            {
                ParamName = "B增益",
                Min = 50, Max = 200, DefaultValue = 100,
                DisplayFormat = "B:{0}%",
                Tip = "蓝色通道增益（100=不变）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            double rGain = paramValues[0] / 100.0;
            double gGain = paramValues[1] / 100.0;
            double bGain = paramValues[2] / 100.0;

            Mat dst = srcMat.Clone();

            if (dst.Channels() == 1)
            {
                // 灰度图只做亮度平衡（用 G 增益）
                using (Mat tmp = new())
                {
                    dst.ConvertTo(tmp, -1, gGain, 0);
                    dst.Dispose();
                    dst = tmp.Clone();
                }
                LastSummary = $"色彩平衡: 灰度亮度 {paramValues[1]}%";
                return dst;
            }

            // 分通道乘增益
            Mat[] ch;
            Cv2.Split(dst, out ch);
            using (Mat r = ch[0], g = ch[1], b = ch[2])
            using (Mat tmp = new())
            {
                r.ConvertTo(tmp, -1, rGain, 0); tmp.CopyTo(r);
                g.ConvertTo(tmp, -1, gGain, 0); tmp.CopyTo(g);
                b.ConvertTo(tmp, -1, bGain, 0); tmp.CopyTo(b);
            }
            Cv2.Merge(ch, dst);
            foreach (var c in ch) c.Dispose();

            LastSummary = $"色彩平衡: R{paramValues[0]}% G{paramValues[1]}% B{paramValues[2]}%";
            return dst;
        }
    }
}
