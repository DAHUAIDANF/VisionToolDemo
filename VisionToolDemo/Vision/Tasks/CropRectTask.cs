using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 裁剪矩形：按 X / Y / 宽 / 高 取出一块，交给链上的后续算子处理。
    ///
    /// 为什么需要它：ROI（视觉页那个"用选区做 ROI"）只作用于**原图**，
    /// 想对链上某一步的结果再裁一块就没辙了。有了这个算子，裁剪就是链上的一步：
    ///   读图 → 裁剪矩形 → 二值化 → …（位置是显式参数，可存进算子里程，可批量复用）
    ///
    /// 两种模式：
    ///   · 固定坐标：X/Y/宽/高 直接是像素（宽/高填 0 表示"到右/下边缘"）；
    ///   · 按比例‰：四个值都是千分比（0~1000），分辨率变了也不用改参数（换相机、换截图尺寸都稳）。
    ///
    /// 视觉页里拉好框后加入本算子，X/Y/宽/高 会**自动按选区填好**，不用手抄。
    /// </summary>
    public class CropRectTask : IVisionTask, IResultReporter
    {
        public string TaskName => "裁剪矩形";

        public string LastSummary { get; private set; } = "";

        /// <summary>实际生效的矩形（图像坐标），供测试与界面核对</summary>
        public Rect LastRect { get; private set; }

        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "模式 0固定坐标1按比例‰", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "模式:{0}", Group = "裁剪",
                Tip = "0 固定坐标：X/Y/宽/高 是像素；\n" +
                      "1 按比例‰：四个值都是千分比（简单说就是「百分之几的 10 倍」），\n" +
                      "  例如 X=500 表示从宽度的一半开始。换分辨率/换相机时不用改参数。" },
            new TaskParamDesc { ParamName = "X 左侧", Min = 0, Max = 20000, DefaultValue = 0,
                DisplayFormat = "X:{0}", Group = "裁剪",
                Tip = "裁剪区左上角的横坐标（相对当前输入图）。0 = 最左。" },
            new TaskParamDesc { ParamName = "Y 上侧", Min = 0, Max = 20000, DefaultValue = 0,
                DisplayFormat = "Y:{0}", Group = "裁剪",
                Tip = "裁剪区左上角的纵坐标。0 = 最上。" +
                      "\n注意：如果结果总是上下偏一截，先确认这个值（或用视觉页选区自动填）。" },
            new TaskParamDesc { ParamName = "宽 0=到右边", Min = 0, Max = 20000, DefaultValue = 0,
                DisplayFormat = "宽:{0}", Group = "裁剪",
                Tip = "裁剪区宽度。0 = 从 X 一直到右边缘。" },
            new TaskParamDesc { ParamName = "高 0=到下边", Min = 0, Max = 20000, DefaultValue = 0,
                DisplayFormat = "高:{0}", Group = "裁剪",
                Tip = "裁剪区高度。0 = 从 Y 一直到下边缘。" },
            new TaskParamDesc { ParamName = "越界时 0自动修正1跳过", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "越界:{0}", Group = "裁剪",
                Tip = "0 = 与图像求交后自动修正（只要还剩一块就继续）；\n" +
                      "1 = 只要超出图像就跳过这一步（配合「跳过即停」能及时刹车）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            LastRect = new Rect();
            Skipped = false;

            Mat dst = srcMat;
            if (srcMat == null || srcMat.Empty())
            {
                Skipped = true;
                LastSummary = "裁剪矩形: 跳过 —— 输入图像为空";
                return dst;
            }

            int mode = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 0, 0, 1);
            int px = paramValues.Length > 1 ? paramValues[1] : 0;
            int py = paramValues.Length > 2 ? paramValues[2] : 0;
            int pw = paramValues.Length > 3 ? paramValues[3] : 0;
            int ph = paramValues.Length > 4 ? paramValues[4] : 0;
            bool skipOutOfRange = paramValues.Length > 5 && paramValues[5] == 1;

            int cols = srcMat.Cols, rows = srcMat.Rows;
            int x, y, w, h;

            if (mode == 0)
            {
                x = px; y = py;
                w = pw > 0 ? pw : cols - x;          // 0 = 到底
                h = ph > 0 ? ph : rows - y;
            }
            else
            {
                // 千分比（0~1000）；填 0 表示"到底"
                x = (int)Math.Round(cols * (Math.Min(1000, px) / 1000.0));
                y = (int)Math.Round(rows * (Math.Min(1000, py) / 1000.0));
                w = pw > 0 ? (int)Math.Round(cols * (Math.Min(1000, pw) / 1000.0)) : cols - x;
                h = ph > 0 ? (int)Math.Round(rows * (Math.Min(1000, ph) / 1000.0)) : rows - y;
            }

            bool outOfRange = x < 0 || y < 0 || x >= cols || y >= rows || w <= 0 || h <= 0
                              || x + w > cols || y + h > rows;
            if (outOfRange && skipOutOfRange)
            {
                Skipped = true;
                LastSummary = string.Format("裁剪矩形: 跳过 —— 矩形 {0},{1} {2}x{3} 超出图像 {4}x{5}",
                    x, y, w, h, cols, rows);
                return dst;
            }

            // 自动修正：与图像求交
            int x1 = Math.Max(0, Math.Min(cols - 1, x));
            int y1 = Math.Max(0, Math.Min(rows - 1, y));
            int x2 = Math.Max(x1 + 1, Math.Min(cols, x + Math.Max(1, w)));
            int y2 = Math.Max(y1 + 1, Math.Min(rows, y + Math.Max(1, h)));
            var rect = new Rect(x1, y1, x2 - x1, y2 - y1);
            LastRect = rect;

            try
            {
                // 视图 + Clone：输出必须是独立对象（调用方会释放它，且不牵连输入图）
                using var view = new Mat(srcMat, rect);
                dst = view.Clone();
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = "裁剪矩形: 失败 —— " + ex.Message;
                return dst;
            }

            LastSummary = string.Format("裁剪矩形: {0}x{1} → {2}x{3} @ ({4},{5}){6}{7}",
                cols, rows, rect.Width, rect.Height, rect.X, rect.Y,
                mode == 1 ? "  [按比例‰]" : "  [固定坐标]",
                outOfRange ? "  （已自动修正越界部分）" : "");
            return dst;
        }
    }
}
