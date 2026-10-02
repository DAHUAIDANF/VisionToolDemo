using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Train
{
    /// <summary>增强方式。图像与其标注框会同步变换，保证增强后的样本标签仍然正确。</summary>
    public enum AugmentKind
    {
        /// <summary>顺时针 90°（新图尺寸变为 高×宽）</summary>
        Rot90,
        /// <summary>180°（尺寸不变）</summary>
        Rot180,
        /// <summary>顺时针 270°，即逆时针 90°（新图尺寸变为 高×宽）</summary>
        Rot270,
        /// <summary>水平镜像（左右翻转）</summary>
        FlipH,
        /// <summary>垂直镜像（上下翻转）</summary>
        FlipV,
        /// <summary>仅做亮度/对比度抖动，几何不变（标注框原样保留）</summary>
        Brightness,
    }

    /// <summary>
    /// 离线数据增强器（参考标注工具 labelimage 的增强对话框：旋转 90/180/270 + 镜像 + 倍数）。
    ///
    /// 为什么是「离线生成图片」而不是训练时在线增强：
    ///   · 离线产物看得见——用户能直接翻 augmented 目录确认增强得对不对，不像在线增强是个黑盒；
    ///   · 增强图可再被外部标注工具打开检查/修正；
    ///   · 与 DeepTrainer.Augment 的在线抖动互不干扰（后者实测在纹理类数据上收益为负，默认关）。
    ///
    /// 正确性核心：标注框必须与图像同步变换。旋转/翻转后框的位置会变，
    /// 若只转图像不转框，增强出来的样本标签就是错的，训练会被污染——
    /// 这是本类最需要保证的部分，故把框变换写成纯函数 TransformBox 以便单测。
    /// </summary>
    public static class TrainAugmentor
    {
        /// <summary>增强图的输出子目录名（位于训练目录内）。</summary>
        public const string OutDirName = "augmented";

        /// <summary>
        /// 对一批标注框做增强：生成增强图片到 训练目录/augmented/，并返回增强后的框（像素坐标）。
        /// </summary>
        /// <param name="folder">训练目录（原图所在目录）</param>
        /// <param name="boxes">原标注框（Image 为相对 folder 的文件名）</param>
        /// <param name="kinds">要做的增强方式（每种生成一份）</param>
        /// <param name="brightnessDelta">亮度增量（仅 Brightness 用；-60~60 合理，0 表示不变）</param>
        /// <param name="log">日志回调（可空）</param>
        /// <returns>增强后产生的框列表（Image 指向 augmented 目录下的新图）</returns>
        public static List<YoloBox> Run(string folder, List<YoloBox> boxes,
                                        IEnumerable<AugmentKind> kinds,
                                        int brightnessDelta = 0,
                                        Action<string> log = null)
        {
            var outBoxes = new List<YoloBox>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("目录不存在：" + folder);
            if (boxes == null || boxes.Count == 0)
                throw new InvalidOperationException("没有可增强的标注框");

            string outDir = Path.Combine(folder, OutDirName);
            Directory.CreateDirectory(outDir);

            // 按图分组，一张图只读一次，避免重复 IO
            var byImage = boxes.GroupBy(b => b.Image);
            var kindList = kinds?.Distinct().ToList() ?? [];

            foreach (var g in byImage)
            {
                string srcRel = g.Key;
                string srcPath = Path.Combine(folder, srcRel);
                if (!File.Exists(srcPath)) continue;

                using Mat src = Cv2.ImRead(srcPath, ImreadModes.Color);
                if (src == null || src.Empty()) continue;
                int W = src.Cols, H = src.Rows;

                foreach (var kind in kindList)
                {
                    // 目标图尺寸：90/270 度后宽高互换
                    bool swap = kind is AugmentKind.Rot90 or AugmentKind.Rot270;
                    int nW = swap ? H : W;
                    int nH = swap ? W : H;

                    string tag = kind.ToString().ToLowerInvariant();
                    string outName = Path.GetFileNameWithoutExtension(srcRel) + "_" + tag + Path.GetExtension(srcRel);
                    string outPath = Path.Combine(outDir, outName);

                    using Mat dst = ApplyToImage(src, kind, brightnessDelta);

                    // 同步变换该图上的每个框（纯函数，尺寸用变换后的 nW/nH 校验）
                    bool wrote = false;
                    foreach (var b in g)
                    {
                        var (nx, ny, nw, nh) = TransformBox(b.X, b.Y, b.W, b.H, W, H, kind);
                        if (nw < 2 || nh < 2) continue;
                        // 防御：理论上恒在界内，但万一原框越界，这里再裁一次并夹到新图尺寸内
                        nx = Math.Max(0, Math.Min(nx, nW - 1));
                        ny = Math.Max(0, Math.Min(ny, nH - 1));
                        nw = Math.Min(nw, nW - nx);
                        nh = Math.Min(nh, nH - ny);
                        if (nw < 2 || nh < 2) continue;

                        outBoxes.Add(new YoloBox(OutDirName + "/" + outName, nx, ny, nw, nh, b.Label));
                        wrote = true;
                    }
                    if (wrote && !Cv2.ImWrite(outPath, dst))
                        log?.Invoke("写图失败（已跳过）：" + outPath);
                }
            }

            log?.Invoke($"增强完成：{kindList.Count} 种方式 × 原图，新增 {outBoxes.Count} 个标注框，图片在 {OutDirName}/");
            return outBoxes;
        }

        /// <summary>对图像施加增强（几何变换或亮度抖动），返回新 Mat（调用方负责释放）。</summary>
        private static Mat ApplyToImage(Mat src, AugmentKind kind, int brightnessDelta)
        {
            switch (kind)
            {
                case AugmentKind.Rot90:
                    {
                        var m = new Mat();
                        Cv2.Rotate(src, m, RotateFlags.Rotate90Clockwise);
                        return m;
                    }
                case AugmentKind.Rot180:
                    {
                        var m = new Mat();
                        Cv2.Rotate(src, m, RotateFlags.Rotate180);
                        return m;
                    }
                case AugmentKind.Rot270:
                    {
                        var m = new Mat();
                        Cv2.Rotate(src, m, RotateFlags.Rotate90Counterclockwise);
                        return m;
                    }
                case AugmentKind.FlipH:
                    {
                        var m = new Mat();
                        Cv2.Flip(src, m, FlipMode.Y);   // Y 轴翻转 = 左右镜像
                        return m;
                    }
                case AugmentKind.FlipV:
                    {
                        var m = new Mat();
                        Cv2.Flip(src, m, FlipMode.X);   // X 轴翻转 = 上下镜像
                        return m;
                    }
                case AugmentKind.Brightness:
                    {
                        // 亮度/对比度抖动：ConvertTo(alpha=1, beta=增量) 即整体加常量，几何完全不变
                        var m = new Mat();
                        src.ConvertTo(m, -1, 1.0, brightnessDelta);
                        return m;
                    }
                default:
                    return src.Clone();
            }
        }

        /// <summary>
        /// 标注框随图像变换（纯函数，无副作用，可单测）。
        /// 约定：变换后图像尺寸——Rot90/Rot270 为 (H, W)，其余为 (W, H)。
        /// 返回值保证落在变换后的图像范围内（前提是输入框在原图范围内）。
        /// </summary>
        public static (int X, int Y, int W, int H) TransformBox(
            int x, int y, int w, int h, int imgW, int imgH, AugmentKind kind)
        {
            switch (kind)
            {
                case AugmentKind.Rot90:
                    // 顺时针 90°：原左上 (x,y) → 新 (imgH - y - h, x)，宽高互换
                    return (imgH - y - h, x, h, w);
                case AugmentKind.Rot180:
                    // 180°：沿两轴各翻一次，宽高不变
                    return (imgW - x - w, imgH - y - h, w, h);
                case AugmentKind.Rot270:
                    // 逆时针 90°：原左上 (x,y) → 新 (y, imgW - x - w)，宽高互换
                    return (y, imgW - x - w, h, w);
                case AugmentKind.FlipH:
                    // 左右镜像：x 镜像，y 不变
                    return (imgW - x - w, y, w, h);
                case AugmentKind.FlipV:
                    // 上下镜像：y 镜像，x 不变
                    return (x, imgH - y - h, w, h);
                case AugmentKind.Brightness:
                default:
                    // 只改像素，几何不变
                    return (x, y, w, h);
            }
        }

        /// <summary>
        /// 把 YOLO 标注（归一化）写成 YOLO 文件，方便增强结果再被外部标注工具打开检查。
        /// 每图一个同名 .txt，行格式：class_id cx cy w h（0~1 归一化）。
        /// </summary>
        public static void WriteYolo(string folder, List<YoloBox> boxes, List<string> classNames, Action<string> log = null)
        {
            if (boxes == null || boxes.Count == 0) return;
            var byImage = boxes.GroupBy(b => b.Image);
            foreach (var g in byImage)
            {
                string path = Path.Combine(folder, g.Key);
                int W = 0, H = 0;
                if (File.Exists(path))
                {
                    using var mat = Cv2.ImRead(path, ImreadModes.Color);
                    if (mat != null && !mat.Empty()) { W = mat.Cols; H = mat.Rows; }
                }
                if (W <= 0 || H <= 0) continue;   // 拿不到尺寸就无法归一化，跳过而不是写错数据

                var lines = new List<string>();
                foreach (var b in g)
                {
                    int id = classNames.IndexOf(b.Label);
                    if (id < 0) { classNames.Add(b.Label); id = classNames.Count - 1; }
                    float cx = (b.X + b.W / 2.0f) / W;
                    float cy = (b.Y + b.H / 2.0f) / H;
                    float bw = b.W / (float)W;
                    float bh = b.H / (float)H;
                    lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0} {1:F6} {2:F6} {3:F6} {4:F6}", id, cx, cy, bw, bh));
                }
                File.WriteAllLines(Path.Combine(folder, Path.GetFileNameWithoutExtension(g.Key) + ".txt"), lines);
            }
            log?.Invoke($"已写出 {byImage.Count()} 个 YOLO 标注文件（类别顺序：{string.Join(" / ", classNames)}）");
        }
    }
}
