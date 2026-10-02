using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using OpenCvSharp;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Tasks;

namespace VisionToolDemo.Vision.Pipeline
{
    public class VisionPipeline
    {
        private readonly List<PipelineStep> _steps = [];

        /// <summary>一次运行中每个步骤的输入/输出快照（索引与步骤对应），供 UI 逐步查看</summary>
        private class PipelineFrame
        {
            public Mat Input;       // 该步执行前的图像
            public Mat Output;      // 该步执行后的图像
            public double ElapsedMs; // 该步耗时
            public string Summary;  // 该步结果摘要（算子实现 IResultReporter 时），运行时即刻捕获
        }

        private readonly List<PipelineFrame> _frames = [];

        public List<PipelineStep> Steps => _steps;

        /// <summary>是否已有最近一次运行的步骤快照</summary>
        public bool HasFrames => _frames.Count > 0;

        /// <summary>第 index 步的输入图像（未运行或越界返回 null，返回值由流水线持有，勿 Dispose）</summary>
        public Mat GetStepInput(int index)
        {
            return index >= 0 && index < _frames.Count ? _frames[index].Input : null;
        }

        /// <summary>第 index 步的输出图像（未运行或越界返回 null，返回值由流水线持有，勿 Dispose）</summary>
        public Mat GetStepOutput(int index)
        {
            return index >= 0 && index < _frames.Count ? _frames[index].Output : null;
        }

        /// <summary>释放所有步骤快照（步骤列表变化后中间图不再有效）</summary>
        public void DisposeFrames()
        {
            foreach (PipelineFrame f in _frames)
            {
                f.Input?.Dispose();
                f.Output?.Dispose();
            }
            _frames.Clear();
        }

        /// <summary>
        /// 串行执行流水线：前一步输出Mat作为下一步输入。
        /// 步骤记录了 ROI 时，在该裁剪区域内处理并把结果写回原图对应位置。
        /// 运行时缓存每步的输入/输出，供界面选中步骤后查看中间图。
        /// </summary>
        /// <param name="src">原始输入图像</param>
        /// <returns>最终输出Mat，调用方用完必须Dispose()</returns>
        public Mat Run(Mat src)
        {
            if (src == null || src.Empty())
                throw new ArgumentException("输入图像为空");

            DisposeFrames();
            Mat current = src.Clone();

            foreach (var step in _steps)
            {
                Mat stepInput = current.Clone();

                // 禁用的步骤跳过执行：输入=输出原样传递，帧序号仍与步骤对齐（列表可查看）
                if (!step.Enabled)
                {
                    _frames.Add(new PipelineFrame
                    {
                        Input = stepInput,
                        Output = current.Clone(),
                        ElapsedMs = 0,
                        Summary = "已禁用，跳过"
                    });
                    continue;
                }

                Stopwatch sw = Stopwatch.StartNew();
                Mat next;
                try
                {
                    next = RunStep(current, step);
                }
                catch (Exception ex)
                {
                    // 标明出错的步骤与原因后向上抛（UI 层弹窗提示），已生成的快照保留
                    stepInput.Dispose();
                    current.Dispose();
                    throw new Exception($"第 {_frames.Count + 1} 步 {step.Task.TaskName} 执行出错: {ex.Message}", ex);
                }
                sw.Stop();
                current.Dispose();
                current = next;
                _frames.Add(new PipelineFrame
                {
                    Input = stepInput,
                    Output = current.Clone(),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Summary = step.Task is IResultReporter rep ? rep.LastSummary : null
                });
            }
            return current;
        }

        /// <summary>第 index 步的结果摘要（实现 IResultReporter 的算子；未运行/越界/无摘要返回 null）</summary>
        public string GetStepSummary(int index)
        {
            return index >= 0 && index < _frames.Count ? _frames[index].Summary : null;
        }

        /// <summary>第 index 步的执行耗时（毫秒；未运行或越界返回 -1）</summary>
        public double GetStepElapsedMs(int index)
        {
            return index >= 0 && index < _frames.Count ? _frames[index].ElapsedMs : -1;
        }

        /// <summary>执行单个步骤：有 ROI 时在裁剪区域处理并写回原图副本</summary>
        private static Mat RunStep(Mat current, PipelineStep step)
        {
            // 掩膜算子的区域为整图坐标：不裁剪；步骤记录了掩膜区域时重新注入（json 加载后可复现）。
            // 笔刷轨迹由 IStatefulTask 序列化保存（快照/复制/加载时恢复），此处只需重注入矩形。
            if (step.Task is MaskTask maskTask)
            {
                if (step.Roi.Width > 0 && step.Roi.Height > 0)
                    maskTask.SetMask(step.Roi);
                return step.Task.Execute(current, step.Params);
            }

            Rect roi = ClipRect(step.Roi, current.Cols, current.Rows);
            bool useRoi = step.Roi.Width > 5 && step.Roi.Height > 5
                          && roi.Width > 5 && roi.Height > 5;

            if (!useRoi)
                return step.Task.Execute(current, step.Params);

            Mat nextFull = current.Clone();
            using (Mat crop = new(current, roi))
            using (Mat stepOut = step.Task.Execute(crop, step.Params))
            {
                if (stepOut.Size() != roi.Size)
                {
                    // 算子改变了尺寸（如缩放/旋转），无法写回 ROI：整图替换
                    nextFull.Dispose();
                    return stepOut.Channels() == 1 ? ToBgr(stepOut) : stepOut.Clone();
                }

                // 处理结果写回原图副本的对应区域（单通道结果转 BGR）
                using (Mat dstRoi = new(nextFull, roi))
                {
                    if (stepOut.Channels() == 1)
                    {
                        using (Mat bgr = new())
                        {
                            Cv2.CvtColor(stepOut, bgr, ColorConversionCodes.GRAY2BGR);
                            bgr.CopyTo(dstRoi);
                        }
                    }
                    else
                    {
                        stepOut.CopyTo(dstRoi);
                    }
                }
            }
            return nextFull;
        }

        private static Mat ToBgr(Mat src)
        {
            Mat dst = new();
            Cv2.CvtColor(src, dst, ColorConversionCodes.GRAY2BGR);
            return dst;
        }

        /// <summary>把 ROI 裁剪到当前图像范围内，防止前一步改变尺寸后越界</summary>
        private static Rect ClipRect(Rect r, int cols, int rows)
        {
            int x = Math.Max(0, Math.Min(r.X, cols - 1));
            int y = Math.Max(0, Math.Min(r.Y, rows - 1));
            return new Rect(x, y, Math.Min(r.Width, cols - x), Math.Min(r.Height, rows - y));
        }

        public void AddStep(PipelineStep step)
        {
            _steps.Add(step);
        }

        /// <summary>在指定位置插入步骤（index 越界时按追加处理），旧快照失效</summary>
        public void InsertStep(int index, PipelineStep step)
        {
            if (index < 0 || index > _steps.Count)
                index = _steps.Count;
            _steps.Insert(index, step);
            DisposeFrames();
        }

        public void RemoveStep(int index)
        {
            if (index >= 0 && index < _steps.Count)
            {
                _steps.RemoveAt(index);
                DisposeFrames();
            }
        }

        public void MoveUp(int index)
        {
            if (index > 0)
            {
                var temp = _steps[index];
                _steps[index] = _steps[index - 1];
                _steps[index - 1] = temp;
                DisposeFrames();
            }
        }

        public void MoveDown(int index)
        {
            if (index >= 0 && index < _steps.Count - 1)
            {
                var temp = _steps[index];
                _steps[index] = _steps[index + 1];
                _steps[index + 1] = temp;
                DisposeFrames();
            }
        }

        public void Clear()
        {
            _steps.Clear();
            DisposeFrames();
        }

        #region Newtonsoft.Json 保存 / 加载

        /// <summary>
        /// 保存流水线到json文件
        /// </summary>
        public void SaveToFile(string filePath)
        {
            var modelList = new List<PipelineStepModel>();
            foreach (var step in _steps)
            {
                modelList.Add(new PipelineStepModel
                {
                    TaskName = step.Task.TaskName,
                    Params = step.Params,
                    RoiX = step.Roi.X,
                    RoiY = step.Roi.Y,
                    RoiWidth = step.Roi.Width,
                    RoiHeight = step.Roi.Height,
                    Enabled = step.Enabled,
                    State = step.Task is IStatefulTask st ? st.SaveState() : null
                });
            }
            string json = JsonConvert.SerializeObject(modelList, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 从json加载流水线，自动从算子注册表恢复算子实例
        /// </summary>
        public void LoadFromFile(string filePath)
        {
            string json = File.ReadAllText(filePath);
            List<PipelineStepModel> modelList = JsonConvert.DeserializeObject<List<PipelineStepModel>>(json);

            _steps.Clear();
            DisposeFrames();
            foreach (var m in modelList)
            {
                // 每步创建独立算子实例：同一算子多个步骤时几何/模板互不覆盖
                IVisionTask task = VisionTaskRegistry.CreateTask(m.TaskName);
                if (task == null)
                {
                    throw new Exception($"算子不存在：{m.TaskName}");
                }
                if (task is IStatefulTask st && !string.IsNullOrEmpty(m.State))
                    st.LoadState(m.State); // 恢复卡尺几何/模板等（损坏的内容由实现方忽略）
                var step = new PipelineStep(task, m.Params)
                {
                    Roi = new Rect(m.RoiX, m.RoiY, m.RoiWidth, m.RoiHeight),
                    Enabled = m.Enabled
                };
                _steps.Add(step);
            }
        }

        #endregion Newtonsoft.Json 保存 / 加载
    }
}