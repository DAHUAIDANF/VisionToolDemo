using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 脏污 / 坏点检测：在**均匀场**（白场或暗场）图上找两类缺陷。
    ///
    ///   · **脏污 / 亮点**（白场图上）—— 用 TOPHAT 形态学提取比周围亮的小目标，
    ///     阈值 + 连通域统计，得到数量、面积、位置。TOPHAT 自动扣除背景亮度梯度，
    ///     不像固定阈值那样被照明不均打败。
    ///   · **坏点 / 暗点**（暗场图上）—— 用 BLACKHAT 找比周围暗的小目标。
    ///
    /// 另支持**多帧模式**：累积多帧后按逐像素均值和标准差判定，
    /// 只有每次都出现的异常才判为真实坏点 —— 这是区分**真实坏点**与**随机噪声**的关键
    /// （单帧上两者无法区分）。
    /// </summary>
    public class DirtDeadPixelTask : IVisionTask, IResultReporter
    {
        public string TaskName => "脏污坏点检测";

        public string LastSummary { get; private set; } = "";

        public int DefectCount { get; private set; }
        /// <summary>缺陷总面积占比（%）</summary>
        public double DefectAreaPercent { get; private set; } = double.NaN;
        /// <summary>多帧模式下的累积帧数</summary>
        public int AccumulatedFrames { get; private set; }

        private Mat _accSum;
        private Mat _accSq;
        private int _frames;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "检测 0亮点1暗点2两者",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "kind:{0}",
                Group = "检测",
                Tip = "0 = 找亮点/脏污（用白场图）；1 = 找暗点/坏点（用暗场图）；2 = 两者都找。"
            },
            new TaskParamDesc
            {
                ParamName = "形态学核",
                Min = 3, Max = 51, DefaultValue = 9,
                DisplayFormat = "k:{0}",
                ForceOdd = true,
                Group = "检测",
                Tip = "TOPHAT/BLACKHAT 的核大小，**必须大于缺陷尺寸**。缺陷比核大就提取不出来。"
            },
            new TaskParamDesc
            {
                ParamName = "对比度门限",
                Min = 1, Max = 255, DefaultValue = 25,
                DisplayFormat = "diff≥{0}",
                Group = "检测",
                Tip = "与局部背景的灰度差超过该值才算缺陷。调高只抓明显缺陷。"
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 1, Max = 100000, DefaultValue = 2,
                DisplayFormat = "area≥{0}",
                Group = "判定",
                Tip = "小于该面积不计（坏点常是 1~4 像素，所以默认很小）。"
            },
            new TaskParamDesc
            {
                ParamName = "最大面积",
                Min = 1, Max = 1000000, DefaultValue = 5000,
                DisplayFormat = "area≤{0}",
                Group = "判定",
                Tip = "大于该面积不计为点状缺陷（大片异常通常是照明/遮挡问题，应单独排查）。"
            },
            new TaskParamDesc
            {
                ParamName = "多帧判定σ倍数",
                Min = 0, Max = 10, DefaultValue = 0,
                DisplayFormat = "kσ:{0}",
                Group = "多帧",
                Tip = "大于 0 开启多帧模式：累积多帧后，只有偏离均值超过该倍数标准差的像素才算缺陷。" +
                      "0 = 单帧模式。填 3~5 可有效排除随机噪声。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0标注1掩膜2统计图",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 原图标注缺陷框；1 = 缺陷二值掩膜；2 = 多帧标准差图（看噪声分布）。"
            },
            new TaskParamDesc
            {
                ParamName = "重置累积 0否1是",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "reset:{0}",
                Group = "多帧",
                Tip = "置 1 后清空已累积的帧（换样品/换场景时用），执行一次后自动复位为 0。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            DefectCount = 0;
            DefectAreaPercent = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "脏污坏点: 输入为空";
                return srcMat?.Clone();
            }

            int kind = Math.Clamp(paramValues[0], 0, 2);
            int ksize = paramValues[1] % 2 == 0 ? paramValues[1] + 1 : paramValues[1];
            int thresh = paramValues[2];
            int minArea = paramValues[3];
            int maxArea = Math.Max(minArea, paramValues[4]);
            double kSigma = paramValues[5];
            int outMode = paramValues[6];
            bool reset = paramValues[7] == 1;
            if (ksize < 3) ksize = 3;

            using Mat gray = VisionHelper.ToGray(srcMat);

            // —— 多帧累积 ——
            if (reset) { _accSum?.Dispose(); _accSum = null; _accSq?.Dispose(); _accSq = null; _frames = 0; }

            using Mat sigmaMap = new Mat(gray.Size(), MatType.CV_32FC1, Scalar.All(0));
            if (kSigma > 0)
            {
                using Mat f = new Mat();
                gray.ConvertTo(f, MatType.CV_32FC1);
                if (_accSum == null)
                {
                    _accSum = new Mat(gray.Size(), MatType.CV_32FC1, Scalar.All(0));
                    _accSq = new Mat(gray.Size(), MatType.CV_32FC1, Scalar.All(0));
                    _frames = 0;
                }
                Cv2.Add(_accSum, f, _accSum);
                using Mat sq = new Mat();
                Cv2.Multiply(f, f, sq);
                Cv2.Add(_accSq, sq, _accSq);
                _frames++;
                AccumulatedFrames = _frames;

                using Mat mean = new Mat();
                using Mat meanSq = new Mat();
                Cv2.Multiply(_accSum, 1.0 / _frames, mean);
                Cv2.Multiply(_accSq, 1.0 / _frames, meanSq);
                using Mat mean2 = new Mat();
                Cv2.Multiply(mean, mean, mean2);
                Cv2.Subtract(meanSq, mean2, sigmaMap);
                Cv2.Max(sigmaMap, Scalar.All(0), sigmaMap);
                Cv2.Sqrt(sigmaMap, sigmaMap);

                if (_frames >= 2)
                {
                    using Mat dev = new Mat();
                    Cv2.Absdiff(f, mean, dev);
                    using Mat kSig = new Mat();
                    Cv2.Multiply(sigmaMap, kSigma, kSig);
                    using Mat ex = new Mat();
                    Cv2.Compare(dev, kSig, ex, CmpTypes.GT);
                    gray.SetTo(Scalar.All(0), ex);
                }
            }
            else if (_accSum != null)
            {
                _accSum.Dispose(); _accSum = null;
                _accSq?.Dispose(); _accSq = null;
                _frames = 0;
            }

            // —— TOPHAT / BLACKHAT 提取点状缺陷 ——
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ksize, ksize));
            using Mat defects = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.Black);

            if (kind == 0 || kind == 2)
            {
                using Mat top = new Mat();
                Cv2.MorphologyEx(gray, top, MorphTypes.TopHat, kernel);
                using Mat t = new Mat();
                Cv2.Threshold(top, t, thresh, 255, ThresholdTypes.Binary);
                Cv2.BitwiseOr(defects, t, defects);
            }
            if (kind == 1 || kind == 2)
            {
                using Mat black = new Mat();
                Cv2.MorphologyEx(gray, black, MorphTypes.BlackHat, kernel);
                using Mat t = new Mat();
                Cv2.Threshold(black, t, thresh, 255, ThresholdTypes.Binary);
                Cv2.BitwiseOr(defects, t, defects);
            }

            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(defects, labels, stats, centroids, PixelConnectivity.Connectivity8);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            long total = 0;
            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea || area > maxArea) continue;
                DefectCount++;
                total += area;
                if (outMode == 0)
                {
                    int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                    int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                    int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                    int bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                    var r = new Rect(Math.Max(0, x - 4), Math.Max(0, y - 4), bw + 8, bh + 8);
                    Cv2.Rectangle(dst, r, Scalar.Red, 1);
                }
            }
            DefectAreaPercent = 100.0 * total / (gray.Cols * (double)gray.Rows);

            if (outMode == 1)
            {
                dst.Dispose();
                dst = defects.Clone();
            }
            else if (outMode == 2)
            {
                using Mat s8 = new Mat();
                Cv2.Normalize(sigmaMap, s8, 0, 255, NormTypes.MinMax);
                using Mat s8b = new Mat();
                s8.ConvertTo(s8b, MatType.CV_8UC1);
                dst.Dispose();
                dst = new Mat();
                Cv2.ApplyColorMap(s8b, dst, ColormapTypes.Turbo);
                MatDraw.DrawText(dst, "多帧标准差", 8, 22, Scalar.White, 14);
            }
            else
            {
                string info = string.Format("缺陷 {0} 处 面积 {1:F4}%{2}",
                    DefectCount, DefectAreaPercent,
                    AccumulatedFrames > 0 ? string.Format("  {0}帧 {1:F1}σ", AccumulatedFrames, kSigma) : "");
                MatDraw.DrawText(dst, info, 8, 22, DefectCount > 0 ? Scalar.Red : Scalar.Lime, 14);
            }

            LastSummary = string.Format("脏污坏点: {0} 检出 {1} 处，面积占比 {2:F4}%{3}",
                kind == 0 ? "亮点" : kind == 1 ? "暗点" : "亮+暗",
                DefectCount, DefectAreaPercent,
                AccumulatedFrames > 0 ? string.Format("（多帧 {0} 帧，{1:F1}σ）", AccumulatedFrames, kSigma) : "");
            return dst;
        }
    }
}
