using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 检测结果中转站：把"匹配算子找到的目标位置"传给"动作算子"。
    ///
    /// 为什么需要它：本流水线在算子之间只传递**图像**（Mat），不传递结构化结果。
    /// 但自动化必须回答"要点哪里" —— 这个位置是模板匹配/形状匹配算出来的，
    /// 而点击算子拿不到它。用一个全局中转站把两者接起来，是最小改动。
    ///
    /// 语义上这也符合直觉："最近一次检测到的目标"在一轮自动化里就是唯一且明确的。
    /// 每一轮循环开始前由运行器 Clear()，避免用到上一轮的陈旧位置
    /// （陈旧位置是最危险的失效模式：目标早就不在那儿了，却还在原地狂点）。
    /// </summary>
    public static class DetectionStore
    {
        private static readonly object _lock = new();

        public static bool HasTarget { get; private set; }
        public static Point2f Center { get; private set; }
        public static double Score { get; private set; }
        public static string Source { get; private set; } = "";

        /// <summary>命中框（原图坐标，模板左上角 + 尺寸）。界面用它显示"框在哪"</summary>
        public static Rect Box { get; private set; }

        /// <summary>命中时的模板倍率（%）。多尺度匹配时它就是"目标比模板大/小多少"</summary>
        public static int ScalePercent { get; private set; }

        /// <summary>检测发生时刻（用于判断结果是否陈旧）</summary>
        public static DateTime Timestamp { get; private set; }

        /// <summary>
        /// 最近一次"检测了但没命中"的原因（没有就为空）。
        /// 动作算子跳过时可以把它一起报出来 —— 只写"没有可用目标"用户不知道是没跑匹配、
        /// 还是跑了但分数不够，这两种情况要调的东西完全不同。
        /// </summary>
        public static string MissReason { get; private set; } = "";

        /// <summary>匹配算子找到目标后调用</summary>
        public static void Publish(Point2f center, double score, string source)
            => Publish(center, score, source, new Rect(), 0);

        /// <summary>匹配算子找到目标后调用（带上命中框与倍率，界面/日志都能直接显示）</summary>
        public static void Publish(Point2f center, double score, string source, Rect box, int scalePercent)
        {
            lock (_lock)
            {
                HasTarget = true;
                Center = center;
                Score = score;
                Source = source ?? "";
                Box = box;
                ScalePercent = scalePercent;
                MissReason = "";
                Timestamp = DateTime.Now;
            }
        }

        /// <summary>
        /// 匹配算子**跑了但没命中**时调用：把目标作废并把原因记下来。
        ///
        /// 为什么必须作废：不作废的话，这一轮里更早某个匹配节点发布的坐标会留在槽里，
        /// 后面的"鼠标点击"就会照着那个陈旧位置点下去 —— 目标早就不在那儿了。
        /// 宁可跳过（用户能在日志里看到原因），也不要瞎点。
        /// </summary>
        public static void Invalidate(string reason)
        {
            lock (_lock)
            {
                HasTarget = false;
                Center = default;
                Score = 0;
                Source = "";
                Box = new Rect();
                ScalePercent = 0;
                MissReason = reason ?? "";
                Timestamp = default;
            }
        }

        /// <summary>取当前目标；无目标返回 false</summary>
        public static bool TryGet(out Point2f center, out double score, out string source)
        {
            lock (_lock)
            {
                center = Center; score = Score; source = Source;
                return HasTarget;
            }
        }

        /// <summary>每轮开始前清空，防止误用上一轮的陈旧位置</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                HasTarget = false;
                Center = default;
                Score = 0;
                Source = "";
                Box = new Rect();
                ScalePercent = 0;
                MissReason = "";
                Timestamp = default;
            }
        }
    }
}
