using System;
using System.Globalization;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 标定：设定"1 像素 = 多少毫米"，让测量值能直接给毫米。
    ///
    /// 工业检测报的都是毫米，而 OpenCV 算出来是像素。两种常见用法：
    ///   · 方式 1（默认，推荐）：量一段已知长度的像素数（比如用测量算子量标准块，
    ///     把结果存进变量），填它的**实际长度**，节点自动算出比例尺；
    ///   · 方式 0：直接填 mm/像素（有出厂标定数据时用）。
    ///
    /// 字符串槽：0 方式1=像素长度（数字或变量名）｜方式0=mm每像素   1 写入变量名（默认「比例尺」）
    /// 之后表达式里可以用 毫米({像素值}) / 像素({毫米值}) 换算。
    /// </summary>
    public class CalibrateTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        public string TaskName => "标定";

        public string LastSummary { get; private set; } = "";
        public Func<int, string> NodeStringProvider { get; set; }

        public double MmPerPixel { get; private set; }
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "方式 0直接填1由长度换算", Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "方式:{0}", Group = "标定",
                Tip = "1（默认）= 由「已知像素长度 + 实际长度」算出比例尺；\n" +
                      "0 = 直接填 mm/像素。" },
            new TaskParamDesc { ParamName = "实际长度 ×0.001mm", Min = 0, Max = 1000000, DefaultValue = 10000,
                DisplayFormat = "实际长度:{0}", Group = "标定",
                Tip = "方式=1 时的已知真实长度，单位 0.001mm。\n" +
                      "例：标准块 10mm → 填 10000（=10mm）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            MmPerPixel = 0;
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int mode = paramValues.Length > 0 ? Math.Clamp(paramValues[0], 0, 1) : 1;
            double knownMm = (paramValues.Length > 1 ? paramValues[1] : 10000) * 0.001;

            string slot0 = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(0) ?? "").Trim());
            string varName = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(1) ?? "").Trim());
            if (varName.Length == 0) varName = "比例尺";

            if (mode == 0)
            {
                if (!double.TryParse(slot0, NumberStyles.Float, CultureInfo.InvariantCulture, out double mpp) || mpp <= 0)
                {
                    Skipped = true;
                    LastSummary = "标定: 跳过 —— 方式=直接填 时，请在第一个框里填 mm/像素（大于 0 的数字）";
                    return dst;
                }
                MmPerPixel = mpp;
            }
            else
            {
                // 第一框可以是数字，也可以是变量名（"量出来的像素长度"）
                string pixelText = slot0;
                string from = "直接填的像素数";
                if (!double.TryParse(pixelText, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
                {
                    if (Automation.AutomationContext.TryResolveVariable(pixelText, out string v))
                    {
                        from = "变量 " + pixelText;
                        if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out px))
                        {
                            Skipped = true;
                            LastSummary = string.Format("标定: 跳过 —— 变量 {0} 的值 \"{1}\" 不是数字", pixelText, v);
                            return dst;
                        }
                    }
                    else
                    {
                        Skipped = true;
                        LastSummary = string.Format("标定: 跳过 —— 第一个框既不是数字也不是已有变量（填了 \"{0}\"）", pixelText);
                        return dst;
                    }
                }
                if (px <= 0 || knownMm <= 0)
                {
                    Skipped = true;
                    LastSummary = string.Format("标定: 跳过 —— 像素长度({0}) 与实际长度({1}mm) 都必须大于 0", px, knownMm);
                    return dst;
                }
                MmPerPixel = knownMm / px;
                LastSummary = string.Format("标定: {0} = {1}mm（{2} 像素 = {3}mm，来自 {4}）→ {5}",
                    varName, MmPerPixel.ToString("0.######", CultureInfo.InvariantCulture),
                    px, knownMm.ToString("0.###", CultureInfo.InvariantCulture), from, varName);
            }

            Automation.AutomationContext.MmPerPixel = MmPerPixel;
            Automation.AutomationContext.SetVariable(varName,
                MmPerPixel.ToString("0.######", CultureInfo.InvariantCulture));

            if (LastSummary.Length == 0)
                LastSummary = string.Format("标定: {0} = {1} mm/像素 → 写入 {2}",
                    varName, MmPerPixel.ToString("0.######", CultureInfo.InvariantCulture), varName);
            return dst;
        }
    }
}
