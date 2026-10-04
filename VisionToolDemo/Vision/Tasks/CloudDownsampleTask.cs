using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 点云体素降采样：输入 XYZ 坐标图（CV_32FC3，来自「深度转点云」），
    /// 按体素网格抽稀——每个体素内只保留第一个点，其余置 NaN（无效）。
    /// 点云过大时显著减少后续处理量，保持几何形状。
    /// </summary>
    public class CloudDownsampleTask : IVisionTask, IResultReporter
    {
        public string TaskName => "点云体素降采样";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "体素尺寸x100",
                Min = 1,
                Max = 2000,
                DefaultValue = 100,
                DisplayFormat = "体素:{0:F2}",
                Tip = "体素边长（与点云同单位）：越大保留点越少；1.0 起，点云噪声大时加大"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_32FC3)
            {
                LastSummary = "点云体素降采样: 跳过 —— 输入不是 CV_32FC3 坐标图（先接「深度转点云」）";
                return srcMat.Clone();
            }
            double vox = Math.Max(0.001, paramValues[0] / 100.0);

            Mat dst = srcMat.Clone();
            var occupied = new HashSet<long>();
            int kept = 0, total = 0;
            unsafe
            {
                for (int y = 0; y < dst.Rows; y++)
                {
                    Vec3f* row = (Vec3f*)dst.Ptr(y);   // 行指针
                    for (int x = 0; x < dst.Cols; x++)
                    {
                        Vec3f p = ((Vec3f*)row)[x];
                        // 非有限值（NaN/Inf）视为无效点，直接跳过
                        if (!float.IsFinite(p.Item0) || !float.IsFinite(p.Item1) || !float.IsFinite(p.Item2))
                            continue;
                        total++;
                        // 体素格索引（整数哈希）
                        long gx = (long)Math.Floor(p.Item0 / vox);
                        long gy = (long)Math.Floor(p.Item1 / vox);
                        long gz = (long)Math.Floor(p.Item2 / vox);
                        long key = (gx * 73856093) ^ (gy * 19349663) ^ (gz * 83492791);
                        if (occupied.Add(key))
                        {
                            kept++;   // 该体素第一个点，保留
                        }
                        else
                        {
                            // 该体素已有保留点 → 置 NaN 无效
                            ((Vec3f*)row)[x] = new Vec3f(float.NaN, float.NaN, float.NaN);
                        }
                    }
                }
            }
            double ratio = total > 0 ? kept * 100.0 / total : 0;
            LastSummary = string.Format("点云降采样: {0} → {1} 点（保留 {2:F1}%）体素 {3:F2}",
                total, kept, ratio, vox);
            return dst;
        }
    }
}
