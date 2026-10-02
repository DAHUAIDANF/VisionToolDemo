using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 畸变校正：用相机内参 + 畸变系数做 remap 去畸变。
    ///
    /// 这个算子是"畸变FOV标定"的**下半场**：标定算子只负责把 K 和畸变系数算出来，
    /// 但算出来的参数一直没法用回图像上 —— 本算子把它接上，形成闭环。
    ///
    /// 支持两种取参数的方式：
    ///   · 直接填内参 fx/fy/cx/cy 与 k1/k2/p1/p2/k3（离线标定结果或厂商标称值）
    ///   · 与"畸变FOV标定"在同一流水线时，通过 CalibrationStore 自动取上一次标定结果
    ///
    /// 校正后图像的尺度会变（去畸变会把画面往外"拉"），
    /// 用"保留比例"参数控制：1.0 = 保留全部像素（有黑边），
    /// &lt;1.0 = 裁掉黑边并放大（无黑边但视野略小）。
    /// </summary>
    public class UndistortTask : IVisionTask, IResultReporter
    {
        public string TaskName => "畸变校正";

        public string LastSummary { get; private set; } = "";

        public double Fx { get; private set; } = double.NaN;
        public double Fy { get; private set; } = double.NaN;
        public double Cx { get; private set; } = double.NaN;
        public double Cy { get; private set; } = double.NaN;

        public Mat CameraMatrix { get; private set; }
        public Mat DistCoeffs { get; private set; }

        /// <summary>校正后实际视野缩放比</summary>
        public double Scale { get; private set; } = double.NaN;

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "参数来源 0手动1标定", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "来源:{0}", Group = "标定", Tip = "0手动：用下面填的 fx/fy/cx/cy 与畸变系数。" +
                "1标定：取同一条流水线里“畸变FOV标定”算子最近一次的结果（必须先跑过标定）。" },
            new TaskParamDesc { ParamName = "fx", Min = 1, Max = 1000000, DefaultValue = 1000,
                DisplayFormat = "fx:{0}", Group = "标定" },
            new TaskParamDesc { ParamName = "fy", Min = 1, Max = 1000000, DefaultValue = 1000,
                DisplayFormat = "fy:{0}", Group = "标定" },
            new TaskParamDesc { ParamName = "cx", Min = -100000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "cx:{0}", Group = "标定", Tip = "主点 X。填 0 自动取图像中心（推荐，除非标定给出了别的值）。" },
            new TaskParamDesc { ParamName = "cy", Min = -100000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "cy:{0}", Group = "标定", Tip = "主点 Y。填 0 自动取图像中心。" },
            new TaskParamDesc { ParamName = "k1 x1000000", Min = -1000000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "k1:{0}", Group = "畸变", Tip = "径向畸变一阶系数，实际值 = 该数 ÷ 1000000。" +
                "桶形畸变 k1 为正，枕形为负。典型 |k1| 在 1e-3 ~ 1e-1。" },
            new TaskParamDesc { ParamName = "k2 x1000000", Min = -1000000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "k2:{0}", Group = "畸变", Tip = "径向二阶系数，实际值 = 该数 ÷ 1000000。" },
            new TaskParamDesc { ParamName = "k3 x1000000", Min = -1000000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "k3:{0}", Group = "畸变", Tip = "径向三阶系数，实际值 = 该数 ÷ 1000000。" },
            new TaskParamDesc { ParamName = "p1 x1000000", Min = -1000000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "p1:{0}", Group = "畸变", Tip = "切向畸变系数，实际值 = 该数 ÷ 1000000。" },
            new TaskParamDesc { ParamName = "p2 x1000000", Min = -1000000, Max = 1000000, DefaultValue = 0,
                DisplayFormat = "p2:{0}", Group = "畸变", Tip = "切向畸变系数，实际值 = 该数 ÷ 1000000。" },
            new TaskParamDesc { ParamName = "保留比例x100", Min = 10, Max = 150, DefaultValue = 100,
                DisplayFormat = "保留:{0}%", Group = "输出", Tip = "100 = 保留全部像素（四周可能有黑边）。" +
                "调小会裁掉黑边并放大画面（无黑边，但视野略小）。" },
            new TaskParamDesc { ParamName = "填充 0黑1边缘", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "填充:{0}", Group = "输出", Tip = "去畸变后画面外的区域怎么填。" +
                "1边缘（默认）：用最近边缘像素延伸，避免黑边干扰后续二值化/缺陷检测。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Scale = double.NaN;
            if (srcMat == null || srcMat.Empty()) return new Mat();

            int source = paramValues[0];
            double fx = paramValues[1], fy = paramValues[2];
            double cxParam = paramValues[3], cyParam = paramValues[4];
            double k1 = paramValues[5] / 1e6, k2 = paramValues[6] / 1e6, k3 = paramValues[7] / 1e6;
            double p1 = paramValues[8] / 1e6, p2 = paramValues[9] / 1e6;
            double keep = paramValues[10] / 100.0;
            int fill = paramValues[11];

            if (source == 1)
            {
                if (!CalibrationStore.TryGet(out double sfx, out double sfy, out double scx,
                        out double scy, out double[] sd))
                {
                    LastSummary = "畸变校正: 标定结果不可用（请先在同一条流水线里跑“畸变FOV标定”，或用来源=手动填写）";
                    return VisionHelper.ToBgrCopy(srcMat);
                }
                fx = sfx; fy = sfy; cxParam = scx; cyParam = scy;
                k1 = sd[0]; k2 = sd[1]; p1 = sd[2]; p2 = sd[3]; k3 = sd[4];
            }

            double cx = Math.Abs(cxParam) < 1e-9 ? srcMat.Cols / 2.0 : cxParam;
            double cy = Math.Abs(cyParam) < 1e-9 ? srcMat.Rows / 2.0 : cyParam;

            if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx <= 0 || fy <= 0)
            {
                LastSummary = "畸变校正: 内参无效（fx/fy 必须为正）";
                return VisionHelper.ToBgrCopy(srcMat);
            }

            Fx = fx; Fy = fy; Cx = cx; Cy = cy;
            CameraMatrix?.Dispose();
            DistCoeffs?.Dispose();
            CameraMatrix = new Mat(3, 3, MatType.CV_64F, Scalar.All(0));
            CameraMatrix.Set(0, 0, fx); CameraMatrix.Set(1, 1, fy);
            CameraMatrix.Set(0, 2, cx); CameraMatrix.Set(1, 2, cy); CameraMatrix.Set(2, 2, 1.0);
            DistCoeffs = new Mat(1, 5, MatType.CV_64F);
            DistCoeffs.Set(0, 0, k1); DistCoeffs.Set(0, 1, k2);
            DistCoeffs.Set(0, 2, p1); DistCoeffs.Set(0, 3, p2); DistCoeffs.Set(0, 4, k3);

            Mat newK = Cv2.GetOptimalNewCameraMatrix(
                CameraMatrix, DistCoeffs, srcMat.Size(), keep, srcMat.Size(), out Rect validRoi);
            Scale = newK.At<double>(0, 0) / fx;

            Mat map1 = new(), map2 = new();
            Cv2.InitUndistortRectifyMap(CameraMatrix, DistCoeffs, new Mat(), newK,
                srcMat.Size(), MatType.CV_16SC2, map1, map2);

            Mat dst = new();
            Cv2.Remap(srcMat, dst, map1, map2, InterpolationFlags.Linear,
                fill == 1 ? BorderTypes.Replicate : BorderTypes.Constant,
                fill == 1 ? new Scalar() : Scalar.Black);

            map1.Dispose(); map2.Dispose();
            newK.Dispose();

            // 有黑边时提示有效区域（边缘填充模式下黑边不存在，就不画了）
            if (fill == 0 && validRoi.Width > 0 && validRoi.Height > 0)
            {
                Cv2.Rectangle(dst, validRoi, Scalar.LimeGreen, 1);
                MatDraw.DrawText(dst, string.Format("有效区 {0}x{1}", validRoi.Width, validRoi.Height), 6, 20, Scalar.LimeGreen, 13);
            }
            else
            {
                MatDraw.DrawText(dst, $"去畸变 fx{fx:F1} k1x1e6 {k1 * 1e6:F1} 缩放{Scale:F3}", 6, 20, Scalar.LimeGreen, 13);
            }

            string srcName = source == 1 ? "标定结果" : "手动参数";
            LastSummary = string.Format(
                "畸变校正: {0}  fx {1:F2} fy {2:F2} 主点 ({3:F1},{4:F1}), k1 {5:E3} k2 {6:E3} p1 {7:E3} p2 {8:E3}, " +
                "视野缩放 {9:F4}, 有效区 {10}x{11}",
                srcName, fx, fy, cx, cy, k1, k2, p1, p2, Scale, validRoi.Width, validRoi.Height);
            return dst;
        }
    }
}
