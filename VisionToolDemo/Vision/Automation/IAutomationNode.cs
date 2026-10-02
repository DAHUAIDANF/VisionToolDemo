using System;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 标记"只用于自动化节点、不作为视觉流水线工具"的算子。
    ///
    /// 为什么需要区分：这些算子（截图/点击/按键/等待）在**视觉流水线**里语义不成立 ——
    /// 流水线的契约是"输入图像 -> 输出图像"，而它们要么替换输入、要么根本不改图只做外部动作。
    /// 混在算子下拉框里会让用户误以为它们能像其它算子一样串联，
    /// 更麻烦的是：自动化的副作用算子在参数面板里改一下参数，就可能真的点一下鼠标。
    /// 所以它们只出现在"自动化"节点页面里。
    /// </summary>
    public interface IAutomationNode
    {
    }

    /// <summary>
    /// 接收"节点图注入的字符串参数"的自动化算子。
    ///
    /// 算子参数体系只支持 int[]（改签名会牵动全部 100 个算子），所以节点上的字符串
    /// 走这条路注入：NodeText = 节点属性里的「文本」框，NodeKey = 「按键 / 组合键」框。
    /// 运行器在执行前统一赋值，算子自己决定怎么用（键盘输入用它当内容、
    /// 全局变量用它当变量名与固定值、屏幕截图用它当保存目录、弹窗用它当正文）。
    /// </summary>
    public interface IStringParamTask
    {
        string NodeText { get; set; }
        string NodeKey { get; set; }
    }

    /// <summary>
    /// 需要"按槽位取字符串"的自动化算子（命令行、表达式、等待条件、窗口操作…）。
    ///
    /// 原来的 IStringParamTask 只有两个字符串（「文本」「按键」框），而这类节点需要更多
    /// （命令+参数+工作目录；或者多行脚本正文）。运行器在执行前注入一个取值函数，
    /// 算子按自己的槽位号去取，槽位含义写在各算子的参数说明里（节点属性面板也按它渲染）。
    /// </summary>
    public interface INodeStringSource
    {
        /// <summary>取本节点第 slot 个字符串槽（0=「文本」框，1=「按键」框，2+ 见该节点说明）</summary>
        Func<int, string> NodeStringProvider { get; set; }
    }

    /// <summary>
    /// 动作算子执行完可以报告"本该做但被跳过了"（没检测到目标、坐标越界、内容为空…）。
    /// 循环运行节点图时用它决定"本轮不合格就走停止"，比解析日志稳。
    /// 注意只给"真的该产生输入"的算子实现：弹窗在干跑下没弹不算跳过。
    /// </summary>
    public interface IActionStateTask
    {
        bool Skipped { get; }
    }
}
