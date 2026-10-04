using System;
using OpenCvSharp;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Vision.External;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// PLC 写入：向采集页已连接的 PLC 写保持寄存器/线圈。
    /// 值来源：0=常量（参数 3 输入值），1=全局变量（变量名 = 节点字符串槽 0）。
    /// 输出原图（PLC 为动作类算子，不改图）。
    /// </summary>
    public class PlcWriteTask : IVisionTask, INodeStringSource, IResultReporter
    {
        public string TaskName => "PLC写入";

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
                Tip = "0=保持寄存器（16位数值，FC06）1=线圈（开关量，FC05）"
            },
            new TaskParamDesc
            {
                ParamName = "地址",
                Min = 0,
                Max = 65535,
                DefaultValue = 0,
                DisplayFormat = "地址:{0}",
                Tip = "写入地址（0 起）"
            },
            new TaskParamDesc
            {
                ParamName = "值来源 0常量1变量",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "来源:{0}",
                Tip = "0=用下一个参数“常量值”；1=用全局变量的值（变量名填在【输入内容】）"
            },
            new TaskParamDesc
            {
                ParamName = "常量值",
                Min = 0,
                Max = 65535,
                DefaultValue = 0,
                DisplayFormat = "值:{0}",
                Tip = "值来源=常量时写入的值；线圈时非 0=ON"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            bool coil = paramValues.Length > 0 && paramValues[0] == 1;
            int addr = paramValues.Length > 1 ? paramValues[1] : 0;
            int src = paramValues.Length > 2 ? paramValues[2] : 0;
            ushort value = 0;
            string what = "";
            if (src == 1)
            {
                string varName = NodeStringProvider?.Invoke(0);
                if (string.IsNullOrWhiteSpace(varName)) varName = "PLC值";
                bool have = AutomationContext.TryResolveVariable(varName, out string vtext);
                if (!have || !ushort.TryParse((vtext ?? "").Trim(), out value))
                {
                    LastSummary = string.Format("PLC写入: 失败 —— 变量 {0} 没有有效数值（当前 \"{1}\"）", varName, vtext);
                    return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
                }
                what = "变量 " + varName;
            }
            else
            {
                value = paramValues.Length > 3 ? (ushort)Math.Clamp(paramValues[3], 0, 65535) : (ushort)0;
                what = "常量 " + value;
            }

            if (AutomationContext.DryRun)
            {
                // 干跑模式：不实际写外部设备，只记录预期动作
                LastSummary = string.Format("PLC写入: 干跑不执行（地址{0} <- {1}）", addr, value);
                return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
            }
            string err = CommHub.WritePlc(addr, coil, value);
            if (err != null)
                LastSummary = "PLC写入: 失败 —— " + err;
            else
                LastSummary = string.Format("PLC写入: 地址{0} <- {1}（{2}）", addr, value, what);
            return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
        }
    }
}
