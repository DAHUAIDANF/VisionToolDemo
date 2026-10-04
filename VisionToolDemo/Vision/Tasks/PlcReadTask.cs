using System;
using OpenCvSharp;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Vision.External;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// PLC 读取：从采集页已连接的 PLC 读保持寄存器/线圈，把值写入全局变量。
    /// 变量名 = 节点字符串槽 0（节点属性「输入内容」第一行）；
    /// 视觉页手动执行时取不到槽位则写「PLC结果」。
    /// 输出原图（PLC 为动作类算子，不改图）。
    /// </summary>
    public class PlcReadTask : IVisionTask, INodeStringSource, IResultReporter
    {
        public string TaskName => "PLC读取";

        public string LastSummary { get; private set; } = "";

        public Func<int, string> NodeStringProvider { get; set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "类型 0寄存器1线圈",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=保持寄存器（16位数值，FC03）1=线圈（开关量，FC01）"
            },
            new TaskParamDesc
            {
                ParamName = "起始地址",
                Min = 0,
                Max = 65535,
                DefaultValue = 0,
                DisplayFormat = "地址:{0}",
                Tip = "起始地址（0 起，通常对应 PLC 里 40001 / 00001 的首地址）"
            },
            new TaskParamDesc
            {
                ParamName = "数量",
                Min = 1,
                Max = 100,
                DefaultValue = 1,
                DisplayFormat = "数量:{0}",
                Tip = "读取个数；多个时写变量为逗号分隔列表"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            bool coil = paramValues.Length > 0 && paramValues[0] == 1;
            int addr = paramValues.Length > 1 ? paramValues[1] : 0;
            int count = paramValues.Length > 2 ? Math.Max(1, paramValues[2]) : 1;
            string varName = NodeStringProvider?.Invoke(0);
            if (string.IsNullOrWhiteSpace(varName)) varName = "PLC结果";

            if (AutomationContext.DryRun)
            {
                // 干跑模式：不实际读外部设备
                LastSummary = string.Format("PLC读取: 干跑不执行（{0}[{1}]）", varName, addr);
                return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
            }
            string err = CommHub.ReadPlc(addr, coil, count, out object[] vals);
            if (err != null)
            {
                LastSummary = "PLC读取: 失败 —— " + err;
                AutomationContext.SetVariable(varName, "读失败:" + err);
            }
            else
            {
                string text = string.Join(",", Array.ConvertAll(vals, v => v.ToString()));
                AutomationContext.SetVariable(varName, text);
                LastSummary = string.Format("PLC读取: {0}[{1}] = {2}", varName, addr, text);
            }
            // PLC 动作类算子不改图：原样返回（独立拷贝，避免与链共享内存）
            return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
        }
    }
}
