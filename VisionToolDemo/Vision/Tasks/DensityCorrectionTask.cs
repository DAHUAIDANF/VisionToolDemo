using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 浓淡补正（灰度范围映射）：把输入灰度范围 [输入下限, 输入上限] 线性映射到
    /// 输出范围 [输出下限, 输出上限]，范围外截断到端点。
    ///
    /// 典型用途：
    ///  - 低对比度图：把挤在一起的灰度带拉开展示（如输入 100~160 → 输出 0~255）
    ///  - 暗部/亮部细节增强：把感兴趣灰度段映射到全范围
    ///  - 反向映射：输出下限=255、输出上限=0 时等效"反相 + 拉伸"
    /// 彩色图逐通道应用同一映射（用 LUT 一次完成）。
    /// </summary>
    public class DensityCorrectionTask : IVisionTask
    {
        public string TaskName => "浓淡补正";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "输入下限",
                Min = 0,
                Max = 254,
                DefaultValue = 0,
                DisplayFormat = "输入下限:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "输入上限",
                Min = 1,
                Max = 255,
                DefaultValue = 255,
                DisplayFormat = "输入上限:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "输出下限",
                Min = 0,
                Max = 255,
                DefaultValue = 0,
                DisplayFormat = "输出下限:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "输出上限",
                Min = 0,
                Max = 255,
                DefaultValue = 255,
                DisplayFormat = "输出上限:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int inMin = paramValues[0];
            int inMax = paramValues[1];
            int outMin = paramValues[2];
            int outMax = paramValues[3];

            // 参数防御：输入范围非法（上限不大于下限）时原样返回，不抛错
            if (inMax <= inMin)
                return srcMat.Clone();

            // 预计算 256 项查找表：范围内线性映射，范围外截断到输出端点
            byte[] lut = new byte[256];
            double scale = (double)(outMax - outMin) / (inMax - inMin);
            for (int i = 0; i < 256; i++)
            {
                int v;
                if (i <= inMin) v = outMin;
                else if (i >= inMax) v = outMax;
                else v = (int)Math.Round(outMin + (i - inMin) * scale);
                lut[i] = (byte)Math.Max(0, Math.Min(255, v));
            }

            // LUT 逐像素映射（灰度/彩色图均适用，彩色图逐通道应用同一映射）
            Mat dst = new();
            Cv2.LUT(srcMat, lut, dst);
            return dst;
        }
    }
}
