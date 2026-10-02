using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 投影分析：灰度沿行/列方向求和得到投影曲线，平滑后叠加显示，
    /// 标出峰/谷位置。用于文本行定位、标记点/装配孔的行列定位。
    /// 参数：方向（0=列投影 1=行投影）、平滑核。
    /// </summary>
    public class ProjectionTask : IVisionTask, IResultReporter
    {
        public string TaskName => "投影分析";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "方向 0列/1行",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "方向:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "平滑核",
                Min = 1,
                Max = 31,
                DefaultValue = 5,
                DisplayFormat = "平滑:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            bool rowMode = paramValues[0] == 1;
            int ksize = Math.Max(1, paramValues[1] | 1);

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat g32 = new())
            using (Mat prof = new())
            {
                gray.ConvertTo(g32, MatType.CV_32F);
                // 列投影：约减成单行（每列之和）；行投影：约减成单列
                Cv2.Reduce(g32, prof, rowMode ? ReduceDimension.Column : ReduceDimension.Row,
                    ReduceTypes.Sum, -1);
                if (ksize > 1)
                    Cv2.GaussianBlur(prof, prof,
                        rowMode ? new OpenCvSharp.Size(1, ksize) : new OpenCvSharp.Size(ksize, 1), 0);

                int n = rowMode ? prof.Rows : prof.Cols;
                double min, max;
                Cv2.MinMaxLoc(prof, out min, out max, out Point pmin, out Point pmax);
                int peak = rowMode ? pmax.Y : pmax.X;
                int valley = rowMode ? pmin.Y : pmin.X;
                double range = Math.Max(1e-9, max - min);

                // 底部/右侧 40% 阴影带内画投影曲线；小图时 band 不能超过图像本身
                // （否则 Rect 越界抛 OpenCV 裸断言 —— 6x8 这类极小图/ROI 拖出边界会崩）
                int band = rowMode ? Math.Max(1, (int)(dst.Cols * 0.4)) : Math.Max(1, (int)(dst.Rows * 0.4));
                band = Math.Min(band, rowMode ? dst.Cols : dst.Rows);
                int ox = rowMode ? dst.Cols - band : 0;
                int oy = rowMode ? 0 : dst.Rows - band;
                using (Mat shade = new(dst, rowMode
                    ? new Rect(ox, 0, band, dst.Rows)
                    : new Rect(0, oy, dst.Cols, band)))
                using (Mat dark = new(shade.Size(), shade.Type(), Scalar.All(0)))
                {
                    Cv2.AddWeighted(shade, 0.45, dark, 0.55, 0, shade);
                }

                Point[] curve = new Point[n];
                for (int i = 0; i < n; i++)
                {
                    double v = rowMode ? prof.Get<float>(i, 0) : prof.Get<float>(0, i);
                    double t = (v - min) / range;
                    curve[i] = rowMode
                        ? new Point(ox + 2 + (int)(t * (band - 5)), i)
                        : new Point(i, oy + band - 2 - (int)(t * (band - 5)));
                }
                Cv2.Polylines(dst, new[] { curve }, false, Scalar.LimeGreen, 1, LineTypes.AntiAlias);

                if (rowMode)
                {
                    Cv2.Line(dst, 0, peak, dst.Cols, peak, Scalar.Red, 1, LineTypes.AntiAlias);
                    Cv2.Line(dst, 0, valley, dst.Cols, valley, Scalar.Blue, 1, LineTypes.AntiAlias);
                    LastSummary = string.Format("行投影: 峰值行 {0} ({1:F0}) 谷值行 {2} ({3:F0})",
                        peak, max, valley, min);
                }
                else
                {
                    Cv2.Line(dst, peak, 0, peak, dst.Rows, Scalar.Red, 1, LineTypes.AntiAlias);
                    Cv2.Line(dst, valley, 0, valley, dst.Rows, Scalar.Orange, 1, LineTypes.AntiAlias);
                    LastSummary = string.Format("列投影: 峰值列 {0} ({1:F0}) 谷值列 {2} ({3:F0})",
                        peak, max, valley, min);
                }
                return dst;
            }
        }
    }
}
