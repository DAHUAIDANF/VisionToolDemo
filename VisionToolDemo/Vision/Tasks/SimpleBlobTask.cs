using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 斑点检测：SimpleBlobDetector 多阈值迭代检测（面积+圆度+颜色过滤），
    /// 圈出每个斑点并编号。与 Blob分析（连通域统计）互补：本算子自带
    /// 尺寸/圆度筛选，对边缘模糊、灰度渐变的斑点更鲁棒。
    /// 参数：极性（0=亮 1=暗）、最小面积、圆度(%)。
    /// </summary>
    public class SimpleBlobTask : IVisionTask, IResultReporter
    {
        public string TaskName => "斑点检测";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "极性 0亮/1暗",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "前景:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 1,
                Max = 100000,
                DefaultValue = 100,
                DisplayFormat = "MinArea:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "圆度%",
                Min = 10,
                Max = 99,
                DefaultValue = 30,
                DisplayFormat = "圆度≥{0}%"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int polarity = paramValues[0] % 2;
            float minArea = Math.Max(1, paramValues[1]);
            float circMin = Math.Max(0.01f, paramValues[2] / 100f);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 多阈值迭代 + 连通域轮廓：语义等价 SimpleBlobDetector，但不依赖它的
                // Feature2D 原生入口（OpenCvSharp 该版本 features2d_Ptr_Feature2D_get 缺失，
                // Windows/Linux 一调就崩）。多阈值下同一个斑点在多个阈值都会检出，
                // 按中心点去重（阈值步长内中心基本不动）。
                var seen = new HashSet<long>();
                int count = 0;
                for (int th = 10; th <= 220; th += 10)
                {
                    using (Mat bin = new())
                    {
                        Cv2.Threshold(gray, bin, th, 255,
                            polarity == 0 ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
                        Point[][] contours;
                        HierarchyIndex[] hierarchy;
                        Cv2.FindContours(bin, out contours, out hierarchy,
                            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                        foreach (Point[] c in contours)
                        {
                            double area = Cv2.ContourArea(c);
                            if (area < minArea) continue;
                            double per = Cv2.ArcLength(c, true);
                            double circ = per <= 0 ? 0 : 4 * Math.PI * area / (per * per);
                            if (circ < circMin) continue;

                            var mm = Cv2.Moments(c);
                            int cx = (int)Math.Round(mm.M10 / Math.Max(1e-9, mm.M00));
                            int cy = (int)Math.Round(mm.M01 / Math.Max(1e-9, mm.M00));
                            // 多阈值同一斑点按中心去重（容差 6px）
                            long key = ((long)(cx / 6)) * 100000 + (cy / 6);
                            if (!seen.Add(key)) continue;

                            count++;
                            Cv2.Circle(dst, cx, cy, Math.Max(6, (int)Math.Round(Math.Sqrt(area / Math.PI))),
                                Scalar.Red, 2, LineTypes.AntiAlias);
                            Cv2.PutText(dst, count.ToString(), new Point(cx + 8, cy - 6),
                                HersheyFonts.HersheySimplex, 0.6, Scalar.Red, 2, LineTypes.AntiAlias);
                        }
                    }
                }

                LastSummary = "斑点检测: " + count + " 个 (多阈值迭代, 面积≥" + minArea + ", 圆度≥" + paramValues[2] + "%)";
                return dst;
            }
        }
    }
}
