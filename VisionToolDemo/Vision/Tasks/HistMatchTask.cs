using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 直方图匹配（规定化）算子：把当前图灰度分布匹配到参考图（模板）的灰度分布。
    /// 用于光照归一化、多相机灰度一致性校正、风格统一。
    /// 原理：各自 CDF 反查映射表。
    /// 参数：直方图分箱（64~256）、通道（0=灰度 1=彩色逐通道匹配）。
    /// </summary>
    public class HistMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "直方图匹配";

        /// <summary>参考图（目标灰度分布来源）</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "直方图分箱",
                Min = 64, Max = 256, DefaultValue = 256,
                DisplayFormat = "分箱:{0}",
                Tip = "灰度直方图分箱数（越大越精细）"
            },
            new TaskParamDesc
            {
                ParamName = "通道模式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "通道:{0}",
                Tip = "0=转灰度后匹配；1=彩色每个通道分别匹配（保留色彩）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "直方图匹配: 未设置参考图（模板）";
                return srcMat.Clone();
            }

            int bins = Math.Max(64, Math.Min(256, paramValues[0]));
            bool colorMode = paramValues[1] != 0;

            if (!colorMode || srcMat.Channels() == 1)
            {
                using (Mat gray = VisionHelper.ToGray(srcMat))
                using (Mat refGray = VisionHelper.ToGray(TemplateMat))
                {
                    byte[] lut = BuildLut(gray, refGray, bins);
                    Mat dst = new();
                    Cv2.LUT(gray, Mat.FromPixelData(256, 1, MatType.CV_8U, lut), dst);
                    LastSummary = $"直方图匹配: 灰度（{bins} 分箱）";
                    return dst;
                }
            }
            else
            {
                // 彩色：逐通道匹配
                Mat[] srcPlanes = new Mat[3], refPlanes = new Mat[3];
                Cv2.Split(srcMat, out srcPlanes);
                Cv2.Split(TemplateMat, out refPlanes);
                Mat[] dstPlanes = new Mat[3];
                for (int c = 0; c < 3; c++)
                {
                    byte[] lut = BuildLut(srcPlanes[c], refPlanes[c], bins);
                    Mat dstChan = new();
                    Cv2.LUT(srcPlanes[c], Mat.FromPixelData(256, 1, MatType.CV_8U, lut), dstChan);
                    dstPlanes[c] = dstChan;
                    srcPlanes[c].Dispose();
                    refPlanes[c].Dispose();
                }
                Mat merged = new();
                Cv2.Merge(dstPlanes, merged);
                foreach (var p in dstPlanes) p.Dispose();
                LastSummary = $"直方图匹配: 彩色逐通道（{bins} 分箱）";
                return merged;
            }
        }

        /// <summary>构建查表：当前图 CDF 反查参考图 CDF 的映射</summary>
        private static byte[] BuildLut(Mat src, Mat reference, int bins)
        {
            int[] srcHist = new int[bins], refHist = new int[bins];
            float[] srcCdf = new float[bins], refCdf = new float[bins];

            // 统计直方图
            for (int y = 0; y < src.Rows; y++)
                for (int x = 0; x < src.Cols; x++)
                {
                    int v = src.At<byte>(y, x);
                    srcHist[Math.Min(bins - 1, v * bins / 256)]++;
                }
            for (int y = 0; y < reference.Rows; y++)
                for (int x = 0; x < reference.Cols; x++)
                {
                    int v = reference.At<byte>(y, x);
                    refHist[Math.Min(bins - 1, v * bins / 256)]++;
                }

            // CDF 归一化
            long total = (long)src.Rows * src.Cols;
            long acc = 0;
            for (int i = 0; i < bins; i++) { acc += srcHist[i]; srcCdf[i] = (float)acc / total; }
            total = (long)reference.Rows * reference.Cols;
            acc = 0;
            for (int i = 0; i < bins; i++) { acc += refHist[i]; refCdf[i] = (float)acc / total; }

            // 映射表：src 灰度 g → 目标灰度（CDF 最接近的参考灰度）
            byte[] lut = new byte[256];
            for (int g = 0; g < 256; g++)
            {
                int b = Math.Min(bins - 1, g * bins / 256);
                float cdf = srcCdf[b];
                int best = 0;
                float bestDiff = float.MaxValue;
                for (int j = 0; j < bins; j++)
                {
                    float d = Math.Abs(refCdf[j] - cdf);
                    if (d < bestDiff) { bestDiff = d; best = j; }
                }
                lut[g] = (byte)Math.Min(255, best * 256 / bins + 256 / bins / 2);
            }
            return lut;
        }
    }
}
