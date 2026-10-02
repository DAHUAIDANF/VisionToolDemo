using OpenCvSharp;
using System.Linq;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 算子级 ROI 公共支持：给"希望只处理图上某一框选区域"的算子（二值化/自动二值化/自适应阈值）
    /// 追加 5 个参数（启用 / X / Y / 宽 / 高）。执行时只在框内做处理，框外保持原图不变。
    /// 与链级 ROI（勾选"用选区做 ROI"整链跑在选区内）互补：这个只作用于当前这一个算子。
    /// 旧链兼容：老保存的算子链参数数组短，缺 ROI 参数时 PadValues 用默认值补足 → 自动整图处理。
    /// </summary>
    public static class RoiRegion
    {
        /// <summary>追加到算子参数末尾的 5 个 ROI 参数描述</summary>
        public static TaskParamDesc[] ParamDescs() => new[]
        {
            new TaskParamDesc
            {
                ParamName = "启用ROI 0整图1框选区域",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "roi:{0}",
                Group = "ROI 区域",
                Tip = "0=整图处理；1=只处理图上框选的区域（区域外保持原样）。" +
                      "参数面板里有「用图上框选填充」按钮，可把图上拉的框直接写入这里。" +
                      "区域内的阈值按框内像素统计，局部光照也能跟住。"
            },
            new TaskParamDesc { ParamName = "ROI X", Min = 0, Max = 100000, DefaultValue = 0, Group = "ROI 区域", Tip = "框选区域左上角 X（像素）" },
            new TaskParamDesc { ParamName = "ROI Y", Min = 0, Max = 100000, DefaultValue = 0, Group = "ROI 区域", Tip = "框选区域左上角 Y（像素）" },
            new TaskParamDesc { ParamName = "ROI 宽", Min = 0, Max = 100000, DefaultValue = 0, Group = "ROI 区域", Tip = "框选区域宽度（像素），0=整图" },
            new TaskParamDesc { ParamName = "ROI 高", Min = 0, Max = 100000, DefaultValue = 0, Group = "ROI 区域", Tip = "框选区域高度（像素），0=整图" },
        };

        /// <summary>
        /// 从参数数组末尾读取 ROI 并裁剪到图像范围内。
        /// 旧链参数短（无 ROI）或未启用时返回 false = 整图处理。
        /// </summary>
        public static bool TryGet(Mat src, int[] v, out Rect r)
        {
            r = default;
            if (src == null || src.Empty()) return false;
            if (v == null || v.Length < 5) return false;          // 旧链：没有 ROI 参数
            int i = v.Length - 5;
            if (v[i] != 1) return false;                          // 未启用
            var raw = new Rect(v[i + 1], v[i + 2], v[i + 3], v[i + 4]);
            if (raw.Width <= 0 || raw.Height <= 0) return false;
            r = raw & new Rect(0, 0, src.Cols, src.Rows);          // 与图像求交，防越界
            return r.Width >= 1 && r.Height >= 1;
        }

        /// <summary>
        /// 把 ROI 内处理结果（8UC1 二值图）贴回整图对应位置。
        /// 整图是彩色时二值图转 BGR（黑白），保证通道数一致；框外像素不受影响。
        /// </summary>
        public static void PasteBinarized(Mat dst, Mat bin, Rect r)
        {
            if (dst == null || bin == null || r.Width <= 0 || r.Height <= 0) return;
            using Mat target = dst[r];
            if (dst.Channels() >= 3)
            {
                using Mat bgr = new Mat();
                Cv2.CvtColor(bin, bgr, ColorConversionCodes.GRAY2BGR);
                bgr.CopyTo(target);
            }
            else bin.CopyTo(target);
        }

        /// <summary>无参数但仍是逐像素变换的算子（直方图均衡），按类型名登记支持 ROI。</summary>
        private static readonly System.Collections.Generic.HashSet<string> WrapByType = new() { "EqualizeHistTask" };

        /// <summary>参数描述里是否已内置"启用ROI"参数（三个二值化算子自己处理 ROI，勿再包装）。</summary>
        public static bool HasBuiltInRoi(TaskParamDesc[] defs)
            => defs != null && defs.Any(d => d.ParamName != null && d.ParamName.StartsWith("启用ROI"));

        /// <summary>该算子是否需要 RunChain 层做通用 ROI 包装（标记了 SupportRoi 或登记了类型名，且未内置 ROI 参数）。</summary>
        public static bool NeedsWrap(TaskParamDesc[] defs, string typeName)
            => defs != null && !HasBuiltInRoi(defs)
               && (defs.Any(d => d.SupportRoi) || (typeName != null && WrapByType.Contains(typeName)));

        /// <summary>ROI 参数在参数数组里占据的总位数：内置型已含在 ParamDescriptions 里（0 额外）；
        /// 包装型追加 5 个；其他算子 0。</summary>
        public static int RoiTotal(TaskParamDesc[] defs, string typeName)
            => HasBuiltInRoi(defs) ? 0 : (NeedsWrap(defs, typeName) ? 5 : 0);

        /// <summary>追加的 5 个 ROI 参数的默认值（未启用=整图处理）。</summary>
        public static readonly int[] DefaultValues = { 0, 0, 0, 0, 0 };

        /// <summary>
        /// 通用贴回：把算子对 ROI 子图处理后的结果贴回整图对应位置（通道数不一致时做转换）。
        /// 用于 RunChain 层统一包装——框外像素始终保持原图，框内替换为处理结果。
        /// </summary>
        public static void PasteAny(Mat dst, Mat patch, Rect r)
        {
            if (dst == null || patch == null || r.Width <= 0 || r.Height <= 0) return;
            if (patch.Rows != r.Height || patch.Cols != r.Width) return;   // 尺寸对不上就不贴，保守
            using Mat target = dst[r];
            if (dst.Channels() == patch.Channels())
            {
                patch.CopyTo(target);
                return;
            }
            if (patch.Channels() == 1)
            {
                using Mat conv = new Mat();
                Cv2.CvtColor(patch, conv, dst.Channels() >= 3 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.GRAY2BGRA);
                conv.CopyTo(target);
                return;
            }
            if (dst.Channels() == 1 && patch.Channels() == 3)
            {
                using Mat gray = new Mat();
                Cv2.CvtColor(patch, gray, ColorConversionCodes.BGR2GRAY);
                gray.CopyTo(target);
                return;
            }
            patch.CopyTo(target);   // 其他组合交给 OpenCV 广播/逐通道复制
        }
    }
}
