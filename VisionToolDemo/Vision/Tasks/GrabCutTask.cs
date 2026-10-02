using System;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// GrabCut 交互式分割：用矩形或"矩形+前景笔迹"作初始标记，迭代求前景/背景的最优划分。
    ///
    /// 与二值化/色差分割的区别：那些按像素值（阈值/色差）判前景，遇到前景与背景**颜色相近**
    /// 或**光照不均**就失效；GrabCut 用颜色统计（GMM）+ 图割，按"区域一致性"分割，
    /// 只要给对初始框，即使前景背景灰度接近也能分出来，且边界贴合目标。
    ///
    /// 用法：框选把目标圈住即用默认（框内为待定前景，框外为确定背景）；
    /// 若框内混入背景，可再用"排除框"标出确定背景区域，提高精度。
    /// </summary>
    public class GrabCutTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "GrabCut分割";

        /// <summary>UI 层注入的 ROI 矩形（图像坐标）：作为初始前景框</summary>
        public Rect InitRect { get; set; }

        /// <summary>UI 层注入的排除框（图像坐标）：标为确定背景，可空</summary>
        public Rect ExcludeRect { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次前景像素占比</summary>
        public double ForegroundPercent { get; private set; }

        public string SaveState()
        {
            return JsonConvert.SerializeObject(new[] { InitRect.X, InitRect.Y, InitRect.Width, InitRect.Height,
                                                       ExcludeRect.X, ExcludeRect.Y, ExcludeRect.Width, ExcludeRect.Height });
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p is { Length: 8 })
                {
                    InitRect = new Rect(p[0], p[1], p[2], p[3]);
                    ExcludeRect = new Rect(p[4], p[5], p[6], p[7]);
                }
            }
            catch { /* 损坏状态忽略 */ }
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "迭代次数",
                Min = 1, Max = 10, DefaultValue = 3,
                DisplayFormat = "iter:{0}",
                Group = "分割",
                Tip = "GrabCut 迭代轮数。1~3 通常够用，调大更精细但更慢；效果不好时优先改初始框而不是加迭代。"
            },
            new TaskParamDesc
            {
                ParamName = "前景连通域最小面积",
                Min = 0, Max = 100000, DefaultValue = 50,
                DisplayFormat = "area≥{0}",
                Group = "后处理",
                Tip = "分割后去掉小于该面积的碎块（噪点被误判为前景），单位像素。设 0 表示不过滤。"
            },
            new TaskParamDesc
            {
                ParamName = "羽化/平滑核",
                Min = 0, Max = 15, DefaultValue = 3,
                DisplayFormat = "smooth:{0}",
                ForceOdd = true,
                Group = "后处理",
                Tip = "对分割结果做形态学开闭 + 平滑，去毛刺、填小孔。设 0 表示不做后处理。"
            },
            new TaskParamDesc
            {
                ParamName = "运行模式 0仅掩膜1叠加2抠图",
                Min = 0, Max = 2, DefaultValue = 1,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0=输出黑白掩膜；1=原图上叠加高亮前景（便于核对）；2=抠出前景、背景置黑。"
            },
            // —— 新增参数必须追加在**末尾** ——
            // 参数值是按**下标**存进流水线 JSON 的（见 PipelineStep.Params），
            // 中途插入会把后面所有参数各挪一位：已保存的流水线会静默错位，
            // 界面上表现为"换了版本之后结果全不对"。曾经把本参数插在
            // 运行模式之前，导致运行模式被读成工作分辨率上限（1px），GrabCut 直接失败。
            new TaskParamDesc
            {
                ParamName = "工作分辨率上限",
                Min = 0, Max = 4000, DefaultValue = 900,
                DisplayFormat = "≤{0}px",
                Group = "性能",
                Tip = "GrabCut 按像素迭代 GMM，耗时随像素数增长：3264x2448 实测 10.8 秒，" +
                "长边缩到 900px 只要 0.8 秒左右。本参数把长边限制到此值再分割，" +
                "结果按比例放回原尺寸（最近邻放大，保持类别标签）。" +
                "分割是区域级判断，适度降采样几乎不影响结果；" +
                "需要抠精确边缘时设为 0（不缩放，最慢最精细）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ForegroundPercent = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "GrabCut: 输入为空";
                return srcMat?.Clone();
            }

            int iterations = Math.Clamp(paramValues[0], 1, 10);
            int minArea = paramValues[1];
            int smooth = paramValues[2];
            int outMode = paramValues[3];
            int workMaxSide = paramValues.Length > 4 ? paramValues[4] : 900;

            using Mat bgrFull = VisionHelper.ToBgrCopy(srcMat);

            // —— 工作分辨率 ——
            // GrabCut 的时间复杂度随像素数线性、单像素代价又随迭代轮数线性，
            // 8MP 上跑 3 轮要 10 秒量级，界面会完全冻结。
            // 分割是**区域级**判断，降采样到长边 900px 后结果几乎一致，
            // 而耗时降到 ~1/10。算完再把掩膜放回原尺寸。
            double workScale = 1.0;
            if (workMaxSide > 0)
            {
                int longSide = Math.Max(bgrFull.Cols, bgrFull.Rows);
                if (longSide > workMaxSide)
                    workScale = (double)workMaxSide / longSide;
            }

            Mat bgr;
            bool disposeBgr = false;
            if (workScale < 1.0)
            {
                bgr = new Mat();
                Cv2.Resize(bgrFull, bgr, new Size(
                    Math.Max(1, (int)Math.Round(bgrFull.Cols * workScale)),
                    Math.Max(1, (int)Math.Round(bgrFull.Rows * workScale))),
                    0, 0, InterpolationFlags.Area);
                disposeBgr = true;
            }
            else
            {
                bgr = bgrFull;
            }

            // 工作分辨率下的框 = 原坐标 × workScale
            Rect ScaleRect(Rect rr) => new Rect(
                (int)Math.Round(rr.X * workScale), (int)Math.Round(rr.Y * workScale),
                (int)Math.Round(rr.Width * workScale), (int)Math.Round(rr.Height * workScale));

            // 未画框时用整图内缩 5% 作默认框（避免框贴边导致 GrabCut 报错）
            Rect rect = ScaleRect(InitRect);
            if (rect.Width < 4 || rect.Height < 4
                || rect.Right > bgr.Cols || rect.Bottom > bgr.Rows || rect.X < 0 || rect.Y < 0)
            {
                rect = new Rect(bgr.Cols / 20, bgr.Rows / 20,
                                Math.Max(4, bgr.Cols * 9 / 10), Math.Max(4, bgr.Rows * 9 / 10));
            }

            using Mat maskSmall = new Mat();
            using Mat bgdModel = new Mat();
            using Mat fgdModel = new Mat();

            // GC_INIT_WITH_RECT：框外为重背景，框内为待定
            Cv2.GrabCut(bgr, maskSmall, rect, bgdModel, fgdModel, iterations, GrabCutModes.InitWithRect);

            // 若指定了排除框，把该区域内标为确定背景后继续迭代。
            // 注意：InitWithMask 要求 mask 里同时存在"确定背景(0)"和"确定前景(1)"样本，
            // 且不同 OpenCV 构建下 InitWithRect 产生的标签语义并不一致（有的矩形内=PR_FGD(3)、
            // 有的矩形内=PR_BGD(2)），直接基于标签值提升会在一部分环境下仍抛
            // "!bgdSamples.empty() && !fgdSamples.empty()"。
            // 所以这里不读标签，直接用几何构造确定前景/背景：前景框内=确定前景(1)、其余=确定背景(0)。
            Rect exRect = ScaleRect(ExcludeRect);
            if (exRect.Width > 3 && exRect.Height > 3
                && exRect.Right <= bgr.Cols && exRect.Bottom <= bgr.Rows
                && exRect.X >= 0 && exRect.Y >= 0)
            {
                maskSmall.SetTo((byte)GrabCutClasses.BGD);                          // 全图先标确定背景(0)
                Cv2.Rectangle(maskSmall, new Point(rect.X, rect.Y), new Point(rect.Right - 1, rect.Bottom - 1),
                              new Scalar((byte)GrabCutClasses.FGD), -1);           // 前景框内 → 确定前景(1)
                Cv2.Rectangle(maskSmall, new Point(exRect.X, exRect.Y), new Point(exRect.Right - 1, exRect.Bottom - 1),
                              new Scalar((byte)GrabCutClasses.BGD), -1);           // 排除框内 → 确定背景(0)
                Cv2.GrabCut(bgr, maskSmall, new Rect(), bgdModel, fgdModel, iterations, GrabCutModes.InitWithMask);
            }

            // —— 掩膜放回原尺寸 ——
            // 必须用 Nearest：掩膜是类别标签（0/1/2/3），线性插值会产生
            // 0.5 这类非类别值，后面按 >PR_BGD 判定时语义就乱了。
            using Mat mask = new Mat();
            if (workScale < 1.0)
                Cv2.Resize(maskSmall, mask, bgrFull.Size(), 0, 0, InterpolationFlags.Nearest);
            else
                maskSmall.CopyTo(mask);

            if (disposeBgr) bgr.Dispose();

            // mask: 0=确定背景 1=确定前景 2=可能背景 3=可能前景
            using Mat fg = new Mat();
            Cv2.Compare(mask, (int)GrabCutClasses.PR_BGD, fg, CmpTypes.GT);   // >2 即可能/确定前景
            fg.ConvertTo(fg, MatType.CV_8UC1, 255);

            // 后处理：开闭去毛刺填孔，再按面积去碎块
            if (smooth >= 3)
            {
                int k = smooth % 2 == 0 ? smooth + 1 : smooth;
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
                Cv2.MorphologyEx(fg, fg, MorphTypes.Open, kernel);
                Cv2.MorphologyEx(fg, fg, MorphTypes.Close, kernel);
            }
            if (minArea > 0)
                RemoveSmallComponents(fg, minArea);

            int total = fg.Rows * fg.Cols;
            int fgCount = Cv2.CountNonZero(fg);
            ForegroundPercent = 100.0 * fgCount / total;

            Mat dst;
            if (outMode == 0)
            {
                dst = fg.Clone();
            }
            else if (outMode == 2)
            {
                dst = new Mat(bgr.Size(), bgr.Type(), Scalar.Black);
                bgr.CopyTo(dst, fg);   // 仅前景像素被拷贝，其余保持黑
            }
            else
            {
                dst = VisionHelper.ToBgrCopy(srcMat);
                using Mat overlay = new Mat(dst.Size(), dst.Type(), new Scalar(0, 200, 0));
                using Mat blended = new Mat();
                Cv2.AddWeighted(dst, 0.65, overlay, 0.35, 0, blended);
                blended.CopyTo(dst, fg);
                // 画初始框与排除框，便于核对给的标记
                Cv2.Rectangle(dst, rect, Scalar.Lime, 1);
                if (ExcludeRect.Width > 3)
                    Cv2.Rectangle(dst, ExcludeRect, Scalar.Red, 1);
            }

            LastSummary = string.Format("GrabCut: 前景 {0:F1}% 框({1},{2},{3},{4}) 迭代{5}",
                ForegroundPercent, rect.X, rect.Y, rect.Width, rect.Height, iterations);
            return dst;
        }

        /// <summary>去掉面积小于阈值的连通域（分割噪点）</summary>
        private static void RemoveSmallComponents(Mat bin, int minArea)
        {
            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);
            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea)
                {
                    using Mat comp = new Mat();
                    Cv2.Compare(labels, i, comp, CmpTypes.EQ);
                    bin.SetTo(Scalar.Black, comp);
                }
            }
        }
    }
}
