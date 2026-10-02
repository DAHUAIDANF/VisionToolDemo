using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 区域裁剪 ROI：把当前图像裁到指定区域并替换当前图，
    /// 后续节点（模板匹配 / OCR / 通用视觉算子）自动只在该区域内检测。
    ///
    /// 与流水线里的"裁剪矩形"算子不同：本算子是**自动化节点专用**
    /// （实现 IAutomationNode，不出现在视觉流水线算子列表里），
    /// 它的语义是"把当前图换成区域图"，由节点运行器在截图同一条路径上
    /// 替换当前图像，让下游节点看到的就是区域 —— 这是节点系统里
    /// "只检测固定区域"链路的关键一环（截图 → ROI → 匹配/识别）。
    /// </summary>
    public class RoiTask : IVisionTask, Automation.IAutomationNode, Automation.IStringParamTask
    {
        public string TaskName => "区域裁剪ROI";

        /// <summary>节点属性里的「文本」框 = 区域变量名（仅"区域来源=变量"时用）</summary>
        public string NodeText { get; set; } = "";

        /// <summary>未使用（接口要求）</summary>
        public string NodeKey { get; set; } = "";

        public string LastSummary { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "区域来源 0固定1变量", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "区域来源:{0}", Group = "ROI",
                Tip = "0 = 用下面的 X / Y / 宽 / 高（固定区域）。\n" +
                "1 = 从**全局变量**读区域：变量名填在【节点属性 → 输入内容】的「文本」框，\n" +
                "    变量值形如 10,20,300,200（X,Y,宽,高，逗号或空格分隔，支持 {变量} 插值），\n" +
                "    适合配合“表达式”节点做动态区域。" },
            new TaskParamDesc { ParamName = "X", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "X:{0}", Group = "ROI", Tip = "区域左上角 X（图像像素，从 0 起）。" },
            new TaskParamDesc { ParamName = "Y", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "Y:{0}", Group = "ROI", Tip = "区域左上角 Y（图像像素，从 0 起）。" },
            new TaskParamDesc { ParamName = "宽", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "宽:{0}", Group = "ROI", Tip = "0 = 到图像右边缘；超出自动裁进图像内。" },
            new TaskParamDesc { ParamName = "高", Min = 0, Max = 100000, DefaultValue = 0,
                DisplayFormat = "高:{0}", Group = "ROI", Tip = "0 = 到图像下边缘；超出自动裁进图像内。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty())
                throw new InvalidOperationException("当前没有图像：前面还没有截图或图像源节点");

            int mode = paramValues != null && paramValues.Length > 0 ? Math.Clamp(paramValues[0], 0, 1) : 0;
            int x, y, w, h;
            string desc;

            if (mode == 1)
            {
                string name = Automation.AutomationContext.ExpandVariables(NodeText ?? "").Trim();
                if (name.Length == 0)
                    throw new InvalidOperationException("区域来源=变量，但还没有填变量名（节点属性 → 输入内容）");
                string raw = Automation.AutomationContext.TryResolveVariable(name, out string val)
                    ? val : "";
                if (!TryParseRegion(raw, out x, out y, out w, out h))
                    throw new InvalidOperationException(string.Format(
                        "变量 {0} 取不到区域：值是 \"{1}\"，应形如 10,20,300,200（X,Y,宽,高）",
                        name, string.IsNullOrEmpty(raw) ? "(这一轮还没有值)" : raw));
                desc = string.Format("变量 {0} = {1},{2} {3}x{4}", name, x, y, w, h);
            }
            else
            {
                x = paramValues.Length > 1 ? paramValues[1] : 0;
                y = paramValues.Length > 2 ? paramValues[2] : 0;
                w = paramValues.Length > 3 ? paramValues[3] : 0;
                h = paramValues.Length > 4 ? paramValues[4] : 0;
                desc = string.Format("{0},{1} {2}x{3}", x, y, w, h);
            }

            // 边界：宽/高 0 = 到右/下边缘；超出自动裁进图像；X/Y 越界或算出空区域 = 明确报错
            int srcW = srcMat.Cols, srcH = srcMat.Rows;
            int rw = w <= 0 ? srcW - x : w;
            int rh = h <= 0 ? srcH - y : h;
            rw = Math.Min(rw, srcW - x);
            rh = Math.Min(rh, srcH - y);
            if (x < 0 || y < 0 || x >= srcW || y >= srcH || rw <= 0 || rh <= 0)
                throw new InvalidOperationException(string.Format(
                    "区域 ({0},{1} {2}x{3}) 在当前图 {4}x{5} 之外，裁不出有效区域",
                    x, y, rw, rh, srcW, srcH));

            var roi = new Rect(x, y, rw, rh);
            Mat cropped = srcMat.Clone(roi);   // 深拷贝，与当前图独立生命周期
            LastSummary = string.Format("区域裁剪ROI: {0} → {1}x{2}（原图 {3}x{4}）",
                desc, cropped.Cols, cropped.Rows, srcW, srcH);
            return cropped;
        }

        /// <summary>解析 "X,Y,宽,高"（逗号或空格分隔，兼容全角逗号）；四个数必须是非负整数</summary>
        private static bool TryParseRegion(string text, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(
                new[] { ',', ' ', '，', '\t', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            int[] v = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), out v[i]) || v[i] < 0) return false;
            x = v[0]; y = v[1]; w = v[2]; h = v[3];
            return true;
        }
    }
}
