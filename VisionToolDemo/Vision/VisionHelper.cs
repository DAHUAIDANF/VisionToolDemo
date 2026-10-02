using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    public static class VisionHelper
    {
        /// <summary>模板图序列化为 base64 PNG（IStatefulTask 保存模板用）；空模板返回 null</summary>
        public static string SaveTemplateState(Mat tpl)
        {
            if (tpl?.Empty() != false)
                return null;
            return Convert.ToBase64String(tpl.ImEncode(".png"));
        }

        /// <summary>从 SaveTemplateState 的文本恢复模板图；空/损坏返回 null</summary>
        public static Mat LoadTemplateState(string state)
        {
            if (string.IsNullOrEmpty(state))
                return null;
            try
            {
                Mat m = Cv2.ImDecode(Convert.FromBase64String(state), ImreadModes.Color);
                return m.Empty() ? null : m;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断是否需要做位深归一化。
        /// OpenCV 绝大多数算子（Threshold / Canny / FindContours / HoughCircles / CLAHE /
        /// DistanceTransform / BlobDetector…）**只接受 8U**，而本项目的 RAW 导入
        /// 在 16 位文件时会产出 CV_16UC1 / CV_16UC3。
        /// 不归一化就会在算子内部抛 OpenCVException —— 实测 98 个算子里有 49 个会崩，
        /// 这正是"导入 16 位 RAW 后一点算子程序就挂"的根因。
        /// </summary>
        public static bool NeedsDepthNormalize(Mat src)
            => src != null && !src.Empty() && src.Depth() != MatType.CV_8U;

        /// <summary>
        /// 把任意位深的图像归一化到 8U：按**实际数据范围**线性拉伸。
        ///
        /// 为什么不是简单右移或截断：
        ///   · 右移（>>8）假定数据已用满 16 位。相机 RAW 常把 12/14 位数据放在
        ///     16 位容器的高位或低位，直接移位会得到全黑或全白的图；
        ///   · 截断（ConvertTo 8U）会把 >255 的一切压成 255，高光全糊成一片。
        /// 按 MinMax 拉伸对"数据占满多少位"不敏感，任何位深都能得到可视结果。
        ///
        /// 浮点图先取绝对值再拉伸 —— 有些算子（如差分）会产出带负值的 CV_32F，
        /// 直接 MinMax 会因负值被 |.| 处理而产生翻转，先取绝对值更符合直觉。
        /// </summary>
        public static Mat NormalizeDepth8U(Mat src)
        {
            if (src == null || src.Empty()) return src?.Clone();

            Mat work = src;
            bool ownWork = false;

            if (src.Depth() == MatType.CV_32F || src.Depth() == MatType.CV_64F)
            {
                // 浮点：可能有负值（差分/卷积结果），取绝对值避免拉伸后翻负
                double mn, mx;
                Cv2.MinMaxLoc(src, out mn, out mx);
                if (mn < 0)
                {
                    // OpenCvSharp 的 Cv2.Abs 只有单参重载（注意不是 C 接口的 Abs(src,dst)）
                    work = Cv2.Abs(src);
                    ownWork = true;
                }
            }

            try
            {
                using Mat f = new();
                work.ConvertTo(f, MatType.CV_32F);

                Cv2.MinMaxLoc(f, out double lo, out double hi);
                if (!double.IsFinite(lo) || !double.IsFinite(hi)) return src.Clone();
                if (hi - lo < 1e-12)
                {
                    // 常数图：拉伸无意义（除零会得到 NaN），统一给中性灰。
                    // 早期这里写的是 Scalar(0,0,0,0)（纯黑），与注释不符：
                    // 一张平坦的 16 位 RAW 会被显示成全黑，看起来像"读取失败"，
                    // 而中灰至少能让用户看出"这是一张有效的均匀图"。
                    var flat = new Mat(src.Size(), MatType.CV_8UC(src.Channels()),
                        Scalar.All(128));
                    return flat;
                }

                double alpha = 255.0 / (hi - lo);
                using Mat shifted = new();
                Cv2.Subtract(f, new Scalar(lo, lo, lo, lo), shifted);
                using Mat scaled = new();
                Cv2.Multiply(shifted, alpha, scaled);

                var dst = new Mat();
                scaled.ConvertTo(dst, MatType.CV_8UC(src.Channels()));
                return dst;
            }
            finally
            {
                if (ownWork) work.Dispose();
            }
        }

        /// <summary>
        /// 将图像转为**8U 单通道**灰度图。
        ///
        /// 这是全项目统一入口，因此把"位深归一化"放在这里做一次，
        /// 而不是让 98 个算子各自判断 —— 那样必然会漏，
        /// 事实也证明漏掉了 49 个。
        /// </summary>
        public static Mat ToGray(Mat src)
        {
            Mat u8 = ToU8(src);

            if (u8.Channels() == 3)
            {
                Mat gray = new();
                Cv2.CvtColor(u8, gray, ColorConversionCodes.BGR2GRAY);
                u8.Dispose();
                return gray;
            }
            if (u8.Channels() == 4)
            {
                Mat gray = new();
                Cv2.CvtColor(u8, gray, ColorConversionCodes.BGRA2GRAY);
                u8.Dispose();
                return gray;
            }
            return u8;
        }

        /// <summary>
        /// 确保图像是 **8U**（保持原通道数）。
        /// 已是 8U 时直接 Clone，不做无谓拷贝之外的处理。
        /// </summary>
        public static Mat ToU8(Mat src)
        {
            if (src == null || src.Empty()) return new Mat();
            if (src.Depth() == MatType.CV_8U)
                return src.Clone();
            return NormalizeDepth8U(src);
        }

        /// <summary>确保输出图是 3-channel BGR 8U 格式的副本（用于绘制彩色结果）</summary>
        public static Mat ToBgrCopy(Mat src)
        {
            if (src == null || src.Empty()) return new Mat();

            Mat u8 = ToU8(src);

            if (u8.Channels() == 3)
                return u8;

            Mat dst = new();
            if (u8.Channels() == 1)
                Cv2.CvtColor(u8, dst, ColorConversionCodes.GRAY2BGR);
            else if (u8.Channels() == 4)
                Cv2.CvtColor(u8, dst, ColorConversionCodes.BGRA2BGR);
            else
                Cv2.CvtColor(u8, dst, ColorConversionCodes.GRAY2BGR);
            u8.Dispose();
            return dst;
        }
    }
}
