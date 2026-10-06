using System;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 骨架化（细化）算子：Zhang-Suen 迭代细化，把二值图目标细化成单像素骨架线。
    /// 用于细线测量、字符细化、导线/血管中心线提取。
    /// 参数：二值化阈值（0~255）、最大迭代（1~20，0=自动收敛）。
    /// </summary>
    public class SkeletonTask : IVisionTask, IResultReporter
    {
        public string TaskName => "骨架化";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "二值化阈值",
                Min = 0, Max = 255, DefaultValue = 128,
                DisplayFormat = "阈值:{0}",
                Tip = "灰度转二值的前景阈值（大于等于阈值视为目标）"
            },
            new TaskParamDesc
            {
                ParamName = "最大迭代",
                Min = 0, Max = 20, DefaultValue = 0,
                DisplayFormat = "迭代:{0}",
                Tip = "细化迭代上限；0=自动迭代到收敛"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int thresh = Math.Max(0, Math.Min(255, paramValues[0]));
            int maxIter = paramValues[1];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 二值化：前景 = 255
                Mat bin = new();
                Cv2.Threshold(gray, bin, thresh, 255, ThresholdTypes.Binary);

                int changed = 1, iter = 0;
                while (changed > 0 && (maxIter <= 0 || iter < maxIter))
                {
                    changed = 0;
                    // 步骤 1
                    changed += ZhangSuenPass(bin, 1);
                    // 步骤 2
                    changed += ZhangSuenPass(bin, 2);
                    iter++;
                }

                // 骨架与输入同尺寸二值图
                LastSummary = $"骨架化: 迭代{iter} 次（阈值{thresh}）";
                return bin;
            }
        }

        /// <summary>Zhang-Suen 单次遍历（step=1 或 2），返回被删除（变 0）的像素数</summary>
        private static int ZhangSuenPass(Mat bin, int step)
        {
            int rows = bin.Rows, cols = bin.Cols;
            byte[] data = new byte[rows * cols];
            System.Runtime.InteropServices.Marshal.Copy(bin.Data, data, 0, data.Length);
            byte[] outData = (byte[])data.Clone();

            int deleted = 0;
            for (int y = 1; y < rows - 1; y++)
            {
                for (int x = 1; x < cols - 1; x++)
                {
                    int idx = y * cols + x;
                    if (data[idx] == 0) continue;   // 背景跳过

                    // 8 邻域（P2 上、P3 右上、P4 右、P5 右下、P6 下、P7 左下、P8 左、P9 左上）
                    int p2 = data[(y - 1) * cols + x] > 0 ? 1 : 0;
                    int p3 = data[(y - 1) * cols + (x + 1)] > 0 ? 1 : 0;
                    int p4 = data[y * cols + (x + 1)] > 0 ? 1 : 0;
                    int p5 = data[(y + 1) * cols + (x + 1)] > 0 ? 1 : 0;
                    int p6 = data[(y + 1) * cols + x] > 0 ? 1 : 0;
                    int p7 = data[(y + 1) * cols + (x - 1)] > 0 ? 1 : 0;
                    int p8 = data[y * cols + (x - 1)] > 0 ? 1 : 0;
                    int p9 = data[(y - 1) * cols + (x - 1)] > 0 ? 1 : 0;

                    // 条件 A：前景邻域数 2~6
                    int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                    if (b < 2 || b > 6) continue;

                    // 条件 B：0→1 的跃迁数 = 1
                    int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0)
                          + (p4 == 0 && p5 == 1 ? 1 : 0) + (p5 == 0 && p6 == 1 ? 1 : 0)
                          + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0)
                          + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                    if (a != 1) continue;

                    if (step == 1)
                    {
                        // 条件 C：P2*P4*P6 = 0；条件 D：P4*P6*P8 = 0
                        if (p2 * p4 * p6 != 0) continue;
                        if (p4 * p6 * p8 != 0) continue;
                    }
                    else
                    {
                        // 条件 C'：P2*P4*P8 = 0；条件 D'：P2*P6*P8 = 0
                        if (p2 * p4 * p8 != 0) continue;
                        if (p2 * p6 * p8 != 0) continue;
                    }

                    outData[idx] = 0;   // 删除该像素
                    deleted++;
                }
            }

            if (deleted > 0)
                Marshal.Copy(outData, 0, bin.Data, outData.Length);
            return deleted;
        }
    }
}
