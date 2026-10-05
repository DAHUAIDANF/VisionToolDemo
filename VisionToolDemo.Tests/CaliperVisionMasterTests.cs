using OpenCvSharp;
using VisionToolDemo.Vision.Tasks;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// VisionMaster 化卡尺回归测试：
    /// 1) 直线卡尺"边缘极性"：黑→白边界只被 暗到亮(2) 检出，亮到暗(1) 必须不报；
    /// 2) 圆卡尺"边缘极性 + 角度范围"：白圆黑底（径向亮→暗）只被 亮到暗(1) 检出，
    ///    90° 圆弧段也足够拟合出圆。
    /// </summary>
    public class CaliperVisionMasterTests
    {
        private static Mat MakeHalfBlackHalfWhite()
        {
            // 64x64：左半黑(0) 右半白(255)，竖边界在 x=32
            var m = new Mat(64, 64, MatType.CV_8UC1, new Scalar(0));
            for (int y = 0; y < 64; y++)
                for (int x = 32; x < 64; x++)
                    m.Set<byte>(y, x, 255);
            return m;
        }

        private static Mat MakeWhiteCircleOnBlack()
        {
            // 64x64 黑底，圆心(32,32) 半径 12 的白圆
            var m = new Mat(64, 64, MatType.CV_8UC1, new Scalar(0));
            Cv2.Circle(m, new Point(32, 32), 12, new Scalar(255), -1);
            return m;
        }

        [Fact]
        public void LineCaliper_PolarDarkToLight_DetectsRisingEdgeOnly()
        {
            using var src = MakeHalfBlackHalfWhite();
            var task = new CaliperTask();
            // 卡尺线横跨边界：左(10,32) → 右(54,32)，扫描方向向右：黑→白 = 暗到亮
            task.SetCaliper(new Point(10, 32), new Point(54, 32));

            // 极性=2（暗到亮）：应检出上升沿
            using var out2 = task.Execute(src, new[] { 20, 100, 2 });
            Assert.Contains("暗到亮", task.LastSummary);
            Assert.Contains("上升沿", task.LastSummary);

            // 极性=1（亮到暗）：不应检出任何边缘
            using var out1 = task.Execute(src, new[] { 20, 100, 1 });
            Assert.Contains("未检出", task.LastSummary);

            // 极性=0（任意）：两条都出（本图只有一条有效边）
            using var out0 = task.Execute(src, new[] { 20, 100, 0 });
            Assert.Contains("直线卡尺", task.LastSummary);
        }

        [Fact]
        public void CircleCaliper_PolarLightToDark_FitsCircle()
        {
            using var src = MakeWhiteCircleOnBlack();
            var task = new CircleCaliperTask();
            task.SetCircle(new Point(32, 32), 12);

            // 白圆黑底：径向向外 = 亮→暗，极性=1 应拟合成功
            using var out1 = task.Execute(src, new[] { 20, 100, 0, 360, 1 });
            Assert.True(task.FitOk, "亮到暗极性应检出边缘并拟合出圆：" + task.LastSummary);
            Assert.True(task.FitPointCount >= 5);

            // 极性=2（暗到亮）：黑底上无上升沿，不应拟合
            using var out2 = task.Execute(src, new[] { 20, 100, 0, 360, 2 });
            Assert.False(task.FitOk);
        }

        [Fact]
        public void CircleCaliper_AngleRange_90Degrees_StillFits()
        {
            using var src = MakeWhiteCircleOnBlack();
            var task = new CircleCaliperTask();
            task.SetCircle(new Point(32, 32), 12);

            // 只扫 0°~90° 四分之一圆弧（起角0 止角90，极性任意），点数应足够拟合
            using var out0 = task.Execute(src, new[] { 20, 100, 0, 90, 0 });
            Assert.True(task.FitOk, "90° 圆弧段应能拟合：" + task.LastSummary);
            Assert.True(task.FitPointCount >= 5);
        }
    }
}
