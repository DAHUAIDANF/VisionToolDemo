using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 亮度均衡（LSC 镜头阴影校正）：消除镜头暗角与光照不均，让整幅图像亮度一致，
    /// 改善后续阈值/检测算子在画面边缘的稳定性。
    /// 双模式：
    ///   · 标定模式（点"导入模板"载入均匀场参考图后生效）：逐像素增益图校正
    ///     gain = 目标亮度 / 参考亮度场——真正的镜头阴影标定；参考图随流水线保存
    ///     （IStatefulTask），换相机/镜头/光源后重新拍一张均匀场导入即可；
    ///   · 自动模式（无参考图）：对当前帧自身用大核高斯估计亮度场做平场。
    /// 与"光照校正"算子的差异：彩色逐通道独立校正（不转灰，顺带修正色彩阴影）、
    /// 目标亮度可调（0=自动取亮度场逐通道均值，保持整体亮度不变）、
    /// 校正强度与原图按比例混合、暗区增益钳位防止噪声被放大成亮斑。
    /// 参考图应拍均匀白板/灰卡（画面内无特征）；"背景核"同时用于参考图去噪平滑，
    /// 核必须远大于画面内前景特征（码点/缺陷/纹理周期），否则前景会被当亮度抹平。
    /// 参数：背景核 / 目标亮度(0=自动) / 校正强度% / 增益上限。
    /// </summary>
    public class LscTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "亮度均衡LSC";

        /// <summary>UI 层赋值：均匀场参考图（白板/灰卡）；null 或空 = 自动模式</summary>
        public Mat ReferenceMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>状态 = 参考图（base64 PNG），随流水线保存/复制</summary>
        public string SaveState() => VisionHelper.SaveTemplateState(ReferenceMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                ReferenceMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                // 亮度场估计核（标定模式下也用于参考图平滑去噪）：
                // 必须远大于前景特征，否则特征被当成亮度变化抹掉
                ParamName = "背景核",
                Min = 3,
                Max = 301,
                DefaultValue = 51,
                DisplayFormat = "核:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                // 校正目标灰度：0 = 自动（亮度场逐通道均值，整体亮度不变）；
                // >0 = 固定目标灰度（把整幅图统一拉到该亮度）
                ParamName = "目标亮度",
                Min = 0,
                Max = 255,
                DefaultValue = 0,
                DisplayFormat = "目标:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 校正强度：校正图与原图的混合比，100 = 全校正；调低可保留部分原始明暗分布
                ParamName = "校正强度%",
                Min = 0,
                Max = 100,
                DefaultValue = 100,
                DisplayFormat = "强度:{0}%",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 暗区增益上限：亮度场越暗增益越大，钳位防止近黑区域噪声被放大
                ParamName = "增益上限",
                Min = 1,
                Max = 16,
                DefaultValue = 8,
                DisplayFormat = "增益≤{0}x",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int ksize = Math.Max(3, (paramValues?.Length > 0 ? paramValues[0] : 51) | 1);
            int targetVal = paramValues?.Length > 1 ? paramValues[1] : 0;
            double strength = Math.Clamp((paramValues?.Length > 2 ? paramValues[2] : 100) / 100.0, 0.0, 1.0);
            double maxGain = Math.Max(1, paramValues?.Length > 3 ? paramValues[3] : 8);

            bool calibrated = ReferenceMat != null && !ReferenceMat.Empty();
            Mat dst = new();

            using (Mat bgr = VisionHelper.ToBgrCopy(srcMat))
            using (Mat bg = new())
            {
                // 亮度场：标定模式 = 参考图（尺寸不符先线性缩放）；自动模式 = 当前帧自身。
                // 大核高斯平滑只留大尺度亮度分布（标定模式下顺带抹掉白板纹理噪声）。
                if (calibrated)
                {
                    using (Mat refBgr = VisionHelper.ToBgrCopy(ReferenceMat))
                    {
                        if (refBgr.Size() != bgr.Size())
                            Cv2.Resize(refBgr, bg, bgr.Size(), 0, 0, InterpolationFlags.Linear);
                        else
                            refBgr.CopyTo(bg);
                    }
                    Cv2.GaussianBlur(bg, bg, new OpenCvSharp.Size(ksize, ksize), 0);
                }
                else
                {
                    Cv2.GaussianBlur(bgr, bg, new OpenCvSharp.Size(ksize, ksize), 0);
                }

                Mat[] chIn = null, bgCh = null, chOut = null;
                try
                {
                    Cv2.Split(bgr, out chIn);
                    Cv2.Split(bg, out bgCh);
                    Scalar mean = Cv2.Mean(bg);
                    chOut = new Mat[3];
                    double t0 = 0, t1 = 0, t2 = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        // 逐通道目标：固定值或该通道亮度场均值（自动，顺带修正色彩阴影）
                        double t = targetVal > 0 ? targetVal : mean[c];
                        if (c == 0) t0 = t; else if (c == 1) t1 = t; else t2 = t;
                        // 亮度场下限 = 目标/增益上限 → 暗区增益被钳在 maxGain 以内
                        Cv2.Max(bgCh[c], t / maxGain, bgCh[c]);
                        chOut[c] = new Mat();
                        Cv2.Divide(chIn[c], bgCh[c], chOut[c], t);
                        // 校正强度混合：dst = 原图*(1-s) + 校正图*s
                        if (strength < 0.999)
                            Cv2.AddWeighted(chIn[c], 1.0 - strength, chOut[c], strength, 0, chOut[c]);
                        chOut[c].ConvertTo(chOut[c], MatType.CV_8U);
                    }
                    Cv2.Merge(chOut, dst);

                    string tgt = targetVal > 0
                        ? $"目标 {targetVal}"
                        : $"目标 自动({t0:F0}/{t1:F0}/{t2:F0})";
                    LastSummary = calibrated
                        ? $"LSC: 标定校正 ({tgt}, 强度 {(int)(strength * 100)}%, 增益≤{maxGain}x)"
                        : $"LSC: 自动平场 ({tgt}, 核 {ksize}, 强度 {(int)(strength * 100)}%, 增益≤{maxGain}x)";
                }
                finally
                {
                    if (chIn != null) foreach (Mat m in chIn) m?.Dispose();
                    if (bgCh != null) foreach (Mat m in bgCh) m?.Dispose();
                    if (chOut != null) foreach (Mat m in chOut) m?.Dispose();
                }
            }
            return dst;
        }
    }
}
