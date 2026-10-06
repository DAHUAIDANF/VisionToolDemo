using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 背景差分运动检测算子：保存背景帧（首帧或按学习率更新），
    /// 当前帧与背景做差 → 阈值 → 运动目标二值掩膜。
    /// 用于固定场景运动目标提取、入侵检测、工件到位判断。
    /// 参数：背景学习率（0=固定背景）、差分阈值、输出模式。
    /// </summary>
    public class BgDiffTask : IVisionTask, IResultReporter
    {
        public string TaskName => "背景差分";

        // 背景帧（跨 Execute 保存）
        private Mat _background;

        public string LastSummary { get; private set; } = "";

        public int MotionArea { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "背景学习率",
                Min = 0, Max = 100, DefaultValue = 0,
                DisplayFormat = "学习率:{0}",
                Tip = "0=固定首帧为背景；>0=每帧按该比例更新背景（适应光照缓慢变化）"
            },
            new TaskParamDesc
            {
                ParamName = "差分阈值",
                Min = 5, Max = 100, DefaultValue = 25,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度差超过该值判为运动（越小越灵敏）"
            },
            new TaskParamDesc
            {
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=运动掩膜（白=运动）；1=原图叠加红色运动区域"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            MotionArea = 0;
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int learnPct = Math.Max(0, Math.Min(100, paramValues[0]));
            int diffThresh = Math.Max(5, Math.Min(100, paramValues[1]));
            int mode = paramValues[2];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 首帧 / 尺寸变化时重置背景
                if (_background == null || _background.Size() != gray.Size())
                {
                    _background?.Dispose();
                    _background = gray.Clone();
                    LastSummary = "背景差分: 已建立背景（首帧），再次执行输出差分";
                    return new Mat(gray.Rows, gray.Cols, MatType.CV_8U, Scalar.All(0));
                }

                // 差分 → 阈值
                using (Mat diff = new())
                {
                    Cv2.Absdiff(_background, gray, diff);
                    using (Mat mask = new())
                    {
                        Cv2.Threshold(diff, mask, diffThresh, 255, ThresholdTypes.Binary);
                        MotionArea = Cv2.CountNonZero(mask);

                        // 学习率更新背景
                        if (learnPct > 0)
                        {
                            double alpha = learnPct / 100.0;
                            Cv2.AddWeighted(_background, 1 - alpha, gray, alpha, 0, _background);
                        }

                        if (mode == 0)
                        {
                            LastSummary = $"背景差分: 运动像素 {MotionArea}（阈值{diffThresh}）";
                            return mask.Clone();
                        }

                        // 原图叠加红色运动区域
                        Mat dst = srcMat.Channels() == 1
                            ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                            : srcMat.Clone();
                        dst.SetTo(new Scalar(0, 0, 255), mask);
                        LastSummary = $"背景差分: 运动像素 {MotionArea}（红色标记）";
                        return dst;
                    }
                }
            }
        }
    }
}
