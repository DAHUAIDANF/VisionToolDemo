using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>形态学组合操作算子：开/闭/顶帽/黑帽。
/// 参数：操作类型（0=开 1=闭 2=顶帽 3=黑帽）、核大小、迭代次数。
/// 开运算=先腐蚀后膨胀（去毛刺）；闭运算=先膨胀后腐蚀（填空洞）。</summary>
public class MorphologyExTask : IVisionTask
    {
        public string TaskName => "形态学操作";

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                SupportRoi = true,
                ParamName = "操作类型 0开1闭2顶帽3黑帽",
                Min = 0,
                Max = 3,
                DefaultValue = 1,
                DisplayFormat = "op:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "核大小",
                Min = 1,
                Max = 21,
                DefaultValue = 3,
                DisplayFormat = "ksize:{0}",
                ForceOdd = true
            },
            new TaskParamDesc
            {
                ParamName = "迭代次数",
                Min = 1,
                Max = 10,
                DefaultValue = 1,
                DisplayFormat = "iter:{0}",
                ForceOdd = false
            }
        };

        // 界面参数 0~3 与 OpenCV 形态学类型的映射表
        private static readonly MorphTypes[] MorphOps = { MorphTypes.Open, MorphTypes.Close, MorphTypes.TopHat, MorphTypes.BlackHat };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 空图保护
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            int ksize = paramValues[1];
            int iter = paramValues[2];

            Mat dst = new();
            // 矩形核；%4 防御越界（界面已限制 0~3）
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(ksize, ksize)))
            {
                // 按选中的操作类型执行一次形态学组合运算
                Cv2.MorphologyEx(srcMat, dst, MorphOps[paramValues[0] % 4], kernel, null, iter);
            }
            return dst;
        }
    }
}