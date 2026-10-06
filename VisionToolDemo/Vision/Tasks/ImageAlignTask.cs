using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像配准算子（相位相关）：当前图与模板（参考图）做相位相关，
    /// 算出亚像素位移，输出对齐（平移补偿）后的当前图。
    /// 用于多图自动对齐、位置漂移补偿、图像拼接前的配准。
    /// 参数：输出模式（0=对齐图 1=位移量可视化）、绘制位移箭头。
    /// </summary>
    public class ImageAlignTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "图像配准";

        /// <summary>参考图（模板）：当前图与之对齐</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=对齐后的图（位移补偿）；1=位移量灰度可视化"
            },
            new TaskParamDesc
            {
                ParamName = "绘制箭头",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "箭头:{0}",
                Tip = "在对齐图上画位移方向箭头（便于观察漂移）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "图像配准: 未设置参考图（模板）";
                return srcMat.Clone();
            }

            int mode = paramValues[0];
            bool drawArrow = paramValues[1] != 0;

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat refGray = VisionHelper.ToGray(TemplateMat))
            {
                // 相位相关（OpenCV 内部做 DFT + 互功率谱 + 逆变换找峰值）
                Point2d shift = Cv2.PhaseCorrelate(refGray, gray, null, out double response);

                double dx = shift.X, dy = shift.Y;
                Mat dst;
                if (mode == 0)
                {
                    // 对齐图：把当前图按位移平移回去
                    using (Mat M = Mat.FromPixelData(2, 3, MatType.CV_32F, new float[] { 1, 0, (float)dx, 0, 1, (float)dy }))
                    {
                        dst = new Mat();
                        Cv2.WarpAffine(srcMat, dst, M, srcMat.Size(), InterpolationFlags.Cubic,
                            BorderTypes.Replicate);
                    }
                }
                else
                {
                    // 位移量可视化：灰度图，像素值 = 距离中心的位移幅度（放大 20 倍显示）
                    dst = new Mat(gray.Rows, gray.Cols, MatType.CV_8U, Scalar.All(0));
                    int cx = dst.Cols / 2, cy = dst.Rows / 2;
                    for (int y = 0; y < dst.Rows; y++)
                        for (int x = 0; x < dst.Cols; x++)
                        {
                            double v = Math.Sqrt((x - cx - dx * 20) * (x - cx - dx * 20)
                                               + (y - cy - dy * 20) * (y - cy - dy * 20));
                            dst.At<byte>(y, x) = (byte)Math.Min(255, v * 2);
                        }
                }

                if (drawArrow && mode == 0 && dst.Channels() == 1)
                    dst = dst.CvtColor(ColorConversionCodes.GRAY2BGR);
                if (drawArrow)
                {
                    Point c = new(dst.Cols / 2, dst.Rows / 2);
                    Point e = new((int)(c.X + dx * 5), (int)(c.Y + dy * 5));
                    Cv2.ArrowedLine(dst, c, e, new Scalar(0, 0, 255), 2, LineTypes.AntiAlias, 0, 0.3);
                }

                LastSummary = $"图像配准: 位移 ΔX={dx:F1} ΔY={dy:F1} 像素（参考 vs 当前）";
                return dst;
            }
        }
    }
}
