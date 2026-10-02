using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 等待/延时：让流水线停一会儿，并可选地把**延时期间的耗时记入摘要**。
    ///
    /// 为什么单独做一个算子：自动化里"等界面反应过来"是刚需，而它必须能被放进
    /// 流水线的任意位置（例如"点了保存 → 等 2 秒 → 截图确认是否出现成功提示"）。
    /// 放在动作算子的"动作后等待"参数里只能覆盖单个动作，无法表达"等一会儿再截图"。
    /// </summary>
    public class WaitTask : IVisionTask, IResultReporter, Automation.IAutomationNode
    {
        public string TaskName => "等待延时";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "等待ms", Min = 0, Max = 60000, DefaultValue = 500,
                DisplayFormat = "等待:{0}ms", Group = "延时",
                Tip = "暂停多少毫秒。自动化里常用 200~2000ms 等待界面响应；" +
                "等一个耗时操作（保存大文件、打开程序）可以给到 3000~10000ms。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            int ms = Math.Max(0, paramValues[0]);
            if (ms > 0) System.Threading.Thread.Sleep(ms);
            LastSummary = string.Format("等待延时: 已等待 {0} ms", ms);
            return VisionHelper.ToBgrCopy(srcMat);
        }
    }
}
