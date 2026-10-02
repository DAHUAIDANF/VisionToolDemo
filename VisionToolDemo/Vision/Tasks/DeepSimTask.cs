using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度学习相似度算子：加载用户自备的「特征提取」ONNX 模型，
    /// 把「参考图」与「当前输入图」各自提取为特征向量，用余弦相似度判定两图是否匹配。
    ///
    /// 典型用途：
    ///   · 图像查重 / 相同外观比对（同一工位、同一物料、同一界面）；
    ///   · 缺陷前后对比（好件设参考，来件相似度低即异常）；
    ///   · 小样本分类的另一种用法（分类模型输出的 logits 也可当特征近似使用）。
    ///
    /// 特征模型约定：
    ///   · 单图像输入 [1,3,H,W]（RGB 0~1，H=W 正方形，边长自动读取或手动指定）；
    ///   · 输出 [1,D] 或 [D] 的特征向量（D ≥ 2），内部做 L2 归一化后比余弦。
    ///   · 训练页导出的分类模型（输出各类别 logits）可直接当特征模型用——同类图
    ///     logits 相近、异类图差异大，对小样本查重场景够用；追求更精细相似度可换
    ///     专用特征模型（如 MobileNet 倒数第二层、ArcFace 等，注意可商用许可）。
    ///
    /// 参考图注入（视觉页专用行）：
    ///   · 「设当前主图为参考」：把页面当前主图提取特征存为参考；
    ///   · 「清除参考」：清空参考特征。
    /// 状态存在任务实例上，链步复用；模型路径/时间戳变化自动重载。
    ///
    /// 安全契约：模型未加载 / 参考未设置 / 推理异常 → 一律防御性返回原图拷贝 + 摘要说明。
    /// </summary>
    public class DeepSimTask : IVisionTask, IResultReporter
    {
        public string TaskName => "深度学习相似度";

        // ============================== 状态（任务实例上，链步复用） ==============================

        /// <summary>特征模型 .onnx 路径（视觉页专用行设置；空=未加载）</summary>
        public string FeatureModelPath = "";

        /// <summary>参考特征向量（L2 归一化后；null=未设置参考图）</summary>
        private float[] _refFeature;

        /// <summary>参考图说明（文件名或"主图"，摘要展示用）</summary>
        private string _refLabel = "";

        /// <summary>特征模型缓存会话 + 热重载时间戳</summary>
        private InferenceSession _session;
        private DateTime _modelStamp = DateTime.MinValue;
        private string _modelPathLoaded = "";

        /// <summary>模型要求的输入边长（自动读取一次缓存；0=未知）</summary>
        private int _modelSide;

        /// <summary>最近失败原因（摘要/排障用）</summary>
        private string _loadError = "";

        /// <summary>最近一次 Execute 的摘要（提示标签 / 运行完成弹窗共用）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>IResultReporter 兼容占位</summary>
        public string LastZxingError => "";

        /// <summary>最近一次相似度（0~1，余弦；-1=未执行成功）</summary>
        public float LastSimilarity = -1f;

        /// <summary>参考图是否已设置（专用行显示状态用）</summary>
        public bool HasReference => _refFeature != null;

        // ============================== 参数 ==============================

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                // 相似度阈值（%）：低于此判定"不匹配"，图上标红；高于/等于标绿
                ParamName = "相似度阈值",
                Min = 1,
                Max = 99,
                DefaultValue = 70,
                DisplayFormat = "阈值:{0}%",
                ForceOdd = false,
                Tip = "与参考图相似度低于此值的输入图判定「不匹配」（图上标红）。\\n" +
                      "误报多就调高，漏报多就调低。"
            },
            new TaskParamDesc
            {
                // 模型输入边长：0=自动读取（推荐，与推理算子一致）
                ParamName = "输入边长",
                Min = 0,
                Max = 1280,
                DefaultValue = 0,
                DisplayFormat = "边:{0}px",
                ForceOdd = false,
                Tip = "特征模型输入边长。0 = 自动读取模型输入尺寸；\\n" +
                      "自动读取失败时手动填（与模型一致）。"
            },
        ];

        // ============================== 公开操作（专用行调用） ==============================

        /// <summary>释放缓存的推理会话（换模型时释放内存）</summary>
        public void UnloadModel()
        {
            _session?.Dispose();
            _session = null;
            _modelPathLoaded = "";
            _modelStamp = DateTime.MinValue;
            _modelSide = 0;
        }

        /// <summary>把一张图提取特征设为参考（视觉页「设当前主图为参考」调用）。返回是否成功。</summary>
        public bool SetReferenceFrom(Mat image, string label)
        {
            if (image == null || image.Empty()) { _loadError = "参考图为空"; return false; }
            try
            {
                // 先确保特征模型会话与输入边长就绪（未执行过 Execute 时 _session 可能还没建）
                EnsureSession(0);
                if (_session == null) { _loadError = "模型未就绪（" + _loadError + "）"; return false; }
                float[] feat = ExtractFeature(image, out string err);
                if (feat == null) { _loadError = "参考图特征提取失败：" + err; return false; }
                _refFeature = feat;
                _refLabel = string.IsNullOrWhiteSpace(label) ? "参考图" : label;
                _loadError = "";
                return true;
            }
            catch (Exception ex)
            {
                _loadError = "参考图特征提取异常：" + ex.Message;
                return false;
            }
        }

        /// <summary>清除参考特征（专用行「清除参考」调用）</summary>
        public void ClearReference()
        {
            _refFeature = null;
            _refLabel = "";
        }

        // ============================== 执行 ==============================

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSimilarity = -1f;
            LastSummary = "";
            _loadError = "";
            if (srcMat?.Empty() != false) return new Mat();

            float threshold = (paramValues?.Length > 0 ? Math.Clamp(paramValues[0], 1, 99) : 70) / 100f;
            int inputSide = paramValues?.Length > 1 ? Math.Clamp(paramValues[1], 0, 1280) : 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 模型未加载：不执行，明确提示 ——
            if (string.IsNullOrWhiteSpace(FeatureModelPath) || !File.Exists(FeatureModelPath))
            {
                LastSummary = string.IsNullOrWhiteSpace(FeatureModelPath)
                    ? "深度学习相似度: 未加载特征模型（先在参数区选择 .onnx 模型）"
                    : "深度学习相似度: 模型文件不存在（" + FeatureModelPath + "）";
                return dst;
            }

            try
            {
                EnsureSession(inputSide);   // 惰性加载 + 自动读边长 + 热重载
                if (_session == null) { LastSummary = "深度学习相似度: 失败（" + _loadError + "）"; return dst; }

                // —— 参考未设置：不执行，明确提示 ——
                if (_refFeature == null)
                {
                    LastSummary = "深度学习相似度: 还没设参考图（参数区点「设当前主图为参考」）";
                    return dst;
                }

                // 提取输入图特征 → 余弦相似度
                float[] feat = ExtractFeature(srcMat, out string err);
                if (feat == null)
                {
                    LastSummary = "深度学习相似度: 特征提取失败（" + err + "）";
                    return dst;
                }
                float sim = Cosine(_refFeature, feat);
                LastSimilarity = sim;
                bool ok = sim >= threshold;

                // 图上标注：左下角 相似度 + 匹配/不匹配（绿/红）
                // GDI+ 绘制整体防御：标注只是辅助，任何绘制异常都降级为 OpenCV 画框/ASCII 文本，
                // 绝不让算子因为标注失败（例如某些环境的 "Parameter is not valid"）
                try
                {
                    using var bmp = MatToBitmap(dst);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        float fs = Math.Clamp(dst.Cols / 90f, 11f, 18f);
                        using var font = MakeSafeFont(fs);
                        string line = string.Format("相似度 {0:P1} — {1}", sim, ok ? "匹配" : "不匹配");
                        var brush = ok ? System.Drawing.Brushes.LimeGreen : System.Drawing.Brushes.Red;
                        // 黑描边 + 彩色正文（任何底图都清晰）
                        g.DrawString(line, font, System.Drawing.Brushes.Black, 5, dst.Rows - fs - 5 + 1);
                        g.DrawString(line, font, brush, 4, dst.Rows - fs - 5);
                    }
                    CopyBitmapBack(bmp, dst);
                }
                catch (Exception drawEx)
                {
                    // 降级：OpenCV 原生画框 + ASCII 相似度文本（中文画不了，但至少给出结论与数字）
                    var c = ok ? new Scalar(0, 220, 0) : new Scalar(0, 0, 255);
                    Cv2.Rectangle(dst, new OpenCvSharp.Point(4, 4),
                        new OpenCvSharp.Point(dst.Cols - 4, dst.Rows - 4), c, 3);
                    Cv2.PutText(dst, string.Format("SIM {0:P0} {1}", sim, ok ? "OK" : "NG"),
                        new OpenCvSharp.Point(8, Math.Max(18, dst.Rows - 12)),
                        HersheyFonts.HersheySimplex, 0.5, c, 2);
                    _loadError = "标注降级（GDI+ 不可用）：" + drawEx.Message;
                }

                LastSummary = string.Format("深度学习相似度: {0:P1}（参考：{1}）{2}",
                    sim, _refLabel, ok ? "匹配" : "不匹配");
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                LastSummary = "深度学习相似度: 失败（" + ex.Message + "）";
            }
            return dst;
        }

        // ============================== 特征提取 ==============================

        /// <summary>确保会话可用：按模型路径/时间戳重载，并解析模型输入边长（缓存到 _modelSide）</summary>
        private void EnsureSession(int inputSide)
        {
            var stamp = File.GetLastWriteTimeUtc(FeatureModelPath);
            if (_session == null || !string.Equals(_modelPathLoaded, FeatureModelPath, StringComparison.OrdinalIgnoreCase)
                || stamp != _modelStamp)
            {
                _session?.Dispose();
                _session = new InferenceSession(FeatureModelPath);
                _modelStamp = stamp;
                _modelPathLoaded = FeatureModelPath;
                _modelSide = 0;
                // 解析输入尺寸
                var meta = _session.InputMetadata.FirstOrDefault();
                var dm = meta.Value.Dimensions;
                if (dm.Length == 4 && dm[2] > 0 && dm[2] == dm[3]) _modelSide = (int)dm[2];
            }
            if (_modelSide <= 0)
            {
                // 模型里没读到正方形边长：用参数指定值；参数也没有就报错
                if (inputSide > 0) _modelSide = inputSide;
                else { _loadError = "无法读取特征模型输入边长，请手动设置「输入边长」"; _session?.Dispose(); _session = null; }
            }
        }

        /// <summary>提取单图特征：letterbox → RGB NCHW 0~1 → 推理 → 首输出展平 L2 归一化。
        /// 返回 null 时 err 给出原因。</summary>
        private float[] ExtractFeature(Mat src, out string err)
        {
            err = "";
            try
            {
                if (_session == null || _modelSide <= 0) { err = "模型未就绪"; return null; }
                int side = _modelSide;
                float[] input = BuildInput(src, side);
                if (input == null) { err = "输入预处理失败"; return null; }

                var inputTensor = new DenseTensor<float>(input, new[] { 1, 3, side, side });
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_session.InputNames.First(), inputTensor) };
                using var results = _session.Run(inputs);
                var outTensor = results.First().AsTensor<float>();
                float[] feat = outTensor.ToArray();
                if (feat == null || feat.Length < 2) { err = "输出特征维数 < 2"; return null; }
                // L2 归一化（零向量保底全 1/sqrt(D)，避免除零）
                double norm = 0;
                foreach (float v in feat) norm += v * (double)v;
                norm = Math.Sqrt(norm);
                if (norm < 1e-9) { for (int i = 0; i < feat.Length; i++) feat[i] = 1f / (float)Math.Sqrt(feat.Length); }
                else for (int i = 0; i < feat.Length; i++) feat[i] = (float)(feat[i] / norm);
                return feat;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        /// <summary>letterbox 预处理：等比缩放 + 灰边填充到 side×side，输出 RGB float NCHW（0~1）</summary>
        private static float[] BuildInput(Mat src, int side)
        {
            if (src == null || src.Empty() || side <= 0) return null;
            float scale = Math.Min((float)side / src.Cols, (float)side / src.Rows);
            int nw = Math.Max(1, (int)Math.Round(src.Cols * scale));
            int nh = Math.Max(1, (int)Math.Round(src.Rows * scale));
            using Mat resized = new Mat();
            Cv2.Resize(src, resized, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Linear);
            using Mat canvas = new Mat(side, side, MatType.CV_8UC3, new Scalar(114, 114, 114));
            resized.CopyTo(canvas[new OpenCvSharp.Rect(0, 0, nw, nh)]);
            float[] data = new float[side * side * 3];
            unsafe
            {
                byte* p = (byte*)canvas.Data;
                int step = (int)canvas.Step();
                int idx = 0;
                for (int y = 0; y < side; y++)
                {
                    byte* row = p + y * step;
                    for (int x = 0; x < side; x++)
                    {
                        data[idx] = row[x * 3 + 2] / 255f;         // R
                        data[idx + side * side] = row[x * 3 + 1] / 255f;          // G
                        data[idx + side * side * 2] = row[x * 3 + 0] / 255f;      // B
                        idx++;
                    }
                }
            }
            return data;
        }

        /// <summary>余弦相似度（两个 L2 归一化向量点积，-1~1 钳到 0~1）</summary>
        private static float Cosine(float[] a, float[] b)
        {
            double dot = 0;
            for (int i = 0; i < a.Length; i++) dot += a[i] * (double)b[i];
            return (float)Math.Clamp(dot, 0, 1);
        }

        /// <summary>
        /// 安全创建字体：优先微软雅黑（中文），创建失败（系统未装该字体等）回退 Arial，
        /// 再失败回退通用默认字体——避免 Font 构造抛 "Parameter is not valid" 让算子失败。
        /// </summary>
        private static System.Drawing.Font MakeSafeFont(float size)
        {
            foreach (var family in new[] { "Microsoft YaHei", "Arial", "Microsoft Sans Serif" })
            {
                try { return new System.Drawing.Font(family, size, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel); }
                catch { /* 换下一个字体 */ }
            }
            return new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, size,
                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        }

        // ============================== GDI+ 标注 ==============================

        /// <summary>把 Mat 拷贝成 GDI+ Bitmap 供绘制（与 DeepLearnTask 同一套防乱码方案）</summary>
        private static System.Drawing.Bitmap MatToBitmap(Mat dst)
        {
            int w = dst.Cols, h = dst.Rows;
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            // 用 32bppArgb：24bpp RGB 位图在部分显示环境（16 位色/远程桌面）下
            // Graphics.FromImage / DrawString 会抛 "Parameter is not valid"，32bpp 兼容性最好
            const System.Drawing.Imaging.PixelFormat fmt = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            var bmp = new System.Drawing.Bitmap(w, h, fmt);
            byte[] row = new byte[w * 4];
            int step = (int)dst.Step();
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, fmt);
            try
            {
                byte[] srcRow = new byte[w * 3];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(dst.Data + (y * step), srcRow, 0, srcRow.Length);
                    // BGR → BGRA（alpha 固定 255）
                    for (int x = 0; x < w; x++)
                    {
                        row[x * 4 + 0] = srcRow[x * 3 + 0];
                        row[x * 4 + 1] = srcRow[x * 3 + 1];
                        row[x * 4 + 2] = srcRow[x * 3 + 2];
                        row[x * 4 + 3] = 255;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + (y * bd.Stride), row.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        /// <summary>把 GDI+ Bitmap 画回 Mat（结果图交付）</summary>
        private static void CopyBitmapBack(System.Drawing.Bitmap bmp, Mat dst)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int step = (int)dst.Step();
                byte[] row = new byte[bmp.Width * 4];
                byte[] bgr = new byte[bmp.Width * 3];
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + (y * bd.Stride), row, 0, row.Length);
                    // BGRA → BGR
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        bgr[x * 3 + 0] = row[x * 4 + 0];
                        bgr[x * 3 + 1] = row[x * 4 + 1];
                        bgr[x * 3 + 2] = row[x * 4 + 2];
                    }
                    System.Runtime.InteropServices.Marshal.Copy(bgr, 0, dst.Data + (y * step), bgr.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
        }
    }
}
