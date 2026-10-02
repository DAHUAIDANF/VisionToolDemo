using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Train
{
    /// <summary>YOLO 标注里的一个框（已换算成像素坐标，可直接当分类样本裁出来用）。</summary>
    public sealed record YoloBox(string Image, int X, int Y, int W, int H, string Label);

    /// <summary>
    /// YOLO 标注导入器：把「每张图一个同名 .txt」的 YOLO 检测标注，转成本工程的标注条目
    /// （像素坐标 x/y/w/h + 类别名），从而可以直接喂给 DeepTrainer.LoadAnnotated 训练。
    ///
    /// 参考项目 labelimage（Qt 标注工具）导出的检测标注就是这种格式，因此本类让
    /// 「用外部标注工具标完 → 回到本软件训练」这条路走得通，不必在本软件里重标一遍。
    ///
    /// YOLO 每行格式（空格分隔，5 个字段，坐标为 0~1 归一化值）：
    ///     class_id  cx  cy  w  h
    /// 其中 cx/cy 是框中心，w/h 是框宽高，都相对整图宽高归一化。
    /// 换算公式：像素左上角 x = (cx - w/2) * 图宽，y = (cy - h/2) * 图高；
    ///           像素宽 = w * 图宽，像素高 = h * 图高。
    /// 换算后必须裁剪到图像边界内——外部工具可能标出略微越界的框，
    /// 不裁剪会让后续裁 ROI 时抛异常或取到空图。
    ///
    /// 类别名来自目录里的 classes.txt / labels.txt / obj.names（每行一个，行号即 class_id）；
    /// 三个都没有时退化为「class0/class1…」，保证仍能训练（只是类别名不好读）。
    /// </summary>
    public static class YoloLabelImporter
    {
        /// <summary>支持的图片扩展名（与 DeepTrainer 保持一致，另加 webp）。</summary>
        private static readonly string[] ImgExts =
            [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"];

        /// <summary>类别名文件的候选名（按顺序找，找到第一个存在的就用）。</summary>
        private static readonly string[] ClassFileNames = ["classes.txt", "labels.txt", "obj.names"];

        /// <summary>
        /// 扫描目录并导入 YOLO 标注。
        /// </summary>
        /// <param name="folder">训练目录（内含图片 + 同名 .txt 标注）</param>
        /// <param name="log">日志回调（可空）</param>
        /// <returns>全部标注框（像素坐标）。目录里一个标注都没有时返回空列表，不抛异常。</returns>
        public static List<YoloBox> Import(string folder, Action<string> log = null)
        {
            var result = new List<YoloBox>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("目录不存在：" + folder);

            var classNames = LoadClassNames(folder, log);

            var images = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => ImgExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (images.Count == 0)
                throw new InvalidOperationException("目录里没有图片（支持 png/jpg/jpeg/bmp/tif/tiff/webp）");

            int imgWithLabel = 0, skippedLines = 0;
            foreach (string imgPath in images)
            {
                string imgName = Path.GetFileName(imgPath);
                string txtPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(imgPath) + ".txt");
                if (!File.Exists(txtPath)) continue;

                // 读一次图拿到真实宽高（YOLO 归一化坐标必须依赖原图尺寸才能还原）
                int imgW = 0, imgH = 0;
                using (var mat = Cv2.ImRead(imgPath, ImreadModes.Color))
                {
                    if (mat == null || mat.Empty()) continue;
                    imgW = mat.Cols;
                    imgH = mat.Rows;
                }
                if (imgW <= 0 || imgH <= 0) continue;

                bool any = false;
                foreach (string raw in File.ReadAllLines(txtPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) { skippedLines++; continue; }

                    // 用不变文化解析：YOLO 文件里是小数点，某些系统区域设置下逗号当小数点会解析失败
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int clsId)
                        || !TryParseFloat(parts[1], out float cx)
                        || !TryParseFloat(parts[2], out float cy)
                        || !TryParseFloat(parts[3], out float bw)
                        || !TryParseFloat(parts[4], out float bh))
                    { skippedLines++; continue; }

                    if (clsId < 0 || bw <= 0 || bh <= 0) { skippedLines++; continue; }

                    // 归一化 → 像素（先算浮点，再四舍五入，避免两次取整累积误差）
                    double px = (cx - bw / 2.0) * imgW;
                    double py = (cy - bh / 2.0) * imgH;
                    double pw = bw * imgW;
                    double ph = bh * imgH;

                    int x = (int)Math.Round(px), y = (int)Math.Round(py);
                    int w = (int)Math.Round(pw), h = (int)Math.Round(ph);

                    // 裁剪到图像边界内：外部工具可能标出越界框，不裁会让后续取 ROI 崩掉
                    int x2 = Math.Min(x + w, imgW), y2 = Math.Min(y + h, imgH);
                    x = Math.Max(0, x); y = Math.Max(0, y);
                    w = x2 - x; h = y2 - y;
                    if (w < 2 || h < 2) { skippedLines++; continue; }   // 太小的框没有训练价值

                    string label = clsId < classNames.Count ? classNames[clsId] : "class" + clsId;
                    result.Add(new YoloBox(imgName, x, y, w, h, label));
                    any = true;
                }
                if (any) imgWithLabel++;
            }

            log?.Invoke($"YOLO 导入：{images.Count} 张图 / {imgWithLabel} 张有标注 / 共 {result.Count} 个框"
                        + (skippedLines > 0 ? $"（跳过 {skippedLines} 行无法解析或过小的标注）" : ""));
            if (result.Count == 0)
                log?.Invoke("没有导入任何标注：确认每张图旁边有同名 .txt，且每行是「类别号 中心x 中心y 宽 高」（0~1 归一化）");
            return result;
        }

        /// <summary>读取类别名文件；找不到就返回空列表（调用方退化为 class{id}）。</summary>
        private static List<string> LoadClassNames(string folder, Action<string> log)
        {
            foreach (string name in ClassFileNames)
            {
                string p = Path.Combine(folder, name);
                if (!File.Exists(p)) continue;
                var names = File.ReadAllLines(p)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                if (names.Count > 0)
                {
                    log?.Invoke($"类别名来自 {name}：{string.Join(" / ", names)}");
                    return names;
                }
            }
            log?.Invoke("没找到 classes.txt / labels.txt / obj.names，类别名按 class0、class1… 生成");
            return [];
        }

        /// <summary>不变文化解析浮点，兼容科学计数法与小数点。</summary>
        private static bool TryParseFloat(string s, out float v)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
               && !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
