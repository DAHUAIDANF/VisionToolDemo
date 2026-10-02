using System;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 特征匹配（ORB）：对模板图与当前图提取 ORB 特征，BFMatcher(Hamming,
    /// 交叉检验) 匹配后按距离取最优若干对，绿线连接、红点标记当前图侧特征点；
    /// 摘要输出特征数/匹配对数/平均距离。
    /// 模板图由 UI 层"导入模板"注入（TemplateMat）。
    /// 参数：最大特征点数、显示匹配对数。
    /// </summary>
    public class FeatureMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "特征匹配";

        /// <summary>UI层赋值：标准模板图片</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>状态 = 模板图（base64 PNG）</summary>
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
                ParamName = "最大特征点数",
                Min = 100,
                Max = 2000,
                DefaultValue = 500,
                DisplayFormat = "特征:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "显示匹配对数",
                Min = 5,
                Max = 100,
                DefaultValue = 20,
                DisplayFormat = "Top:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "特征匹配: 请先导入模板图片";
                return dst;
            }

            int nFeatures = paramValues[0];
            int topK = paramValues[1];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat templGray = VisionHelper.ToGray(TemplateMat))
            using (ORB orb = ORB.Create(nFeatures))
            using (Mat descSrc = new())
            using (Mat descTpl = new())
            {
                orb.DetectAndCompute(gray, null, out KeyPoint[] kpSrc, descSrc);
                orb.DetectAndCompute(templGray, null, out KeyPoint[] kpTpl, descTpl);

                if (kpSrc.Length < 2 || kpTpl.Length < 2)
                {
                    LastSummary = "特征匹配: 特征点不足 (当前图 " + kpSrc.Length + " / 模板 " + kpTpl.Length + ")";
                    return dst;
                }

                using (BFMatcher matcher = new(NormTypes.Hamming, true))
                {
                    DMatch[] matches = matcher.Match(descSrc, descTpl);
                    DMatch[] good = matches.OrderBy(m => m.Distance).Take(topK).ToArray();

                    foreach (DMatch m in good)
                    {
                        Point2f a = kpSrc[m.QueryIdx].Pt;
                        Point2f b = kpTpl[m.TrainIdx].Pt;
                        Cv2.Line(dst,
                            new Point((int)Math.Round(a.X), (int)Math.Round(a.Y)),
                            new Point((int)Math.Round(b.X), (int)Math.Round(b.Y)),
                            Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                        Cv2.Circle(dst, (int)Math.Round(a.X), (int)Math.Round(a.Y), 3, Scalar.Red, -1, LineTypes.AntiAlias);
                    }

                    double avg = good.Length > 0 ? good.Average(m => m.Distance) : 0;
                    LastSummary = string.Format(
                        "特征匹配: 特征 {0}/{1}, 匹配 {2} 对 (显示前 {3}, 平均距离 {4:F1})",
                        kpSrc.Length, kpTpl.Length, matches.Length, good.Length, avg);
                    return dst;
                }
            }
        }
    }
}
