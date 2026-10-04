using System;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 纹理分析（GLCM 灰度共生矩阵）：对比度 / 能量 / 熵 / 同质性 / 相关性。
    ///
    /// 用途：表面纹理缺陷（划痕、橘皮、织纹、粗糙度变化）在灰度直方图上常常
    /// 区分不出来，但像素对的**空间共生关系**会明显变化——GLCM 就是量化这个关系的。
    /// 例如：
    ///   · 粗糙/噪声表面 → 对比度、熵高，能量低；
    ///   · 光滑均匀表面 → 能量、同质性高，对比度低；
    ///   · 方向性纹理（拉丝/织纹）→ 沿纹理方向的"相关性"显著高于垂直方向。
    ///
    /// 输出：原图拷贝 + 4 个方向（0°/45°/90°/135°，距离=1 像素）的 5 项特征均值，
    /// 摘要里给出全部数值，可配合「规则判定/表达式」节点做阈值判断。
    /// </summary>
    public class GlcmTextureTask : IVisionTask, IResultReporter
    {
        public string TaskName => "纹理分析GLCM";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次各方向的对比度均值（供规则节点用）</summary>
        public double Contrast { get; private set; }
        public double Energy { get; private set; }
        public double Entropy { get; private set; }
        public double Homogeneity { get; private set; }
        public double Correlation { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "量化灰度级",
                Min = 8,
                Max = 64,
                DefaultValue = 16,
                DisplayFormat = "灰度级:{0}",
                Tip = "灰度量化到 N 级再统计共生（8/16/32/64）。级数越高越精细但越慢、越稀疏；纹理粗用 16，细纹理用 32"
            },
            new TaskParamDesc
            {
                ParamName = "像素距离",
                Min = 1,
                Max = 8,
                DefaultValue = 1,
                DisplayFormat = "距离:{0}",
                Tip = "共生像素对的距离（像素）。1=相邻像素（细纹理），2~4=稍粗的周期纹理"
            },
            new TaskParamDesc
            {
                ParamName = "方向 0全向1水平2垂直3对角",
                Min = 0,
                Max = 3,
                DefaultValue = 0,
                DisplayFormat = "方向:{0}",
                Tip = "0=四个方向取平均（各向同性纹理）1=0°水平 2=90°垂直 3=135°对角（方向性纹理如拉丝/织纹选对应方向）"
            },
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int levels = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 16, 8, 64);
            int dist = Math.Clamp(paramValues.Length > 1 ? paramValues[1] : 1, 1, 8);
            int dir = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 0, 0, 3);

            using Mat gray = VisionHelper.ToGray(srcMat);
            // 量化到 [0, levels)
            using Mat q = new();
            gray.ConvertTo(q, MatType.CV_32FC1, levels / 256.0);
            int rows = q.Rows, cols = q.Cols;
            float[] px = new float[rows * cols];
            q.GetArray(out px);

            // 四个方向的像素对（dx, dy）：0°=(1,0) 45°=(1,1) 90°=(0,1) 135°=(1,-1)
            (int dx, int dy)[] dirs = { (1, 0), (1, 1), (0, 1), (1, -1) };
            double cSum = 0, eSum = 0, hSum = 0, nSum = 0, corrSum = 0;
            int used = 0;

            for (int d = 0; d < 4; d++)
            {
                if (dir != 0 && dir - 1 != d) continue;   // 只算指定方向
                (int dx, int dy) = dirs[d];
                long[,] glcm = new long[levels, levels];
                long total = 0;

                for (int y = 0; y < rows; y++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= rows) continue;
                    for (int x = 0; x < cols; x++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= cols) continue;
                        int i = (int)px[y * cols + x];
                        int j = (int)px[ny * cols + nx];
                        if (i < 0 || i >= levels || j < 0 || j >= levels) continue;
                        glcm[i, j]++;
                        total++;
                    }
                }

                if (total == 0) continue;

                // 归一化概率 + 行/列边际概率（相关性用）
                double[,] p = new double[levels, levels];
                double[] pr = new double[levels];
                double[] pc = new double[levels];
                for (int i = 0; i < levels; i++)
                    for (int j = 0; j < levels; j++)
                    {
                        p[i, j] = glcm[i, j] / (double)total;
                        pr[i] += p[i, j];
                        pc[j] += p[i, j];
                    }

                double contrast = 0, energy = 0, entropy = 0, homogeneity = 0;
                for (int i = 0; i < levels; i++)
                    for (int j = 0; j < levels; j++)
                    {
                        double v = p[i, j];
                        if (v <= 0) continue;
                        contrast += (i - j) * (i - j) * v;
                        energy += v * v;
                        entropy -= v * Math.Log(v + 1e-12);
                        homogeneity += v / (1.0 + (i - j) * (i - j));
                    }

                // 相关性（需均值/标准差，对边际分布计算）
                double muI = 0, muJ = 0;
                for (int i = 0; i < levels; i++) { muI += i * pr[i]; muJ += i * pc[i]; }
                double si = 0, sj = 0;
                for (int i = 0; i < levels; i++)
                {
                    si += pr[i] * (i - muI) * (i - muI);
                    sj += pc[i] * (i - muJ) * (i - muJ);
                }
                si = Math.Sqrt(si); sj = Math.Sqrt(sj);
                double corr = 0;
                if (si > 1e-9 && sj > 1e-9)
                {
                    for (int i = 0; i < levels; i++)
                        for (int j = 0; j < levels; j++)
                            corr += p[i, j] * (i - muI) * (j - muJ) / (si * sj);
                }

                cSum += contrast; eSum += energy; hSum += homogeneity; nSum += entropy; corrSum += corr;
                used++;
            }

            if (used == 0)
            {
                LastSummary = "纹理分析GLCM: 图像太小，没有可统计的像素对";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            Contrast = cSum / used;
            Energy = eSum / used;
            Homogeneity = hSum / used;
            Entropy = nSum / used;
            Correlation = corrSum / used;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            string[] dirNames = { "全向", "水平", "垂直", "对角" };
            var sb = new StringBuilder();
            sb.AppendFormat("纹理分析GLCM: {0} 对比度={1:F3} 能量={2:F4} 熵={3:F3} 同质性={4:F3} 相关性={5:F3}",
                dirNames[dir], Contrast, Energy, Entropy, Homogeneity, Correlation);
            LastSummary = sb.ToString();
            return dst;
        }
    }
}
