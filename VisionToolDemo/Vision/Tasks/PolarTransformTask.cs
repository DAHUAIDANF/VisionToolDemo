using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 极坐标变换算子：整图按极坐标（或对数极坐标）展开，支持反向变换。
    /// 线性极坐标：旋转不变分析（环形缺陷展开成水平条纹）；
    /// 对数极坐标：旋转+缩放不变分析（尺度变化转成平移）。
    /// 参数：类型（0=线性极坐标 1=对数极坐标 2=反变换）、输出高（%）、起始角度（度）。
    /// </summary>
    public class PolarTransformTask : IVisionTask, IResultReporter
    {
        public string TaskName => "极坐标变换";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "类型",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                Tip = "0=线性极坐标 1=对数极坐标 2=反向（极坐标恢复为直角坐标）"
            },
            new TaskParamDesc
            {
                ParamName = "输出高度",
                Min = 10, Max = 100, DefaultValue = 60,
                DisplayFormat = "高度:{0}%",
                Tip = "输出图高度 = 源图短边一半的百分比（对应最大半径）"
            },
            new TaskParamDesc
            {
                ParamName = "起始角度",
                Min = 0, Max = 360, DefaultValue = 0,
                DisplayFormat = "角度:{0}°",
                Tip = "展开的起始角度（度），便于把特征转到期望位置"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int type = paramValues[0];
            int heightPct = Math.Max(10, paramValues[1]);
            int startAngle = paramValues[2] % 360;

            double half = Math.Min(srcMat.Cols, srcMat.Rows) / 2.0;
            int dstH = Math.Max(32, (int)(half * heightPct / 100.0));
            int dstW = srcMat.Cols;   // 宽 = 圆周长对应的像素数（保持原宽信息量）

            Point2f center = new(srcMat.Cols / 2f, srcMat.Rows / 2f);

            Mat dst = new();
            try
            {
                // 角度偏移：把起始角度转成像素偏移（角度 → 弧长）
                double angleRad = startAngle * Math.PI / 180.0;
                double maxRadius = half;

                if (type == 2)
                {
                    // 反变换：极坐标 → 直角坐标（flags 加 WarpInverseMap）
                    Cv2.WarpPolar(srcMat, dst, new Size(srcMat.Cols, srcMat.Rows),
                        center, maxRadius,
                        InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
                        WarpPolarMode.Linear);
                }
                else
                {
                    var mode = type == 1 ? WarpPolarMode.Log : WarpPolarMode.Linear;
                    Cv2.WarpPolar(srcMat, dst, new Size(dstW, dstH),
                        center, maxRadius, InterpolationFlags.Linear, mode);
                }
            }
            catch (Exception ex)
            {
                LastSummary = $"极坐标变换: 失败（{ex.Message}）";
                return srcMat.Clone();
            }

            string[] names = { "线性极坐标", "对数极坐标", "反向恢复" };
            LastSummary = $"极坐标变换: {names[type]}（起始角{startAngle}°）";
            return dst;
        }
    }
}
