using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 角点检测（Shi-Tomasi）：GoodFeaturesToTrack 找最强的若干角点，
    /// 红圈标出并编号，摘要输出角点数量。
    /// 用于标定板角点、工件定位特征提取。
    /// 参数：最大角点数、质量阈值(%)、最小间距。
    /// </summary>
    public class CornerTask : IVisionTask, IResultReporter
    {
        public string TaskName => "角点检测";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "最大角点数",
                Min = 1,
                Max = 500,
                DefaultValue = 100,
                DisplayFormat = "Max:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "质量阈值%",
                Min = 1,
                Max = 100,
                DefaultValue = 10,
                DisplayFormat = "质量:{0}%"
            },
            new TaskParamDesc
            {
                ParamName = "最小间距",
                Min = 1,
                Max = 100,
                DefaultValue = 10,
                DisplayFormat = "间距:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int maxCorners = paramValues[0];
            double quality = paramValues[1] / 100.0;
            double minDist = paramValues[2];

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Point2f[] corners = Cv2.GoodFeaturesToTrack(gray, maxCorners, quality, minDist,
                    null, 3, false, 0.04);
                for (int i = 0; i < corners.Length; i++)
                {
                    int x = (int)Math.Round(corners[i].X), y = (int)Math.Round(corners[i].Y);
                    Cv2.Circle(dst, x, y, 5, Scalar.Red, 2, LineTypes.AntiAlias);
                    Cv2.PutText(dst, (i + 1).ToString(), new Point(x + 6, y - 4),
                        HersheyFonts.HersheySimplex, 0.45, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
                }
                LastSummary = "角点检测: " + corners.Length + " 个 (Shi-Tomasi)";
                return dst;
            }
        }
    }
}
