using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 模板差分（缺陷检测）：把模板图与当前图按灰度逐像素求差，差值超阈值处
    /// 连通域去噪后按最小缺陷面积筛选，红框标注差异区域，摘要输出缺陷数量。
    /// 模板图由 UI 层"导入模板"注入（TemplateMat）；要求模板与当前图同尺寸，
    /// 不同尺寸时自动缩放模板对齐（演示用途，无旋转/平移配准）。
    /// 参数：差异阈值、去噪腐蚀核、最小缺陷面积。
    /// </summary>
    public class TemplateDiffTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "模板差分";

        /// <summary>UI层赋值：标准模板图片</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>状态 = 模板图（base64 PNG）</summary>
        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "差异阈值",
                Min = 1,
                Max = 255,
                DefaultValue = 40,
                DisplayFormat = "阈值:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "去噪核",
                Min = 1,
                Max = 21,
                DefaultValue = 3,
                DisplayFormat = "腐蚀:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "最小缺陷面积",
                Min = 1,
                Max = 20000,
                DefaultValue = 50,
                DisplayFormat = "MinArea:{0}"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "模板差分: 请先导入模板图片";
                return dst;
            }

            int diffThresh = paramValues[0];
            int ksize = Math.Max(1, paramValues[1] | 1);
            int minArea = Math.Max(1, paramValues[2]);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            using (Mat templGray = VisionHelper.ToGray(TemplateMat))
            {
                if (templGray.Cols != gray.Cols || templGray.Rows != gray.Rows)
                    Cv2.Resize(templGray, templGray, new OpenCvSharp.Size(gray.Cols, gray.Rows));

                using (Mat diff = new())
                using (Mat bin = new())
                using (Mat labels = new())
                using (Mat stats = new())
                using (Mat centroids = new())
                {
                    Cv2.Absdiff(gray, templGray, diff);
                    Cv2.Threshold(diff, bin, diffThresh, 255, ThresholdTypes.Binary);
                    using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                        new OpenCvSharp.Size(ksize, ksize)))
                        Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel);

                    int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids,
                        PixelConnectivity.Connectivity8);
                    int defects = 0;
                    for (int i = 1; i < n; i++)
                    {
                        int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                        if (area < minArea)
                            continue;
                        defects++;
                        int x = stats.Get<int>(i, (int)ConnectedComponentsTypes.Left);
                        int y = stats.Get<int>(i, (int)ConnectedComponentsTypes.Top);
                        int w = stats.Get<int>(i, (int)ConnectedComponentsTypes.Width);
                        int h = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);
                        Cv2.Rectangle(dst, new Rect(x, y, w, h), Scalar.Red, 2, LineTypes.AntiAlias);
                        Cv2.PutText(dst, defects.ToString(), new Point(x, Math.Max(12, y - 4)),
                            HersheyFonts.HersheySimplex, 0.5, Scalar.Red, 1, LineTypes.AntiAlias);
                    }

                    LastSummary = defects > 0
                        ? "模板差分: 检出缺陷 " + defects + " 处 (差异阈值 " + diffThresh + ")"
                        : "模板差分: 未检出缺陷 (差异阈值 " + diffThresh + ")";
                    return dst;
                }
            }
        }
    }
}
