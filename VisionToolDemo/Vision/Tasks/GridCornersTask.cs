using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 棋盘/网格角点检测算子：提取标定板（棋盘格）内角点，输出角点坐标并绘制，
    /// 可选亚像素精化。用于相机标定、透视校正取点。
    /// 参数：角点列数、角点行数、亚像素精化开关。
    /// </summary>
    public class GridCornersTask : IVisionTask, IResultReporter
    {
        public string TaskName => "棋盘角点检测";

        public string LastSummary { get; private set; } = "";

        public int CornerCount { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "角点列数",
                Min = 2, Max = 30, DefaultValue = 9,
                DisplayFormat = "列:{0}",
                Tip = "棋盘格内角点列数（格子数-1）"
            },
            new TaskParamDesc
            {
                ParamName = "角点行数",
                Min = 2, Max = 30, DefaultValue = 6,
                DisplayFormat = "行:{0}",
                Tip = "棋盘格内角点行数（格子数-1）"
            },
            new TaskParamDesc
            {
                ParamName = "亚像素精化",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "精化:{0}",
                Tip = "1=角点位置亚像素精化（更准）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            CornerCount = 0;
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Size pattern = new(Math.Max(2, paramValues[0]), Math.Max(2, paramValues[1]));
            bool refine = paramValues[2] != 0;

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                Point2f[] corners;
                bool ok = Cv2.FindChessboardCorners(gray, pattern, out corners,
                    ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck);

                // 输出图：彩色底 + 角点
                Mat dst = srcMat.Channels() == 1
                    ? srcMat.CvtColor(ColorConversionCodes.GRAY2BGR)
                    : srcMat.Clone();

                if (ok && corners != null && corners.Length > 0)
                {
                    if (refine)
                    {
                        Size winSize = new(5, 5), zeroZone = new(-1, -1);
                        Cv2.CornerSubPix(gray, corners, winSize, zeroZone,
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.Count, 30, 0.01));
                    }
                    foreach (var c in corners)
                        Cv2.Circle(dst, (int)c.X, (int)c.Y, 3, new Scalar(0, 0, 255), -1);
                    Cv2.DrawChessboardCorners(dst, pattern, corners, ok);
                    CornerCount = corners.Length;
                    LastSummary = $"棋盘角点检测: 检出 {CornerCount} 个角点（{pattern.Width}×{pattern.Height}）";
                }
                else
                {
                    LastSummary = $"棋盘角点检测: 未检出（期望 {pattern.Width}×{pattern.Height}）";
                }
                return dst;
            }
        }
    }
}
