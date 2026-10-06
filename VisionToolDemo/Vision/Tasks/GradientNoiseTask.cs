using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 渐变/杂色算子（PS 风格）：生成线性/径向渐变背景，或在图上叠加杂色噪声。
    /// 参数：模式（0=线性渐变 1=径向渐变 2=杂色叠加）、灰度/彩色、强度、方向。
    /// </summary>
    public class GradientNoiseTask : IVisionTask, IResultReporter
    {
        public string TaskName => "渐变杂色";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "模式",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=线性渐变 1=径向渐变 2=杂色叠加"
            },
            new TaskParamDesc
            {
                ParamName = "颜色",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "彩色:{0}",
                Tip = "0=灰度渐变/杂色；1=彩色（蓝→橙渐变、彩色杂色）"
            },
            new TaskParamDesc
            {
                ParamName = "强度",
                Min = 1, Max = 100, DefaultValue = 40,
                DisplayFormat = "强度:{0}",
                Tip = "渐变明暗对比或杂色强度（%）"
            },
            new TaskParamDesc
            {
                ParamName = "方向",
                Min = 0, Max = 3, DefaultValue = 0,
                DisplayFormat = "方向:{0}",
                Tip = "线性渐变方向：0=左→右 1=上→下 2=左上→右下 3=环形；杂色模式忽略"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int mode = paramValues[0];
            bool color = paramValues[1] != 0;
            int intensity = Math.Max(1, paramValues[2]);
            int direction = paramValues[3] % 4;

            if (mode == 2)
            {
                // 杂色叠加：原图 + 随机噪声
                Mat dst = srcMat.Clone();
                using (Mat noise = new Mat(srcMat.Rows, srcMat.Cols, srcMat.Type()))
                {
                    Cv2.Randu(noise, new Scalar(0), new Scalar(255));
                    double alpha = intensity / 100.0;
                    Cv2.AddWeighted(dst, 1 - alpha * 0.5, noise, alpha * 0.5, 0, dst);
                }
                LastSummary = $"渐变杂色: 杂色叠加（强度{intensity}%）";
                return dst;
            }

            // 渐变：生成渐变底图，与原图混合（透明度=强度）
            Mat grad = new Mat(srcMat.Rows, srcMat.Cols, color ? MatType.CV_8UC3 : MatType.CV_8UC1);
            int w = grad.Cols, h = grad.Rows;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double t;
                    switch (direction)
                    {
                        case 0: t = (double)x / Math.Max(1, w - 1); break;
                        case 1: t = (double)y / Math.Max(1, h - 1); break;
                        case 2: t = ((double)x / w + (double)y / h) / 2.0; break;
                        default:
                            double dx2 = x - w / 2.0, dy2 = y - h / 2.0;
                            t = Math.Sqrt(dx2 * dx2 + dy2 * dy2) / Math.Max(1, Math.Sqrt(w * w + h * h) / 2);
                            break;
                    }
                    byte v = (byte)(20 + t * 235 * intensity / 100.0);
                    if (color)
                    {
                        byte r = (byte)((1 - t) * 60 + t * 255);
                        byte g = (byte)((1 - t) * 120 + t * 140);
                        byte b = (byte)((1 - t) * 230 + t * 40);
                        grad.At<Vec3b>(y, x) = new Vec3b(b, g, r);
                    }
                    else
                    {
                        grad.At<byte>(y, x) = v;
                    }
                }
            }

            // 混合
            Mat dst2 = new();
            double mix = intensity / 100.0;
            if (srcMat.Channels() == grad.Channels())
                Cv2.AddWeighted(srcMat, 1 - mix, grad, mix, 0, dst2);
            else if (grad.Channels() == 1)
            {
                using (Mat gray = VisionHelper.ToGray(srcMat))
                    Cv2.AddWeighted(gray, 1 - mix, grad, mix, 0, dst2);
            }
            else
            {
                using (Mat gray = VisionHelper.ToGray(srcMat))
                using (Mat g3 = new())
                {
                    Cv2.CvtColor(gray, g3, ColorConversionCodes.GRAY2BGR);
                    Cv2.AddWeighted(g3, 1 - mix, grad, mix, 0, dst2);
                }
            }

            string[] names = { "线性", "径向", "杂色" };
            LastSummary = $"渐变杂色: {names[mode]}渐变（强度{intensity}%）";
            return dst2;
        }
    }
}
