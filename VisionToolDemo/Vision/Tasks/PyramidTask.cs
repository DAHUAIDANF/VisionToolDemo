using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 图像金字塔算子：高斯金字塔（逐层高斯模糊+降采样）或拉普拉斯金字塔（高斯层间差），
    /// 输出指定层级，可上采样恢复回原图尺寸（多尺度分析 / 金字塔重建）。
    /// 参数：类型（0=高斯 1=拉普拉斯）、层级（0~5，0=原尺寸）、恢复原尺寸（0=否 1=是）。
    /// 图尺寸不足时层级自动降级，保证算子永远能出图。
    /// </summary>
    public class PyramidTask : IVisionTask, IResultReporter
    {
        public string TaskName => "图像金字塔";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "类型",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=高斯金字塔（模糊+降采样） 1=拉普拉斯金字塔（层间差值，突出边缘细节）"
            },
            new TaskParamDesc
            {
                ParamName = "层级",
                Min = 0,
                Max = 5,
                DefaultValue = 1,
                DisplayFormat = "层级:{0}",
                Tip = "0=原尺寸，1=½，2=¼……；图太小会自动降级到可达层级"
            },
            new TaskParamDesc
            {
                ParamName = "恢复原尺寸",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "恢复:{0}",
                Tip = "1=把所选层级上采样回原图大小（金字塔重建，拉普拉斯常见用法）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int type = paramValues[0];
            int wantLevel = Math.Max(0, Math.Min(5, paramValues[1]));
            bool restore = paramValues[2] != 0;

            // 实际可达层级：每层降采样要求宽高都 >= 4（保证 PyrUp 能正常还原）
            int level = 0;
            {
                int w = srcMat.Cols, h = srcMat.Rows;
                while (w >= 4 && h >= 4 && level < wantLevel)
                {
                    w /= 2;
                    h /= 2;
                    level++;
                }
            }

            Mat dst;
            if (type == 0)
            {
                // ---- 高斯金字塔：逐层 高斯模糊 → 降采样 ----
                dst = srcMat.Clone();
                for (int i = 0; i < level; i++)
                {
                    Mat next = new();
                    using (Mat blur = new())
                    {
                        Cv2.GaussianBlur(dst, blur, new OpenCvSharp.Size(5, 5), 0);
                        Cv2.PyrDown(blur, next);
                    }
                    dst.Dispose();
                    dst = next;
                }
            }
            else
            {
                // ---- 拉普拉斯金字塔：L 层 = 高斯[L] - PyrUp(高斯[L+1]) ----
                // 先构建 0..level+1 层高斯金字塔（拉普拉斯第 level 层需要第 level+1 层高斯）
                var gauss = new List<Mat> { srcMat.Clone() };
                for (int i = 0; i <= level; i++)
                {
                    Mat next = new();
                    using (Mat blur = new())
                    {
                        Cv2.GaussianBlur(gauss[i], blur, new OpenCvSharp.Size(5, 5), 0);
                        Cv2.PyrDown(blur, next);
                    }
                    gauss.Add(next);
                }
                // 差值并对齐到上一层尺寸
                dst = new();
                using (Mat up = new())
                {
                    Cv2.PyrUp(gauss[level + 1], up, gauss[level].Size());
                    Cv2.Subtract(gauss[level], up, dst);
                }
                foreach (var m in gauss) m.Dispose();
            }

            // 恢复原尺寸：逐级上采样
            if (restore && level > 0)
            {
                for (int i = 0; i < level; i++)
                {
                    Mat up = new();
                    Cv2.PyrUp(dst, up);
                    dst.Dispose();
                    dst = up;
                }
            }

            string typeName = type == 0 ? "高斯" : "拉普拉斯";
            string note = (restore && level > 0) ? "，已恢复原尺寸" : "";
            LastSummary = $"{typeName}金字塔: 层级{level} {dst.Cols}×{dst.Rows}{note}";
            return dst;
        }
    }
}
