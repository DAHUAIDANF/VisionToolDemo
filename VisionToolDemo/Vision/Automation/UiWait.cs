using System;
using System.Threading;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 自动化执行中的"等待"钩子：默认就是 Thread.Sleep（无 UI 环境/命令行模式照常工作）。
    /// WPF 界面启动时由 Program.cs 注入"泵消息版"（Ui.SleepResponsive）：
    /// 节点图真实执行（鼠标模拟/打字间隔/失败重试等待）期间界面保持响应，
    /// 窗口能拖动、日志能滚动、也方便把鼠标甩到角落中止——不再整窗假死。
    /// </summary>
    public static class UiWait
    {
        /// <summary>毫秒数 → 等待。默认 Thread.Sleep；可被 UI 层替换成带消息泵的实现。</summary>
        public static Action<int> Sleep { get; set; } = ms => Thread.Sleep(ms);
    }
}
