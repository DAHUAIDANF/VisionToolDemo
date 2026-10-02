namespace VisionToolDemo.Vision.Pipeline
{
    /// <summary>
    /// 流水线步骤【数据模型，JSON序列化专用，不含算子实例】
    /// </summary>
    public class PipelineStepModel
    {
        public string TaskName { get; set; }
        public int[] Params { get; set; }

        /// <summary>步骤记录的 ROI；宽高为 0 表示处理整图（兼容旧文件）</summary>
        public int RoiX { get; set; }
        public int RoiY { get; set; }
        public int RoiWidth { get; set; }
        public int RoiHeight { get; set; }

        /// <summary>是否启用该步骤；false = 运行时跳过（兼容旧文件，缺省视为启用）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>算子的外部状态 JSON（卡尺几何/测量线/模板图），由 IStatefulTask 导出；无状态为 null</summary>
        public string State { get; set; }
    }
}
