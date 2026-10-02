using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// DPM 二维码识别：专用于**工件表面直接打点/刻蚀**的 Data Matrix（DPM = Direct Part Marking）。
    ///
    /// 为什么需要独立算子（而不是用"条码/二维码识别"）：
    ///   · 通用识别流程（QRCodeDetector / ZXing）针对**印刷**的清晰码设计，
    ///     对金属打点、激光刻蚀、低对比、弧面反光的码几乎必失败；
    ///   · BarcodeTask 里虽然内置了点阵引擎，但它排在 QR/1D 之后作为**兜底**，
    ///     识别失败时用户看到的是"条码/二维码识别: 未识别到"，无法判断
    ///     是码没拍到、还是点阵参数不合适；
    ///   · 本算子把点阵引擎单独暴露，并提供**只属于 DPM 的参数**
    ///     （判暗阈值、锐度下限、最小节距、搜索预算）和**诊断输出**，
    ///     让"为什么这个码没读出来"变得可排查。
    ///
    /// 引擎实现见 DotMatrix.TryDecode：极性与核尺寸多档 × 阈值多档的双向扫描，
    /// 配合网格拟合 + ECC200 边框校验 + RS 纠错。
    /// </summary>
    public class DpmCodeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "DPM二维码";

        public string LastSummary { get; private set; } = "";

        /// <summary>是否成功解码</summary>
        public bool Decoded { get; private set; }

        /// <summary>解码文本</summary>
        public string Text { get; private set; } = "";

        /// <summary>符号四角（整图坐标，顺序与 DotMatrix 返回一致）</summary>
        public Point2f[] Corners { get; private set; } = [];

        /// <summary>符号中心</summary>
        public Point2f Center { get; private set; }

        /// <summary>符号外接矩形边长（px，估算的符号大小）</summary>
        public double SizePx { get; private set; } = double.NaN;

        /// <summary>识别耗时（ms）—— DPM 调试时最关心的量之一</summary>
        public long ElapsedMs { get; private set; }

        /// <summary>最近一次失败的原因链（各档位的失败摘要），供排障显示</summary>
        public string Diagnostic { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "判暗阈值偏置%", Min = 10, Max = 80, DefaultValue = 30,
                DisplayFormat = "判暗:{0}%", Group = "点阵",
                Tip = "打点模块判为“暗”的方窗白色占比阈值。" +
                "30 = 直接采用格点级 Otsu 门限。调低 = 门限降低 = 更容易判暗，" +
                "适合**低对比**的浅打点（激光浅刻、磨砂面）；调高更严，适合高对比。" +
                "先试 20~25，若仍读不出再试 40 以上。" },
            new TaskParamDesc { ParamName = "锐度下限%", Min = 50, Max = 150, DefaultValue = 90,
                DisplayFormat = "锐度:{0}%", Group = "点阵",
                Tip = "网格成立的圆均值锐度下限（两轴之和）。" +
                "90 = 默认 0.9。**畸变/斜拍**导致格点被拉偏时调低到 70~80 放宽判定；" +
                "调到 50 以下会把噪声纹理也当成网格，误检率上升。" },
            new TaskParamDesc { ParamName = "最小节距px", Min = 2, Max = 100, DefaultValue = 5,
                DisplayFormat = "节距>={0}px", Group = "点阵",
                Tip = "点与点的间距小于此值即判为非点阵纹理。" +
                "**高分辨率拍小码**时点距可能只有 4~6px，若报“无码”可试降到 3~4；" +
                "调太低会把金属纹理/砂纹误认成点阵，耗时也会显著上升。" },
            new TaskParamDesc { ParamName = "搜索预算ms", Min = 500, Max = 60000, DefaultValue = 5000,
                DisplayFormat = "预算:{0}ms", Group = "性能",
                Tip = "单次识别的墙钟上限，防止无码图把多档穷举跑满（实测可到分钟级）。" +
                "**调大**能救回难码但界面等待变长（本算子已在后台线程执行，不会卡界面）；" +
                "**调小**适合流水线节拍优先的场景。" },
            new TaskParamDesc { ParamName = "显示 0框1诊断", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "显示:{0}", Group = "输出",
                Tip = "0 = 在原图上画识别框。1 = 叠加失败原因链（排障用），" +
                "内容同时写入结果摘要。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Decoded = false;
            Text = "";
            Corners = [];
            Center = default;
            SizePx = double.NaN;
            ElapsedMs = 0;
            Diagnostic = "";

            // 先校验输入再构造输出图：反过来的话，传进来的 Mat 已被释放时
            // 会在 ToBgrCopy 的 Empty() 上抛 ObjectDisposedException，
            // 而不是走"输入为空"的友好提示。
            if (srcMat == null || srcMat.IsDisposed || srcMat.Empty())
            {
                LastSummary = "DPM二维码: 输入为空";
                return new Mat();
            }

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int darkRatio = Math.Clamp(paramValues[0], 10, 80);
            int sharpness = Math.Clamp(paramValues[1], 50, 150);
            int minPitch = Math.Clamp(paramValues[2], 2, 100);
            int budget = Math.Clamp(paramValues[3], 500, 60000);
            int display = paramValues[4];

            // 点阵引擎只吃 8U 单通道。16 位 RAW 必须归一化，
            // 否则会直接抛 "src.type() == CV_8UC1"。
            using Mat gray = VisionHelper.ToGray(srcMat);

            // 收集引擎内部的打点日志：失败时拼成原因链，让"为什么没读出"可排查
            var logs = new List<string>();
            var opt = new DotMatrix.Options
            {
                DarkRatioPercent = darkRatio,
                MinSharpnessPercent = sharpness,
                MinPitch = minPitch,
                SearchBudgetOverrideMs = budget,
                DebugLog = s =>
                {
                    // 引擎会打很多条，只留前若干条避免摘要过长
                    if (logs.Count < 12) logs.Add(s);
                },
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            string text;
            Point2f[] quad;
            try
            {
                ok = DotMatrix.TryDecode(gray, opt, out text, out quad);
            }
            catch (Exception ex)
            {
                sw.Stop();
                ElapsedMs = sw.ElapsedMilliseconds;
                Diagnostic = "点阵引擎内部异常: " + ex.Message;
                LastSummary = "DPM二维码: 引擎异常 — " + ex.Message;
                Cv2.PutText(dst, "DPM: engine error", new Point(8, 24),
                    HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 1, LineTypes.AntiAlias);
                return dst;
            }
            sw.Stop();
            ElapsedMs = sw.ElapsedMilliseconds;

            Decoded = ok;

            if (ok && !string.IsNullOrEmpty(text))
            {
                Text = text;
                Corners = quad ?? [];
                if (Corners.Length >= 4)
                {
                    double cx = 0, cy = 0;
                    foreach (Point2f p in Corners) { cx += p.X; cy += p.Y; }
                    Center = new Point2f((float)(cx / Corners.Length), (float)(cy / Corners.Length));

                    // 符号边长取四角相邻距离的平均，便于判断"码占多大"
                    double sum = 0;
                    int n = 0;
                    for (int i = 0; i < Corners.Length; i++)
                    {
                        Point2f a = Corners[i], b = Corners[(i + 1) % Corners.Length];
                        sum += Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
                        n++;
                    }
                    if (n > 0) SizePx = sum / n;

                    // 画识别框（四角连线 + 角点标记）
                    var pts = new Point[Corners.Length];
                    for (int i = 0; i < Corners.Length; i++)
                        pts[i] = new Point((int)Math.Round(Corners[i].X), (int)Math.Round(Corners[i].Y));
                    Cv2.Polylines(dst, new[] { pts }, true, Scalar.LimeGreen, 2, LineTypes.AntiAlias);
                    foreach (Point p in pts)
                        Cv2.Circle(dst, p, 4, Scalar.Red, -1, LineTypes.AntiAlias);
                    Cv2.Circle(dst, new Point((int)Math.Round(Center.X), (int)Math.Round(Center.Y)),
                        3, Scalar.Yellow, -1, LineTypes.AntiAlias);
                }

                MatDraw.DrawText(dst, "DPM: " + Text, 8, 24, Scalar.LimeGreen, 16);
                Cv2.PutText(dst, $"{(SizePx > 0 ? SizePx.ToString("F0") + "px  " : "")}{ElapsedMs}ms",
                    new Point(8, 46), HersheyFonts.HersheySimplex, 0.45, Scalar.Yellow, 1, LineTypes.AntiAlias);

                LastSummary = string.Format("DPM二维码: \"{0}\"  ({1}ms{2})",
                    Text, ElapsedMs, SizePx > 0 ? string.Format(", 边长 {0:F0}px", SizePx) : "");
                return dst;
            }

            // —— 失败：给出可操作的原因链 ——
            Diagnostic = logs.Count > 0 ? string.Join(" | ", logs) : "(无引擎日志)";

            MatDraw.DrawText(dst, "DPM: 未识别到点阵码", 8, 24, Scalar.Red, 16);
            MatDraw.DrawText(dst, string.Format("判暗{0}%  锐度{1}%  节距>={2}px  {3}ms",
                darkRatio, sharpness, minPitch, ElapsedMs), 8, 46, Scalar.Yellow, 12);
            MatDraw.DrawText(dst, "建议: 先框选码区(ROI)再运行 / 调低判暗阈值或最小节距",
                8, 68, Scalar.Orange, 12);

            if (display == 1 && logs.Count > 0)
            {
                for (int i = 0; i < logs.Count && i < 8; i++)
                    Cv2.PutText(dst, logs[i], new Point(8, 94 + (i * 18)),
                        HersheyFonts.HersheySimplex, 0.4, Scalar.Gray, 1, LineTypes.AntiAlias);
            }

            LastSummary = string.Format(
                "DPM二维码: 未识别到（判暗{0}% 锐度{1}% 节距>={2}px, {3}ms）。" +
                "建议框选码区后重跑，或调低判暗阈值/最小节距。引擎日志: {4}",
                darkRatio, sharpness, minPitch, ElapsedMs,
                logs.Count > 0 ? logs[0] : "无");
            return dst;
        }
    }
}
