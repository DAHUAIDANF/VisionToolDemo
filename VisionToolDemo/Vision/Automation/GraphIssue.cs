namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 一条校验问题。界面用它逐条列出（并双击定位到节点），
    /// 与参考项目的 issue{level, code, message, node_id} 一个意思：
    /// 只有 IsError 会拦住运行，警告只是提醒，避免"什么都不能跑"。
    /// </summary>
    public sealed class GraphIssue
    {
        /// <summary>相关节点 id（0 = 与具体节点无关）</summary>
        public int NodeId { get; set; }

        /// <summary>是不是错误（错误会拦截运行，警告不会）</summary>
        public bool IsError { get; set; }

        /// <summary>问题描述。用**属性**：WPF 的 {Binding Message} 不认字段（会静默渲染成空白）</summary>
        public string Message { get; set; } = "";

        public string LevelText => IsError ? "错误" : "提醒";

        public static GraphIssue Error(int nodeId, string message)
            => new() { NodeId = nodeId, IsError = true, Message = message };

        public static GraphIssue Warn(int nodeId, string message)
            => new() { NodeId = nodeId, IsError = false, Message = message };

        public override string ToString() => (IsError ? "[错误] " : "[提醒] ") + Message;
    }
}
