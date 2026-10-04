using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenCvSharp;
using VisionToolDemo.Vision.Automation;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 定时触发节点：按固定间隔节流执行，配合「循环」节点做周期采集/检测。
    ///
    /// 行为：节点记录自己上次放行的时间，距上次不足「间隔毫秒」时**阻塞等待到点**，
    /// 到点后放行（输出原图，不改变当前图像）。因此把
    /// 「定时触发 → 相机取图 → 检测算子 → … → 循环」串起来，就能得到
    /// 固定节拍的周期采集检测流水线（如每 2 秒抓一次相机并判 OK/NG）。
    ///
    /// 节点 Id 由运行器注入（NodeId），跨次执行的节拍状态按节点保存，
    /// 不随每次 CreateTask 的新实例丢失。
    /// </summary>
    public class TimerTriggerTask : IVisionTask, IResultReporter, Automation.IAutomationNode
    {
        public string TaskName => "定时触发";

        public string LastSummary { get; private set; } = "";

        /// <summary>运行器注入的节点 Id（同一节点多次执行共用节拍状态）</summary>
        public int NodeId { get; set; } = -1;

        private static readonly Dictionary<int, long> _lastPassMs = new();

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "间隔(秒)",
                Min = 0,
                Max = 3600,
                DefaultValue = 2,
                DisplayFormat = "间隔:{0}s",
                Tip = "两次放行的最小间隔秒数。配合循环节点 = 周期采集/检测节拍；0=不节流立即通过"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            int seconds = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 2, 0, 3600);
            if (seconds <= 0)
            {
                LastSummary = "定时触发: 间隔=0，立即通过（不节流）";
                return Output(srcMat, 0);
            }

            long intervalMs = seconds * 1000L;
            long now = Environment.TickCount64;
            int key = NodeId >= 0 ? NodeId : GetHashCode();
            lock (_lastPassMs)
            {
                if (!_lastPassMs.TryGetValue(key, out long last))
                {
                    _lastPassMs[key] = now;   // 首次立即放行
                    LastSummary = string.Format("定时触发: 首次放行（间隔 {0}s）", seconds);
                    return Output(srcMat, 0);
                }

                long elapsed = now - last;
                if (elapsed >= intervalMs)
                {
                    _lastPassMs[key] = now;
                    LastSummary = string.Format("定时触发: 已到节拍，放行（距上次 {0:F1}s）", elapsed / 1000.0);
                    return Output(srcMat, 0);
                }

                // 未到节拍：阻塞等待剩余时间（跟随「等待延时」节点的行为，运行在工作流线程）
                long remain = intervalMs - elapsed;
                LastSummary = string.Format("定时触发: 等待 {0:F1}s 到节拍…", remain / 1000.0);
                UiWait.Sleep(remain > 0 ? (int)Math.Min(remain, int.MaxValue) : 0);
                lock (_lastPassMs) _lastPassMs[key] = Environment.TickCount64;
                LastSummary = string.Format("定时触发: 到点放行（间隔 {0}s）", seconds);
                return Output(srcMat, (int)remain);
            }
        }

        private static Mat Output(Mat srcMat, int waitedMs)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            return srcMat.Clone();
        }
    }
}
