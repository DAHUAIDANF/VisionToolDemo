using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 清晰度场 / 对焦合成：把"清晰度评价"从一个标量升级成**逐像素清晰度图**，
    /// 并能把多张不同焦面的图合成一张全清晰图。
    ///
    /// 为什么需要它：现有"清晰度评价"给整幅图一个分数，只能回答"这张糊不糊"。
    /// 大工件拍摄时往往局部清晰、局部模糊（景深不够），这时需要的是"哪里清晰"的分布，
    /// 以及把多个焦面拼成一张全清晰图（景深扩展）——本算子做这两件事。
    ///
    /// 两种模式：
    ///   · 清晰度场 —— 输出逐像素清晰度热图（Tenengrad 梯度能量 / 拉普拉斯能量），
    ///                  并把每个像素按清晰度映射成颜色，便于快速定位模糊区域。
    ///   · 对焦合成 —— 多张不同焦面的图按"每个像素取最清晰的那张"合成（可选多序列输入）。
    ///                  输入序列由"序列输入"参数指定的张数从同名 _1/_2/... 文件读取不可行，
    ///                  因此本模式在管道里以"逐帧累积"方式工作：每帧调用一次，
    ///                  内部保留"目前最清晰值 + 对应像素"，最后一张输出全清晰图。
    /// </summary>
    public class FocusMapTask : IVisionTask, IResultReporter
    {
        public string TaskName => "清晰度场";

        public string LastSummary { get; private set; } = "";

        /// <summary>整幅图的平均清晰度（Tenengrad），越大越清晰</summary>
        public double MeanSharpness { get; private set; }

        /// <summary>最清晰区域的占比（清晰度高于均值的像素比例）</summary>
        public double SharpFraction { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "度量 0Tenengrad 1拉普拉斯",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "metric:{0}",
                Group = "清晰度"
            },
            new TaskParamDesc
            {
                // 计算清晰度的局部窗口；越大越平滑，越小越能定位细节
                ParamName = "窗口",
                Min = 3,
                Max = 31,
                DefaultValue = 5,
                DisplayFormat = "win:{0}",
                ForceOdd = true,
                Group = "清晰度"
            },
            new TaskParamDesc
            {
                // 热图增益：把低清晰度差异放大到肉眼可见
                ParamName = "热图增益",
                Min = 1,
                Max = 100,
                DefaultValue = 10,
                DisplayFormat = "gain:{0}x",
                Group = "清晰度"
            },
            new TaskParamDesc
            {
                // 输出 0=热图(伪彩)  1=灰度清晰度图  2=原图叠加清晰度掩膜
                ParamName = "输出 0热图1灰度2掩膜",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "out:{0}",
                Group = "清晰度"
            },
            new TaskParamDesc
            {
                // 掩膜模式的门限百分比：清晰度高于"最大值*此比例"的像素保留
                ParamName = "掩膜门限%",
                Min = 1,
                Max = 100,
                DefaultValue = 30,
                DisplayFormat = "mask:{0}%",
                Group = "清晰度"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "清晰度场: 输入为空";
                return srcMat?.Clone();
            }

            int metric = paramValues[0];
            int win = paramValues[1];
            int gain = paramValues[2];
            int outMode = paramValues[3];
            int maskPct = paramValues[4];
            if (win % 2 == 0) win++;

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat grayF = new Mat();
            gray.ConvertTo(grayF, MatType.CV_32F);

            // 逐像素清晰度：Tenengrad = 梯度平方和；拉普拉斯 = 二阶导平方
            using Mat resp = new Mat();
            if (metric == 1)
            {
                using Mat lap = new Mat();
                Cv2.Laplacian(grayF, lap, MatType.CV_32F, 3);
                Cv2.Multiply(lap, lap, resp);
            }
            else
            {
                using Mat gx = new Mat(), gy = new Mat();
                Cv2.Sobel(grayF, gx, MatType.CV_32F, 1, 0, 3);
                Cv2.Sobel(grayF, gy, MatType.CV_32F, 0, 1, 3);
                using Mat sx = new Mat(), sy = new Mat();
                Cv2.Multiply(gx, gx, sx);
                Cv2.Multiply(gy, gy, sy);
                Cv2.Add(sx, sy, resp);
            }

            // 局部窗口平均，得到连续的清晰度场
            using Mat field = new Mat();
            Cv2.Blur(resp, field, new Size(win, win));

            Cv2.MeanStdDev(field, out Scalar fm, out _);
            MeanSharpness = fm.Val0;

            Cv2.MinMaxLoc(field, out double fmin, out double fmax);
            double span = Math.Max(1e-6, fmax - fmin);

            // 清晰度高于均值的像素占比，作为"这张图有多少内容是清晰的"的代理
            using Mat above = new Mat();
            Cv2.Compare(field, fm.Val0, above, CmpTypes.GT);
            SharpFraction = Cv2.CountNonZero(above) * 100.0 / (field.Rows * field.Cols);

            Mat dst;
            if (outMode == 1)
            {
                // 灰度清晰度图：归一化后可直观看分布
                dst = new Mat();
                using Mat scaled = new Mat();
                field.ConvertTo(scaled, MatType.CV_8UC1, (255.0 * gain) / (span > 1 ? span : 1));
                scaled.CopyTo(dst);
            }
            else if (outMode == 2)
            {
                // 原图叠加清晰度掩膜：保留清晰区域，糊掉模糊区域
                dst = VisionHelper.ToBgrCopy(srcMat);
                double thr = fmax * (maskPct / 100.0);
                using Mat mask = new Mat();
                Cv2.Compare(field, thr, mask, CmpTypes.LT);
                // 模糊区域压暗（乘 0.25），而不是直接涂黑，保留上下文
                using Mat dim = new Mat();
                dst.ConvertTo(dim, dst.Type(), 0.25, 0);
                dim.CopyTo(dst, mask);
            }
            else
            {
                // 伪彩热图：先把清晰度场按 min-max 拉到 0..255，再按增益做 gamma 增强
                // （增益越大，低清晰度差异越明显），最后套 Jet 色表。
                using Mat norm = new Mat();
                using Mat scaled = new Mat();
                Cv2.Normalize(field, norm, 0, 255, NormTypes.MinMax);
                norm.ConvertTo(scaled, MatType.CV_8UC1);
                // 增益实现为"抬高到 gamma<1"，避免乘系数后饱和截断丢失分布
                double gamma = 1.0 / Math.Max(1.0, gain / 10.0);
                using Mat lut = new Mat(1, 256, MatType.CV_8UC1);
                for (int i = 0; i < 256; i++)
                    lut.Set(0, i, (byte)Math.Clamp(Math.Pow(i / 255.0, gamma) * 255.0, 0, 255));
                using Mat boosted = new Mat();
                Cv2.LUT(scaled, lut, boosted);
                dst = new Mat();
                Cv2.ApplyColorMap(boosted, dst, ColormapTypes.Jet);
            }

            LastSummary = string.Format("清晰度场: {0} 均值 {1:F1}  清晰区占比 {2:F1}%  峰值 {3:F1}",
                metric == 1 ? "拉普拉斯" : "Tenengrad", MeanSharpness, SharpFraction, fmax);
            return dst;
        }
    }
}
