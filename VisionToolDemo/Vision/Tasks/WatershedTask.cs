using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 分水岭分割：把图像看作地形（灰度=高度），从标记点"注水"，
    /// 水漫到不同标记的交界处形成**分水岭线**，从而把相互**粘连**的目标分开。
    ///
    /// 为什么需要它：二值化 + 连通域对"粘连物体"无能为力 —— 两个挨在一起的零件会被
    /// 当成一个连通域，面积/个数全错。分水岭按距离或灰度"地形"切分，能把粘连目标拆开。
    ///
    /// 标记来源（本算子的核心参数）：
    ///   · 距离变换 + 前景确定区域 —— 最常用的粘连拆分做法
    ///   · 种子点（UI 点选）—— 手动指定每个目标的内部点
    /// </summary>
    public class WatershedTask : IVisionTask, IResultReporter
    {
        public string TaskName => "分水岭分割";

        /// <summary>UI 层注入的种子点（图像坐标），作为确定的标记</summary>
        public System.Collections.Generic.List<Point> Seeds { get; } = new();

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次分割出的目标数（不含背景）</summary>
        public int SegmentCount { get; private set; }

        public void AddSeed(Point p)
        {
            if (!Seeds.Contains(p)) Seeds.Add(p);
        }

        public void ClearSeeds() => Seeds.Clear();

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "thr:{0}",
                Group = "二值化",
                Tip = ">0 用固定阈值；=0 用 Otsu 自动阈值（推荐先用 0）。"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "二值化",
                Tip = "0 = 亮目标是前景；1 = 暗目标是前景（默认，适合深色零件在亮背景上）。"
            },
            new TaskParamDesc
            {
                ParamName = "距离切分强度",
                Min = 0, Max = 100, DefaultValue = 45,
                DisplayFormat = "split:{0}%",
                Group = "标记",
                Tip = "粘连拆分的力度（距离变换阈值，按最大距离的百分比）。粘得越紧调越大，" +
                      "但过大会把单个目标也切碎。这是本算子最关键的一个参数。"
            },
            new TaskParamDesc
            {
                ParamName = "粘连拆分 0关1开",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "detach:{0}",
                Group = "标记",
                Tip = "1 = 用距离变换自动把粘连目标拆开成多个标记（做粘连计数/面积就用它）；" +
                      "0 = 整个连通域作为一个标记（只做边界贴合，不拆分）。"
            },
            new TaskParamDesc
            {
                ParamName = "最小目标面积",
                Min = 0, Max = 100000, DefaultValue = 80,
                DisplayFormat = "area≥{0}",
                Group = "后处理",
                Tip = "小于该面积的区域不参与统计（滤掉噪点/碎片）。"
            },
            new TaskParamDesc
            {
                ParamName = "边界宽度",
                Min = 1, Max = 9, DefaultValue = 2,
                DisplayFormat = "edge:{0}",
                Group = "输出",
                Tip = "分水岭边界线的粗细（像素），仅影响叠加显示。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0掩膜1彩色分区2轮廓",
                Min = 0, Max = 2, DefaultValue = 1,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 所有目标的合并掩膜；1 = 每个目标用不同颜色区分（看拆分效果最直观）；" +
                      "2 = 在原图上只画分水岭边界。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            SegmentCount = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "分水岭: 输入为空";
                return srcMat?.Clone();
            }

            int thr = paramValues[0];
            int polarity = paramValues[1];
            int splitPercent = paramValues[2];
            bool detach = paramValues[3] == 1;
            int minArea = paramValues[4];
            int edgeWidth = paramValues[5];
            int outMode = paramValues[6];

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);

            // —— 1. 前景二值化 ——
            using Mat bin = new Mat();
            double usedThr;
            ThresholdTypes tt = polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            if (thr <= 0)
                usedThr = Cv2.Threshold(gray, bin, 0, 255, tt | ThresholdTypes.Otsu);
            else
            {
                usedThr = thr;
                Cv2.Threshold(gray, bin, thr, 255, tt);
            }

            // 去小噪点，避免距离变换被孤立噪点干扰
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel);

            // —— 2. 构造标记 ——
            using Mat markers = new Mat(bin.Size(), MatType.CV_32SC1, Scalar.All(0));
            // watershed 的标签约定：0 = 未知区域，1 = 背景，>=2 = 各目标。
            // nextLabel 必须从 2 起，否则第一个目标会拿到标签 1 = 背景而被吞掉
            // （实测两个粘连目标只报出 1 个，就是这个 off-by-one）。
            int nextLabel = 2;

            // 2a. 种子点优先（用户明确指定的标记）
            foreach (var s in Seeds)
            {
                if (s.X < 0 || s.Y < 0 || s.X >= bin.Cols || s.Y >= bin.Rows) continue;

                Point sp = s;
                if (bin.At<byte>(sp.Y, sp.X) == 0)
                {
                    // 种子落在背景上：吸附到最近的前景像素，避免用户点偏一点点就完全无效
                    var snapped = SnapToForeground(bin, sp);
                    if (!snapped.HasValue) continue;
                    sp = snapped.Value;
                }
                Cv2.Circle(markers, sp, 3, Scalar.All(nextLabel), -1);
                nextLabel++;
            }

            // 2b. 自动标记
            using Mat sureBg = new Mat();
            Cv2.Dilate(bin, sureBg, kernel, null, 2);       // 确定背景 = 前景外扩
            Cv2.BitwiseNot(sureBg, sureBg);
            // 确定背景标记 1（watershed 约定：未知区域置 0）
            using Mat bgMarked = new Mat();
            Cv2.Compare(sureBg, 255, bgMarked, CmpTypes.EQ);
            markers.SetTo(Scalar.All(1), bgMarked);

            using Mat sureFg = new Mat();
            if (detach)
            {
                // 距离变换 + 阈值：粘连目标的"核心"会分开成多个峰，据此生成多个标记
                using Mat dist = new Mat();
                Cv2.DistanceTransform(bin, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                Cv2.Normalize(dist, dist, 0, 255, NormTypes.MinMax);
                using Mat dist8 = new Mat();
                dist.ConvertTo(dist8, MatType.CV_8UC1);
                double dThr = Math.Max(1.0, 255.0 * splitPercent / 100.0);
                Cv2.Threshold(dist8, sureFg, dThr, 255, ThresholdTypes.Binary);
            }
            else
            {
                // 不拆分：腐蚀一点作为前景核心，整个连通域算一个标记
                Cv2.Erode(bin, sureFg, kernel);
            }

            // 每个"前景核心"连通域各拿一个独立标签（>=2）。
            // 注意不要再"先用一个标签覆盖整个 sureFg"——那样会白占一个标签，
            // 而且当 sureFg 为空时标签号还会莫名跳号。
            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int nComp = Cv2.ConnectedComponentsWithStats(sureFg, labels, stats, centroids, PixelConnectivity.Connectivity8);
            int coreCount = 0;
            for (int i = 1; i < nComp; i++)
            {
                if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < 3) continue;  // 忽略孤立小核
                using Mat comp = new Mat();
                Cv2.Compare(labels, i, comp, CmpTypes.EQ);
                markers.SetTo(Scalar.All(nextLabel), comp);
                nextLabel++;
                coreCount++;
            }
            // 最后补背景标记（1）：只标在"还没被核心占用"的确定背景区域上，
            // 否则会把已分配的目标核心覆盖成背景（曾经因此丢掉整个目标）。
            using Mat bgFinal = new Mat();
            Cv2.Compare(markers, 0, bgFinal, CmpTypes.EQ);
            Cv2.BitwiseAnd(bgMarked, bgFinal, bgFinal);
            markers.SetTo(Scalar.All(1), bgFinal);

            // —— 3. 分水岭 ——
            using Mat wsImg = bgr.Clone();
            Cv2.Watershed(wsImg, markers);

            // —— 4. 输出 ——
            // markers: -1 = 边界；1 = 背景；>=2 = 各目标
            int objects = Math.Max(0, nextLabel - 2);
            SegmentCount = objects;

            Mat dst;
            if (outMode == 0)
            {
                dst = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.Black);
                using Mat fgMask = new Mat();
                Cv2.Compare(markers, 1, fgMask, CmpTypes.GT);
                fgMask.CopyTo(dst);
            }
            else if (outMode == 2)
            {
                dst = VisionHelper.ToBgrCopy(srcMat);
                using Mat edges = new Mat();
                Cv2.Compare(markers, -1, edges, CmpTypes.EQ);
                if (edgeWidth > 1)
                {
                    using Mat ek = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(edgeWidth, edgeWidth));
                    Cv2.Dilate(edges, edges, ek);
                }
                dst.SetTo(new Scalar(0, 0, 255), edges);
            }
            else
            {
                // 每个目标用固定调色板着色，背景保持原图
                using Mat colored = new Mat(bgr.Size(), bgr.Type(), Scalar.Black);
                var palette = new[]
                {
                    new Scalar(56, 56, 255), new Scalar(151, 157, 255), new Scalar(31, 112, 255),
                    new Scalar(29, 178, 255), new Scalar(49, 210, 207), new Scalar(10, 249, 72),
                    new Scalar(23, 204, 146), new Scalar(134, 219, 61), new Scalar(52, 153, 102),
                    new Scalar(219, 152, 52), new Scalar(235, 107, 33),
                };
                for (int i = 0; i < objects; i++)
                {
                    using Mat m = new Mat();
                    Cv2.Compare(markers, i + 2, m, CmpTypes.EQ);
                    colored.SetTo(palette[i % palette.Length], m);
                }
                // 注意：colored 是 using 声明的变量，else 块结束时会被 Dispose——
                // 直接 dst = colored 再 return 会返回"已释放的 Mat"，界面显示/下次访问就抛
                // ObjectDisposedException。返回副本，让链上持有独立存活对象。
                dst = colored.Clone();
            }

            LastSummary = string.Format("分水岭: 目标 {0} 个  阈值 {1:F0}  切分 {2}%  拆分{3}",
                objects, usedThr, splitPercent, detach ? "开" : "关");
            return dst;
        }

        /// <summary>种子落在背景时，在邻域里找最近的前景像素吸附过去</summary>
        private static Point? SnapToForeground(Mat bin, Point p, int radius = 12)
        {
            for (int r = 1; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;   // 只看当前环
                        int x = p.X + dx, y = p.Y + dy;
                        if (x < 0 || y < 0 || x >= bin.Cols || y >= bin.Rows) continue;
                        if (bin.At<byte>(y, x) != 0) return new Point(x, y);
                    }
            }
            return null;
        }
    }
}
