using System;
using OpenCvSharp;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Vision.External;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 相机取图：从采集页已连接的相机抓一帧，作为当前图像继续处理。
    /// 相机未连接时明确提示并原样返回输入图（不炸链）。
    /// </summary>
    public class CameraGrabTask : IVisionTask, IResultReporter
    {
        public string TaskName => "相机取图";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => Array.Empty<TaskParamDesc>();

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            using var frame = CommHub.TryGrabCamera();
            if (frame == null || frame.Empty())
            {
                LastSummary = "相机取图: 失败 —— 相机未连接或取帧失败（先在“采集”页连接相机）";
                return srcMat != null && !srcMat.Empty() ? srcMat.Clone() : new Mat();
            }
            LastSummary = string.Format("相机取图: {0}x{1}", frame.Cols, frame.Rows);
            return frame.Clone();   // 返回独立帧：调用方负责释放
        }
    }
}
