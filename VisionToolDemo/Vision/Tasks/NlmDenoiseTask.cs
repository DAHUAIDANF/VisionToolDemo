using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// NLM 去噪：非局部均值去噪，比高斯/中值滤波更好保留边缘细节，
    /// 用于低照度噪声图、照片级噪声的预处理。彩色图走彩色版，灰度图走单通道版。
    /// 参数：去噪强度、模板窗口、搜索窗口（均为奇数）。
    /// </summary>
    public class NlmDenoiseTask : IVisionTask, IResultReporter
    {
        public string TaskName => "NLM去噪";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "去噪强度",
                Min = 3,
                Max = 30,
                DefaultValue = 10,
                DisplayFormat = "强度:{0}"
            },
            new TaskParamDesc
            {
                ParamName = "模板窗口",
                Min = 3,
                Max = 15,
                DefaultValue = 7,
                DisplayFormat = "模板:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "搜索窗口",
                Min = 7,
                Max = 35,
                DefaultValue = 21,
                DisplayFormat = "搜索:{0}",
                ForceOdd = true
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                return new Mat();

            float h = Math.Max(1, paramValues[0]);
            int template = Math.Max(3, paramValues[1] | 1);
            int search = Math.Max(5, paramValues[2] | 1);

            // NLM 只支持 8U（彩色还必须是 8UC3/8UC4）。先统一到 8U，
            // 否则 16 位 RAW 会抛 "Unsupported depth! Only CV_8U is supported"。
            using Mat src8 = VisionHelper.ToU8(srcMat);

            // 灰度图走单通道 FastNlMeansDenoising，避免 BGR 往返的多余开销
            Mat dst = new();
            if (src8.Channels() == 1)
            {
                Cv2.FastNlMeansDenoising(src8, dst, h, template, search);
            }
            else
            {
                Cv2.FastNlMeansDenoisingColored(src8, dst, h, 10, template, search);
            }

            LastSummary = "NLM去噪: 强度 " + paramValues[0] + ", 模板 " + template + ", 搜索 " + search;
            return dst;
        }
    }
}
