using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>霍夫圆检测算子：用霍夫梯度法在图中找圆并叠加绘制（圆心红点 + 绿色圆框）。
    /// 参数：dp（累加器分辨率倒数）、最小圆心距离、Canny 高阈值、累加器阈值、最小/最大半径。
    /// 通过 IResultReporter 汇报本次检出的圆数量。</summary>
    public class HoughCircleTask : IVisionTask, IResultReporter
    {
        public string TaskName => "霍夫圆检测";

        /// <summary>最近一次 Execute 的结果摘要（检出圆数量）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "dp",
                Min = 1,
                Max = 5,
                DefaultValue = 1,
                DisplayFormat = "dp:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最小圆心距离",
                Min = 1,
                Max = 200,
                DefaultValue = 20,
                DisplayFormat = "minDist:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "Canny高阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 100,
                DisplayFormat = "cannyThresh:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "累加器阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 30,
                DisplayFormat = "accumThresh:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最小半径",
                Min = 0,
                Max = 200,
                DefaultValue = 5,
                DisplayFormat = "minR:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "最大半径",
                Min = 0,
                Max = 300,
                DefaultValue = 100,
                DisplayFormat = "maxR:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";   // 每次执行先清空上次摘要
            int dp = paramValues[0];
            int minDist = paramValues[1];
            int cannyThresh = paramValues[2];
            int accumThresh = paramValues[3];
            int minRadius = paramValues[4];
            int maxRadius = paramValues[5];

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 输出图 = 原图的 BGR 副本，检测结果直接画在副本上
                Mat dst = VisionHelper.ToBgrCopy(srcMat);

                // 霍夫梯度法：参数含义见 ParamDescriptions（dp/minDist/canny/accum/minR/maxR）
                CircleSegment[] circles = Cv2.HoughCircles(
                    gray,
                    HoughModes.Gradient,
                    dp,
                    minDist,
                    cannyThresh,
                    accumThresh,
                    minRadius,
                    maxRadius
                );

                if (circles != null)
                {
                    foreach (var circle in circles)
                    {
                        // 圆心画 2px 红点；圆轮廓画 2px 绿圈
                        Cv2.Circle(dst, (Point)circle.Center, 2, Scalar.Red, -1);
                        Cv2.Circle(dst, (Point)circle.Center, (int)circle.Radius, Scalar.Green, 2);
                    }
                }

                // 汇报给链摘要：这次检出了几个圆
                LastSummary = $"霍夫圆: 检出 {(circles == null ? 0 : circles.Length)} 个";
                return dst;
            }
        }
    }
}