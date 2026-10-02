using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 异物 / 偏位检测：以**参考图**为基准，检出新增的异物，并量测目标相对基准的偏移。
    ///
    /// 两种工作模式：
    ///   0 参考图对比 —— 用"当前图 − 参考图"的绝对差找新增异物，
    ///      再用相位相关量测整体偏移。适合流水线上产品位置固定的场景。
    ///   1 基准位置对比 —— 不依赖参考图，用"当前目标质心 − 标定的目标位置"量偏移。
    ///      适合只关心装配偏位、不需要找异物的场景。
    ///
    /// 与"模板差分"的区别：模板差分的 ROI 由 UI 框选，且只做差分不做量测；
    /// 本算子支持整体**配准后再差分**（否则工件放偏一点就会整幅误报），
    /// 并输出偏位量 / 异物面积 / 异物数量三个可进公差的指标。
    /// </summary>
    public class OffsetInspectTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "异物偏位检测";

        public string LastSummary { get; private set; } = "";

        /// <summary>参考图（模式 0 使用）；null 表示未设置</summary>
        public Mat ReferenceMat { get; set; }

        /// <summary>整体偏移 X（px），正值 = 当前图内容相对基准右移</summary>
        public double OffsetX { get; private set; } = double.NaN;

        public double OffsetY { get; private set; } = double.NaN;

        /// <summary>整体偏移量（px）</summary>
        public double OffsetMagnitude { get; private set; } = double.NaN;

        /// <summary>偏移方向（度）</summary>
        public double OffsetAngle { get; private set; } = double.NaN;

        /// <summary>异物数量</summary>
        public int ForeignCount { get; private set; }

        /// <summary>异物总面积占比（%）</summary>
        public double ForeignAreaPercent { get; private set; } = double.NaN;

        /// <summary>最大异物面积（px²）</summary>
        public double MaxForeignArea { get; private set; } = double.NaN;

        /// <summary>判定：0 = OK，非 0 = NG 原因位标志（1=偏位超差，2=异物超差）</summary>
        public int Verdict { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "模式 0参考图1基准位置", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}", Group = "检测", Tip = "0参考图：用当前图与参考图的差分找异物，" +
                "同时量测偏移（需要先在界面上设置参考图）。" +
                "1基准位置：不依赖参考图，只量目标质心相对标定位置的偏移。" },
            new TaskParamDesc { ParamName = "阈值", Min = 0, Max = 255, DefaultValue = 127,
                DisplayFormat = "阈值:{0}", Group = "二值化" },
            new TaskParamDesc { ParamName = "极性 0亮1暗", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "极性:{0}", Group = "二值化" },
            new TaskParamDesc { ParamName = "差分门限", Min = 1, Max = 255, DefaultValue = 25,
                DisplayFormat = "门限:{0}", Group = "检测", Tip = "当前图与参考图灰度差超过多少算异物。" +
                "太低会因噪声/照明波动误报，太高会漏掉浅色异物。20~35 常用。" },
            new TaskParamDesc { ParamName = "先配准", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "配准:{0}", Group = "检测", Tip = "差分前先做相位相关配准补偿整体偏移。" +
                "**建议开启**：否则工件放偏几个像素就会让整条轮廓都被当成异物。" },
            new TaskParamDesc { ParamName = "最小异物面积", Min = 1, Max = 100000, DefaultValue = 25,
                DisplayFormat = "面积>={0}", Group = "筛选" },
            new TaskParamDesc { ParamName = "最小异物长度", Min = 1, Max = 5000, DefaultValue = 5,
                DisplayFormat = "长度>={0}", Group = "筛选", Tip = "外接框对角线小于此值的检出丢弃，" +
                "用于过滤孤立噪声点。" },
            new TaskParamDesc { ParamName = "偏位公差px", Min = 0, Max = 100000, DefaultValue = 5,
                DisplayFormat = "偏位<={0}px", Group = "判定" },
            new TaskParamDesc { ParamName = "异物面积公差%", Min = 0, Max = 100, DefaultValue = 100,
                DisplayFormat = "异物<={0}%", Group = "判定", Tip = "异物总面积占图面的百分比上限。" +
                "100 表示只报告不判定。" },
            new TaskParamDesc { ParamName = "像素当量um", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "当量:{0}um", Group = "换算" },
        ];

        private static void DrawCrossAt(Mat dst, double x, double y, Scalar color, int arm)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            Cv2.Line(dst, ix - arm, iy, ix + arm, iy, color, 1, LineTypes.AntiAlias);
            Cv2.Line(dst, ix, iy - arm, ix, iy + arm, color, 1, LineTypes.AntiAlias);
        }

        public string SaveState()
        {
            if (ReferenceMat == null || ReferenceMat.Empty()) return null;
            return VisionHelper.SaveTemplateState(ReferenceMat);
        }

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            ReferenceMat?.Dispose();
            ReferenceMat = m;
        }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            OffsetX = OffsetY = OffsetMagnitude = OffsetAngle = double.NaN;
            ForeignCount = 0;
            ForeignAreaPercent = MaxForeignArea = double.NaN;
            Verdict = 0;
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (srcMat == null || srcMat.Empty()) return dst;

            int mode = paramValues[0];
            int threshold = paramValues[1], polarity = paramValues[2];
            double diffThresh = paramValues[3];
            bool doAlign = paramValues[4] == 1;
            double minArea = paramValues[5];
            double minLen = paramValues[6];
            double tolOffset = paramValues[7];
            double tolAreaPct = paramValues[8];
            double mmPerPx = paramValues[9] / 1000.0;

            using Mat gray = VisionHelper.ToGray(srcMat);

            // 基准位置模式：质心 vs 图像中心（用作标定位置的代理）
            if (mode == 1)
            {
                using Mat bin = new();
                Cv2.Threshold(gray, bin, threshold, 255,
                    polarity == 1 ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
                Moments m = Cv2.Moments(bin, true);
                if (Math.Abs(m.M00) < 1e-9)
                {
                    LastSummary = "异物偏位检测: 未找到目标（检查阈值/极性）";
                    return dst;
                }
                double cx = m.M10 / m.M00, cy = m.M01 / m.M00;
                double bx = gray.Cols / 2.0, by = gray.Rows / 2.0;
                OffsetX = cx - bx;
                OffsetY = cy - by;
                OffsetMagnitude = Math.Sqrt((OffsetX * OffsetX) + (OffsetY * OffsetY));
                OffsetAngle = Math.Atan2(OffsetY, OffsetX) * 180.0 / Math.PI;
                Verdict = OffsetMagnitude > tolOffset ? 1 : 0;

                DrawCrossAt(dst, bx, by, Scalar.Gray, 12);
                DrawCrossAt(dst, cx, cy, Verdict == 0 ? Scalar.LimeGreen : Scalar.Red, 12);
                Cv2.ArrowedLine(dst, new Point((int)Math.Round(bx), (int)Math.Round(by)),
                    new Point((int)Math.Round(cx), (int)Math.Round(cy)),
                    Scalar.Yellow, 1, LineTypes.AntiAlias, 0, 0.15);
                MatDraw.DrawText(dst, string.Format("偏位 {0:F2}px{1} (公差 {2:F1})", OffsetMagnitude,
                    mmPerPx > 0 ? string.Format("/{0:F4}mm", OffsetMagnitude * mmPerPx) : "", tolOffset), 6, 20, Verdict == 0 ? Scalar.LimeGreen : Scalar.Red, 13);

                LastSummary = string.Format("异物偏位检测: 偏移 {0:F3}px ({1:F2},{2:F3}), 方向 {3:F1}deg, {4}",
                    OffsetMagnitude, OffsetX, OffsetY, OffsetAngle, Verdict == 0 ? "OK" : "NG-偏位超差");
                return dst;
            }

            // —— 模式 0：参考图对比 ——
            if (ReferenceMat == null || ReferenceMat.Empty())
            {
                LastSummary = "异物偏位检测: 未设置参考图（请在界面上先设置，或改用基准位置模式）";
                return dst;
            }

            using Mat refGray = VisionHelper.ToGray(ReferenceMat);
            if (refGray.Size() != gray.Size())
            {
                LastSummary = string.Format("异物偏位检测: 参考图尺寸 {0}x{1} 与当前图 {2}x{3} 不一致",
                    refGray.Cols, refGray.Rows, gray.Cols, gray.Rows);
                return dst;
            }

            using Mat cur = new(), rf = new();
            gray.ConvertTo(cur, MatType.CV_32F);
            refGray.ConvertTo(rf, MatType.CV_32F);

            // 相位相关配准：估计当前图相对参考图的整体平移。
            // 不配准时，工件放偏 1px 就会让所有轮廓变成"异物"，误报量级远超真实缺陷。
            double dx = 0, dy = 0;
            if (doAlign)
            {
                using Mat hann = new();
                Cv2.CreateHanningWindow(hann, new Size(gray.Cols, gray.Rows), MatType.CV_32F);
                using Mat wa = new(), wb = new();
                Cv2.Multiply(cur, hann, wa, 1.0, MatType.CV_32F);
                Cv2.Multiply(rf, hann, wb, 1.0, MatType.CV_32F);
                Point2d shift = Cv2.PhaseCorrelate(wa, wb, new Mat(), out _);
                dx = shift.X; dy = shift.Y;
                OffsetX = -dx; OffsetY = -dy;   // 内容位移方向与补偿方向相反
                OffsetMagnitude = Math.Sqrt((OffsetX * OffsetX) + (OffsetY * OffsetY));
                OffsetAngle = Math.Atan2(OffsetY, OffsetX) * 180.0 / Math.PI;

                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
                {
                    using Mat M = new Mat(2, 3, MatType.CV_64F, Scalar.All(0));
                    M.Set(0, 0, 1.0); M.Set(1, 1, 1.0);
                    M.Set(0, 2, dx); M.Set(1, 2, dy);
                    using Mat shifted = new();
                    Cv2.WarpAffine(cur, shifted, M, cur.Size(),
                        InterpolationFlags.Linear, BorderTypes.Replicate);
                    shifted.CopyTo(cur);
                }
            }
            else
            {
                OffsetX = OffsetY = 0;
                OffsetMagnitude = 0;
                OffsetAngle = 0;
            }

            using Mat diff = new();
            Cv2.Absdiff(cur, rf, diff);
            using Mat diff8 = new();
            diff.ConvertTo(diff8, MatType.CV_8U);

            using Mat mask = new();
            Cv2.Threshold(diff8, mask, diffThresh, 255, ThresholdTypes.Binary);

            // 形态学开运算去掉孤立噪点（1~2px 的差分噪声非常常见）
            using (Mat el = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, el);

            Cv2.FindContours(mask, out Point[][] cs, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            int count = 0;
            double maxArea = 0, areaSum = 0;
            foreach (Point[] c in cs)
            {
                double a = Cv2.ContourArea(c);
                if (a < minArea) continue;
                Rect r = Cv2.BoundingRect(c);
                double diag = Math.Sqrt((r.Width * (double)r.Width) + (r.Height * r.Height));
                if (diag < minLen) continue;

                count++;
                areaSum += a;
                if (a > maxArea) maxArea = a;

                Cv2.Rectangle(dst, r, Scalar.Orange, 1);
                var cen = new Point(r.X + (r.Width / 2), r.Y + (r.Height / 2));
                Cv2.Circle(dst, cen, 3, Scalar.Red, -1);
                Cv2.PutText(dst, string.Format("{0:F0}", a),
                    new Point(r.X, Math.Max(12, r.Y - 3)),
                    HersheyFonts.HersheySimplex, 0.36, Scalar.Yellow, 1, LineTypes.AntiAlias);
            }

            ForeignCount = count;
            ForeignAreaPercent = 100.0 * areaSum / (srcMat.Cols * (double)srcMat.Rows);
            MaxForeignArea = count > 0 ? maxArea : double.NaN;

            bool badOffset = OffsetMagnitude > tolOffset;
            bool badForeign = ForeignAreaPercent > tolAreaPct;
            Verdict = (badOffset ? 1 : 0) | (badForeign ? 2 : 0);

            Scalar vc = Verdict == 0 ? Scalar.LimeGreen : Scalar.Red;
            MatDraw.DrawText(dst, string.Format("偏位 {0:F2}px (公差 {1:F0})", OffsetMagnitude, tolOffset), 6, 20, badOffset ? Scalar.Red : Scalar.LimeGreen, 13);
            MatDraw.DrawText(dst, string.Format("异物 {0} 处, 面积 {1:F3}% (公差 {2:F0}%)", count, ForeignAreaPercent, tolAreaPct), 6, 40, badForeign ? Scalar.Red : Scalar.LimeGreen, 13);
            Cv2.PutText(dst, Verdict == 0 ? "OK" : "NG",
                new Point(6, 64), HersheyFonts.HersheySimplex, 0.8, vc, 2, LineTypes.AntiAlias);

            string verdictText = Verdict switch
            {
                0 => "OK",
                1 => "NG-偏位超差",
                2 => "NG-异物超差",
                _ => "NG-偏位+异物",
            };
            LastSummary = string.Format(
                "异物偏位检测: {0}  (偏位 {1:F3}px, 异物 {2} 处, 面积 {3:F3}%, 最大 {4})",
                verdictText, OffsetMagnitude, count, ForeignAreaPercent,
                count > 0 ? string.Format("{0:F0}px²", maxArea) : "无");
            return dst;
        }
    }
}
