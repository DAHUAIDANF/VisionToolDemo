using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 混合模式算子（PS 图层混合核心）：把一张"前景/叠加图"按指定的混合模式
    /// 与当前图（底图）混合，支持前景不透明度。
    /// 前景图复用"模板/参考图"通道：先在参数面板点「导入模板图…」载入前景图，
    /// 或用图上选区当前景；未导入前景时返回原图并给出提示。
    /// 参数：模式（0=正片叠底 1=滤色 2=叠加 3=柔光 4=强光 5=变暗 6=变亮
    /// 7=差值 8=排除 9=颜色加深 10=颜色减淡）、前景不透明度（%）、前景尺寸适应
    /// （0=拉伸铺满 1=等比缩放居中）。
    /// </summary>
    public class BlendModeTask : IVisionTask, IResultReporter
    {
        /// <summary>UI 注入的前景图（复用模板行）</summary>
        public Mat TemplateMat { get; set; }

        /// <summary>最近一次执行摘要（界面标签/自动化可用）</summary>
        public string LastSummary { get; private set; } = "";

        public string TaskName => "混合模式";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "模式", Min = 0, Max = 10, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=正片叠底 1=滤色 2=叠加 3=柔光 4=强光 5=变暗 6=变亮 7=差值 8=排除 9=颜色加深 10=颜色减淡"
            },
            new TaskParamDesc
            {
                ParamName = "不透明度", Min = 0, Max = 100, DefaultValue = 100,
                DisplayFormat = "不透明度:{0}%",
                Tip = "前景参与混合的比例：100=完全覆盖，0=完全不可见"
            },
            new TaskParamDesc
            {
                ParamName = "前景适应", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "前景:{0}",
                Tip = "0=拉伸铺满整张图 1=等比缩放居中（不变形）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int mode = paramValues[0];
            int opacity = paramValues[1];
            int fit = paramValues[2];

            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "混合模式: 请先导入前景图（参数面板「导入模板图…」）";
                return srcMat.Clone();
            }

            // 前景缩放到与原图同尺寸（等比模式：保持比例居中，透明区域用黑色填充）
            Mat fg = FitForeground(srcMat, TemplateMat, fit);

            // 转 32F（0~1）逐元素计算，避免 8U 中间结果溢出/截断
            using Mat a = ToFloat01(srcMat);
            using Mat b = ToFloat01(fg);
            Mat blended = Blend(a, b, mode);

            // 按不透明度合成：result = 底图*(1-t) + 混合结果*t
            double t = opacity / 100.0;
            Mat outF = new();
            Cv2.AddWeighted(blended, t, a, 1.0 - t, 0, outF);

            Mat dst = new();
            outF.ConvertTo(dst, MatType.CV_8UC3, 255.0, 0);
            LastSummary = string.Format("混合模式: 模式{0} 不透明度{1}%{2}",
                mode, opacity, fit == 1 ? "（等比居中）" : "");
            return dst;
        }

        /// <summary>把前景图调整到与原图同尺寸；返回 null 表示无前景图可用</summary>
        private static Mat FitForeground(Mat src, Mat fg, int fit)
        {
            if (fg == null || fg.Empty()) return null;
            Mat f = new();
            if (fg.Cols == src.Cols && fg.Rows == src.Rows)
                return fg.Clone();
            if (fit == 1)
            {
                // 等比缩放居中：按较长边缩放到铺满后裁剪（保持比例不变形）
                double scale = Math.Max((double)src.Cols / fg.Cols, (double)src.Rows / fg.Rows);
                int nw = (int)Math.Round(fg.Cols * scale), nh = (int)Math.Round(fg.Rows * scale);
                using Mat scaled = new();
                Cv2.Resize(fg, scaled, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Cubic);
                int ox = (src.Cols - nw) / 2, oy = (src.Rows - nh) / 2;
                using Mat canvas = Mat.Zeros(src.Rows, src.Cols, MatType.CV_8UC3);
                var roi = new OpenCvSharp.Rect(Math.Max(0, ox), Math.Max(0, oy), Math.Min(nw, src.Cols), Math.Min(nh, src.Rows));
                using Mat sub = new(scaled, new OpenCvSharp.Rect(
                    Math.Max(0, -ox), Math.Max(0, -oy), roi.Width, roi.Height));
                sub.CopyTo(new Mat(canvas, roi));
                return canvas.Clone();
            }
            Cv2.Resize(fg, f, new OpenCvSharp.Size(src.Cols, src.Rows), 0, 0, InterpolationFlags.Cubic);
            return f;
        }

        /// <summary>8U BGR → 32F BGR（0~1）</summary>
        private static Mat ToFloat01(Mat m)
        {
            Mat f = new();
            m.ConvertTo(f, MatType.CV_32FC3, 1.0 / 255.0, 0);
            return f;
        }

        /// <summary>两个 0~1 浮点图按模式逐元素混合（PS 标准混合公式）</summary>
        private static Mat Blend(Mat a, Mat b, int mode)
        {
            Mat r;
            switch (mode)
            {
                case 0: // 正片叠底：a*b
                    r = a.Mul(b);
                    break;
                case 1: // 滤色：1-(1-a)(1-b)
                    r = OneMinus(Mul(OneMinus(a), OneMinus(b)));
                    break;
                case 2: // 叠加：a<=0.5 ? 2ab : 1-2(1-a)(1-b)
                    r = SelectLE(a, 0.5, Mul(Scale(a, 2), b), OneMinus(Mul(Scale(OneMinus(a), 2), OneMinus(b))));
                    break;
                case 3: // 柔光（简化标准公式）
                    r = SoftLight(a, b);
                    break;
                case 4: // 强光：b<=0.5 ? 2ab : 1-2(1-a)(1-b)
                    r = SelectLE(b, 0.5, Mul(Scale(a, 2), b), OneMinus(Mul(Scale(OneMinus(a), 2), OneMinus(b))));
                    break;
                case 5: // 变暗
                    r = Min(a, b);
                    break;
                case 6: // 变亮
                    r = Max(a, b);
                    break;
                case 7: // 差值
                    r = AbsDiff(a, b);
                    break;
                case 8: // 排除：a+b-2ab
                    r = Sub(Add(a, b), Mul(Scale(a, 2), b));
                    break;
                case 9: // 颜色加深：a==0 ? 0 : 1-(1-b)/a
                    r = ColorBurn(a, b);
                    break;
                case 10: // 颜色减淡：a==1 ? 1 : b/(1-a)
                    r = ColorDodge(a, b);
                    break;
                default:
                    r = a.Clone();
                    break;
            }
            return r;
        }

        // —— 混合公式的 Mat 辅助（OpenCvSharp Mat 逐元素运算；所有输出显式传 dst）——
        private static Mat OneMinus(Mat m) { Mat r = new(); Cv2.Subtract(new Scalar(1), m, r); return r; }
        private static Mat Scale(Mat m, double s) { Mat r = new(); Cv2.Multiply(m, s, r); return r; }
        private static Mat Add(Mat a, Mat b) { Mat r = new(); Cv2.Add(a, b, r); return r; }
        private static Mat Sub(Mat a, Mat b) { Mat r = new(); Cv2.Subtract(a, b, r); return r; }
        private static Mat Mul(Mat a, Mat b) { Mat r = new(); Cv2.Multiply(a, b, r); return r; }
        private static Mat Min(Mat a, Mat b) { Mat r = new(); Cv2.Min(a, b, r); return r; }
        private static Mat Max(Mat a, Mat b) { Mat r = new(); Cv2.Max(a, b, r); return r; }
        private static Mat AbsDiff(Mat a, Mat b) { Mat r = new(); Cv2.Absdiff(a, b, r); return r; }

        /// <summary>按 a&lt;=th 选择 t 否则 f（阈值生成 0/1 掩码，转 32F 加权混合）</summary>
        private static Mat SelectLE(Mat a, double th, Mat t, Mat f)
        {
            using Mat mask = new();
            Cv2.Threshold(a, mask, th, 1.0, ThresholdTypes.BinaryInv); // a<=th → 1
            return MaskMix(mask, t, f);
        }

        /// <summary>柔光：软光混合（近似标准公式）</summary>
        private static Mat SoftLight(Mat a, Mat b)
        {
            return SelectLE(a, 0.5, Mul(b, Scale(a, 2)), OneMinus(Mul(OneMinus(b), Scale(OneMinus(a), 2))));
        }

        /// <summary>颜色加深：a==0 ? 0 : 1-(1-b)/a（除法逐元素，防除零）</summary>
        private static Mat ColorBurn(Mat a, Mat b)
        {
            using Mat nb = OneMinus(b);
            using Mat safe = MaxEps(a);
            Mat ratio = new();
            Cv2.Divide(nb, safe, ratio);
            using Mat ratio1 = OneMinus(ratio);
            return MaskZero(a, ratio1);
        }

        /// <summary>颜色减淡：a==1 ? 1 : b/(1-a)（除法逐元素，防除零）</summary>
        private static Mat ColorDodge(Mat a, Mat b)
        {
            using Mat na = OneMinus(a);
            using Mat safe = MaxEps(na);
            Mat ratio = new();
            Cv2.Divide(b, safe, ratio);
            return MaskOne(a, ratio);
        }

        /// <summary>防止除零：把 0 抬到极小值（接近 0，只影响边界像素的极端情况）</summary>
        private static Mat MaxEps(Mat m)
        {
            Mat r = new();
            Cv2.Max(m, new Scalar(1e-4), r);
            return r;
        }

        /// <summary>把 0/1 掩码（8U）转 32F 后做加权选择 mask*t + (1-mask)*f</summary>
        private static Mat MaskMix(Mat mask8, Mat t, Mat f)
        {
            using Mat maskf = new();
            mask8.ConvertTo(maskf, MatType.CV_32F);
            return Add(Mul(t, maskf), Mul(f, OneMinus(maskf)));
        }

        /// <summary>a 接近 0 处取 f 的补（颜色加深边界：a=0 → 结果=黑即 ratio 补）</summary>
        private static Mat MaskZero(Mat a, Mat f)
        {
            using Mat mask = new();
            Cv2.Threshold(a, mask, 1e-3, 1.0, ThresholdTypes.BinaryInv); // a<=1e-3 → 1
            return MaskMix(mask, f, f); // 边界与正常都取 f（1-(1-b)/a 在 a→0 时趋于 -inf→钳到黑）
        }

        /// <summary>a 接近 1 处取 1（颜色减淡边界：a=1 → 结果=1）</summary>
        private static Mat MaskOne(Mat a, Mat f)
        {
            using Mat mask = new();
            Cv2.Threshold(a, mask, 1.0 - 1e-3, 1.0, ThresholdTypes.Binary); // a>=1-1e-3 → 1
            return MaskMix(mask, Mat.Ones(a.Rows, a.Cols, MatType.CV_32FC3), f);
        }
    }
}
