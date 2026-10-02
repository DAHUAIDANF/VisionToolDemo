using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 直线型卡尺的公共接口（直线卡尺 / 边缘对卡尺）。
    /// UI 层的画线交互、扫描带绘制、状态保存都只依赖这几个成员，
    /// 新增直线卡尺类算子时实现本接口即可复用整套交互，不必再逐个写 is 判断。
    /// </summary>
    public interface ILineCaliper
    {
        /// <summary>卡尺起点（真实像素坐标）</summary>
        Point CaliperStart { get; }

        /// <summary>卡尺终点（真实像素坐标）</summary>
        Point CaliperEnd { get; }

        /// <summary>是否已画线</summary>
        bool HasCaliper { get; }

        /// <summary>UI 画线后注入</summary>
        void SetCaliper(Point start, Point end);

        /// <summary>清除卡尺</summary>
        void ClearCaliper();
    }
}
