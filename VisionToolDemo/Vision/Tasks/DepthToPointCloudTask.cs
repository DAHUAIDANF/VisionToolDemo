using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度转点云：16U 深度图 + 相机内参 → XYZ 坐标图（CV_32FC3，同尺寸，
    /// 每像素存该点的三维坐标）。Z = 深度×比例；X = (x−cx)·Z/fx；Y = (y−cy)·Z/fy。
    /// 输出可直接接「点云体素降采样」等 3D 算子。
    /// </summary>
    public class DepthToPointCloudTask : IVisionTask, IResultReporter
    {
        public string TaskName => "深度转点云";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "深度比例x100",
                Min = 1,
                Max = 10000,
                DefaultValue = 100,
                DisplayFormat = "比例:{0:F2}",
                Tip = "深度值 × 比例 = 毫米（100=1.0 时深度值即毫米）"
            },
            new TaskParamDesc
            {
                ParamName = "fx",
                Min = 100,
                Max = 20000,
                DefaultValue = 1000,
                DisplayFormat = "fx:{0}",
                Tip = "相机内参焦距 fx（像素），标定获得；估不准时先试 1000"
            },
            new TaskParamDesc
            {
                ParamName = "fy",
                Min = 100,
                Max = 20000,
                DefaultValue = 1000,
                DisplayFormat = "fy:{0}",
                Tip = "相机内参焦距 fy（像素）"
            },
            new TaskParamDesc
            {
                ParamName = "cx",
                Min = -1,
                Max = 20000,
                DefaultValue = -1,
                DisplayFormat = "cx:{0}",
                Tip = "主点 cx（像素）；-1=自动用图像中心"
            },
            new TaskParamDesc
            {
                ParamName = "cy",
                Min = -1,
                Max = 20000,
                DefaultValue = -1,
                DisplayFormat = "cy:{0}",
                Tip = "主点 cy（像素）；-1=自动用图像中心"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            if (srcMat.Type() != MatType.CV_16UC1)
            {
                LastSummary = "深度转点云: 跳过 —— 输入不是 16U 深度图";
                return srcMat.Clone();
            }
            double scale = paramValues[0] / 100.0;
            double fx = Math.Max(1, paramValues[1]);
            double fy = Math.Max(1, paramValues[2]);
            double cx = paramValues[3] >= 0 ? paramValues[3] : srcMat.Cols / 2.0;
            double cy = paramValues[4] >= 0 ? paramValues[4] : srcMat.Rows / 2.0;

            Mat cloud = new Mat(srcMat.Rows, srcMat.Cols, MatType.CV_32FC3);
            unsafe
            {
                // 深度图按行读取（16U），直接写 float3 坐标
                for (int y = 0; y < srcMat.Rows; y++)
                {
                    var row = srcMat.GetUnsafePointer(y);   // ushort*
                    var outRow = cloud.GetUnsafePointer(y); // Vec3f*
                    for (int x = 0; x < srcMat.Cols; x++)
                    {
                        ushort d = ((ushort*)row)[x];
                        double z = d * scale;
                        float px = (float)((x - cx) * z / fx);
                        float py = (float)((y - cy) * z / fy);
                        ((Vec3f*)outRow)[x] = new Vec3f(px, py, (float)z);
                    }
                }
            }
            LastSummary = string.Format("深度转点云: {0}×{1} = {2} 点（fx {3:F0} fy {4:F0}）",
                srcMat.Cols, srcMat.Rows, (long)srcMat.Cols * srcMat.Rows, fx, fy);
            return cloud;
        }
    }
}
