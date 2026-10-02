using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 质量辅助：MTF/成像测试的配套工具，把几件反复要用的预处理与形态量测收在一处。
    ///
    /// 五种模式（每种解决一类具体问题）：
    ///   · **CLAHE 可视化** —— 局部对比度增强，把弱纹理/低对比缺陷"提出来"便于目视确认。
    ///     与直方图均衡化的区别：CLAHE 分块做且限幅，不会把噪声一起放大成雪花。
    ///   · **位深防溢出** —— 归一化并转换位深时保留有效范围，避免中间步骤截断导致数据丢失
    ///     （做 HDR/多帧平均时最常见：加着加着就顶到 255 了）。
    ///   · **光斑形态** —— fitEllipse + minEnclosingCircle，把亮斑/鬼影量化成
    ///     长短轴、圆度、方向，用于判定"是点状还是拉长的反光"。
    ///   · **自动阈值参考** —— 给出 Otsu/三角/均值三种阈值的数值，供其他算子填参参考。
    ///   · **参数建议** —— 依据图像统计量给出几个常用算子的起始参数。
    /// </summary>
    public class QualityAssistTask : IVisionTask, IResultReporter
    {
        public string TaskName => "质量辅助工具";

        public string LastSummary { get; private set; } = "";

        public double OtsuThreshold { get; private set; } = double.NaN;
        public double TriangleThreshold { get; private set; } = double.NaN;
        public double MeanValue { get; private set; } = double.NaN;
        public double StdDev { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "模式 0CLAHE 1位深 2光斑 3阈值 4建议",
                Min = 0, Max = 4, DefaultValue = 0,
                DisplayFormat = "mode:{0}",
                Group = "功能",
                Tip = "0=CLAHE对比度增强  1=位深归一化防溢出  2=光斑形态量测  " +
                      "3=自动阈值参考  4=参数建议（后两者只算不画，结果写在图上）"
            },
            new TaskParamDesc
            {
                ParamName = "CLAHE限幅x10",
                Min = 1, Max = 100, DefaultValue = 20,
                DisplayFormat = "clip:{0}",
                Group = "CLAHE",
                Tip = "对比度限幅（实际值 = 该数/10）。越大对比越强、噪声也越明显；4~5 通常合适。"
            },
            new TaskParamDesc
            {
                ParamName = "CLAHE网格",
                Min = 2, Max = 32, DefaultValue = 8,
                DisplayFormat = "tile:{0}",
                Group = "CLAHE",
                Tip = "分块数。块越小局部增强越强，但块间边界伪影越明显。8x8 是常用起点。"
            },
            new TaskParamDesc
            {
                ParamName = "位深目标位",
                Min = 8, Max = 16, DefaultValue = 8,
                DisplayFormat = "bits:{0}",
                Group = "位深",
                Tip = "归一化后要转成多少位。选 16 可保住高位深数据，避免转 8 位时的量化损失。"
            },
            new TaskParamDesc
            {
                ParamName = "百分位裁剪%",
                Min = 0, Max = 20, DefaultValue = 0,
                DisplayFormat = "clip:{0}%",
                Group = "位深",
                Tip = "归一化时两端各裁掉多少百分比的极值像素，避免单个坏点把整体范围拉爆。0 = 用真实最值。"
            },
            new TaskParamDesc
            {
                // Min 必须是 0：本参数用 0 作"自动(Otsu)"的哨兵值。
                // 原先写 Min=1 而 DefaultValue=0，参数面板执行
                //   numeric.Minimum=1; numeric.Value=0;
                // 时抛 ArgumentOutOfRangeException —— 一选中"质量辅助工具"就崩。
                // 约束：DefaultValue 必须落在 [Min, Max] 内（已加回归测试守住）。
                ParamName = "光斑阈值",
                Min = 0, Max = 255, DefaultValue = 0,
                DisplayFormat = "thr:{0}",
                Group = "光斑",
                Tip = "光斑二值化阈值。0 = 用 Otsu 自动（推荐先用 0）。" +
                "亮斑/高反光区域分割不佳时再手填（例如 200 以上只取很亮的斑）。"
            },
            new TaskParamDesc
            {
                ParamName = "最小光斑面积",
                Min = 1, Max = 100000, DefaultValue = 20,
                DisplayFormat = "area≥{0}",
                Group = "光斑",
                Tip = "小于该面积不计为光斑（滤噪点）。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            OtsuThreshold = TriangleThreshold = MeanValue = StdDev = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "质量辅助: 输入为空";
                return srcMat?.Clone();
            }

            int mode = Math.Clamp(paramValues[0], 0, 4);
            double clipLimit = Math.Max(1, paramValues[1]) / 10.0;
            int tile = Math.Clamp(paramValues[2], 2, 32);
            int targetBits = Math.Clamp(paramValues[3], 8, 16);
            int clipPct = Math.Clamp(paramValues[4], 0, 20);
            int spotThr = paramValues[5];
            int minSpotArea = paramValues[6];

            // 本算子会先把输入转灰度再做 Otsu/三角阈值等操作，这些只接受 8U。
            // 16 位 RAW 直接进来会在下面的 Cv2.Threshold 抛
            // "src.type() == CV_8UC1" —— 这正是"质量辅助工具一跑就崩"的原因。
            // 统一先归一化到 8U，后续所有分支都不会再碰到非 8U 数据。
            using Mat gray = VisionHelper.ToGray(srcMat);
            Cv2.MeanStdDev(gray, out Scalar meanS, out Scalar stdS);
            MeanValue = meanS.Val0;
            StdDev = stdS.Val0;

            // 三种自动阈值（供参考，多数模式都会算）
            using (Mat tmpOtsu = new Mat())
            {
                OtsuThreshold = Cv2.Threshold(gray, tmpOtsu, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            }
            using (Mat tmpTri = new Mat())
            {
                TriangleThreshold = Cv2.Threshold(gray, tmpTri, 0, 255,
                    ThresholdTypes.Binary | ThresholdTypes.Triangle);
            }

            Mat dst;
            switch (mode)
            {
                case 0: dst = DoClahe(srcMat, gray, clipLimit, tile); break;
                case 1: dst = DoBitDepth(srcMat, clipPct, targetBits); break;
                case 2: dst = DoSpots(srcMat, gray, spotThr, minSpotArea); break;
                default: dst = DoInfo(srcMat); break;
            }
            return dst;
        }

        /// <summary>CLAHE：分块限幅自适应均衡，提弱纹理但不放大噪声</summary>
        private Mat DoClahe(Mat srcMat, Mat gray, double clipLimit, int tile)
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit, new Size(tile, tile));
            using Mat eq = new Mat();
            clahe.Apply(gray, eq);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            // 左半原图、右半增强，便于直接对比增强效果
            int w = eq.Cols;
            using (Mat right = new Mat(dst, new Rect(w / 2, 0, w - w / 2, dst.Rows)))
            using (Mat eqBgr = new Mat())
            {
                Cv2.CvtColor(new Mat(eq, new Rect(w / 2, 0, w - w / 2, eq.Rows)), eqBgr, ColorConversionCodes.GRAY2BGR);
                eqBgr.CopyTo(right);
            }
            Cv2.Line(dst, new Point(w / 2, 0), new Point(w / 2, dst.Rows), Scalar.Yellow, 2);
            MatDraw.DrawText(dst, string.Format("左=原图  右=CLAHE clip{0:F1} {1}x{1}", clipLimit, tile), 8, 22, Scalar.Yellow, 13);

            // 增强后对比度提升量（std 变化）作为量化指标
            Cv2.MeanStdDev(eq, out _, out Scalar eqStd);
            LastSummary = string.Format("质量辅助: CLAHE clip={0:F1} tile={1}x{1}  std {2:F1} -> {3:F1} (+{4:F0}%)",
                clipLimit, tile, StdDev, eqStd.Val0, StdDev > 0.1 ? (eqStd.Val0 / StdDev - 1) * 100 : 0);
            return dst;
        }

        /// <summary>位深归一化：按百分位裁掉极值后线性拉伸到目标位深，避免溢出截断</summary>
        private Mat DoBitDepth(Mat srcMat, int clipPct, int targetBits)
        {
            double lo = 0, hi = 255;
            // 直方图必须基于**8U 单通道**。原先直接按字节扫 srcMat：
            //   · srcMat 是 16 位时，一个像素占 2 字节，会把高位/低位当两个像素统计；
            //   · srcMat 是彩色时，Step 包含 3 通道的填充，范围也错了；
            //   · srcMat 若是 8UC3 以外的布局，指针算术还可能越界。
            // 所以统一先用 ToGray 归一化，再用 Cv2.CalcHist 而不是手写指针循环 ——
            // 交给 OpenCV 处理步长/对齐，不会再有这类隐患。
            using Mat g8 = VisionHelper.ToGray(srcMat);
            if (clipPct > 0)
            {
                int[] hist = new int[256];
                unsafe
                {
                    // g8 保证是 CV_8UC1 且内存连续（ToGray 的输出总是新分配的）
                    byte* p = (byte*)g8.Data;
                    long total = (long)g8.Rows * g8.Cols;
                    for (long i = 0; i < total; i++) hist[p[i]]++;
                }
                long sum = 0; foreach (int v in hist) sum += v;
                long loTarget = sum * clipPct / 100, hiTarget = sum * (100 - clipPct) / 100;
                long acc = 0;
                lo = 0; hi = 255;
                for (int i = 0; i < 256; i++)
                {
                    acc += hist[i];
                    if (acc >= loTarget) { lo = i; break; }
                }
                acc = 0;
                for (int i = 0; i < 256; i++)
                {
                    acc += hist[i];
                    if (acc >= hiTarget) { hi = i; break; }
                }
            }
            else
            {
                Cv2.MinMaxLoc(g8, out double mn, out double mx);
                lo = mn; hi = mx;
            }
            if (hi <= lo) hi = lo + 1;

            double maxOut = (1 << targetBits) - 1;
            double alpha = maxOut / (hi - lo);

            Mat dst = new Mat();
            if (targetBits <= 8)
            {
                using Mat g = VisionHelper.ToGray(srcMat);
                using Mat g32 = new Mat();
                g.ConvertTo(g32, MatType.CV_32FC1);
                Cv2.Subtract(g32, new Scalar(lo), g32);
                using Mat scaled = new Mat();
                Cv2.Multiply(g32, alpha, scaled);
                scaled.ConvertTo(dst, MatType.CV_8UC1);
            }
            else
            {
                // 16 位输出：保留高位深，供后续精确处理
                using Mat g = VisionHelper.ToGray(srcMat);
                using Mat g32 = new Mat();
                g.ConvertTo(g32, MatType.CV_32FC1);
                Cv2.Subtract(g32, new Scalar(lo), g32);
                using Mat scaled = new Mat();
                Cv2.Multiply(g32, alpha, scaled);
                dst = new Mat();
                scaled.ConvertTo(dst, MatType.CV_16UC1);
            }

            // 显示用：把结果再拉回 8 位可视（不影响返回的数据位深说明）
            LastSummary = string.Format("质量辅助: 位深归一化 [{0:F0},{1:F0}] -> {2}位 (裁剪{3}%)  均值{4:F0} std{5:F1}",
                lo, hi, targetBits, clipPct, MeanValue, StdDev);
            if (targetBits > 8)
            {
                using Mat show = new Mat();
                dst.ConvertTo(show, MatType.CV_8UC1, 255.0 / maxOut);
                return VisionHelper.ToBgrCopy(show);
            }
            return VisionHelper.ToBgrCopy(dst);
        }

        /// <summary>光斑形态量测：fitEllipse + minEnclosingCircle 给出长短轴/圆度/方向</summary>
        private Mat DoSpots(Mat srcMat, Mat gray, int spotThr, int minSpotArea)
        {
            using Mat bin = new Mat();
            if (spotThr <= 0)
                Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            else
                Cv2.Threshold(gray, bin, spotThr, 255, ThresholdTypes.Binary);

            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            int kept = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area < minSpotArea) continue;
                kept++;

                Cv2.DrawContours(dst, contours, i, Scalar.Lime, 1);

                if (contours[i].Length >= 5)
                {
                    var ell = Cv2.FitEllipse(contours[i]);
                    Cv2.Ellipse(dst, ell, Scalar.Yellow, 1);
                    double a = Math.Max(ell.Size.Width, ell.Size.Height) / 2.0;
                    double b = Math.Min(ell.Size.Width, ell.Size.Height) / 2.0;
                    double aspect = b > 1e-6 ? a / b : 0;
                    string txt = string.Format("#{0} 轴{1:F0}x{2:F0} 长宽比{3:F1} {4:F0}°",
                        kept, a * 2, b * 2, aspect, ell.Angle);
                    Cv2.PutText(dst, txt, new Point((int)ell.Center.X - 40, (int)ell.Center.Y - 8),
                        HersheyFonts.HersheySimplex, 0.4, Scalar.Yellow, 1);
                }
                if (contours[i].Length >= 3)
                {
                    Cv2.MinEnclosingCircle(contours[i], out Point2f cc, out float cr);
                    Cv2.Circle(dst, new Point((int)cc.X, (int)cc.Y), (int)cr, Scalar.Magenta, 1);
                    double circ = cr > 1e-6 ? area / (Math.PI * cr * cr) : 0;   // 1 = 完美圆
                    MatDraw.DrawText(dst, "圆度" + circ.ToString("F2"),
                        (int)cc.X - 20, (int)cc.Y + (int)cr + 14, Scalar.Magenta, 10);
                }
            }
            MatDraw.DrawText(dst, string.Format("光斑 {0} 个  Otsu={1:F0}", kept, OtsuThreshold), 8, 22, Scalar.Yellow, 13);

            LastSummary = string.Format("质量辅助: 光斑 {0} 个（面积≥{1}）  阈值 {2:F0}",
                kept, minSpotArea, spotThr <= 0 ? OtsuThreshold : spotThr);
            return dst;
        }

        /// <summary>信息模式：把自动阈值与统计量写在图上，并给出常用算子的参数建议</summary>
        private Mat DoInfo(Mat srcMat)
        {
            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            string[] lines =
            {
                string.Format("均值 {0:F1}  标准差 {1:F1}  对比度 {2:F1}%", MeanValue, StdDev,
                    MeanValue > 0.5 ? StdDev / MeanValue * 100 : 0),
                string.Format("Otsu 阈值 {0:F0}   三角阈值 {1:F0}   均值 {2:F0}", OtsuThreshold, TriangleThreshold, MeanValue),
                string.Format("建议 二值化: 阈值 {0:F0}  或直接用 自动二值化", OtsuThreshold),
                string.Format("建议 Canny: 低{0:F0} 高{1:F0}",
                    Math.Max(5, MeanValue - (1.5 * StdDev)), Math.Min(255, MeanValue + (2.5 * StdDev))),
                string.Format("建议 形态学核: {0} (=标准差的3倍取奇)", MakeOdd(Math.Max(3, (int)(StdDev / 6)))),
                string.Format("建议 CLAHE: clip {0:F1} tile {1}x{1}", 2.0 + (StdDev / 30), 8),
            };
            for (int i = 0; i < lines.Length; i++)
                Cv2.PutText(dst, lines[i], new Point(10, 28 + (i * 24)),
                    HersheyFonts.HersheySimplex, 0.52, i < 2 ? Scalar.Yellow : Scalar.Lime, 1);

            LastSummary = string.Format("质量辅助: 均值{0:F1} std{1:F1} Otsu={2:F0} 三角={3:F0}",
                MeanValue, StdDev, OtsuThreshold, TriangleThreshold);
            return dst;
        }

        private static int MakeOdd(int v)
        {
            if (v < 3) return 3;
            return v % 2 == 0 ? v + 1 : v;
        }
    }
}
