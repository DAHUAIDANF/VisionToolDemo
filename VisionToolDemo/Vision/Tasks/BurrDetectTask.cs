using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 毛刺/尖角检测算子：沿轮廓逐点计算拐角（相邻向量夹角），
    /// 夹角小于阈值的点判定为尖角/毛刺，在原图上画红圈标记。
    /// 用于去毛刺检查、冲压件/注塑件飞边检测。
    /// 参数：角度阈值（10~170，越小越尖）、最小跨度（滤波窗口）、检测模式。
    /// </summary>
    public class BurrDetectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "毛刺尖角检测";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "角度阈值",
                Min = 10, Max = 170, DefaultValue = 60,
                DisplayFormat = "角度:{0}°",
                Tip = "拐角小于该角度判定为尖角/毛刺（越小越严苛）"
            },
            new TaskParamDesc
            {
                ParamName = "滤波窗口",
                Min = 1, Max = 15, DefaultValue = 5,
                DisplayFormat = "窗口:{0}",
                Tip = "轮廓平滑窗口（越大越忽略小锯齿）"
            },
            new TaskParamDesc
            {
                ParamName = "最小长度",
                Min = 1, Max = 50, DefaultValue = 8,
                DisplayFormat = "长度:{0}",
                Tip = "毛刺最小长度（像素），短于此忽略"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int angleThresh = paramValues[0];
            int window = Math.Max(1, paramValues[1]);
            int minLen = Math.Max(1, paramValues[2]);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 二值化 + 找最大轮廓
                Mat bin = new();
                Cv2.Threshold(gray, bin, 128, 255, ThresholdTypes.Binary);
                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(bin, out contours, out hierarchy,
                    RetrievalModes.List, ContourApproximationModes.ApproxNone);

                // 输出：彩色底 + 标记
                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();

                int total = 0;
                foreach (var c in contours)
                {
                    if (c.Length < window * 2 + 2) continue;
                    total += DetectBurrs(c, dst, angleThresh, window, minLen);
                }
                bin.Dispose();

                LastSummary = $"毛刺尖角检测: 检出 {total} 处（角度<{angleThresh}° 长度>{minLen}px）";
                return dst;
            }
        }

        /// <summary>沿轮廓检测尖角并画圈，返回检出数</summary>
        private static int DetectBurrs(Point[] contour, Mat dst, int angleThresh, int window, int minLen)
        {
            int n = contour.Length;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                Point p = contour[i];
                Point a = contour[(i - window + n) % n];
                Point b = contour[(i + window) % n];

                // 向量与夹角
                double v1x = p.X - a.X, v1y = p.Y - a.Y;
                double v2x = b.X - p.X, v2y = b.Y - p.Y;
                double l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
                double l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                if (l1 < 1 || l2 < 1) continue;

                double cos = (v1x * v2x + v1y * v2y) / (l1 * l2);
                cos = Math.Max(-1, Math.Min(1, cos));
                double ang = Math.Acos(cos) * 180.0 / Math.PI;

                // 毛刺长度：从 p 到两侧中点的距离
                double len = Math.Min(l1, l2);

                if (ang < angleThresh && len >= minLen)
                {
                    Cv2.Circle(dst, p, 6, new Scalar(0, 0, 255), 2);
                    count++;
                    i += window;   // 跳过相邻重复点
                }
            }
            return count;
        }
    }
}
