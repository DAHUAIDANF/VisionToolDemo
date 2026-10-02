using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 屏幕截图：抓取桌面（整屏 / 指定显示器 / 自定义区域）作为流水线输入。
    ///
    /// 它是个"源"算子 —— 忽略传入的图像，直接返回抓到的屏幕。
    /// 自动化循环里由运行器直接调用抓屏（这样能同时记录屏幕原点，供后续点击换算坐标），
    /// 本算子主要用于手动验证抓屏区域是否选对。
    /// </summary>
    public class ScreenGrabTask : IVisionTask, IResultReporter, Automation.IAutomationNode, Automation.IStringParamTask
    {
        public string TaskName => "屏幕截图";

        /// <summary>节点属性里的「文本」框 = 保存目录（留空则用 AutomationContext.SaveImageDir）</summary>
        public string NodeText { get; set; } = "";

        /// <summary>未使用（接口要求）</summary>
        public string NodeKey { get; set; } = "";

        /// <summary>本次真正写出的文件路径（未保存则为空）</summary>
        public string LastSavedPath { get; private set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>本次截图对应的屏幕原点（虚拟桌面坐标）</summary>
        public int OriginX { get; private set; }
        public int OriginY { get; private set; }
        public int CapturedWidth { get; private set; }
        public int CapturedHeight { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "区域 0全屏1主屏2自定义", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "区域:{0}", Group = "截图",
                Tip = "0全屏：抓所有显示器拼成的整个虚拟桌面。\n" +
                "1主屏：只抓主显示器。\n" +
                "2自定义：用下面的 X/Y/宽/高 指定区域（屏幕坐标，可超出后自动裁剪）。" },
            new TaskParamDesc { ParamName = "X", Min = -10000, Max = 10000, DefaultValue = 0,
                DisplayFormat = "X:{0}", Group = "截图" },
            new TaskParamDesc { ParamName = "Y", Min = -10000, Max = 10000, DefaultValue = 0,
                DisplayFormat = "Y:{0}", Group = "截图" },
            new TaskParamDesc { ParamName = "宽", Min = 0, Max = 10000, DefaultValue = 800,
                DisplayFormat = "宽:{0}", Group = "截图", Tip = "0 = 从 X 到屏幕右边缘" },
            new TaskParamDesc { ParamName = "高", Min = 0, Max = 10000, DefaultValue = 600,
                DisplayFormat = "高:{0}", Group = "截图", Tip = "0 = 从 Y 到屏幕下边缘" },
            // 新增参数一律追加到末尾：参数在流水线 JSON 里按索引保存，
            // 插到中间会让已保存的图全部错位。
            new TaskParamDesc { ParamName = "隐藏本软件窗口 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "隐藏本窗口:{0}", Group = "截图",
                Tip = "1 = 抓屏前先把本软件的窗口藏起来，抓完自动恢复（不抢焦点）。\n" +
                "本程序最大化运行时，不隐藏的话抓到的「桌面」几乎全是自己的界面。\n" +
                "节点图里这个开关会覆盖整轮运行：只藏这一次截图的话，后面鼠标点击" +
                "的坐标会被恢复出来的窗口挡住，等于点自己。" },
            // 以下是"保存图片"相关，一律追加在末尾（参数按索引保存）
            new TaskParamDesc { ParamName = "保存图片 0否1是", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "保存图片:{0}", Group = "保存",
                Tip = "1 = 把这张截图存成文件（留档/事后核对）。\n" +
                "文件名自动带时间戳（截图_20260514_103045_123.png），不会覆盖旧文件。\n" +
                "目录：节点属性里「文本」框填（支持 {变量}）；留空则用**程序所在目录**下的“截图”文件夹。\n" +
                "**干跑时不写文件**，只在日志里说明本该存到哪里。" },
            new TaskParamDesc { ParamName = "保存格式 0PNG1JPG2BMP", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "格式:{0}", Group = "保存",
                Tip = "PNG 无损（推荐留档）；JPG 体积小；BMP 最大但无压缩。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            LastSavedPath = null;
            int mode = Math.Clamp(paramValues[0], 0, 2);
            int x = paramValues[1], y = paramValues[2];
            int w = paramValues[3], h = paramValues[4];

            System.Drawing.Rectangle region;
            if (mode == 0) region = Automation.ScreenCapture.VirtualBounds;
            else if (mode == 1) region = Automation.ScreenCapture.PrimaryBounds;
            else
            {
                var vb = Automation.ScreenCapture.VirtualBounds;
                int rw = w <= 0 ? vb.Right - x : w;
                int rh = h <= 0 ? vb.Bottom - y : h;
                region = new System.Drawing.Rectangle(x, y, Math.Max(1, rw), Math.Max(1, rh));
            }

            // 参数数组可能来自旧图（长度不足 6），越界读会抛，按"不隐藏"处理
            bool hideSelf = paramValues.Length > 5 && paramValues[5] == 1;
            // 走统一入口：隐藏 or 移出屏幕由 AutomationContext 的策略决定
            IDisposable hideScope = hideSelf ? Automation.ScreenCapture.SuppressOwnWindows() : null;
            try
            {
                if (!Automation.ScreenCapture.TryCapture(region, out Mat mat, out string err))
                {
                    LastSummary = "屏幕截图: " + err;
                    return new Mat();
                }

                // 记下屏幕原点，供后续"点击检测到的目标"换算坐标
                var actual = System.Drawing.Rectangle.Intersect(
                    region, Automation.ScreenCapture.VirtualBounds);
                OriginX = actual.Left;
                OriginY = actual.Top;
                CapturedWidth = mat.Cols;
                CapturedHeight = mat.Rows;
                Automation.AutomationContext.CaptureOriginX = OriginX;
                Automation.AutomationContext.CaptureOriginY = OriginY;
                // 标记"本轮确实是抓屏来的"：匹配算子据此决定要不要报屏幕坐标
                Automation.AutomationContext.HasCapture = true;

                // 记下这张图的坐标换算比：DPI 不感知时"图上像素 ≠ SetCursorPos 的坐标"，
                // 不换算会让后面所有基于图像坐标的点击整体偏移
                double imgScale = Automation.CoordinateSpace.ImageToScreenScale(mat);
                Automation.AutomationContext.CaptureImageToScreenScale = imgScale;

                string extra = hideSelf
                    ? "  已让本窗口不挡路(" + Automation.ScreenCapture.SuppressStrategyName + ")"
                    : "";

                // 坐标空间不一致必须报出来：光看图像尺寸是发现不了的
                if (Math.Abs(imgScale - 1.0) > 0.001)
                {
                    var procSize = Automation.CoordinateSpace.ProcessScreenSize;
                    extra += string.Format("  [图上像素≠屏幕坐标：本进程坐标空间 {0}x{1}，换算 ×{2:F3}]",
                        procSize.Width, procSize.Height, imgScale);
                }

                // —— 保存图片 ——
                bool save = paramValues.Length > 6 && paramValues[6] == 1;
                int fmt = paramValues.Length > 7 ? Math.Clamp(paramValues[7], 0, 2) : 0;
                if (save)
                {
                    string dir = string.IsNullOrWhiteSpace(NodeText)
                        ? Automation.AutomationContext.SaveImageDir
                        : Automation.AutomationContext.ExpandVariables(NodeText.Trim());
                    if (Automation.AutomationContext.DryRun)
                    {
                        extra += string.Format("  （干跑：本应保存到 {0}）", dir);
                    }
                    else if (TrySaveImage(mat, dir, fmt, out string path, out string saveErr))
                    {
                        LastSavedPath = path;
                        extra += "  已保存 " + System.IO.Path.GetFileName(path);
                    }
                    else
                    {
                        extra += "  保存失败: " + saveErr;
                    }
                }

                LastSummary = string.Format("屏幕截图: {0}x{1} @ 屏幕({2},{3}){4}",
                    mat.Cols, mat.Rows, OriginX, OriginY, extra);
                return mat;
            }
            finally
            {
                hideScope?.Dispose();
            }
        }

        /// <summary>
        /// 把截图写进磁盘。文件名带毫秒时间戳，并保证不覆盖已有文件；
        /// 保存失败只返回原因，不让上层崩 —— 截图本身是成功的，
        /// 保存失败不该毁掉整轮自动化（用户至少还能在结果图里看到画面）。
        /// </summary>
        private static bool TrySaveImage(Mat img, string dir, int fmt, out string path, out string error)
        {
            path = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) { error = "保存目录为空"; return false; }
                System.IO.Directory.CreateDirectory(dir);
                if (img == null || img.Empty()) { error = "图像为空"; return false; }

                string ext = fmt switch { 1 => ".jpg", 2 => ".bmp", _ => ".png" };
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string file = System.IO.Path.Combine(dir, "截图_" + stamp + ext);
                for (int n = 2; System.IO.File.Exists(file); n++)
                    file = System.IO.Path.Combine(dir, string.Format("截图_{0}_{1}{2}", stamp, n, ext));

                if (!Cv2.ImWrite(file, img)) { error = "写入失败（目录是否可写?）"; return false; }
                path = file;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
