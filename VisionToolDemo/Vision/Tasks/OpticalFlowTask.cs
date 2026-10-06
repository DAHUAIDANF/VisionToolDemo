using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 光流运动检测算子：保存上一帧，与当前帧做稠密光流（Farneback），
    /// 输出运动幅度图（HSV 编码：色相=方向，亮度=幅度），或幅度灰度图。
    /// 用于运动检测、振动分析、位移估计。
    /// 注意：算子内部保存上一帧，连续执行两次以上才有光流输出；首次执行返回幅度为 0。
    /// 参数：输出模式（0=HSV 编码 1=幅度灰度）、幅度阈值、显示上限。
    /// </summary>
    public class OpticalFlowTask : IVisionTask, IResultReporter
    {
        public string TaskName => "光流运动";

        // 上一帧（跨 Execute 保存）
        private Mat _prevGray;

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "输出模式",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}",
                Tip = "0=HSV 彩色编码（色相=运动方向，亮度=幅度）；1=幅度灰度"
            },
            new TaskParamDesc
            {
                ParamName = "幅度阈值",
                Min = 0, Max = 100, DefaultValue = 5,
                DisplayFormat = "阈值:{0}",
                Tip = "低于该幅度的像素视为静止（黑）"
            },
            new TaskParamDesc
            {
                ParamName = "显示上限",
                Min = 5, Max = 200, DefaultValue = 40,
                DisplayFormat = "上限:{0}",
                Tip = "最大幅度映射到最亮（大于此的饱和）"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            int mode = paramValues[0];
            int thresh = paramValues[1];
            int cap = Math.Max(5, paramValues[2]);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                if (_prevGray == null || _prevGray.Size() != gray.Size())
                {
                    _prevGray?.Dispose();
                    _prevGray = gray.Clone();
                    LastSummary = "光流运动: 首帧已保存，再次执行输出光流";
                    Mat first = new Mat(gray.Rows, gray.Cols, mode == 0 ? MatType.CV_8UC3 : MatType.CV_8UC1, Scalar.All(0));
                    return first;
                }

                // Farneback 稠密光流
                using (Mat flow = new())
                {
                    Cv2.CalcOpticalFlowFarneback(_prevGray, gray, flow, 0.5, 3, 15, 3, 5, 1.2, 0);

                    if (mode == 0)
                    {
                        // HSV 编码
                        Mat hsv = new Mat(gray.Rows, gray.Cols, MatType.CV_8UC3, new Scalar(0, 255, 255));
                        using (Mat mag = new(), ang = new())
                        {
                            Cv2.CartToPolar(flow, flow, mag, ang, true);
                            // hsv[...,0] = 角度/2；hsv[...,2] = 幅度（限幅）
                            Mat[] planes;
                            Cv2.Split(hsv, out planes);
                            try
                            {
                                Mat angDeg = new();
                                ang.ConvertTo(angDeg, MatType.CV_8U, 180.0 / (Math.PI * 2));
                                Cv2.ConvertScaleAbs(angDeg, planes[0], 1, 0);
                                angDeg.Dispose();
                                Mat magU = new();
                                mag.ConvertTo(magU, MatType.CV_8U, 255.0 / cap, 0);
                                Cv2.Threshold(magU, planes[2], thresh, 255, ThresholdTypes.Binary);
                                Cv2.Multiply(magU, planes[2], planes[2], 1.0 / 255.0);
                                magU.Dispose();
                            }
                            finally
                            {
                                Cv2.Merge(planes, hsv);
                                foreach (var p in planes) p?.Dispose();
                            }
                        }
                        Mat vis = new();
                        Cv2.CvtColor(hsv, vis, ColorConversionCodes.HSV2BGR);

                        // 更新上一帧
                        _prevGray.Dispose();
                        _prevGray = gray.Clone();

                        LastSummary = "光流运动: HSV 编码输出（彩色=方向，亮=幅度）";
                        return vis;
                    }
                    else
                    {
                        // 幅度灰度
                        using (Mat mag = new(), ang = new())
                        {
                            Cv2.CartToPolar(flow, flow, mag, ang, true);
                            Mat magU = new();
                            mag.ConvertTo(magU, MatType.CV_8U, 255.0 / cap, 0);
                            Cv2.Threshold(magU, magU, thresh, 0, ThresholdTypes.Tozero);

                            _prevGray.Dispose();
                            _prevGray = gray.Clone();
                            LastSummary = "光流运动: 幅度灰度输出";
                            return magU;
                        }
                    }
                }
            }
        }
    }
}
