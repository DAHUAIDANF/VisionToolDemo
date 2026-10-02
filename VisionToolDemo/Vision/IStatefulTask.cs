namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 算子持有需要随流水线保存/复制的外部状态（卡尺几何、测量线、模板图等）时实现本接口。
    /// 保存流水线时逐步骤导出状态，加载/复制步骤时恢复，使配置可完整复现。
    /// </summary>
    public interface IStatefulTask
    {
        /// <summary>把几何/模板等状态序列化为 JSON 文本；无状态时返回 null</summary>
        string SaveState();

        /// <summary>从 JSON 文本恢复状态（空串或损坏的内容应直接忽略，不抛异常）</summary>
        void LoadState(string state);
    }
}
