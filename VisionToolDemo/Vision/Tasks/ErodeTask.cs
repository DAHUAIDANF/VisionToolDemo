using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>腐蚀算子：用矩形核取邻域最小值，亮区收缩、去掉细小白点毛刺。
/// 参数：核大小（奇数）、迭代次数。</summary>
public class ErodeTask : IVisionTask
    {
        public string TaskName => "腐蚀";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "核大小",
                Min = 1,
                Max = 21,
                DefaultValue = 3,
                DisplayFormat = "核:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "迭代次数",
                Min = 1,
                Max = 10,
                DefaultValue = 1,
                DisplayFormat = "迭代:{0}",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            Mat dst = new();
            // 矩形结构元按参数大小创建
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(paramValues[0], paramValues[0])))
            {
                // 迭代 paramValues[1] 次：多次腐蚀 = 亮区收缩更明显
                Cv2.Erode(srcMat, dst, kernel, null, paramValues[1]);
            }
            return dst;
        }
    }
}