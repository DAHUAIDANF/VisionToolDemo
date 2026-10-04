using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像拼接：把当前图与**模板图（第二幅）**拼成一张全景/长图。
    ///
    /// 两种配准：
    ///   · ORB 自动配准（默认）——两图有重叠区时自动求单应矩阵（RANSAC），
    ///     能处理平移+轻微旋转/缩放（分块拍摄的大工件、多段拍照拼全）；
    ///   · 固定重叠平移——两图只是简单平移错位（传送带分段拍照），按重叠像素
    ///     直接对位，快且稳定。
    ///
    /// 重叠区做线性渐隐融合，接缝处不会出现明显亮度跳变。
    /// 用法：在算子参数里导入第二张图（模板槽），或直接放算子链第一段。
    /// </summary>
    public class ImageStitchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "图像拼接";

        /// <summary>第二幅图（UI 通过模板导入）</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次配准用的内点对数 / 匹配对数（自动配准时）</summary>
        public int GoodMatches { get; private set; }

        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null) TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "配准 0自动1固定重叠",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "配准:{0}",
                Tip = "0=ORB特征自动配准（两图有重叠，可带轻微旋转缩放）1=按固定重叠量平移对位（下一参数给重叠像素，传送带分段拍照用）"
            },
            new TaskParamDesc
            {
                ParamName = "固定重叠px",
                Min = 0,
                Max = 5000,
                DefaultValue = 100,
                DisplayFormat = "重叠:{0}px",
                Tip = "配准=1 时两图的重叠像素数（第二幅相对当前图向右/下重叠这么多像素）"
            },
            new TaskParamDesc
            {
                ParamName = "方向 0右1左2下3上",
                Min = 0,
                Max = 3,
                DefaultValue = 0,
                DisplayFormat = "方向:{0}",
                Tip = "固定重叠时第二幅的摆放方向：0=右边 1=左边 2=下边 3=上边。自动配准失败时也按此方向兜底"
            },
            new TaskParamDesc
            {
                ParamName = "融合宽度px",
                Min = 0,
                Max = 2000,
                DefaultValue = 0,
                DisplayFormat = "融合:{0}px",
                Tip = "重叠区线性渐隐宽度。0=自动按重叠区的 60%；填 0 且无重叠时不做融合直接覆盖"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int mode = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 0, 0, 1);
            int overlap = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 100, 0, 5000);
            int dir = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 0, 0, 3);
            int blend = Math.Max(0, paramValues.Length > 3 ? paramValues[3] : 0);

            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "图像拼接: 请先导入第二幅图（参数面板的模板槽）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            Mat a = srcMat;                    // 当前图
            Mat b = TemplateMat;               // 第二幅

            // 1) 自动配准：ORB + 单应矩阵（RANSAC）
            Mat homography = null;
            if (mode == 0)
            {
                homography = TryAutoRegister(a, b);
                if (homography == null)
                {
                    // 配准失败，按固定方向平移兜底
                    homography = TranslationHomography(b.Cols, b.Rows, dir, overlap);
                    GoodMatches = -1;   // 标记走兜底
                }
            }
            else
            {
                homography = TranslationHomography(b.Cols, b.Rows, dir, overlap);
            }

            // 2) 计算拼接画布
            using Mat aPts = new Mat(1, 4, MatType.CV_32FC2);
            aPts.Set(0, 0, new Point2f(0, 0));
            aPts.Set(0, 1, new Point2f(a.Cols, 0));
            aPts.Set(0, 2, new Point2f(a.Cols, a.Rows));
            aPts.Set(0, 3, new Point2f(0, a.Rows));
            using Mat bPts = new Mat(1, 4, MatType.CV_32FC2);
            bPts.Set(0, 0, new Point2f(0, 0));
            bPts.Set(0, 1, new Point2f(b.Cols, 0));
            bPts.Set(0, 2, new Point2f(b.Cols, b.Rows));
            bPts.Set(0, 3, new Point2f(0, b.Rows));

            using Mat bWarped = new();
            Cv2.PerspectiveTransform(bPts, bWarped, homography);

            float minX = 0, minY = 0, maxX = a.Cols, maxY = a.Rows;
            for (int i = 0; i < 4; i++)
            {
                Vec2f v = bWarped.At<Vec2f>(0, i);
                minX = Math.Min(minX, v.Item0);
                minY = Math.Min(minY, v.Item1);
                maxX = Math.Max(maxX, v.Item0);
                maxY = Math.Max(maxY, v.Item1);
            }
            int cw = (int)Math.Ceiling(maxX - minX);
            int ch = (int)Math.Ceiling(maxY - minY);
            if (cw <= 0 || ch <= 0 || cw > 20000 || ch > 20000)
            {
                homography.Dispose();
                LastSummary = "图像拼接: 画布尺寸异常，配准结果不可用";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            // 3) 变换到画布坐标系（平移 -minX,-minY）
            using Mat T = Mat.Eye(3, 3, MatType.CV_64FC1);
            T.Set<double>(0, 2, -minX);
            T.Set<double>(1, 2, -minY);
            using Mat Hfinal = new();
            Cv2.Gemm(T, homography, 1, null, 0, Hfinal);

            using Mat canvas = new Mat(ch, cw, a.Type(), Scalar.All(0));
            using Mat maskA = new Mat(ch, cw, MatType.CV_8UC1, Scalar.All(0));
            using Mat maskB = new Mat(ch, cw, MatType.CV_8UC1, Scalar.All(0));

            // A 直接平移到画布
            int ox = (int)Math.Round(-minX), oy = (int)Math.Round(-minY);
            Rect ra = new(ox, oy, a.Cols, a.Rows);
            Rect raClipped = ra.Intersect(new Rect(0, 0, cw, ch));
            if (raClipped.Width > 0 && raClipped.Height > 0)
            {
                using Mat roi = new(canvas, raClipped);
                using Mat sub = new(a, new Rect(raClipped.X - ox, raClipped.Y - oy, raClipped.Width, raClipped.Height));
                sub.CopyTo(roi);
                using Mat roiM = new(maskA, raClipped);
                roiM.SetTo(Scalar.All(255));
            }

            // B 经 Hfinal 变换到画布
            using Mat warpB = new();
            Cv2.WarpPerspective(b, warpB, Hfinal, new Size(cw, ch), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            Cv2.WarpPerspective(Mat.Ones(b.Rows, b.Cols, MatType.CV_8UC1), maskB, Hfinal, new Size(cw, ch),
                InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));

            // 4) 重叠区线性融合（A 在上层时：alpha = maskB 的渐隐）
            int blendW = blend > 0 ? blend : Math.Max(10, overlap == 0 ? (int)(Math.Min(a.Cols, b.Cols) * 0.2) : overlap);
            using Mat alpha = new Mat(ch, cw, MatType.CV_32FC1, Scalar.All(0));
            BuildBlendAlpha(maskA, maskB, alpha, blendW);

            using Mat aF = new();
            canvas.ConvertTo(aF, MatType.CV_32FC3);
            using Mat bF = new();
            warpB.ConvertTo(bF, MatType.CV_32FC3);
            using Mat outF = new();
            Cv2.AddWeighted(aF, 1, bF, 0, 0, outF);   // 占位（下面逐像素融合）

            // 逐像素：在 B 有像素的区域按 alpha 混合
            var al = alpha; var mb = maskB;   // 4.10 无 MatIndexer，改用 At/Set
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    if (mb.At<byte>(y, x) == 0) continue;   // B 没覆盖到 → 保持 A
                    float t = al.At<float>(y, x);
                    Vec3f va = aF.At<Vec3f>(y, x), vb = bF.At<Vec3f>(y, x);
                    outF.Set(y, x, new Vec3f(
                        vb.Item0 * t + va.Item0 * (1 - t),
                        vb.Item1 * t + va.Item1 * (1 - t),
                        vb.Item2 * t + va.Item2 * (1 - t)));
                }
            // 没有 B 覆盖的画布空白（理论上不存在，防边界）
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (mb.At<byte>(y, x) == 0 && aF.At<Vec3f>(y, x) == default(Vec3f))
                        outF.Set(y, x, bF.At<Vec3f>(y, x));

            using Mat out8 = new();
            outF.ConvertTo(out8, srcMat.Type());
            homography.Dispose();

            string how = mode == 0 ? (GoodMatches >= 0 ? string.Format("ORB配准 {0}对", GoodMatches) : "配准失败→平移兜底") : "固定重叠";
            LastSummary = string.Format(
                "图像拼接: {0} → {1}x{2}（第二幅 {3}x{4}）", how, out8.Cols, out8.Rows, b.Cols, b.Rows);
            return out8;
        }

        /// <summary>ORB 配准两图，返回把 B 变换到 A 坐标系的单应矩阵；特征不足/不可解返回 null</summary>
        private Mat TryAutoRegister(Mat a, Mat b)
        {
            using Mat ga = VisionHelper.ToGray(a);
            using Mat gb = VisionHelper.ToGray(b);
            using ORB orb = ORB.Create(1200);
            using Mat da = new();
            using Mat db = new();
            orb.DetectAndCompute(ga, null, out KeyPoint[] ka, da);
            orb.DetectAndCompute(gb, null, out KeyPoint[] kb, db);
            if (ka.Length < 8 || kb.Length < 8) return null;

            using BFMatcher matcher = new(NormTypes.Hamming, true);
            DMatch[] matches = matcher.Match(da, db);
            if (matches.Length < 8) return null;
            Array.Sort(matches, (p, q) => p.Distance.CompareTo(q.Distance));
            int keep = Math.Min(60, matches.Length);

            var src = new List<Point2f>();
            var dst = new List<Point2f>();
            for (int i = 0; i < keep; i++)
            {
                src.Add(kb[matches[i].TrainIdx].Pt);    // B 上的点
                dst.Add(ka[matches[i].QueryIdx].Pt);    // A 上的点
            }
            using Mat srcM = Mat.FromArray(src.ToArray());
            using Mat dstM = Mat.FromArray(dst.ToArray());
            Mat H = Cv2.FindHomography(srcM, dstM, HomographyMethods.Ransac, 3);
            if (H == null || H.Empty()) return null;
            GoodMatches = keep;
            return H;
        }

        /// <summary>按方向 + 重叠量构造平移单应矩阵（把 B 移到 A 的指定一侧）</summary>
        private static Mat TranslationHomography(int bW, int bH, int dir, int overlap)
        {
            double tx = 0, ty = 0;
            switch (dir)
            {
                case 1: tx = -bW + overlap; break;              // B 在左
                case 2: ty = overlap; break;                    // B 在下（A 上 B 下：B 顶部与 A 底部重叠）
                case 3: ty = -bH + overlap; break;              // B 在上
                default: tx = overlap; break;                   // B 在右
            }
            return Mat.FromArray(new double[,]
            {
                { 1, 0, tx },
                { 0, 1, ty },
                { 0, 0, 1 },
            });
        }

        /// <summary>构建融合权重：A∩B 区域里，从 A 侧到 B 侧 alpha 0→1 线性过渡</summary>
        private static void BuildBlendAlpha(Mat maskA, Mat maskB, Mat alpha, int blendW)
        {
            // 对每个 B 像素：找其到 A 边界的近似距离（沿 x 或 y 按主轴），简单可靠做法：
            // 在重叠区内按列/行主方向渐变。为通用性，用距离变换成本高；
            // 这里简化为：A∪B 中不在 A 内的 B 区 alpha=1；重叠区按到 A 边界的线性插值。
            // 用 OpenCV 距离变换太重，改为按方向构造：重叠区一般在 A 的右/下/左/上边缘。
            // 由于场景多为单侧重叠，这里用"列方向距离"近似即可满足视觉平滑。
            var ma = maskA; var mb = maskB;   // At/Set 访问
            int rows = alpha.Rows, cols = alpha.Cols;
            int eff = Math.Max(2, blendW);

            // 先标记重叠区（A∩B）与 B-only 区
            byte[,] ov = new byte[rows, cols];
            int ovMin = int.MaxValue, ovMax = int.MinValue;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    if (mb.At<byte>(y, x) == 0) continue;
                    if (ma.At<byte>(y, x) > 0) { ov[y, x] = 1; ovMin = Math.Min(ovMin, x); ovMax = Math.Max(ovMax, x); }
                }

            // 按重叠区水平跨度做线性渐变（假设左右拼接；上下拼接时跨度仍可用列近似，视觉可接受）
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    if (mb.At<byte>(y, x) == 0) continue;
                    if (ov[y, x] == 0) { alpha.Set<float>(y, x, 1f); continue; }
                    if (ovMax <= ovMin) { alpha.Set<float>(y, x, 0.5f); continue; }
                    float t = Math.Clamp((x - ovMin) / (float)(ovMax - ovMin), 0f, 1f);
                    alpha.Set<float>(y, x, t);
                }
        }
    }
}
