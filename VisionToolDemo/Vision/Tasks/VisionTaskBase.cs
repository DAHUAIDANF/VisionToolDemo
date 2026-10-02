using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 空图时的返回行为（与 docs/empty_guard_classification.md 的类别一一对应）。
    ///
    /// 本枚举只表达「返回值」这一个维度；「摘要怎么写」由
    /// <see cref="VisionTaskBase.EmptySummaryText"/> 单独表达，两者正交。
    /// 分类见 docs/empty_guard_classification.md（128 行，由 tools/ 下脚本按
    /// 架构文档 §3.4 的固定算法生成，禁止手抄）。
    /// </summary>
    public enum EmptyImageBehavior
    {
        /// <summary>A / B 类：返回一个 0x0 的空 Mat（<c>new Mat()</c>）。</summary>
        ReturnNewEmptyMat = 0,

        /// <summary>C 类：返回 <c>srcMat?.Clone()</c>（克隆，不是入参本身）。</summary>
        ReturnSourceClone = 1,

        /// <summary>D 类：抛异常（RoiTask），消息由 <see cref="VisionTaskBase.OnEmptyException"/> 复刻。</summary>
        Throw = 2,
    }

    /// <summary>
    /// 全部算子（128 个）的公共基类：把两件每个算子都在重复做的事收口到这里 ——
    /// ① 空图保护；② <see cref="LastSummary"/> 的声明与赋值方式。
    ///
    /// 设计要点（改动前请先读架构文档 §3.2 / §3.4）：
    /// - 用**模板方法**：<see cref="Execute"/> 统一做空判定，通过后转调 <see cref="ExecuteCore"/>。
    ///   算子类只写算法本体，不再各自写 <c>if (srcMat == null || srcMat.Empty())</c>。
    /// - **默认保护，但支持显式不保护**：<see cref="RequiresImage"/> 默认 true；
    ///   不消费输入图的算子（自动化节点 / 图像源，共 17 个）必须**逐个显式**重写为 false，
    ///   绝不能反过来让基类默认不保护 —— 那会让这 17 个从「正常执行」变成「返回空图」。
    /// - 空图行为由 <see cref="EmptyBehavior"/> + <see cref="EmptySummaryText"/> 两个虚成员声明，
    ///   **不做运行时推断**：111 处手写保护实测有 5 种语义，统一成一种即等于改行为。
    /// </summary>
    public abstract class VisionTaskBase : IVisionTask
    {
        /// <summary>工具显示名称（下拉框文字）。由各算子实现。</summary>
        public abstract string TaskName { get; }

        /// <summary>参数列表。由各算子实现。</summary>
        public abstract TaskParamDesc[] ParamDescriptions { get; }

        /// <summary>
        /// 最近一次 Execute 的结果摘要。
        /// 签名 / 可见性与改造前各算子里的声明**逐字符一致**（<c>public string LastSummary { get; protected set; } = "";</c>），
        /// 外部读取方（AutomationGraph / OperatorChain / VisionPipeline）零改动。
        /// </summary>
        public string LastSummary { get; protected set; } = "";

        // ------------------------------------------------------------ 空图策略（两个正交维度）

        /// <summary>
        /// false = 该算子不消费输入图（自动化节点 / 图像源 / 模板匹配），基类**跳过**空图保护。
        /// 默认 true。实测共 17 个算子需要重写为 false，逐个显式写，禁止默认关闭。
        /// </summary>
        protected virtual bool RequiresImage => true;

        /// <summary>空图时的返回行为。默认返回空 Mat（A 类，实测占绝大多数）。</summary>
        protected virtual EmptyImageBehavior EmptyBehavior => EmptyImageBehavior.ReturnNewEmptyMat;

        /// <summary>
        /// 空图时写入 <see cref="LastSummary"/> 的文本。
        /// null = 保留上一次摘要（A 类）；其他 = 写入该文本（B / C 类）。
        /// 另有一档 <c>""</c>：用于「原码把 <c>LastSummary = "";</c> 写在 guard **之前**」的算子 ——
        /// 这类算子走空图路径时摘要会被清空（如 8 个形位公差算子，实测证据见
        /// docs/baseline_emptyprobe.txt 的 empty 行），必须显式声明为 <c>""</c> 才能逐字节保行为。
        /// </summary>
        protected virtual string EmptySummaryText => null;

        /// <summary>空判定。做成虚方法是为了让方言算子（DpmCodeTask 需追加 IsDisposed）能显式 override。</summary>
        protected virtual bool IsEmptyImage(Mat m) => m == null || m.Empty();

        /// <summary>A 类空图返回值。默认 0x0 空 Mat。</summary>
        protected virtual Mat OnEmptyMat(Mat src) => new Mat();

        /// <summary>D 类空图异常。默认消息复刻 RoiTask 的原文案。</summary>
        protected virtual Exception OnEmptyException(Mat src) =>
            new InvalidOperationException(TaskName + ": 输入图像为空");

        // ------------------------------------------------------------ 模板方法

        /// <summary>
        /// 统一入口：先做空判定，通过后才进算法本体。
        /// 声明为 virtual 是为了给 E 类逃生口（CropRectTask 带着 <c>Skipped</c> 副作用，
        /// 基类模板覆盖不了，需要 override 本方法自行处理）。
        /// </summary>
        public virtual Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (RequiresImage && IsEmptyImage(srcMat))
            {
                string s = EmptySummaryText;
                if (s != null) LastSummary = s;

                if (EmptyBehavior == EmptyImageBehavior.Throw) throw OnEmptyException(srcMat);
                if (EmptyBehavior == EmptyImageBehavior.ReturnSourceClone) return srcMat?.Clone();
                return OnEmptyMat(srcMat);
            }
            return ExecuteCore(srcMat, paramValues);
        }

        /// <summary>算法本体。算子类把原来的 Execute 体搬到这里（改签名，算法整段不动）。</summary>
        protected abstract Mat ExecuteCore(Mat srcMat, int[] paramValues);

        // ------------------------------------------------------------ 摘要

        /// <summary>
        /// 写摘要。传了参数就按 <see cref="string.Format(string, object[])"/> 格式化，没传就原样写入 ——
        /// 这样原来写 <c>LastSummary = "xxx: " + v</c> 的地方改成 <c>SetSummary("xxx: {0}", v)</c>
        /// 后输出逐字节不变（注意：格式化用当前区域性与字符串拼接等价，实测已核对）。
        /// </summary>
        protected void SetSummary(string format, params object[] args) =>
            LastSummary = args is { Length: > 0 } ? string.Format(format, args) : format;

        /// <summary>清空摘要（等价于原码的 <c>LastSummary = "";</c>）。</summary>
        protected void ResetSummary() => LastSummary = "";
    }
}
