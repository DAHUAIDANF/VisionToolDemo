using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 几何定位算子：以导入的模板小图为基准，在搜索区域内做多角度边缘模板匹配
    /// （先按角度步长粗搜，再在最优角附近细搜），输出目标中心坐标、旋转角度与匹配得分，
    /// 并在结果图上绘制带角度的定位框与中心十字线。
    /// 模板图由 UI 层"导入模板"注入（TemplateMat）；配合 ROI 框选目标附近区域可加速搜索。
    /// 参数：匹配阈值、角度搜索范围（±度）、角度步长、模板缩放。
    /// </summary>
    public class GeometricLocatorTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "几何定位";

        // UI层赋值：定位目标模板图片
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次定位结果（相对传入 srcMat 的坐标，供流水线/后续扩展读取）</summary>
        public bool Found { get; private set; }

        public Point2f Center { get; private set; }
        public double Angle { get; private set; }
        public double Score { get; private set; }

        /// <summary>状态 = 模板图（base64 PNG）</summary>
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
                ParamName = "匹配阈值",
                Min = 10,
                Max = 100,
                DefaultValue = 60,
                DisplayFormat = "阈值:{0}%",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 搜索 ±range 度（0 = 只平移匹配不旋转）
                ParamName = "角度范围",
                Min = 0,
                Max = 180,
                DefaultValue = 45,
                DisplayFormat = "±{0}°",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 粗搜步长；之后在最优角 ±步长 内以步长/5 细搜
                ParamName = "角度步长",
                Min = 1,
                Max = 10,
                DefaultValue = 3,
                DisplayFormat = "步长:{0}°",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 模板先按此百分比缩放再匹配（模板与原图分辨率不一致时用）
                ParamName = "模板缩放",
                Min = 10,
                Max = 400,
                DefaultValue = 100,
                DisplayFormat = "缩放:{0}%",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            Found = false;
            Score = 0;
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "几何定位: 请先导入模板图片";
                return dst;
            }

            int threshPercent = paramValues[0];
            double angleRange = paramValues[1];
            int coarseStep = Math.Max(1, paramValues[2]);
            int scalePercent = paramValues[3];
            double matchThresh = threshPercent / 100.0;

            // 模板灰度 + 指定缩放（统一 Linear：Area 会把 QR/纹理类模板平均成灰块，分数反而掉）
            using Mat srcGray = VisionHelper.ToGray(srcMat);
            using Mat templFull = VisionHelper.ToGray(TemplateMat);
            int nw = (int)Math.Round(templFull.Cols * scalePercent / 100.0);
            int nh = (int)Math.Round(templFull.Rows * scalePercent / 100.0);
            if (nw < 8 || nh < 8 || nw > srcGray.Cols || nh > srcGray.Rows)
            {
                LastSummary = string.Format("几何定位: 缩放 {0}% 后模板 {1}x{2} 超出可匹配范围 (源图 {3}x{4})",
                    scalePercent, nw, nh, srcGray.Cols, srcGray.Rows);
                return dst;
            }
            // 手动创建的中间模板统一收进此列表，finally 统一释放（templFull 由 using 管理）
            List<Mat> tempTemplates = [];
            Mat templBase = templFull;
            if (templFull.Cols != nw || templFull.Rows != nh)
                tempTemplates.Add(templBase = ResizeTemplate(templFull, nw, nh));

            // 性能保障：源图过长边超 1000px 时整体缩小源图与模板，命中坐标按因子还原
            int maxSide = Math.Max(srcGray.Cols, srcGray.Rows);
            double down = maxSide > 1000 ? 1000.0 / maxSide : 1.0;
            Mat searchGray = srcGray;
            Mat searchDown = null;
            try
            {
                if (down < 1.0)
                {
                    searchDown = new Mat();
                    Cv2.Resize(srcGray, searchDown, new Size(
                        (int)Math.Round(srcGray.Cols * down), (int)Math.Round(srcGray.Rows * down)),
                        0, 0, InterpolationFlags.Linear);
                    searchGray = searchDown;

                    // 模板同步缩小，且缩放后不得小于 8px
                    int tw2 = Math.Max(8, (int)Math.Round(templBase.Cols * down));
                    int th2 = Math.Max(8, (int)Math.Round(templBase.Rows * down));
                    if (tw2 != templBase.Cols || th2 != templBase.Rows)
                        tempTemplates.Add(templBase = ResizeTemplate(templBase, tw2, th2));
                }
                Mat templSearch = templBase;

                // 边缘匹配：搜索图 Canny 一次（粗/细搜共用）；模板每角度旋转后再 Canny。
                // 二值边缘图必须配 CCorrNormed（归一化点积）；CCoeffNormed 在稀疏二值图上失真（实测 0.17 vs 1.0）
                using Mat tplCanny = new();
                Cv2.Canny(templSearch, tplCanny, 80, 180);
                if (Cv2.CountNonZero(tplCanny) < 10)
                {
                    LastSummary = "几何定位: 模板边缘过少（目标太平滑，可换更清晰的目标图）";
                    return dst;
                }

                using Mat searchEdges = new();
                Cv2.Canny(searchGray, searchEdges, 80, 180);

                // 粗搜：-range…+range 按步长扫描
                double bestAngle = 0, bestScore = -1;
                Point bestLoc = default;
                Size bestSize = default;
                for (double a = -angleRange; a <= angleRange + 1e-9; a += coarseStep)
                    MatchOneAngle(searchEdges, templSearch, a,
                        ref bestAngle, ref bestScore, ref bestLoc, ref bestSize);

                if (bestScore < 0)
                {
                    LastSummary = "几何定位: 搜索失败（模板旋转后边缘过少，可换更清晰的目标图）";
                    return dst;
                }

                // 细搜：最优角 ±粗搜步长 内以步长/5 精扫
                double fineStep = Math.Max(0.2, coarseStep / 5.0);
                for (double a = bestAngle - coarseStep; a <= bestAngle + coarseStep + 1e-9; a += fineStep)
                    MatchOneAngle(searchEdges, templSearch, a,
                        ref bestAngle, ref bestScore, ref bestLoc, ref bestSize);

                if (bestScore < matchThresh)
                {
                    LastSummary = string.Format("几何定位: 未定位到目标 (最高得分 {0:F2}, 阈值 {1}%)",
                        bestScore, threshPercent);
                    return dst;
                }

                // 命中坐标按缩小因子还原到源图坐标系；旋转模板的包围盒中心 = 匹配命中框中心
                double cx = (bestLoc.X + (bestSize.Width / 2.0)) / down;
                double cy = (bestLoc.Y + (bestSize.Height / 2.0)) / down;
                Found = true;
                Center = new Point2f((float)cx, (float)cy);
                Angle = bestAngle;
                Score = bestScore;

                // —— 绘制：旋转定位框（绿）+ 中心十字（黄）+ 角度/得分文本（白字黑边） ——
                DrawLocator(dst, cx, cy, bestSize.Width / down, bestSize.Height / down, bestAngle, bestScore);

                LastSummary = string.Format("几何定位: 中心({0:F1},{1:F1}) 角度 {2:F1}° 得分 {3:F2}",
                    cx, cy, bestAngle, bestScore);
                return dst;
            }
            finally
            {
                foreach (Mat m in tempTemplates)
                    m.Dispose();
                searchDown?.Dispose();
            }
        }

        // 粗搜/细搜共用的单角度匹配：旋转模板 → Canny → 与搜索图边缘图匹配，优于当前最优则更新
        private static void MatchOneAngle(Mat searchEdges, Mat templGray, double angleDeg,
            ref double bestAngle, ref double bestScore, ref Point bestLoc, ref Size bestSize)
        {
            using Mat warped = RotateTemplate(templGray, angleDeg, out _, out _);

            using Mat wCanny = new();
            Cv2.Canny(warped, wCanny, 80, 180);
            if (Cv2.CountNonZero(wCanny) < 10)
                return;

            using Mat result = new();
            Cv2.MatchTemplate(searchEdges, wCanny, result, TemplateMatchModes.CCorrNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            if (maxVal > bestScore)
            {
                bestScore = maxVal;
                bestAngle = angleDeg;
                bestLoc = maxLoc;
                bestSize = warped.Size();
            }
        }

        /// <summary>绕模板中心旋转角度；angle=0 直接克隆。bbox 中心始终等于模板中心。</summary>
        private static Mat RotateTemplate(Mat templ, double angleDeg, out int bw, out int bh)
        {
            Size sz = templ.Size();
            if (Math.Abs(angleDeg) < 1e-9)
            {
                bw = sz.Width;
                bh = sz.Height;
                return templ.Clone();
            }

            double rad = angleDeg * Math.PI / 180.0;
            bw = (int)Math.Ceiling((sz.Width * Math.Abs(Math.Cos(rad))) + (sz.Height * Math.Abs(Math.Sin(rad))));
            bh = (int)Math.Ceiling((sz.Width * Math.Abs(Math.Sin(rad))) + (sz.Height * Math.Abs(Math.Cos(rad))));

            using Mat rot = Cv2.GetRotationMatrix2D(new Point2f(sz.Width / 2f, sz.Height / 2f), angleDeg, 1.0);
            // 平移旋转矩阵：让旋转后内容落在 [0,bh)×[0,bw) 输出图内
            rot.At<double>(0, 2) += (bw / 2.0) - (sz.Width / 2.0);
            rot.At<double>(1, 2) += (bh / 2.0) - (sz.Height / 2.0);
            Mat warped = new();
            Cv2.WarpAffine(templ, warped, rot, new Size(bw, bh),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
            return warped;
        }

        /// <summary>结果图上画定位框与十字（图像 Y 向下，正角 = 逆时针，与 RotateTemplate 一致）</summary>
        private static void DrawLocator(Mat dst, double cx, double cy, double w, double h, double angleDeg, double score)
        {
            double rad = angleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(rad), sinT = Math.Sin(rad);

            // 模板四角绕中心旋转：用 GetRotationMatrix2D 的转置（正角逆时针），保证框与匹配到的模板同向
            double[] hw = { -w / 2, w / 2, w / 2, -w / 2 };
            double[] hh = { -h / 2, -h / 2, h / 2, h / 2 };
            Point[] corners = new Point[4];
            for (int i = 0; i < 4; i++)
            {
                double rx = (hw[i] * cosT) + (hh[i] * sinT);
                double ry = (hh[i] * cosT) - (hw[i] * sinT);
                corners[i] = new Point((int)Math.Round(cx + rx), (int)Math.Round(cy + ry));
            }
            for (int i = 0; i < 4; i++)
                Cv2.Line(dst, corners[i], corners[(i + 1) % 4], Scalar.LimeGreen, 2, LineTypes.AntiAlias);

            // 中心十字
            int arm = Math.Max(6, (int)(Math.Min(w, h) / 3));
            Cv2.Line(dst, new Point((int)(cx - arm), (int)cy), new Point((int)(cx + arm), (int)cy),
                Scalar.Yellow, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, new Point((int)cx, (int)(cy - arm)), new Point((int)cx, (int)(cy + arm)),
                Scalar.Yellow, 2, LineTypes.AntiAlias);

            string text = $"angle={angleDeg:F1} score={score:F2}";
            Point tp = new((int)cx + 10, (int)cy - 10);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.Black, 3, LineTypes.AntiAlias);
            Cv2.PutText(dst, text, tp, HersheyFonts.HersheySimplex, 0.7, Scalar.White, 1, LineTypes.AntiAlias);
        }

        // 统一 Linear：Area 在大幅缩小 QR/纹理类模板时会把模块平均成灰块，分数反而掉
        private static Mat ResizeTemplate(Mat templ, int nw, int nh)
        {
            Mat m = new();
            Cv2.Resize(templ, m, new Size(nw, nh), 0, 0, InterpolationFlags.Linear);
            return m;
        }
    }
}