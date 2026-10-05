using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>算子里程里的一步（可序列化：算子名 + 参数 + 模板图）</summary>
    public sealed class ChainStepData
    {
        public string OpName { get; set; } = "";
        public int[] Values { get; set; } = Array.Empty<int>();

        /// <summary>文本参数值（与 Values 槽位一一对应；仅 TextDefault 参数槽位有值，其余为 null）。
        /// 旧链文件没有该字段时为 null，执行时按空文本处理。</summary>
        public string[] Texts { get; set; }

        /// <summary>模板/参考图（PNG 的 base64；空 = 这个算子不需要模板）</summary>
        public string TemplatePngBase64 { get; set; } = "";
    }

    /// <summary>一步的执行结果</summary>
    public sealed class ChainStepResult
    {
        public string OpName = "";
        public long Ms;
        public string Summary = "";
        public bool Ok = true;
        public Mat Output;
    }

    /// <summary>
    /// 算子里程：一串"算子 + 参数（+模板）"，以及把它们依次跑完的能力。
    ///
    /// 为什么把它从界面里抽出来：视觉页（人工调）和批量处理（跑文件夹）用的是**同一条链**，
    /// 而且链要能存成文件给别人用/给无人值守用。界面只管编辑，执行语义在这里。
    /// </summary>
    public static class OperatorChain
    {
        public static string ToJson(IEnumerable<ChainStepData> steps)
            => JsonConvert.SerializeObject(steps ?? Enumerable.Empty<ChainStepData>(), Formatting.Indented);

        public static List<ChainStepData> FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ChainStepData>();
            try
            {
                var list = JsonConvert.DeserializeObject<List<ChainStepData>>(json);
                return list ?? new List<ChainStepData>();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("算子里程文件解析失败：" + ex.Message, ex);
            }
        }

        /// <summary>Mat → base64 PNG（模板随链一起存）</summary>
        public static string EncodeTemplate(Mat mat)
        {
            if (mat == null || mat.Empty()) return "";
            try
            {
                Cv2.ImEncode(".png", mat, out byte[] buf);
                return Convert.ToBase64String(buf);
            }
            catch { return ""; }
        }

        public static Mat DecodeTemplate(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;
            try
            {
                byte[] buf = Convert.FromBase64String(base64);
                return Cv2.ImDecode(buf, ImreadModes.Color);
            }
            catch { return null; }
        }

        /// <summary>
        /// 依次执行整条链。input 的像素不会被修改（每步算子都会返回新图）。
        /// 返回**最后成功那一步**的输出（调用方负责释放）；全部失败或链为空时返回 null。
        /// error 非空表示中途出错（此时 results 里会带上出错那一步的信息）。
        /// </summary>
        public static Mat Run(IList<ChainStepData> steps, Mat input,
            out List<ChainStepResult> results, out string error)
        {
            results = new List<ChainStepResult>();
            error = null;
            if (steps == null || steps.Count == 0) { error = "算子里程是空的"; return null; }
            if (input == null || input.Empty()) { error = "没有输入图像"; return null; }

            Mat current = input;
            Mat last = null;
            var temps = new List<Mat>();

            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    var row = new ChainStepResult { OpName = step.OpName };
                    results.Add(row);

                    var task = VisionTaskRegistry.GetTask(step.OpName);
                    if (task == null)
                    {
                        row.Ok = false;
                        row.Summary = "找不到算子";
                        error = string.Format("第 {0} 步的算子不存在：{1}", i + 1, step.OpName);
                        return last;
                    }

                    try
                    {
                        if (Automation.AutomationSupport.NeedsTemplate(task))
                        {
                            var tpl = DecodeTemplate(step.TemplatePngBase64);
                            if (tpl == null || tpl.Empty())
                            {
                                // 可选参考图的算子（枯叶噪声/镜头阴影校正）缺模板不阻断，按自参考方式继续；
                                // 必需模板的算子（模板匹配/图像拼接等）缺模板直接报错
                                if (Automation.AutomationSupport.TemplateRequired(task))
                                {
                                    row.Ok = false;
                                    row.Summary = "缺少模板图";
                                    error = string.Format("第 {0} 步（{1}）需要模板图，但算子里程里没有", i + 1, step.OpName);
                                    tpl?.Dispose();
                                    return last;
                                }
                            }
                            else
                            {
                                temps.Add(tpl);
                                Automation.AutomationSupport.ApplyTemplate(task, tpl);
                            }
                        }

                        var sw = Stopwatch.StartNew();
                        // 文本参数注入：把链文件里的文本值写入算子的 NodeText（二维码内容等）
                        if (task is Automation.IStringParamTask sp)
                        {
                            var defs = task.ParamDescriptions;
                            int ti = -1;
                            for (int j = 0; j < (defs?.Length ?? 0); j++)
                                if (defs[j].TextDefault != null) { ti = j; break; }
                            sp.NodeText = (ti >= 0 && step.Texts != null && ti < step.Texts.Length && step.Texts[ti] != null)
                                ? step.Texts[ti] : "";
                        }
                        Mat output = task.Execute(current, step.Values ?? Array.Empty<int>());
                        sw.Stop();

                        row.Ms = sw.ElapsedMilliseconds;
                        row.Summary = (task as IResultReporter)?.LastSummary ?? "";
                        row.Output = output;
                        current = output;
                        last = output;
                    }
                    catch (Exception ex)
                    {
                        row.Ok = false;
                        row.Summary = "失败：" + ex.Message;
                        error = string.Format("第 {0} 步（{1}）执行失败：{2}", i + 1, step.OpName, ex.Message);
                        return last;
                    }
                }
                return last;
            }
            finally
            {
                // 模板只是"注入用"的临时对象，执行完就该释放（各算子不会接管它的所有权）
                foreach (var t in temps) t?.Dispose();
            }
        }

        /// <summary>按"图像文件的扩展名"判断能不能处理（批量与拖放共用一份清单）</summary>
        public static readonly string[] ImageExtensions =
        [
            ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".raw",
        ];

        public static bool IsImageFile(string path)
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ImageExtensions.Contains(ext)) return true;
            return Vision.RawCameraLoader.IsCameraRaw(path);
        }
    }
}
