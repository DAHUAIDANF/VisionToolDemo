using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 前景抠图算子（GrabCut）：支持两段式框选——
    /// 先在图上拖框框住前景（InitRect），可选再拖一个排除框（ExcludeRect），
    /// 迭代分割前景/背景，输出前景掩膜或抠图（背景置黑）。
    /// 参数：迭代次数、输出模式。
    /// </summary>
    public class GrabCutTask : IVisionTask, IResultReporter
    {
        public string TaskName => "前景抠图";

        /// <summary>前景初始矩形（界面拖拽设置；未设置时默认取整图缩进 10%）</summary>
        public Rect InitRect { get; set; } = new();

        /// <summary>排除矩形（可选：框内像素强制为背景）</summary>
        public Rect ExcludeRect { get; set; } = new();

        public string LastSummary { get; private set; } = "";

        public int FgRatio { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "迭代次数",
                Min = 1, Max = 10, DefaultValue = 5,
                DisplayFormat = "迭代:{0}",
                Tip = "GrabCut 迭代次数（越多越精细越慢）"
            },
            new TaskParamDesc
            {
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "模式:{0}",
                Tip = "0=前景掩膜（白=前景）；1=抠图（背景置黑）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int iter = Math.Max(1, Math.Min(10, paramValues[0]));
            int mode = paramValues[1];

            Mat bgr = srcMat.Channels() == 1
                ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                : srcMat.Clone();

            // 初始矩形：优先用界面画的前景框，否则默认整图缩进 10%
            Rect rect = InitRect.Width > 2 && InitRect.Height > 2
                ? ClampRect(InitRect, bgr.Cols, bgr.Rows)
                : new Rect(bgr.Cols / 10, bgr.Rows / 10, bgr.Cols * 4 / 5, bgr.Rows * 4 / 5);

            using (Mat mask = new Mat(bgr.Rows, bgr.Cols, MatType.CV_8UC1, Scalar.All(0)))
            using (Mat bgd = new Mat(1, 64, MatType.CV_64FC1), fgd = new Mat(1, 64, MatType.CV_64FC1))
            {
                // 第一次：矩形初始化分割
                Cv2.GrabCut(bgr, mask, rect, bgd, fgd, iter, GrabCutModes.InitWithRect);

                // 排除框：框内像素强制为确定背景，再做一次 Eval 迭代
                if (ExcludeRect.Width > 2 && ExcludeRect.Height > 2)
                {
                    Rect ex = ClampRect(ExcludeRect, bgr.Cols, bgr.Rows);
                    using (Mat exMask = new Mat(ex.Height, ex.Width, MatType.CV_8UC1, Scalar.All(0)))
                    {
                        exMask.CopyTo(mask[ex]);
                    }
                    Cv2.GrabCut(bgr, mask, rect, bgd, fgd, iter, GrabCutModes.Eval);
                }

                // 掩膜：可能/确定前景 → 白
                Mat fgMask = new Mat(bgr.Rows, bgr.Cols, MatType.CV_8UC1, Scalar.All(0));
                for (int y = 0; y < bgr.Rows; y++)
                {
                    for (int x = 0; x < bgr.Cols; x++)
                    {
                        byte v = mask.At<byte>(y, x);
                        if (v == 1 || v == 3)
                            fgMask.Set<byte>(y, x, 255);
                    }
                }

                FgRatio = Cv2.CountNonZero(fgMask) * 100 / Math.Max(1, bgr.Rows * bgr.Cols);

                if (mode == 0)
                {
                    LastSummary = $"前景抠图: 前景占比 {FgRatio}%";
                    return fgMask;
                }

                // 抠图：背景置黑
                bgr.SetTo(new Scalar(0, 0, 0), ~fgMask);
                LastSummary = $"前景抠图: 前景占比 {FgRatio}%（背景置黑）";
                return bgr;
            }
        }

        /// <summary>把矩形裁剪到图像范围内</summary>
        private static Rect ClampRect(Rect r, int w, int h)
        {
            int x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
            int x2 = Math.Min(w, r.X + r.Width), y2 = Math.Min(h, r.Y + r.Height);
            return new Rect(x, y, Math.Max(1, x2 - x), Math.Max(1, y2 - y));
        }
    }
}
