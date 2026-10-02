using System;
using System.Drawing;
using VisionToolDemo.Vision.Automation;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 坐标系换算回归测试（覆盖 ROI 框选/点击坐标链路：图像坐标 → 屏幕坐标）。
    /// ImageToScreen 是纯数学，Windows/Linux 均可运行。
    /// </summary>
    public class CoordinateSpaceImageToScreenTests
    {
        [Theory]
        // originX, originY, scale, imageX, imageY, 期望 screenX, screenY
        [InlineData(0, 0, 1.0, 0, 0, 0, 0)]
        [InlineData(0, 0, 1.0, 123, 456, 123, 456)]
        [InlineData(100, 200, 1.0, 50, 60, 150, 260)]           // 截图原点偏移
        [InlineData(100, 200, 0.667, 300, 400, 300, 467)]       // 150% 缩放：300*0.667=200.1→200，400*0.667=266.8→267
        [InlineData(100, 200, 1.5, 10, 20, 115, 230)]           // 放大的换算
        [InlineData(100, 200, 0.0, 50, 60, 150, 260)]           // scale<=0 按 1.0 兜底
        [InlineData(100, 200, 1.0, -50, -60, 50, 140)]          // 负图像坐标
        public void ImageToScreen_AppliesOriginAndScale(
            int ox, int oy, double scale, double ix, double iy, int ex, int ey)
        {
            AutomationContext.CaptureOriginX = ox;
            AutomationContext.CaptureOriginY = oy;

            Point p = CoordinateSpace.ImageToScreen(ix, iy, scale);

            Assert.Equal(ex, p.X);
            Assert.Equal(ey, p.Y);
        }

        [Fact]
        public void ImageToScreenScale_NullOrEmptyImage_ReturnsOne()
        {
            // 空输入直接返回 1.0，不触碰 Win32 屏幕查询（Windows/Linux 行为一致）
            Assert.Equal(1.0, CoordinateSpace.ImageToScreenScale(null));
            using var empty = new OpenCvSharp.Mat();
            Assert.Equal(1.0, CoordinateSpace.ImageToScreenScale(empty));
        }
    }
}
