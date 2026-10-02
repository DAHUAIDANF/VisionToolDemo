using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像算术与逻辑运算：两幅图（当前图 与 参考图/常量）之间的加/减/乘/除/与/或/异或/绝对差，
    /// 以及缩放偏移（gain/offset）。
    ///
    /// 已有的"模板差分"只能做 |A−B| 一种运算，且不提供增益/偏移调整；
    /// 本算子把双图运算这条最常用的缺陷检测链路补齐：
    ///   · 相减（A−B）      —— 缺陷检测（比绝对差更能看出亮/暗方向）
    ///   · 绝对差 |A−B|     —— 无方向性的差异检测
    ///   · 除法（A/B）      —— 平场校正（消除照明不均，比相减更适合乘性误差）
    ///   · 与/或/异或       —— 掩膜合成、区域提取
    ///
    /// 关键工程点：运算默认在 **32F** 域做再加偏移抬回 8U，
    /// 直接在 8U 域算减法会在负值处饱和截断（0−10 变成 0 而不是 −10），
    /// 这是缺陷检测最常见的"看不见暗缺陷"的根因。
    /// </summary>
    public class ImageArithTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "图像算术逻辑";

        public string LastSummary { get; private set; } = "";

        /// <summary>第二操作数（参考图）；null 时用常量</summary>
        public Mat OperandMat { get; set; }

        /// <summary>结果的有效值范围（运算后、映射前）</summary>
        public double ResultMin { get; private set; } = double.NaN;
        public double ResultMax { get; private set; } = double.NaN;
        public double ResultMean { get; private set; } = double.NaN;

        /// <summary>结果中非零像素占比（%）</summary>
        public double NonZeroPercent { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "运算 0加1减2乘3除4与5或6异或7绝对差", Min = 0, Max = 7, DefaultValue = 7,
                DisplayFormat = "运算:{0}", Group = "运算", Tip = "0加 1减 2乘 3除 4与 5或 6异或 7绝对差。" +
                "缺陷检测常用 7绝对差（无方向）或 1减（能看出亮暗方向）。" +
                "平场校正用 3除（乘性误差）。掩膜合成用 4与/5或/6异或。" },
            new TaskParamDesc { ParamName = "第二操作数 0常量1图", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "操作数:{0}", Group = "运算", Tip = "0常量：用一个固定值参与运算。" +
                "1图：与参考图逐像素运算（需要先在界面上设置参考图）。" },
            new TaskParamDesc { ParamName = "常量值", Min = 0, Max = 255, DefaultValue = 128,
                DisplayFormat = "常量:{0}", Group = "运算" },
            new TaskParamDesc { ParamName = "增益x100", Min = 1, Max = 1000, DefaultValue = 100,
                DisplayFormat = "增益:{0}", Group = "后处理", Tip = "结果整体乘以该系数。100 = 1.0x。" +
                "绝对差结果通常很暗，增益 300~500 能放大缺陷便于观察。" },
            new TaskParamDesc { ParamName = "偏移", Min = -255, Max = 255, DefaultValue = 0,
                DisplayFormat = "偏移:{0}", Group = "后处理", Tip = "结果整体加上该值。" +
                "做减法/绝对差时配合偏移把结果抬到中灰，避免负值被截断成黑色。" },
            new TaskParamDesc { ParamName = "输出 0灰度1伪彩", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "输出:{0}", Group = "输出" },
        ];

        public string SaveState()
        {
            if (OperandMat == null || OperandMat.Empty()) return null;
            return VisionHelper.SaveTemplateState(OperandMat);
        }

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            OperandMat?.Dispose();
            OperandMat = m;
        }

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            ResultMin = ResultMax = ResultMean = NonZeroPercent = double.NaN;
            if (srcMat == null || srcMat.Empty()) return new Mat();

            int op = paramValues[0];
            int operandMode = paramValues[1];
            double constVal = paramValues[2];
            double gain = paramValues[3] / 100.0;
            double offset = paramValues[4];
            int colorOut = paramValues[5];

            Mat a = ToFloat01(srcMat);
            Mat b;

            if (operandMode == 1)
            {
                if (OperandMat == null || OperandMat.Empty())
                {
                    a.Dispose();
                    LastSummary = "图像算术逻辑: 未设置第二操作数图（请先设置参考图，或改用常量模式）";
                    return VisionHelper.ToBgrCopy(srcMat);
                }
                if (OperandMat.Size() != srcMat.Size())
                {
                    a.Dispose();
                    LastSummary = string.Format("图像算术逻辑: 参考图尺寸 {0}x{1} 与当前图 {2}x{3} 不一致",
                        OperandMat.Cols, OperandMat.Rows, srcMat.Cols, srcMat.Rows);
                    return VisionHelper.ToBgrCopy(srcMat);
                }
                b = ToFloat01(OperandMat);
            }
            else
            {
                b = new Mat(a.Size(), a.Type(), Scalar.All(constVal));
            }

            using (a)
            using (b)
            using (Mat res = new())
            {
                switch (op)
                {
                    case 0: Cv2.Add(a, b, res); break;
                    case 1: Cv2.Subtract(a, b, res); break;
                    case 2: Cv2.Multiply(a, b, res); break;
                    case 3:
                        // 除法要防零：把 0 抬到 1 避免 inf（平场校正时对照区常是 0）
                        using (Mat safe = new())
                        {
                            Cv2.Max(b, new Scalar(1.0), safe);
                            Cv2.Divide(a, safe, res);
                        }
                        break;
                    case 4: Cv2.BitwiseAnd(a, b, res); break;
                    case 5: Cv2.BitwiseOr(a, b, res); break;
                    case 6: Cv2.BitwiseXor(a, b, res); break;
                    default: Cv2.Absdiff(a, b, res); break;
                }

                // 增益/偏移在浮点域做，最后才截断到 8U —— 这是不丢暗缺陷的关键
                if (Math.Abs(gain - 1.0) > 1e-9) Cv2.Multiply(res, gain, res);
                if (Math.Abs(offset) > 1e-9) Cv2.Add(res, new Scalar(offset), res);

                Cv2.MinMaxLoc(res, out double mn, out double mx);
                ResultMin = mn; ResultMax = mx;
                ResultMean = Cv2.Mean(res).Val0;

                using Mat res8 = new();
                res.ConvertTo(res8, MatType.CV_8U);
                NonZeroPercent = 100.0 * Cv2.CountNonZero(res8) / (res8.Rows * (double)res8.Cols);

                Mat dst;
                if (colorOut == 1)
                {
                    dst = new Mat();
                    Cv2.ApplyColorMap(res8, dst, ColormapTypes.Jet);
                }
                else
                {
                    dst = VisionHelper.ToBgrCopy(res8);
                }

                string opName = op switch
                {
                    0 => "加", 1 => "减", 2 => "乘", 3 => "除",
                    4 => "与", 5 => "或", 6 => "异或", _ => "绝对差",
                };
                Cv2.PutText(dst, string.Format("{0}  gain{1:F2} off{2:F0}", opName, gain, offset),
                    new Point(6, 20), HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1, LineTypes.AntiAlias);

                LastSummary = string.Format(
                    "图像算术逻辑: {0} (操作数 {1}) 结果范围 {2:F2}~{3:F2}, 均值 {4:F2}, 非零 {5:F2}%, gain {6:F2} off {7:F0}",
                    opName, operandMode == 1 ? "参考图" : constVal.ToString("F0"),
                    mn, mx, ResultMean, NonZeroPercent, gain, offset);
                return dst;
            }
        }

        /// <summary>
        /// 统一转成 32F 灰度（0~255 量程）。
        /// 必须在 32F 域运算：8U 域做减法会在负值处饱和到 0，
        /// 暗缺陷（比参考暗的区域）会整片丢失。
        /// </summary>
        private static Mat ToFloat01(Mat src)
        {
            using Mat gray = VisionHelper.ToGray(src);
            Mat f = new();
            gray.ConvertTo(f, MatType.CV_32F);
            return f;
        }
    }
}
