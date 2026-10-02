namespace VisionToolDemo.Vision
{
    public class TaskParamDesc
    {
        public string ParamName { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
        public int DefaultValue { get; set; }
        public string DisplayFormat { get; set; }
        public bool ForceOdd { get; set; }

        /// <summary>参数分组名（VisionMaster 式可折叠分组）。
        /// 为空时由参数名前缀自动推断（见 ParamGroups.GroupOf），算子无需逐个标注。</summary>
        public string Group { get; set; }

        /// <summary>参数说明（鼠标悬停提示）。用于解释 "x10"、"极性 0亮/1暗"、"操作类型 0开1闭…"
        /// 这类只看名字无法理解的参数，以及给出推荐取值范围。为空时不显示提示。</summary>
        public string Tip { get; set; }

        /// <summary>
        /// 算子是否支持"算子级 ROI"（在参数面板虚拟追加"启用ROI/X/Y/宽/高"5 个参数，
        /// 执行时由 PipelinePage 统一包装：只在框选区域内处理，区域外保持原图不变）。
        /// 只对"逐像素/局部变换、输出=处理后的图"这类算子置 true；检测/测量/统计类不适用。
        /// 已把 ROI 参数写进 ParamDescriptions 的算子（如二值化系列）无需再置此标记。
        /// </summary>
        public bool SupportRoi { get; set; }
    }

    /// <summary>参数分组：把参数按 Group 归并（保持首次出现顺序），供参数面板做折叠显示。</summary>
    public static class ParamGroups
    {
        /// <summary>参数名前缀 → 分组名。按前缀长度降序匹配，先长后短，避免"点阵判暗阈值"
        /// 被更短的前缀抢走。未命中任何前缀的参数归入"常规"。</summary>
        private static readonly (string Prefix, string Group)[] Rules =
        [
            ("检测", "检测范围"),
            ("点阵", "点阵 DataMatrix"),
            ("ZXing", "增强引擎"),
            ("卡尺", "卡尺"),
            ("圆卡尺", "卡尺"),
            ("掩膜", "掩膜"),
            ("模板", "模板匹配"),
            ("匹配", "模板匹配"),
            ("阈值", "二值化"),
            ("形态", "形态学"),
            ("核", "形态学"),
            ("Canny", "边缘检测"),
            ("Sobel", "边缘检测"),
            ("Laplacian", "边缘检测"),
            ("霍夫", "霍夫变换"),
            ("圆", "霍夫变换"),
            ("直线", "直线拟合"),
            ("轮廓", "轮廓"),
            ("面积", "轮廓"),
            ("颜色", "颜色"),
            ("HSV", "颜色"),
            ("缩放", "几何变换"),
            ("旋转", "几何变换"),
            ("翻转", "几何变换"),
            ("透视", "几何变换"),
        ];

        public const string DefaultGroup = "常规";

        /// <summary>按前缀长度降序排好的规则表（长前缀优先，避免 "点阵判暗阈值" 被短前缀抢走）。
        /// 构造一次即可，判定时无需每次排序。</summary>
        private static readonly (string Prefix, string Group)[] Ordered = BuildOrdered();

        private static (string Prefix, string Group)[] BuildOrdered()
        {
            var list = new System.Collections.Generic.List<(string Prefix, string Group)>(Rules);
            list.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
            return [.. list];
        }

        /// <summary>取参数所属分组：显式 Group 优先，否则按名称前缀推断</summary>
        public static string GroupOf(TaskParamDesc d)
        {
            if (d == null) return DefaultGroup;
            if (!string.IsNullOrEmpty(d.Group)) return d.Group;
            string name = d.ParamName ?? "";
            // 长前缀优先："圆卡尺" 必须先于 "圆"
            foreach ((string prefix, string group) in Ordered)
            {
                if (name.StartsWith(prefix, System.StringComparison.Ordinal))
                    return group;
            }

            return DefaultGroup;
        }
    }
}