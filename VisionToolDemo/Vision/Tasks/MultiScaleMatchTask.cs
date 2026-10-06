using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 多尺度模板匹配算子：模板在 50%~150% 多倍率下缩放，逐档做归一化相关匹配，
    /// 取全局最高分定位。抗尺度变化，适合模板与目标大小不一致的场景。
    /// 参数：最小倍率（%）、最大倍率（%）、步数（倍率档数）、匹配阈值（%）。
    /// </summary>
    public class MultiScaleMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "多尺度模板匹配";

        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        public bool Found { get; private set; }

        public double BestScore { get; private set; }

        public Point2f BestCenter { get; private set; }

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
                ParamName = "最小倍率",
                Min = 50, Max = 100, DefaultValue = 80,
                DisplayFormat = "最小:{0}%",
                Tip = "模板缩放下限（相对原尺寸）"
            },
            new TaskParamDesc
            {
                ParamName = "最大倍率",
                Min = 100, Max = 200, DefaultValue = 130,
                DisplayFormat = "最大:{0}%",
                Tip = "模板缩放上限（相对原尺寸）"
            },
            new TaskParamDesc
            {
                ParamName = "倍率步数",
                Min = 3, Max = 20, DefaultValue = 8,
                DisplayFormat = "档数:{0}",
                Tip = "在最小~最大之间分的档数（越多越慢越准）"
            },
            new TaskParamDesc
            {
                ParamName = "匹配阈值",
                Min = 50, Max = 100, DefaultValue = 75,
                DisplayFormat = "阈值:{0}%",
                Tip = "归一化相关分数下限，低于判定未命中"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Found = false;
            BestScore = 0;
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "多尺度模板匹配: 未设置模板";
                return srcMat.Clone();
            }

            int minPct = Math.Max(50, paramValues[0]);
            int maxPct = Math.Min(200, Math.Max(paramValues[1], minPct + 1));
            int steps = Math.Max(3, Math.Min(20, paramValues[2]));
            int threshPct = paramValues[3];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat tplGray = VisionHelper.ToGray(TemplateMat))
            {
                double best = -1;
                double bestScale = 1;
                Point bestLoc = default;

                // 多倍率扫描
                for (int i = 0; i < steps; i++)
                {
                    double scale = (minPct + (maxPct - minPct) * i / (double)(steps - 1)) / 100.0;
                    Size tplSize = new(
                        Math.Max(4, (int)(tplGray.Width * scale)),
                        Math.Max(4, (int)(tplGray.Height * scale)));
                    if (tplSize.Width > gray.Width || tplSize.Height > gray.Height)
                        continue;

                    using (Mat tpl = new())
                    {
                        Cv2.Resize(tplGray, tpl, tplSize, 0, 0, InterpolationFlags.Linear);
                        using (Mat res = new())
                        {
                            Cv2.MatchTemplate(gray, tpl, res, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);
                            if (maxVal > best)
                            {
                                best = maxVal;
                                bestScale = scale;
                                bestLoc = maxLoc;
                            }
                        }
                    }
                }

                BestScore = best * 100;
                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();

                if (best * 100 >= threshPct)
                {
                    Found = true;
                    int w = (int)(tplGray.Width * bestScale);
                    int h = (int)(tplGray.Height * bestScale);
                    Rect box = new(bestLoc, new Size(w, h));
                    Cv2.Rectangle(dst, box, new Scalar(0, 255, 0), 2);
                    BestCenter = new Point2f(bestLoc.X + w / 2f, bestLoc.Y + h / 2f);
                    LastSummary = $"多尺度模板匹配: 命中 分数{best * 100:F0}% 倍率{bestScale * 100:F0}% 中心{BestCenter.X:F0},{BestCenter.Y:F0}";
                }
                else
                {
                    LastSummary = $"多尺度模板匹配: 未命中（最高 {best * 100:F0}% < {threshPct}%）";
                }
                return dst;
            }
        }
    }
}
