namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 需要把最近一次执行的结果摘要显示到提示标签（lblROI）的算子实现此接口，
    /// UI 层在 RunProcess 后统一读取显示；摘要须在每次 Execute 开头重置。
    /// </summary>
    public interface IResultReporter
    {
        /// <summary>最近一次 Execute 的结果摘要（中文，供界面标签显示）</summary>
        string LastSummary { get; }
    }
}
