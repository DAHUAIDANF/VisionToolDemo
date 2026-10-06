using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 特征点匹配定位算子（ORB + 汉明距离匹配）：用模板图在当前图中找特征点，
    /// 通过单应性把模板角点投影到当前图，画出定位框。
    /// 对旋转/光照/部分遮挡比模板匹配更鲁棒。
    /// 参数：特征点数（每图上限）、最小匹配数（低于判定未命中）、绘制匹配（画框+连线）。
    /// </summary>
    public class FeatureMatchTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "特征点匹配定位";

        /// <summary>UI 层赋值：选择的模板图片</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>本次是否命中（供自动化"条件"节点分支用）</summary>
        public bool Found { get; private set; }

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
                ParamName = "特征点数",
                Min = 200,
                Max = 3000,
                DefaultValue = 800,
                DisplayFormat = "特征:{0}",
                Tip = "每张图提取的特征点上限（越多越慢、越准）"
            },
            new TaskParamDesc
            {
                ParamName = "最小匹配数",
                Min = 4,
                Max = 100,
                DefaultValue = 12,
                DisplayFormat = "匹配:{0}",
                Tip = "合格匹配对数下限，低于此值判定未命中"
            },
            new TaskParamDesc
            {
                ParamName = "绘制匹配",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "绘制:{0}",
                Tip = "1=结果图画出定位框；0=只出原图"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Found = false;
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "特征点匹配定位: 未设置模板";
                return srcMat.Clone();
            }

            int nFeatures = paramValues[0];
            int minMatches = Math.Max(4, paramValues[1]);
            bool draw = paramValues[2] != 0;

            using (var gray = VisionHelper.ToGray(srcMat))
            using (var tplGray = VisionHelper.ToGray(TemplateMat))
            using (var orb = ORB.Create(nFeatures))
            {
                Mat descSrc = new();
                orb.DetectAndCompute(gray, null, out KeyPoint[] kpSrc, descSrc);
                Mat descTpl = new();
                orb.DetectAndCompute(tplGray, null, out KeyPoint[] kpTpl, descTpl);

                if (descSrc.Empty() || descTpl.Empty())
                {
                    descSrc.Dispose(); descTpl.Dispose();
                    LastSummary = $"特征点匹配定位: 未提取到特征点（源 {kpSrc.Length} / 模板 {kpTpl.Length}）";
                    return srcMat.Clone();
                }

                // 汉明距离匹配：模板 → 源
                List<DMatch> good = new();
                using (var bf = new BFMatcher(NormTypes.Hamming, false))
                {
                    var matches = bf.Match(descTpl, descSrc);
                    // 按距离升序取前 2/3，距离越小越相似
                    var sorted = matches.OrderBy(m => m.Distance).Take(Math.Max(minMatches * 2, matches.Length * 2 / 3)).ToList();
                    // 再按最近邻距离比筛选（dist1 / dist2 < 0.8 是好匹配）
                    foreach (var m in sorted)
                        if (m.Distance < 64)   // 汉明距离阈值（ORB 描述子 32 字节，<64 相当严格）
                            good.Add(m);
                }
                descSrc.Dispose(); descTpl.Dispose();

                Mat result;
                if (good.Count < minMatches)
                {
                    LastSummary = $"特征点匹配定位: 未命中（匹配 {good.Count}/{minMatches}）";
                    Found = false;
                    result = srcMat.Clone();
                    if (draw && result.Channels() == 1)
                        result = result.CvtColor(ColorConversionCodes.GRAY2BGR);
                    return result;
                }

                // 用匹配点求单应性（RANSAC），把模板四角投影到当前图
                Point2f[] srcPts = good.Select(m => kpTpl[m.QueryIdx].Pt).ToArray();
                Point2f[] dstPts = good.Select(m => kpSrc[m.TrainIdx].Pt).ToArray();
                Point2f center = default;

                using (Mat srcPtsMat = Mat.FromPixelData(srcPts.Length, 1, MatType.CV_32FC2, srcPts))
                using (Mat dstPtsMat = Mat.FromPixelData(dstPts.Length, 1, MatType.CV_32FC2, dstPts))
                using (Mat H = Cv2.FindHomography(srcPtsMat, dstPtsMat, HomographyMethods.Ransac, 4.0))
                {
                    // 模板四角（原图坐标）
                    Point2f[] corners =
                    {
                        new(0, 0),
                        new(TemplateMat.Width, 0),
                        new(TemplateMat.Width, TemplateMat.Height),
                        new(0, TemplateMat.Height),
                    };
                    using (Mat cornersMat = Mat.FromPixelData(4, 1, MatType.CV_32FC2, corners))
                    {
                        // 投影到当前图
                        Point2f[] projected;
                        try
                        {
                            Mat dst = new();
                            Cv2.PerspectiveTransform(cornersMat, dst, H);
                            projected = new Point2f[4];
                            for (int i = 0; i < 4; i++)
                                projected[i] = dst.At<Point2f>(i, 0);
                            dst.Dispose();
                        }
                        catch
                        {
                            projected = corners;
                        }

                        // 输出图：彩色底 + 定位框
                        result = srcMat.Channels() == 1
                            ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                            : srcMat.Clone();

                        if (draw)
                        {
                            // 投影框
                            for (int i = 0; i < 4; i++)
                            {
                                Point a = new((int)projected[i].X, (int)projected[i].Y);
                                Point b = new((int)projected[(i + 1) % 4].X, (int)projected[(i + 1) % 4].Y);
                                Cv2.Line(result, a, b, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
                            }
                            // 中心点
                            center = (projected[0] + projected[2]) * 0.5f;
                            Cv2.Circle(result, (int)center.X, (int)center.Y, 4, new Scalar(0, 0, 255), -1);
                            // 匹配点连线（前 24 对）
                            foreach (var m in good.Take(24))
                            {
                                Point a = new((int)kpTpl[m.QueryIdx].Pt.X, (int)kpTpl[m.QueryIdx].Pt.Y);
                                Point b = new((int)kpSrc[m.TrainIdx].Pt.X, (int)kpSrc[m.TrainIdx].Pt.Y);
                                Cv2.Line(result, a, b, new Scalar(255, 180, 0), 1, LineTypes.AntiAlias);
                            }
                        }
                        Found = true;
                        LastSummary = $"特征点匹配定位: 命中（匹配 {good.Count} 对，中心 {center.X:F0},{center.Y:F0}）";
                    }
                }
                return result;
            }
        }
    }
}
