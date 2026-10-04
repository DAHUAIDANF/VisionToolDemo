using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 频域滤波（FFT）：低通 / 高通 / 带通 / 陷波。
    ///
    /// 原理：把图像 DFT 到频域，用圆形掩膜（可带平滑过渡带宽）乘频谱，
    /// 再反变换回空间域。适合处理**周期性纹理**（网纹、摩尔纹、织物纹理）：
    ///   · 低通 —— 平滑去周期噪点，保留整体；
    ///   · 高通 —— 只留边缘/细节，去掉低频光照不均（阴影）；
    ///   · 带通 —— 只提取某频段的周期纹理（缺陷定位）；
    ///   · 陷波 —— 挖掉特定频段（去掉网纹/摩尔纹，其它频率不动）。
    ///
    /// 与 DftSpectrumTask（只看频谱图）不同：这里输出的是**滤波还原后的图像**。
    /// </summary>
    public class FftFilterTask : IVisionTask, IResultReporter
    {
        public string TaskName => "频域滤波FFT";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "滤波类型 0低通1高通2带通3陷波",
                Min = 0,
                Max = 3,
                DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=低通(平滑去周期噪点) 1=高通(去光照不均/只留边缘) 2=带通(提取某频段周期纹理) 3=陷波(挖掉网纹/摩尔纹频段)"
            },
            new TaskParamDesc
            {
                ParamName = "中心频率%",
                Min = 1,
                Max = 98,
                DefaultValue = 40,
                DisplayFormat = "中心:{0}%",
                Tip = "掩膜半径中心，相对图像短边一半的百分比。低通=截止半径；高通=起始半径；带通/陷波=频段中心"
            },
            new TaskParamDesc
            {
                ParamName = "带宽%",
                Min = 0,
                Max = 98,
                DefaultValue = 15,
                DisplayFormat = "带宽:{0}%",
                Tip = "带通/陷波的频段宽度，也是低通/高通边缘的平滑过渡宽度（0=硬截止）。相对图像短边一半的百分比"
            },
            new TaskParamDesc
            {
                ParamName = "输出图 0滤波1频谱",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "输出:{0}",
                Tip = "0=滤波还原后的图像（默认）1=频域幅度谱（对数缩放，调试用）"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int type = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 0, 0, 3);
            int centerPct = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 40, 1, 98);
            int bandPct = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 15, 0, 98);
            int output = Math.Clamp(paramValues.Length > 3 ? paramValues[3] : 0, 0, 1);

            // 灰度化 → float
            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat f = new();
            gray.ConvertTo(f, MatType.CV_32FC1);

            // 补零到偶数尺寸（DFT 对偶数尺寸更快且避免边界伪影）
            int rows = Cv2.GetOptimalDFTSize(f.Rows);
            int cols = Cv2.GetOptimalDFTSize(f.Cols);
            using Mat padded = new();
            Cv2.CopyMakeBorder(f, padded, 0, rows - f.Rows, 0, cols - f.Cols, BorderTypes.Constant, Scalar.All(0));

            // 合并成双通道复数（虚部 0）→ 正向 DFT
            using Mat complex = new();
            Cv2.Merge(new[] { padded, padded.Clone() }, complex);
            Cv2.Dft(complex, complex, DftFlags.ComplexOutput);

            // 拆实部/虚部 → 移到中心（DC 在中心，方便用半径掩膜）
            using Mat planes0 = new();
            using Mat planes1 = new();
            Cv2.Split(complex, out Mat[] planes);
            ShiftQuadrants(planes[0]);
            ShiftQuadrants(planes[1]);
            planes[0].CopyTo(planes0);
            planes[1].CopyTo(planes1);
            foreach (var p in planes) p.Dispose();

            // 构建圆形掩膜（带线性过渡带）
            using Mat mask = BuildMask(rows, cols, centerPct, bandPct, type);

            if (output == 1)
            {
                // 频谱幅度谱：log(1+sqrt(R^2+I^2))，归一化显示
                using Mat mag = new();
                Cv2.Magnitude(planes0, planes1, mag);
                using Mat log = new();
                Cv2.Add(mag, Scalar.All(1), mag);   // log(1+mag) 防 log(0)
                Cv2.Log(mag, log);
                using Mat norm = new();
                Cv2.Normalize(log, norm, 0, 255, NormTypes.MinMax);
                Mat gray8 = new();
                norm.ConvertTo(gray8, MatType.CV_8UC1);
                LastSummary = string.Format(
                    "频域滤波FFT: 频谱图 {0}x{1}（中心频率 {2}% 带宽 {3}%）", rows, cols, centerPct, bandPct);
                return gray8;
            }

            // 频谱 × 掩膜 → 反变换（DC 移回角落 → 逆 DFT 取实部）
            using Mat r2 = new();
            using Mat i2 = new();
            Cv2.Multiply(planes0, mask, r2);
            Cv2.Multiply(planes1, mask, i2);
            ShiftQuadrants(r2);
            ShiftQuadrants(i2);

            using Mat filtered = new();
            Cv2.Merge(new[] { r2, i2 }, filtered);
            using Mat outComplex = new();
            Cv2.Dft(filtered, outComplex, DftFlags.Inverse | DftFlags.ComplexOutput | DftFlags.Scale);
            using Mat outPlane = new();
            Cv2.Split(outComplex, out Mat[] op);
            op[0].CopyTo(outPlane);
            foreach (var p in op) p.Dispose();

            // 裁回原尺寸并转 8U
            using Mat roi = new(outPlane, new Rect(0, 0, srcMat.Cols, srcMat.Rows));
            using Mat normalized = new();
            Cv2.Normalize(roi, normalized, 0, 255, NormTypes.MinMax);
            Mat result = new();
            normalized.ConvertTo(result, MatType.CV_8UC1);
            if (srcMat.Channels() == 3)
            {
                Mat bgr = new();
                Cv2.CvtColor(result, bgr, ColorConversionCodes.GRAY2BGR);
                result.Dispose();
                result = bgr;
            }

            string[] names = { "低通", "高通", "带通", "陷波" };
            LastSummary = string.Format(
                "频域滤波FFT: {0}（中心 {1}% 带宽 {2}%）→ {3}x{4}",
                names[type], centerPct, bandPct, result.Cols, result.Rows);
            return result;
        }

        /// <summary>把频谱四象限对角交换，使 DC 分量位于中心（正变换后调用；逆变换前再调一次还原）</summary>
        private static void ShiftQuadrants(Mat m)
        {
            int cx = m.Cols / 2, cy = m.Rows / 2;
            using Mat tmp = new();
            m.CopyTo(tmp);
            // 1↔4 象限、2↔3 象限对角交换
            using (Mat q1 = new(tmp, new Rect(0, 0, cx, cy)))
            using (Mat q4 = new(tmp, new Rect(cx, cy, m.Cols - cx, m.Rows - cy)))
            using (Mat r1 = new(m, new Rect(cx, cy, m.Cols - cx, m.Rows - cy)))
            using (Mat r4 = new(m, new Rect(0, 0, cx, cy)))
            {
                q1.CopyTo(r1);
                q4.CopyTo(r4);
            }
            using (Mat q2 = new(tmp, new Rect(cx, 0, m.Cols - cx, cy)))
            using (Mat q3 = new(tmp, new Rect(0, cy, cx, m.Rows - cy)))
            using (Mat r2 = new(m, new Rect(0, cy, cx, m.Rows - cy)))
            using (Mat r3 = new(m, new Rect(cx, 0, m.Cols - cx, cy)))
            {
                q2.CopyTo(r3);
                q3.CopyTo(r2);
            }
        }

        /// <summary>按类型构建 0~1 的圆形掩膜（带线性过渡带），DC 在中心</summary>
        private static Mat BuildMask(int rows, int cols, int centerPct, int bandPct, int type)
        {
            Mat mask = new Mat(rows, cols, MatType.CV_32FC1, Scalar.All(1));
            float half = Math.Min(rows, cols) / 2f;                       // 短边一半（像素）
            float r0 = Math.Max(1f, half * centerPct / 100f);             // 主半径（像素）
            float bw = Math.Max(0f, half * bandPct / 100f);               // 带宽（像素）
            float cx = cols / 2f, cy = rows / 2f;

            // 用 At/Set 访问（OpenCvSharp 4.10 Mat 像素接口）
            for (int y = 0; y < rows; y++)
            {
                float dy = y - cy;
                for (int x = 0; x < cols; x++)
                {
                    float dx = x - cx;
                    float r = (float)Math.Sqrt(dx * dx + dy * dy);
                    float v;
                    switch (type)
                    {
                        case 1:   // 高通：r>=r0 通过，边缘线性过渡
                            v = r <= r0 - bw ? 0f : (r >= r0 + bw || bw < 0.5f ? 1f : (r - (r0 - bw)) / (2 * bw));
                            break;
                        case 2:   // 带通：|r - r0| <= bw 通过
                            {
                                float d = Math.Abs(r - r0);
                                v = d <= bw ? 1f : 0f;
                                break;
                            }
                        case 3:   // 陷波：|r - r0| <= bw 挖掉，其余通过
                            {
                                float d = Math.Abs(r - r0);
                                v = d <= bw ? 0f : 1f;
                                break;
                            }
                        default:  // 低通：r<=r0 通过，边缘线性过渡
                            v = r >= r0 + bw ? 0f : (r <= r0 - bw || bw < 0.5f ? 1f : (r0 + bw - r) / (2 * bw));
                            break;
                    }
                    mask.Set<float>(y, x, v);
                }
            }
            return mask;
        }
    }
}
