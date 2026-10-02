using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 形态学重建 / 孔洞填充：把"按核大小做形态学"升级为"按连通性重建"。
    ///
    /// 与形态学操作的区别（这才是它存在的理由）：开运算只能按核大小抹掉小结构，
    /// 孔洞/缝隙只要比核大就留下来了；形态学重建不做尺寸判断，而是从标记点出发
    /// 沿着同一连通域长回来——不管这个域有多大、形状多复杂。
    ///
    /// 四种模式：
    ///   · 孔洞填充   —— 填掉闭合区域内部的洞（DPM 点、打标字符、铸件气孔最常用）
    ///   · 开重建     —— 抹掉所有"细"结构，保留任何宽度的粗结构（比开运算干净，
    ///                  且不会像开运算那样把细长结构的尖端磨圆）
    ///   · 闭重建     —— 填掉所有"细"缝隙，保留原始边界不被膨胀改变
    ///   · 边界清除   —— 只删掉与图像边框相连的连通域（去图像边缘的杂物/黑边）
    /// </summary>
    public class MorphReconstructTask : IVisionTask, IResultReporter
    {
        public string TaskName => "形态学重建";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "模式 0填洞1开重建2闭重建3清边界",
                Min = 0,
                Max = 3,
                DefaultValue = 0,
                DisplayFormat = "mode:{0}",
                Group = "形态学"
            },
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "thr:{0}",
                Group = "二值化"
            },
            new TaskParamDesc
            {
                ParamName = "自动阈值 0手动1Otsu",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "otsu:{0}",
                Group = "二值化"
            },
            new TaskParamDesc
            {
                // 重建前先做一次腐蚀：腐蚀多少决定"多细的结构会被抹掉"。
                // 仅开/闭重建模式生效。
                ParamName = "核大小",
                Min = 1,
                Max = 21,
                DefaultValue = 3,
                DisplayFormat = "ksize:{0}",
                ForceOdd = true,
                Group = "形态学"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "形态学"
            },
            new TaskParamDesc
            {
                ParamName = "迭代上限",
                Min = 1,
                Max = 500,
                DefaultValue = 100,
                DisplayFormat = "iter≤{0}",
                Group = "形态学"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "形态学重建: 输入为空";
                return srcMat?.Clone();
            }

            int mode = paramValues[0];
            int thr = paramValues[1];
            bool autoThr = paramValues[2] == 1;
            int ksize = paramValues[3];
            int polarity = paramValues[4];
            int maxIter = paramValues[5];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            double used;
            // 极性沿用 BlobTask 约定：0 = 亮目标为前景(Binary)，1 = 暗目标为前景(BinaryInv)。
            // 二值化后一律是"选中的前景 = 白"，各模式函数也按此约定进出，无非二次反相。
            ThresholdTypes tt = polarity == 1
                ? ThresholdTypes.BinaryInv
                : ThresholdTypes.Binary;
            if (autoThr)
            {
                used = Cv2.Threshold(gray, bin, 0, 255, tt | ThresholdTypes.Otsu);
            }
            else
            {
                used = thr;
                Cv2.Threshold(gray, bin, thr, 255, tt);
            }

            Mat dst = mode switch
            {
                1 => OpenReconstruct(bin, ksize, maxIter),
                2 => CloseReconstruct(bin, ksize, maxIter),
                3 => RemoveBorder(bin),
                _ => FillHoles(bin),
            };

            int total = dst.Rows * dst.Cols;
            int white = Cv2.CountNonZero(dst);
            string modeName = mode switch { 1 => "开重建", 2 => "闭重建", 3 => "清边界", _ => "填洞" };
            LastSummary = string.Format("形态学重建: {0} 阈值 {1:F0} 前景 {2:F1}%",
                modeName, used, 100.0 * white / total);
            return dst;
        }

        /// <summary>
        /// 孔洞填充（前景=白）：从图像边框处的"背景"泛洪，泛洪到的是与外界连通的背景；
        /// 没被泛洪到的背景就是被前景包住的孔洞。结果 = 原前景 ∪ 孔洞。
        /// 注意泛洪必须在背景上进行：先取反相（背景=白），从边框四边泛洪，
        /// 再把"能与外界连通的背景"抠掉，剩下的白就是孔洞。
        /// </summary>
        private static Mat FillHoles(Mat bin)
        {
            // 1px 黑边保证图像最外圈一定是背景，避免前景贴边时泛洪无处起步
            using Mat pad = new();
            Cv2.CopyMakeBorder(bin, pad, 1, 1, 1, 1, BorderTypes.Constant, Scalar.Black);

            using Mat inv = new();
            Cv2.BitwiseNot(pad, inv);               // 背景=白，前景=黑

            // 从边框泛洪背景（四角至少有一个是背景，安全）
            using Mat flood = inv.Clone();
            Cv2.FloodFill(flood, new Point(0, 0), Scalar.All(128));

            // 泛洪后：128 = 与外界连通的背景，255 = 被前景围住的孔洞，0 = 前景
            using Mat holes = new();
            Cv2.Compare(flood, 255, holes, CmpTypes.EQ);

            Mat result = new();
            Cv2.BitwiseOr(pad, holes, result);      // 原前景 ∪ 孔洞

            Mat cropped = new(result, new Rect(1, 1, bin.Cols, bin.Rows));
            return cropped.Clone();
        }

        /// <summary>
        /// 开重建：腐蚀得到标记，测地膨胀到收敛。结果 = 保留所有"包含至少一个
        /// 存活点"的连通域，因此细结构被整体删除，粗结构完整保留（不像开运算磨圆尖角）。
        /// </summary>
        private static Mat OpenReconstruct(Mat bin, int ksize, int maxIter)
        {
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ksize, ksize));
            using Mat marker = new();
            Cv2.Erode(bin, marker, kernel);
            if (Cv2.CountNonZero(marker) == 0)
                return new Mat(bin.Rows, bin.Cols, MatType.CV_8UC1, Scalar.Black);

            return Reconstruct(marker, bin, maxIter);
        }

        /// <summary>
        /// 闭重建：把"细窄的背景缝隙"填掉，但不改变目标外轮廓（不像闭运算那样整体长大）。
        ///
        /// 做法是"补集上的开重建"：对反相图（背景=白）先腐蚀掉细窄的背景结构，
        /// 再以腐蚀结果为标记、反相图为掩模做测地重建，最后反相回来。
        ///
        /// 注意：腐蚀必须用**足够大的核**才能吃掉缝隙。实测一条 4px 高的缝隙在反相图里
        /// 与大片背景相连，3x3/5x5 只会在它边缘削一层而不会断开；这里按"缝隙宽度"
        /// 语义取核，用 ksize 直接作为腐蚀核尺寸，并在引导注释里说明该参数决定"多窄的缝会被补上"。
        /// </summary>
        private static Mat CloseReconstruct(Mat bin, int ksize, int maxIter)
        {
            using Mat inv = new();
            Cv2.BitwiseNot(bin, inv);

            using Mat opened = OpenReconstruct(inv, ksize, maxIter);

            Mat result = new();
            Cv2.BitwiseNot(opened, result);
            return result;
        }

        /// <summary>
        /// 形态学重建核心：marker 在 mask 内反复测地膨胀直到不再变化。
        /// 按位与实现"测地"约束（膨胀结果不得越出 mask），用 compare 判断收敛。
        /// </summary>
        private static Mat Reconstruct(Mat marker, Mat mask, int maxIter)
        {
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Mat cur = marker.Clone();
            Mat prev = new();

            for (int i = 0; i < maxIter; i++)
            {
                prev.Dispose();
                prev = cur.Clone();

                using Mat dil = new();
                Cv2.Dilate(cur, dil, kernel);
                cur.Dispose();
                cur = new Mat();
                Cv2.BitwiseAnd(dil, mask, cur);

                using Mat diff = new();
                Cv2.Absdiff(cur, prev, diff);
                if (Cv2.CountNonZero(diff) == 0)
                    break;
            }
            prev.Dispose();
            return cur;
        }

        /// <summary>
        /// 边界清除（前景=白）：删掉所有与图像边框相连的前景连通域，只保留完全在内部的。
        /// 做法：给 1px 黑边 → 反相使背景=白 → 从边框泛洪"外部背景" → 被泛洪标记过的
        /// 前景连通域即与外界相连者，从前景里减掉。
        /// </summary>
        private static Mat RemoveBorder(Mat bin)
        {
            using Mat pad = new();
            Cv2.CopyMakeBorder(bin, pad, 1, 1, 1, 1, BorderTypes.Constant, Scalar.Black);

            // 判据：前景连通域是否落在**原图**最外一圈（第一/最后行、列）。
            // 两个坑（都实测踩过）：
            //   1) 用"外部背景膨胀后与前景求交"当种子——外部背景会从前景与边框之间的缝隙
            //      绕进来（实测 200x200 图 corner 块贴边时，外部背景仍占 37604/40804），
            //      膨胀后把内部块也带上，整幅图被当成边界连通域删空；
            //   2) 在 pad 之后取边——1px 黑边让最外圈恒为背景，种子永远是空，等于不删。
            // 所以必须在 pad 之前的原图上采边。
            using Mat edgeForeground = new Mat(bin.Rows, bin.Cols, MatType.CV_8UC1, Scalar.Black);
            int last = bin.Rows - 1, lastC = bin.Cols - 1;
            for (int x = 0; x <= lastC; x++)
            {
                edgeForeground.Set(0, x, bin.At<byte>(0, x));
                edgeForeground.Set(last, x, bin.At<byte>(last, x));
            }
            for (int y = 0; y <= last; y++)
            {
                edgeForeground.Set(y, 0, bin.At<byte>(y, 0));
                edgeForeground.Set(y, lastC, bin.At<byte>(y, lastC));
            }

            using Mat touchingOrig = new();
            Cv2.BitwiseAnd(edgeForeground, bin, touchingOrig);   // 位于原图最外一圈的前景像素 = 种子

            if (Cv2.CountNonZero(touchingOrig) == 0)
            {
                // 没有任何前景接触边框：全部保留
                return bin.Clone();
            }

            // 种子搬到 pad 坐标系（+1,+1）后做测地重建，整块捞出与边框相连的前景连通域
            using Mat touching = new Mat(pad.Rows, pad.Cols, MatType.CV_8UC1, Scalar.Black);
            for (int y = 0; y < bin.Rows; y++)
                for (int x = 0; x < bin.Cols; x++)
                    if (touchingOrig.At<byte>(y, x) != 0)
                        touching.Set(y + 1, x + 1, (byte)255);

            using Mat recon = Reconstruct(touching, pad, 500);
            using Mat keep = new();
            Cv2.BitwiseNot(recon, keep);
            using Mat kept = new();
            Cv2.BitwiseAnd(pad, keep, kept);

            Mat cropped = new(kept, new Rect(1, 1, bin.Cols, bin.Rows));
            return cropped.Clone();
        }
    }
}
