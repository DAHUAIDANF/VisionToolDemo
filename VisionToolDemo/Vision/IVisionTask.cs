using OpenCvSharp;
using System;

namespace VisionToolDemo.Vision
{
    public interface IVisionTask
    {
        /// <summary>工具显示名称（下拉框文字）</summary>
        string TaskName { get; }

        /// <summary>参数列表，算法自己声明需要哪些滑动条参数</summary>
        TaskParamDesc[] ParamDescriptions { get; }

        /// <summary>执行图像处理</summary>
        /// <param name="srcMat">输入图像（可能是ROI子图）</param>
        /// <param name="paramValues">按顺序传入的参数值数组</param>
        /// <returns>处理后的Mat</returns>
        Mat Execute(Mat srcMat, int[] paramValues);
    }
}