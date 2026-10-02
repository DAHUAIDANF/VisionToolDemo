using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 前后景分离：估计并**提取背景**，再用除法/减法把背景从图像里去掉，只留前景。
    ///
    /// 与"光照校正/LSC"的区别：那些的目标是**拉平亮度**让阈值好用，输出仍是完整图像；
    /// 本算子的目标是**直接给出去掉背景后的前景**（或前景掩膜），用于料盘/传送带上的
    /// 工件提取、表面缺陷凸显（去掉壳体本身的纹理背景，只留异常）。
    ///
    /// 三种背景估计方式：
    ///   · 大核模糊   —— 用远大于目标的模糊核把目标"抹掉"，剩下的就是背景照明场；
    ///   · 中值背景   —— 大核中值滤波，对目标遮挡不敏感（目标占比大时比模糊更干净）；
    ///   · 参考图     —— 用一张空场图（无工件的料盘图）作背景，逐像素相减/相除。最准。
    /// </summary>
    public class ForegroundSplitTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "前后景分离";

        /// <summary>UI 层注入的参考背景图（可选）</summary>
        public Mat ReferenceMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次分割出的前景占比</summary>
        public double ForegroundPercent { get; private set; }

        public string SaveState() => VisionHelper.SaveTemplateState(ReferenceMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null) ReferenceMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "背景估计 0模糊1中值2参考图",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "mode:{0}",
                Group = "背景",
                Tip = "怎么得到背景。模糊/中值无需额外图，自动从当前图估计；" +
                      "参考图最准（用空料盘的图），需要先点【导入模板】载入参考图。"
            },
            new TaskParamDesc
            {
                ParamName = "背景核大小",
                Min = 5, Max = 301, DefaultValue = 51,
                DisplayFormat = "k:{0}",
                ForceOdd = true,
                Group = "背景",
                Tip = "估计背景用的核，**必须明显大于目标尺寸**，否则目标会被当成背景一起抹掉。"
            },
            new TaskParamDesc
            {
                ParamName = "分离方式 0除法1相减",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "op:{0}",
                Group = "分离",
                Tip = "0 除法：校正乘性光照不均（明暗渐变），最常用；1 相减：去加性背景（整体亮度差）。"
            },
            new TaskParamDesc
            {
                ParamName = "增强增益%",
                Min = 10, Max = 500, DefaultValue = 150,
                DisplayFormat = "gain:{0}%",
                Group = "分离",
                Tip = "把分离后的差异放大，便于看清/后续阈值。100% = 原样。"
            },
            new TaskParamDesc
            {
                ParamName = "前景阈值",
                Min = 0, Max = 255, DefaultValue = 30,
                DisplayFormat = "thr:{0}",
                Group = "前景",
                Tip = "分离结果与该值的比较门限；只有掩膜输出模式会用到。0 = 用 Otsu 自动。"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "前景",
                Tip = "0 = 比背景亮的是前景；1 = 比背景暗的是前景。"
            },
            new TaskParamDesc
            {
                ParamName = "最小前景面积",
                Min = 0, Max = 100000, DefaultValue = 50,
                DisplayFormat = "area≥{0}",
                Group = "前景",
                Tip = "掩膜输出时滤掉小于该面积的碎块。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0前景图1掩膜2背景图",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 去背景后的前景图；1 = 前景二值掩膜；2 = 估计出的背景图（用于核对核大小是否合适）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ForegroundPercent = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "前后景分离: 输入为空";
                return srcMat?.Clone();
            }

            int mode = paramValues[0];
            int ksize = paramValues[1];
            int op = paramValues[2];
            int gain = paramValues[3];
            int thr = paramValues[4];
            int polarity = paramValues[5];
            int minArea = paramValues[6];
            int outMode = paramValues[7];

            if (ksize % 2 == 0) ksize++;
            if (ksize < 3) ksize = 3;

            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);

            // —— 1. 估计背景 ——
            Mat background;
            string how;
            if (mode == 2 && ReferenceMat != null && !ReferenceMat.Empty())
            {
                background = new Mat();
                Mat refGray = VisionHelper.ToGray(ReferenceMat);
                if (refGray.Size() != bgr.Size())
                    Cv2.Resize(refGray, background, bgr.Size(), 0, 0, InterpolationFlags.Area);
                else
                    refGray.CopyTo(background);
                refGray.Dispose();
                how = "参考图";
            }
            else
            {
                using Mat gray = VisionHelper.ToGray(srcMat);
                background = new Mat();
                if (mode == 1)
                {
                    // 中值：对目标遮挡不敏感
                    Cv2.MedianBlur(gray, background, ksize);
                    how = "中值 k=" + ksize;
                }
                else
                {
                    // 大核盒式模糊（等价于均值背景）—— 比高斯快很多且够用
                    Cv2.Blur(gray, background, new Size(ksize, ksize));
                    how = "模糊 k=" + ksize;
                }
                if (mode == 2) how += " (无参考图，退回模糊)";
            }

            using Mat bg = background;

            // —— 2. 分离 ——
            using Mat fgGray = new Mat();
            if (op == 0)
            {
                // 除法：src / bg * 128，拉平乘性光照
                using Mat srcF = new Mat();
                using Mat bgF = new Mat();
                VisionHelper.ToGray(srcMat).ConvertTo(srcF, MatType.CV_32F);
                bg.ConvertTo(bgF, MatType.CV_32F);
                // 背景加一个小偏置再相除，避免背景接近 0 的像素产生除零/爆值
                using Mat bgSafe = new Mat();
                Cv2.Add(bgF, new Scalar(1.0), bgSafe);
                using Mat ratio = new Mat();
                Cv2.Divide(srcF, bgSafe, ratio, 128.0);
                ratio.ConvertTo(fgGray, MatType.CV_8UC1);
            }
            else
            {
                using Mat gray = VisionHelper.ToGray(srcMat);
                using Mat diff = new Mat();
                Cv2.Absdiff(gray, bg, diff);
                diff.ConvertTo(fgGray, MatType.CV_8UC1);
            }

            // —— 3. 增强 ——
            if (gain != 100)
            {
                double a = gain / 100.0;
                fgGray.ConvertTo(fgGray, MatType.CV_8UC1, a, 0);
            }

            // —— 4. 掩膜 ——
            using Mat mask = new Mat();
            double usedThr;
            ThresholdTypes tt = polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            if (thr <= 0)
                usedThr = Cv2.Threshold(fgGray, mask, 0, 255, tt | ThresholdTypes.Otsu);
            else
            {
                usedThr = thr;
                Cv2.Threshold(fgGray, mask, thr, 255, tt);
            }

            if (minArea > 0)
                RemoveSmallComponents(mask, minArea);

            ForegroundPercent = 100.0 * Cv2.CountNonZero(mask) / (mask.Rows * mask.Cols);

            Mat dst;
            switch (outMode)
            {
                case 1:
                    dst = mask.Clone();
                    break;
                case 2:
                    dst = new Mat();
                    bg.ConvertTo(dst, MatType.CV_8UC1);
                    break;
                default:
                    dst = fgGray.Clone();
                    break;
            }

            LastSummary = string.Format("前后景分离: {0}  {1}  阈值{2:F0}  前景 {3:F1}%",
                how, op == 0 ? "除法" : "相减", usedThr, ForegroundPercent);
            return dst;
        }

        private static void RemoveSmallComponents(Mat bin, int minArea)
        {
            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);
            for (int i = 1; i < n; i++)
            {
                if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < minArea)
                {
                    using Mat comp = new Mat();
                    Cv2.Compare(labels, i, comp, CmpTypes.EQ);
                    bin.SetTo(Scalar.Black, comp);
                }
            }
        }
    }
}
