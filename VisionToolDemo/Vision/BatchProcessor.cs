using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>批量处理里的一行结果</summary>
    public sealed class BatchRow
    {
        public string File = "";
        public bool Ok;
        public long Ms;
        public string Summary = "";
        public string OutPath = "";
        public string Error = "";
    }

    /// <summary>
    /// 批量处理：对一个文件夹里的每张图跑同一条算子里程，结果图另存并生成报告。
    ///
    /// 与旧界面一致的行为：
    ///   · 结果图存到「输出目录」，默认 <输入目录>\处理结果；
    ///   · 生成「批量处理报告.txt」（逐文件：结果、耗时、摘要、失败原因）；
    ///   · .raw 裸数据用同一套参数（一批共用），相机 RAW 自动解码。
    /// 与旧界面的差别：这里跑的是**算子里程文件**，不依赖任何界面状态，因此也能在无人值守下用。
    /// </summary>
    public static class BatchProcessor
    {
        /// <summary>默认输出子目录名（与旧界面一致）</summary>
        public const string DefaultOutputFolderName = "处理结果";

        public const string ReportFileName = "批量处理报告.txt";

        /// <summary>
        /// 跑一批。progress(已完成数, 总数, 当前文件) 可为 null。
        /// report 返回报告全文；reportPath 返回报告文件路径（写失败时为空）。
        /// </summary>
        public static List<BatchRow> Run(string inputDir, string outputDir,
            IList<ChainStepData> chain, RawImageParams rawParams,
            Action<int, int, string> progress, out string report, out string reportPath)
        {
            report = "";
            reportPath = "";
            var rows = new List<BatchRow>();

            if (!Directory.Exists(inputDir))
            {
                report = "输入文件夹不存在：" + inputDir;
                return rows;
            }
            if (chain == null || chain.Count == 0)
            {
                report = "算子里程是空的：先在视觉页搭好链并导出，或在这里选一个算子里程文件";
                return rows;
            }

            string outRoot = string.IsNullOrWhiteSpace(outputDir)
                ? Path.Combine(inputDir, DefaultOutputFolderName)
                : outputDir;
            try { Directory.CreateDirectory(outRoot); }
            catch (Exception ex)
            {
                report = "建不了输出目录：" + ex.Message;
                return rows;
            }

            var files = Directory.GetFiles(inputDir)
                .Where(OperatorChain.IsImageFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                report = "该文件夹里没有可处理的图片（支持 jpg/jpeg/png/bmp/tif/tiff/webp/raw 与相机 RAW）";
                return rows;
            }

            var swAll = Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                progress?.Invoke(i, files.Count, Path.GetFileName(file));

                var row = new BatchRow { File = Path.GetFileName(file) };
                var sw = Stopwatch.StartNew();
                Mat src = null;
                Mat result = null;
                try
                {
                    if (!TryLoadImage(file, rawParams, out src, out string loadErr))
                    {
                        row.Error = loadErr;
                    }
                    else
                    {
                        result = OperatorChain.Run(chain, src, out List<ChainStepResult> steps, out string runErr);
                        row.Ms = sw.ElapsedMilliseconds;
                        row.Summary = steps.Count > 0 ? steps[steps.Count - 1].Summary : "";
                        if (!string.IsNullOrEmpty(runErr)) row.Error = runErr;
                        else row.Ok = result != null;

                        if (result != null && !result.Empty())
                        {
                            row.OutPath = Path.Combine(outRoot,
                                Path.GetFileNameWithoutExtension(file) + "_结果.png");
                            try { Cv2.ImWrite(row.OutPath, result); }
                            catch (Exception ex) { row.Error = "结果图保存失败：" + ex.Message; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    row.Error = ex.Message;
                }
                finally
                {
                    src?.Dispose();
                    // result 是算子返回的新图，存完即可释放（不是输入的一部分）
                    if (result != null && !ReferenceEquals(result, src)) result.Dispose();
                    sw.Stop();
                    if (row.Ms == 0) row.Ms = sw.ElapsedMilliseconds;
                }

                rows.Add(row);
            }
            swAll.Stop();
            progress?.Invoke(files.Count, files.Count, "");

            report = BuildReport(inputDir, outRoot, chain, rows, swAll.ElapsedMilliseconds);
            try
            {
                reportPath = Path.Combine(inputDir, ReportFileName);
                File.WriteAllText(reportPath, report, Encoding.UTF8);
            }
            catch { reportPath = ""; }
            return rows;
        }

        /// <summary>单文件加载：普通图片 / 裸 .raw / 相机 RAW</summary>
        public static bool TryLoadImage(string path, RawImageParams rawParams, out Mat mat, out string error)
        {
            mat = null;
            error = null;
            try
            {
                if (RawCameraLoader.IsCameraRaw(path))
                    return RawCameraLoader.TryLoad(path, out mat, out error);

                if (string.Equals(Path.GetExtension(path), ".raw", StringComparison.OrdinalIgnoreCase))
                {
                    if (rawParams == null)
                    {
                        error = "这是裸 .raw 数据，需要先给定宽高/位深等参数（视觉页打开时会弹参数框，批量会用最近一次的参数）";
                        return false;
                    }
                    return RawImageLoader.TryLoad(path, rawParams, out mat, out error);
                }

                mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat == null || mat.Empty())
                {
                    error = "读不出图像（格式不支持或文件损坏）";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string BuildReport(string inputDir, string outputDir,
            IList<ChainStepData> chain, List<BatchRow> rows, long totalMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("批量处理报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("输入目录：" + inputDir);
            sb.AppendLine("输出目录：" + outputDir);
            sb.AppendLine("算子里程：" + string.Join(" → ", chain.Select(s => s.OpName)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "共 {0} 个文件：成功 {1}，失败 {2}，总耗时 {3}ms",
                rows.Count, rows.Count(r => r.Ok), rows.Count(r => !r.Ok), totalMs));
            sb.AppendLine(new string('=', 72));
            foreach (var r in rows)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0}] {1}  {2}ms",
                    r.Ok ? "OK" : "NG", r.File, r.Ms));
                if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine("      摘要：" + r.Summary);
                if (!string.IsNullOrWhiteSpace(r.OutPath)) sb.AppendLine("      结果：" + r.OutPath);
                if (!string.IsNullOrWhiteSpace(r.Error)) sb.AppendLine("      原因：" + r.Error);
            }
            return sb.ToString();
        }
    }
}
