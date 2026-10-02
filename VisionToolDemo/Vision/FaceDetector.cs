using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 人脸检测共享助手：供「深度学习推理」算子的人脸增强、「目标跟踪」算子的人脸追踪、训练页标注的自动检测人脸复用。
    ///
    /// 双引擎设计（开源可商用）：
    ///   1) YuNet（OpenCV Zoo 官方 ONNX 人脸检测，Apache-2.0）—— 优先。
    ///      对自然场景/多人/侧脸/低光（WIDER FACE 基准）检出率远高于 Haar；
    ///      模型 face_detection_yunet_2023mar.onnx 已随工程拷贝到程序输出目录（约 232KB）。
    ///   2) Haar 级联（OpenCV 经典，BSD）—— 回退。YuNet 模型缺失或未检出人脸时使用。
    ///
    /// 查找顺序：显式指定路径 → 程序目录 → 当前目录；都找不到时 Available=false，
    /// 调用方应降级（普通滑窗/手动框选）并给出提示，绝不崩溃。
    /// </summary>
    public static class FaceDetector
    {
        // ---------- Haar 级联（回退引擎） ----------
        private static CascadeClassifier _cascade;
        private static string _loadedHaarPath = "";

        // ---------- YuNet（优先引擎） ----------
        private const string YunetModelName = "face_detection_yunet_2023mar.onnx";
        // 输入尺寸按图片动态设定（长边 clamp 到 [320, 1024]），保证小脸/密集人群不因缩小而漏检；
        // FaceDetectorYN 无 setInputSize，需要按 (模型,尺寸) 缓存重建会话。
        private const int YunetInputMin = 320, YunetInputMax = 1024;
        // 分数阈值 0.3：官方 demo 用 0.9（严），但夜间/远景/密集小脸（WIDER 最难档）需要低阈值才不漏检；
        // 误检框会在后续算子链（滑窗分类/ROI）被置信度过滤，这里以召回优先。
        private static readonly float YunetScoreThr = 0.3f, YunetNmsThr = 0.3f;
        private const int YunetTopK = 5000;
        private static FaceDetectorYN _yunet;
        private static string _loadedYunetPath = "";
        private static Size _loadedYunetSize;

        /// <summary>是否任一引擎可用（懒加载）</summary>
        public static bool Available
        {
            get
            {
                if (ResolveModelPath(null) != null) return true;
                if (ResolveYunetModelPath(null) != null) return true;
                return false;
            }
        }

        /// <summary>
        /// 解析 Haar 级联模型文件路径：explicitPath 非空且存在且扩展名为 .xml 时用之；
        /// 否则在程序目录/当前目录查找 haarcascade_frontalface_default.xml；找不到返回 null。
        /// </summary>
        public static string ResolveModelPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)
                && string.Equals(Path.GetExtension(explicitPath), ".xml", StringComparison.OrdinalIgnoreCase))
                return explicitPath;
            string name = "haarcascade_frontalface_default.xml";
            var cands = new[]
            {
                Path.Combine(AppContext.BaseDirectory, name),
                Path.Combine(AppContext.BaseDirectory, "Vision", name),   // 兼容旧构建（未展平到 Vision 子目录）
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name),
                Path.Combine(Environment.CurrentDirectory, name),
            };
            foreach (var c in cands)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>
        /// 解析 YuNet ONNX 模型路径：explicitPath 非空且存在且扩展名为 .onnx 时用之；
        /// 否则在程序目录/当前目录查找 face_detection_yunet_2023mar.onnx；找不到返回 null。
        /// 注意：显式路径必须是 YuNet 的 .onnx，传 Haar 的 .xml 会被忽略（避免引擎串用）。
        /// </summary>
        public static string ResolveYunetModelPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)
                && string.Equals(Path.GetExtension(explicitPath), ".onnx", StringComparison.OrdinalIgnoreCase))
                return explicitPath;
            var cands = new[]
            {
                Path.Combine(AppContext.BaseDirectory, YunetModelName),
                Path.Combine(AppContext.BaseDirectory, "Vision", YunetModelName),   // 兼容旧构建
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, YunetModelName),
                Path.Combine(Environment.CurrentDirectory, YunetModelName),
            };
            foreach (var c in cands)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>懒加载 Haar 级联（线程安全由调用方保证；训练页/算子链单线程调用）</summary>
        private static void TryLoadHaar(string explicitPath)
        {
            string path = ResolveModelPath(explicitPath);
            if (path == null) return;
            if (string.Equals(_loadedHaarPath, path, StringComparison.OrdinalIgnoreCase) && _cascade != null) return;
            try
            {
                var c = new CascadeClassifier(path);
                _cascade?.Dispose();
                _cascade = c;
                _loadedHaarPath = path;
            }
            catch
            {
                _cascade = null;
            }
        }

        /// <summary>懒加载 YuNet（OpenCvSharp FaceDetectorYN；模型路径或输入尺寸变化时重建）</summary>
        private static FaceDetectorYN TryLoadYunet(string explicitPath, Size inputSize)
        {
            string path = ResolveYunetModelPath(explicitPath);
            if (path == null) return null;
            if (_yunet != null && string.Equals(_loadedYunetPath, path, StringComparison.OrdinalIgnoreCase)
                && _loadedYunetSize == inputSize)
                return _yunet;
            try
            {
                _yunet?.Dispose();
                _yunet = FaceDetectorYN.Create(path, "", inputSize, YunetScoreThr, YunetNmsThr, YunetTopK);
                _loadedYunetPath = path;
                _loadedYunetSize = inputSize;
            }
            catch
            {
                _yunet = null;
            }
            return _yunet;
        }

        /// <summary>YuNet 检测：按图长边动态定输入尺寸 → 推理 → 坐标映射回原图 → 尺寸过滤/裁剪到图内</summary>
        private static OpenCvSharp.Rect[] DetectYunet(Mat bgr, string explicitPath)
        {
            if (bgr == null || bgr.Empty()) return Array.Empty<OpenCvSharp.Rect>();
            // 输入尺寸 = 长边 clamp[320,1024] 的等比缩略（小脸不因缩小漏检，大图不因超大输入过慢）
            int maxDim = Math.Max(bgr.Cols, bgr.Rows);
            int side = Math.Clamp(maxDim, YunetInputMin, YunetInputMax);
            var input = new Size(side, (int)Math.Round(side * (bgr.Rows / (double)bgr.Cols)));
            if (input.Height < 1) input = new Size(side, side);

            var y = TryLoadYunet(explicitPath, input);
            if (y == null) return Array.Empty<OpenCvSharp.Rect>();
            try
            {
                using var small = new Mat();
                Cv2.Resize(bgr, small, input, 0, 0, InterpolationFlags.Area);
                using var faces = new Mat();
                int n = y.Detect(small, faces);
                if (n <= 0 || faces.Empty() || faces.Rows == 0) return Array.Empty<OpenCvSharp.Rect>();

                double sx = bgr.Cols / (double)input.Width;
                double sy = bgr.Rows / (double)input.Height;
                int W = bgr.Cols, H = bgr.Rows;
                var list = new List<OpenCvSharp.Rect>();
                for (int r = 0; r < faces.Rows && list.Count < 64; r++)
                {
                    float fx = faces.At<float>(r, 0), fy = faces.At<float>(r, 1);
                    float w = faces.At<float>(r, 2), h = faces.At<float>(r, 3);
                    if (w <= 0 || h <= 0) continue;
                    var rect = new OpenCvSharp.Rect(
                        Math.Clamp((int)Math.Round(fx * sx), 0, W - 1),
                        Math.Clamp((int)Math.Round(fy * sy), 0, H - 1),
                        Math.Min((int)Math.Round(w * sx), W),
                        Math.Min((int)Math.Round(h * sy), H));
                    rect.Width = Math.Min(rect.Width, W - rect.X);
                    rect.Height = Math.Min(rect.Height, H - rect.Y);
                    if (rect.Width >= 16 && rect.Height >= 16)
                        list.Add(rect);
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<OpenCvSharp.Rect>();
            }
        }

        /// <summary>串行化所有引擎调用：CascadeClassifier / FaceDetectorYN 均为非线程安全的 native 对象，
        /// 训练页/算子链/目标跟踪可能从多线程调用，防止并发检测导致 AccessViolation。</summary>
        private static readonly object _sync = new object();

        /// <summary>
        /// 在 BGR 图上检测人脸，返回原图像素坐标的人脸框（已裁剪到图内）。
        /// 双引擎：YuNet 优先；YuNet 模型缺失/未检出时回退 Haar。
        /// explicitPath 为 null 时自动查找程序目录模型；模型缺失/无人脸返回空数组。
        /// </summary>
        public static OpenCvSharp.Rect[] Detect(Mat bgr, string explicitPath = null, double scaleFactor = 1.1, int minNeighbors = 5)
        {
            if (bgr == null || bgr.Empty()) return Array.Empty<OpenCvSharp.Rect>();
            lock (_sync)
            {
                return DetectCore(bgr, explicitPath, scaleFactor, minNeighbors);
            }
        }

        /// <summary>Detect 的锁内实现（引擎对象非线程安全，必须持锁调用）</summary>
        private static OpenCvSharp.Rect[] DetectCore(Mat bgr, string explicitPath, double scaleFactor, int minNeighbors)
        {

            // 1) YuNet 优先（自然场景/多人检出率高）
            var yunetFaces = DetectYunet(bgr, explicitPath);
            if (yunetFaces.Length > 0) return yunetFaces;

            // 2) Haar 回退（传统级联，正脸/标准图场景）。
            //    大图直接 DetectMultiScale 会因窗口级联过深触发 native AccessViolation，
            //    统一先缩放到长边 ≤1024 再检测，坐标映射回原图（顺带提速）。
            TryLoadHaar(explicitPath);
            if (_cascade == null) return Array.Empty<OpenCvSharp.Rect>();
            try
            {
                Mat detectSrc = bgr;
                using Mat scaled = new Mat();
                double scale = 1.0;
                if (Math.Max(bgr.Cols, bgr.Rows) > 1024)
                {
                    scale = 1024.0 / Math.Max(bgr.Cols, bgr.Rows);
                    Cv2.Resize(bgr, scaled,
                        new OpenCvSharp.Size((int)Math.Round(bgr.Cols * scale), (int)Math.Round(bgr.Rows * scale)),
                        0, 0, InterpolationFlags.Area);
                    detectSrc = scaled;
                }
                using Mat gray = new Mat();
                Cv2.CvtColor(detectSrc, gray, ColorConversionCodes.BGR2GRAY);
                // 直方图均衡提升暗光检出；scaleFactor 越小越慢但越全（默认 1.1）
                Cv2.EqualizeHist(gray, gray);
                OpenCvSharp.Rect[] faces = _cascade.DetectMultiScale(gray, scaleFactor, minNeighbors,
                    HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(40, 40));
                if (faces == null || faces.Length == 0) return Array.Empty<OpenCvSharp.Rect>();
                // 坐标映射回原图 + 裁剪到图内 + 过滤过小框
                int W = bgr.Cols, H = bgr.Rows;
                return faces
                    .Where(f => f.Width >= 16 && f.Height >= 16)
                    .Select(f => new OpenCvSharp.Rect(
                        Math.Clamp((int)Math.Round(f.X / scale), 0, W - 1),
                        Math.Clamp((int)Math.Round(f.Y / scale), 0, H - 1),
                        Math.Min((int)Math.Round(f.Width / scale), W),
                        Math.Min((int)Math.Round(f.Height / scale), H)))
                    .Select(f => new OpenCvSharp.Rect(f.X, f.Y,
                        Math.Min(f.Width, W - f.X), Math.Min(f.Height, H - f.Y)))
                    .Where(f => f.Width >= 16 && f.Height >= 16)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<OpenCvSharp.Rect>();
            }
        }

        /// <summary>人脸框按面积从大到小排序（取最大脸=最近/最清晰目标）</summary>
        public static OpenCvSharp.Rect Biggest(OpenCvSharp.Rect[] faces)
        {
            if (faces == null || faces.Length == 0) return default;
            OpenCvSharp.Rect best = faces[0];
            foreach (var f in faces)
                if (f.Width * (long)f.Height > best.Width * (long)best.Height) best = f;
            return best;
        }
    }
}
