using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision.Tasks;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 自动化节点与视觉算子之间的适配层。
    ///
    /// 集中在这里的两个原因：
    ///   1. 模板/参考图注入必须"一处改、处处生效" —— 主界面上曾因为只列了 4 个算子，
    ///      换了模板却对另外 5 个无效（静默失效）；节点图里"通用视觉算子"节点同样要覆盖全部。
    ///   2. 结果文字（OCR/条码）要能被"全局变量"节点取到，判定逻辑也只该写一遍。
    /// </summary>
    public static class AutomationSupport
    {
        /// <summary>这个算子是否需要外部模型文件（深度学习推理：.onnx + 标签文件；
        /// 深度学习相似度：.onnx 特征模型）</summary>
        public static bool NeedsModel(IVisionTask task) => task is DeepLearnTask or DeepSimTask;

        /// <summary>这个算子是否需要模板/参考图（有则显示模板槽；注入模板都走 ApplyTemplate）</summary>
        public static bool NeedsTemplate(IVisionTask task) => task switch
        {
            TemplateMatchTask or TemplateDiffTask or FeatureMatchTask or GeometricLocatorTask
                or AffineAlignTask or ShapeMatchTask or ForegroundSplitTask or OffsetInspectTask
                or ImageArithTask or BlendModeTask or InpaintTask
                or ImageStitchTask or DeadLeavesTask or LscTask
                or ImageAlignTask or HistMatchTask => true,
            // "等待条件"的模板命中模式也要模板（它自己截图自己匹配）
            WaitConditionTask => true,
            _ => false,
        };

        /// <summary>
        /// 这个算子的模板/参考图是否**必需**（缺了就不能正确运行，链执行/链文件加载要报错）。
        /// 与 NeedsTemplate 的差别：枯叶噪声、镜头阴影校正的参考图是可选增强，
        /// 不传也能按自参考/局部方式运行 —— 所以 NeedsTemplate=true（显示模板槽）但 TemplateRequired=false。
        /// </summary>
        public static bool TemplateRequired(IVisionTask task) => task switch
        {
            TemplateMatchTask or TemplateDiffTask or FeatureMatchTask or GeometricLocatorTask
                or AffineAlignTask or ShapeMatchTask or ForegroundSplitTask or OffsetInspectTask
                or ImageArithTask or BlendModeTask or InpaintTask or ImageStitchTask
                or ImageAlignTask or HistMatchTask => true,
            WaitConditionTask => true,
            _ => false,
        };

        /// <summary>把模板/参考图注入算子；返回是否注入成功（不认识该算子返回 false）</summary>
        public static bool ApplyTemplate(IVisionTask task, Mat template)
        {
            if (task == null || template == null) return false;
            switch (task)
            {
                case TemplateMatchTask t: t.TemplateMat = template; return true;
                case TemplateDiffTask t: t.TemplateMat = template; return true;
                case FeatureMatchTask t: t.TemplateMat = template; return true;
                case GeometricLocatorTask t: t.TemplateMat = template; return true;
                case AffineAlignTask t: t.TemplateMat = template; return true;
                case ShapeMatchTask t: t.TemplateMat = template; return true;
                case ForegroundSplitTask t: t.ReferenceMat = template; return true;
                case OffsetInspectTask t: t.ReferenceMat = template; return true;
                case ImageArithTask t: t.OperandMat = template; return true;
                case BlendModeTask t: t.TemplateMat = template; return true;
                case InpaintTask t: t.TemplateMat = template; return true;
                case ImageStitchTask t: t.TemplateMat = template; return true;   // 图像拼接的第二幅图
                case DeadLeavesTask t: t.ReferenceMat = template; return true;   // 枯叶噪声参考频谱图（可选）
                case LscTask t: t.ReferenceMat = template; return true;          // 镜头阴影校正参考图（可选）
                case WaitConditionTask t: t.TemplateMat = template; return true;
                default: return false;
            }
        }

        /// <summary>
        /// 取算子的"目标位置"结果（匹配类/定位类/镜头/码）。
        ///
        /// 为什么要有这一层：能在图上找目标的算子不止"模板匹配"一个，但节点输出表原先只认它，
        /// 于是"形状匹配命中后想取中心坐标存进变量"会得到"该节点没有这项结果"——
        /// 用户只能看着日志猜。这里把各算子的字段名差异收拢到一处。
        /// Found 之外还给出 score/scale（没有的填 0/NaN），坐标一律用图像坐标。
        /// </summary>
        public static bool TryGetTarget(IVisionTask task, out bool found,
            out float x, out float y, out double score, out int scale)
        {
            found = false;
            x = y = 0;
            score = double.NaN;
            scale = 0;
            switch (task)
            {
                case TemplateMatchTask t:
                    found = t.Found; x = t.BestCenter.X; y = t.BestCenter.Y;
                    score = t.BestScore; scale = t.BestScale;
                    return true;
                case ShapeMatchTask t:                 // 形状匹配：抗旋转/缩放，字段是 Center/Score
                    found = t.Found; x = t.Center.X; y = t.Center.Y; score = t.Score;
                    return true;
                case GeometricLocatorTask t:
                    found = t.Found; x = t.Center.X; y = t.Center.Y; score = t.Score;
                    return true;
                case LensDetectTask t:
                    found = t.Found; x = t.Center.X; y = t.Center.Y;
                    return true;
                case DpmCodeTask t:                    // DPM 码：解码成功才算找到，中心就是码的位置
                    found = t.Decoded; x = t.Center.X; y = t.Center.Y;
                    return true;
                case CircleCaliperTask t:              // 圆卡尺：有圆就把圆心当目标
                    found = t.HasCircle || t.FitOk;
                    x = (float)(t.FitOk ? t.FitCenter.X : t.Center.X);
                    y = (float)(t.FitOk ? t.FitCenter.Y : t.Center.Y);
                    return true;
                case BarcodeTask t:                    // 条码：多个码时取第一个的中心（四角平均）
                    if (t.LastResults.Count > 0 && t.LastResults[0].Corners.Length > 0)
                    {
                        var corners = t.LastResults[0].Corners;
                        float sx = 0, sy = 0;
                        foreach (var c in corners) { sx += c.X; sy += c.Y; }
                        found = true;
                        x = sx / corners.Length;
                        y = sy / corners.Length;
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>该算子能不能提供"目标位置"这类结果（界面提示用）</summary>
        public static bool CanLocate(IVisionTask task)
            => task is TemplateMatchTask or ShapeMatchTask or GeometricLocatorTask or LensDetectTask
                or DpmCodeTask or CircleCaliperTask or BarcodeTask;

        /// <summary>
        /// 取算子的"文字结果"（OCR/条码/DPM 码）。自动化里这些文字要能存进全局变量、
        /// 或用 {变量} 插值到键盘输入/弹窗内容里，所以统一在这里取。
        /// </summary>
        public static bool TryGetText(IVisionTask task, out string text)
        {
            switch (task)
            {
                case OcrTask t: text = t.Text; return true;
                case BarcodeTask t:
                    // 条码算子把每个码放在 LastResults 里（Text/TypeName/四角），没有单一 Text
                    text = string.Join(" | ", t.LastResults
                        .Select(r => r.Text).Where(x => !string.IsNullOrEmpty(x)));
                    return true;
                case DpmCodeTask t: text = t.Text; return true;
                default: text = null; return false;
            }
        }
    }
}
