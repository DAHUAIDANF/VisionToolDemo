using OpenCvSharp;
using System;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>霍夫直线检测算子：Canny 找边缘后，在参数空间累加找直线，叠加红色直线。
    /// 参数：阈值（越高要求越严格，检出越少但越可靠）。</summary>
    public class HoughLinesTask : IVisionTask, IResultReporter
    {
        public string TaskName => "霍夫直线检测";

        /// <summary>最近一次 Execute 的结果摘要（检出直线数量）</summary>
        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "阈值",
                Min = 10,
                Max = 300,
                DefaultValue = 80,
                DisplayFormat = "阈值:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 先 Canny 提取边缘，直线检测才可靠
                Mat canny = new();
                Cv2.Canny(gray, canny, 50, 150);

                // 输出画在副本上，不污染原图
                Mat dst = srcMat.Clone();
                // 标准霍夫变换：rho 步长 1px、theta 步长 1°，累加器阈值=参数
                LineSegmentPolar[] lines = Cv2.HoughLines(canny, 1, Math.PI / 180, paramValues[0]);
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        // 极坐标(ρ,θ) → 直线上两点，两端各延长 1000px 保证画满
                        float rho = line.Rho;
                        float theta = line.Theta;
                        double a = Math.Cos(theta);
                        double b = Math.Sin(theta);
                        double x0 = a * rho;
                        double y0 = b * rho;
                        Point pt1 = new((int)(x0 - (1000 * b)), (int)(y0 + (1000 * a)));
                        Point pt2 = new((int)(x0 + (1000 * b)), (int)(y0 - (1000 * a)));
                        Cv2.Line(dst, pt1, pt2, Scalar.Red, 2);
                    }
                }
                canny.Dispose();
                LastSummary = $"霍夫直线: 检出 {(lines == null ? 0 : lines.Length)} 条 (阈值 {paramValues[0]})";
                return dst;
            }
        }
    }
}