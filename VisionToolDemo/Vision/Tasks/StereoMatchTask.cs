using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 立体匹配（SGBM 双目）：输入**左右拼接图**（左右各占一半的灰度图），
    /// 用 SGBM 计算视差图并归一化为 8U 可视化（近=亮、远=暗）。
    /// 左右相机需已行对齐（可用「标定」类算子校正后再拼接）。
    /// </summary>
    public class StereoMatchTask : IVisionTask, IResultReporter
    {
        public string TaskName => "立体匹配SGBM";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "视差范围",
                Min = 16,
                Max = 512,
                DefaultValue = 64,
                DisplayFormat = "范围:{0}",
                Tip = "搜索的视差级数（16 的倍数）：越大能测越近的物体，越慢"
            },
            new TaskParamDesc
            {
                ParamName = "块大小",
                Min = 3,
                Max = 21,
                DefaultValue = 9,
                DisplayFormat = "块:{0}",
                ForceOdd = true,
                Tip = "匹配窗口大小（奇数）：越大越平滑，边缘越糊；越小细节越多噪声越大"
            },
            new TaskParamDesc
            {
                ParamName = "视差下限",
                Min = -100,
                Max = 100,
                DefaultValue = 0,
                DisplayFormat = "下限:{0}",
                Tip = "最小视差：负值可测近处物体（物体越近视差越大）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int numDisp = Math.Max(16, (paramValues[0] / 16) * 16);   // 16 的倍数
            int block = Math.Max(3, paramValues[1] | 1);              // 奇数
            int minDisp = paramValues[2];

            // 拆左右：约定输入为左右拼接图（等宽两半）
            int half = srcMat.Cols / 2;
            if (half < 16)
            {
                LastSummary = "立体匹配: 失败 —— 图像太窄，无法拆成左右两半";
                return srcMat.Clone();
            }
            using Mat left = new Mat(srcMat, new OpenCvSharp.Rect(0, 0, half, srcMat.Rows));
            using Mat right = new Mat(srcMat, new OpenCvSharp.Rect(half, 0, srcMat.Cols - half, srcMat.Rows));
            using Mat leftG = new();
            using Mat rightG = new();
            if (left.Channels() == 3)
            {
                Cv2.CvtColor(left, leftG, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(right, rightG, ColorConversionCodes.BGR2GRAY);
            }
            else
            {
                left.CopyTo(leftG);
                right.CopyTo(rightG);
            }

            using var sgbm = StereoSGBM.Create(
                minDisp, numDisp, block,
                p1: 8 * block * block,
                p2: 32 * block * block,
                disp12MaxDiff: 1,
                preFilterCap: 63,
                uniquenessRatio: 10,
                speckleWindowSize: 100,
                speckleRange: 32,
                mode: StereoSGBM.Mode.SGBM);
            using Mat disp = new();
            sgbm.Compute(leftG, rightG, disp);   // CV_16S：值 = 视差×16

            // 视差图归一化 8U（视差×16 → 0~255）
            Cv2.MinMaxLoc(disp, out double mn, out double mx, out _, out _);
            double span = mx - mn;
            if (span < 1) span = 1;
            Mat dst = new();
            disp.ConvertTo(dst, MatType.CV_8U, 255.0 / span, -mn * 255.0 / span);
            LastSummary = string.Format("立体匹配: 视差 {0:F1}~{1:F1}（×16）", mn / 16.0, mx / 16.0);
            return dst;
        }
    }
}
