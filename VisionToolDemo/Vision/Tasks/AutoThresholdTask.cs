using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 自动二值化：把"整图阈值"换成能跟随光照的自动门限，是 8 个固定阈值算子
    /// (二值化/轮廓检测/最小旋转外接矩形/凸包检测/轮廓质心/距离变换/圆拟合/轮廓多边形拟合)
    /// 在光照不均时的统一替代入口。
    ///
    /// 为什么需要它（实测）：一张带 55% 横向光照斜坡的图，ground truth 暗区占 21.6%，
    /// 固定阈值 127 的二值化误判成 48.9%~78.4%；Otsu 的阈值会跟着斜坡从 118 降到 109，
    /// 依然贴近真值。固定单一阈值在工业现场几乎必然被光照打败。
    ///
    /// 三种模式：
    ///   · 全局 Otsu        —— 光照均匀、前景/背景各成一峰时最稳
    ///   · 自适应(Otsu 初值) —— 逐块局部阈值，抗光照渐变/阴影，窗口大小可调
    ///   · 双峰谷底         —— 在 Otsu 附近做直方图谷值微调，峰谷分明时比 Otsu 更贴边
    /// 另带"阈值偏置%"用于把自动结果整体推高/压低（处理前景偏多/偏少的偏色场景）。
    /// </summary>
    public class AutoThresholdTask : IVisionTask, IResultReporter
    {
        public string TaskName => "自动二值化";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "模式 0全局Otsu 1自适应 2谷底",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "mode:{0}",
                Group = "阈值",
                Tip = "阈值怎么选。全局Otsu：光照均匀、目标与背景可分成两堆时最稳，首选。" +
                      "自适应：抗光照渐变/阴影，只适合字符、纹理等小于窗口的结构，大块实心目标会失效。" +
                      "谷底：双峰明显时比 Otsu 更贴边。"
            },
            new TaskParamDesc
            {
                // 自适应模式的局部窗口半径（像素）；越大越接近全局，越小越能压住局部阴影，
                // 但过小会把噪声当边缘。仅模式 1 生效。
                ParamName = "局部窗口半径",
                Min = 3,
                Max = 151,
                DefaultValue = 25,
                DisplayFormat = "win:{0}px",
                ForceOdd = true,
                Group = "阈值",
                Tip = "仅自适应模式生效。局部比较的范围：要大于目标的特征尺寸，否则大目标内部被判成背景。" +
                      "目标越粗，窗口要越大。"
            },
            new TaskParamDesc
            {
                // 自动阈值后的固定偏置：负值让更多像素判白（前景变多），正值相反。
                ParamName = "阈值偏置",
                Min = -80,
                Max = 80,
                DefaultValue = 0,
                DisplayFormat = "bias:{0}",
                Group = "阈值"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "阈值"
            },
            new TaskParamDesc
            {
                // 局部阈值前先做一次盒式平滑，抑制传感器噪声造成的椒盐二值化。
                ParamName = "去噪核",
                Min = 0,
                Max = 15,
                DefaultValue = 3,
                DisplayFormat = "denoise:{0}",
                ForceOdd = true,
                Group = "阈值"
            },
            ..RoiRegion.ParamDescs(),
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "自动二值化: 输入为空";
                return srcMat?.Clone();
            }

            int mode = paramValues[0];
            int winRadius = paramValues[1];
            int bias = paramValues[2];
            int polarity = paramValues[3];
            int denoise = paramValues[4];
            string modeName = mode switch { 1 => "自适应", 2 => "谷底", _ => "全局Otsu" };

            // 算子级 ROI：只在框选区域做二值化，区域外保持原图不变（阈值按框内像素统计）
            if (RoiRegion.TryGet(srcMat, paramValues, out Rect roi))
            {
                Mat dstRoi = srcMat.Clone();
                using Mat sub = new Mat(srcMat, roi);                // ROI 子视图
                using Mat gray = VisionHelper.ToGray(sub);
                using Mat work = denoise >= 3 ? Blur(gray, denoise) : gray.Clone();
                using Mat bin = new Mat();
                double used = mode switch
                {
                    1 => Adaptive(work, bin, winRadius, bias, polarity),
                    2 => Valley(work, bin, bias, polarity),
                    _ => Otsu(work, bin, bias, polarity),
                };
                RoiRegion.PasteBinarized(dstRoi, bin, roi);
                int total = roi.Width * roi.Height;
                int white = Cv2.CountNonZero(bin);
                LastSummary = string.Format("自动二值化(框选 {4}x{5}): {0} 阈值 {1:F0}  白 {2:F1}% 黑 {3:F1}%",
                    modeName, used, 100.0 * white / total, 100.0 * (total - white) / total, roi.Width, roi.Height);
                return dstRoi;
            }

            using Mat grayAll = VisionHelper.ToGray(srcMat);
            using Mat workAll = denoise >= 3 ? Blur(grayAll, denoise) : grayAll.Clone();

            Mat dst = new();
            double usedAll;
            switch (mode)
            {
                case 1:
                    usedAll = Adaptive(workAll, dst, winRadius, bias, polarity);
                    break;
                case 2:
                    usedAll = Valley(workAll, dst, bias, polarity);
                    break;
                default:
                    usedAll = Otsu(workAll, dst, bias, polarity);
                    break;
            }

            int totalAll = dst.Rows * dst.Cols;
            int whiteAll = Cv2.CountNonZero(dst);
            LastSummary = string.Format("自动二值化: {0} 阈值 {1:F0}  白 {2:F1}% 黑 {3:F1}%",
                modeName, usedAll, 100.0 * whiteAll / totalAll, 100.0 * (totalAll - whiteAll) / totalAll);
            return dst;
        }

        private static Mat Blur(Mat gray, int k)
        {
            int kk = k % 2 == 0 ? k + 1 : k;
            Mat m = new();
            Cv2.Blur(gray, m, new Size(kk, kk));
            return m;
        }

        /// <summary>全局 Otsu + 偏置。极性 1 表示暗前景（输出前景为白）。</summary>
        private static double Otsu(Mat gray, Mat dst, int bias, int polarity)
        {
            // Binary 把"亮于阈值"的像素置白。极性 1 = 暗目标是前景，需要反相让暗->白；
            // 极性 0 = 亮目标是前景，保持 Binary 不动。(早期写成 polarity==0 才反相，结果
            // 默认极性下输出的是背景，暗区占比 21.6% 的图被判成 78.5%。)
            ThresholdTypes tt = polarity == 1
                ? ThresholdTypes.BinaryInv
                : ThresholdTypes.Binary;

            double thr = Cv2.Threshold(gray, dst, 0, 255, tt | ThresholdTypes.Otsu);
            double applied = Clamp(thr + bias);
            if (bias != 0)
                Cv2.Threshold(gray, dst, applied, 255, tt);
            return applied;
        }

        /// <summary>
        /// 自适应：局部高斯均值阈值，抗光照渐变/阴影。
        ///
        /// C 不要求用户填：取 C = 全局均值 - Otsu 阈值，使"局部均值 - C"在全图尺度上恰好
        /// 回落到 Otsu 门限（手填 C 最常见的问题就是不知道填多少）。
        ///
        /// 重要限制（实测，不是参数没调好）：自适应阈值按定义只比较"每个像素 vs 它的邻域"，
        /// 因此**无法分割比窗口更大的实心区域**——窗口整个落在暗区内部时局部均值≈暗值，
        /// 门限随之塌陷。实测一张 300x260 的实心暗块（占图 21.6%）：block=51 只判出 0.9%，
        /// 一路放大到 block=401 也才 12.8%，永远追不上真值。这类"大块面"场景请用
        /// 全局 Otsu 或"光照校正 + Otsu"；自适应留给纹理/字符/细结构这类小于窗口的目标。
        /// </summary>
        private static double Adaptive(Mat gray, Mat dst, int winRadius, int bias, int polarity)
        {
            Scalar mean = Cv2.Mean(gray);
            using Mat otsuTmp = new();
            double otsuThr = Cv2.Threshold(gray, otsuTmp, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

            // "局部均值 - C ≈ Otsu"，即 C ≈ 全局均值 - Otsu。极性只决定 Binary/BinaryInv。
            double c = mean.Val0 - otsuThr + bias;

            int block = winRadius * 2 + 1;
            if (block < 3) block = 3;
            if (block % 2 == 0) block++;

            ThresholdTypes tt = polarity == 1
                ? ThresholdTypes.BinaryInv
                : ThresholdTypes.Binary;

            Cv2.AdaptiveThreshold(gray, dst, 255, AdaptiveThresholdTypes.GaussianC, tt, block, c);
            return otsuThr + bias;
        }

        /// <summary>
        /// 谷底：在 Otsu 阈值附近 ±24 内找直方图最小值作为门限。双峰明显时比 Otsu 更贴谷底，
        /// 单峰/低对比时找不到有意义谷底就回退 Otsu。
        /// </summary>
        private static double Valley(Mat gray, Mat dst, int bias, int polarity)
        {
            using Mat otsuTmp = new();
            double otsuThr = Cv2.Threshold(gray, otsuTmp, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

            int[] hist = new int[256];
            unsafe
            {
                byte* p = (byte*)gray.Data;
                long n = (long)gray.Rows * gray.Step();
                for (long i = 0; i < n; i++) hist[p[i]]++;
            }

            int lo = (int)System.Math.Max(1, otsuThr - 24);
            int hi = (int)System.Math.Min(254, otsuThr + 24);
            int best = (int)otsuThr, bestVal = int.MaxValue;
            for (int t = lo; t <= hi; t++)
            {
                // 3 点平滑，避免单点噪声当选
                int v = hist[t - 1] + hist[t] + hist[t + 1];
                if (v < bestVal) { bestVal = v; best = t; }
            }

            // 谷底必须真的比 Otsu 处更低才算"有谷"；否则说明是单峰/低对比图，
            // 此时任何"谷值"都只是噪声起伏，回退 Otsu 更稳。
            int otsuVal = hist[(int)otsuThr - 1] + hist[(int)otsuThr] + hist[(int)otsuThr + 1];
            if (bestVal >= otsuVal)
                best = (int)otsuThr;

            double applied = Clamp(best + bias);
            ThresholdTypes tt = polarity == 1
                ? ThresholdTypes.BinaryInv
                : ThresholdTypes.Binary;
            Cv2.Threshold(gray, dst, applied, 255, tt);
            return applied;
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
