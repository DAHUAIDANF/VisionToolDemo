namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 一条规则的判定结果。
    ///
    /// 为什么把它做成一等结构（而不是只写进日志）：工业质检要的是"为什么判 NG"，
    /// 最终结果里必须能列出**每条规则的名字、是否通过、以及不通过的原因**，
    /// 这样 NG 时能直接告诉现场是哪个条件不满足（参考项目的 RuleResult / FinalResult 就是这么设计的）。
    /// </summary>
    public sealed class RuleResult
    {
        /// <summary>规则名（一般是变量名或节点标题，用来区分是哪一条）</summary>
        public string Name = "";

        /// <summary>是否通过</summary>
        public bool Ok;

        /// <summary>人类可读的判定说明（含实际值与期望值）</summary>
        public string Reason = "";

        /// <summary>产生这条规则的节点 id</summary>
        public int NodeId;

        public override string ToString() => (Ok ? "OK  " : "NG  ") + Name + "  " + Reason;
    }
}
