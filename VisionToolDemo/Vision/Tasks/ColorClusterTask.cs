using System;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 颜色聚类：KMeans 把像素按颜色聚成 K 簇并以簇中心色重建图像，
    /// 摘要输出各簇占比。用于分色统计、色块计数、背景/前景粗分割。
    /// 参数：聚类数 K、迭代次数。
    /// </summary>
    public class ColorClusterTask : IVisionTask, IResultReporter
    {
        public string TaskName => "颜色聚类";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "聚类数",
                Min = 2,
                Max = 8,
                DefaultValue = 4,
                DisplayFormat = "K:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "迭代次数",
                Min = 1,
                Max = 50,
                DefaultValue = 10,
                DisplayFormat = "迭代:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int k = Math.Min(8, Math.Max(2, paramValues[0]));
            int iters = Math.Max(1, paramValues[1]);

            Mat dst = new(srcMat.Size(), MatType.CV_8UC3);
            using (Mat labels = new())
            using (Mat centers = new())
            using (Mat f32 = new())
            {
                // 颜色聚类按像素 3 通道做 Kmeans，所以输入必须规整成 8U 的 3 通道 BGR。
                // 单通道灰度（Reshape(3,..) 会报 "total width is not divisible by channels"）
                // 与 16 位 RAW 都在这里被统一处理。
                using Mat bgr = VisionHelper.ToBgrCopy(srcMat);

                // Reshape 要求内存连续；ROI 选区传入的是子矩阵（不连续）会抛异常。
                // 先 ConvertTo（输出总是新分配、连续），直接把 Reshape 视图喂给 Kmeans（只读），
                // 省去一次整图浮点拷贝
                bgr.ConvertTo(f32, MatType.CV_32F);
                using (Mat samples = f32.Reshape(3, f32.Rows * f32.Cols))
                {
                    Cv2.Kmeans(samples, k, labels,
                        new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, iters, 1.0),
                        3, KMeansFlags.RandomCenters, centers);
                }

                unsafe
                {
                    byte* dp = (byte*)dst.Data;
                    int* lp = (int*)labels.Data;
                    float* cp = (float*)centers.Data;
                    int n = srcMat.Rows * srcMat.Cols;
                    long[] cnt = new long[k];
                    for (int i = 0; i < n; i++)
                    {
                        int l = lp[i];
                        cnt[l]++;
                        dp[(i * 3) + 0] = (byte)Math.Round(cp[(l * 3) + 0]);
                        dp[(i * 3) + 1] = (byte)Math.Round(cp[(l * 3) + 1]);
                        dp[(i * 3) + 2] = (byte)Math.Round(cp[(l * 3) + 2]);
                    }
                    long sum = cnt.Sum();
                    string parts = string.Join(" ",
                        cnt.Select(c => (sum > 0 ? c * 100.0 / sum : 0).ToString("F0") + "%"));
                    LastSummary = "颜色聚类: K=" + k + " → " + parts;
                }

                return dst;
            }
        }
    }
}
