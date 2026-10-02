using System;
using OpenCvSharp;
using VisionToolDemo.Vision;

namespace VisionToolDemo.Vision.Pipeline
{
    /// <summary>
    /// 流水线运行时步骤：算子实例 + 参数 + 添加时记录的 ROI + 启用状态
    /// </summary>
    public class PipelineStep
    {
        public IVisionTask Task { get; set; }
        public int[] Params { get; set; }

        /// <summary>添加步骤时记录的 ROI（图像坐标）；空矩形表示处理整图</summary>
        public Rect Roi { get; set; }

        /// <summary>是否启用；false = 运行流水线时跳过该步骤（图像原样传递）</summary>
        public bool Enabled { get; set; } = true;

        public PipelineStep(IVisionTask task, int[] paramValues)
        {
            Task = task;
            Params = paramValues;
        }

        /// <summary>
        /// 深复制步骤：算子用注册表新建实例并恢复其状态（卡尺几何/模板等独立），
        /// 参数与 ROI/启用状态一并复制。供“复制步骤”与批量等场景使用。
        /// </summary>
        public PipelineStep Clone()
        {
            IVisionTask task = VisionTaskRegistry.CreateTask(Task.TaskName) ?? Task;
            if (task is IStatefulTask dst && Task is IStatefulTask src)
            {
                string state = src.SaveState();
                if (state != null)
                    dst.LoadState(state);
            }
            return new PipelineStep(task, (int[])Params.Clone()) { Roi = Roi, Enabled = Enabled };
        }
    }
}
