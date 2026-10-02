using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Train
{
    /// <summary>
    /// 纯 C# 小型卷积网络分类训练器（零外部依赖，CPU 可跑，小样本友好）。
    ///
    /// 为什么自研而不是接 PyTorch/ML.NET：
    ///   · 软件定位是"离线检测工作台"，训练功能要开箱即用——用户机器不必装 Python/PyTorch；
    ///   · 小样本场景（每类几十~几百张、固定机位/光照）用一个小 CNN 就能收敛，
    ///     没必要背上几 GB 的深度学习框架依赖；
    ///   · 训练出的权重可直接导出 ONNX（TrainOnnxExporter），被「深度学习推理」算子加载，
    ///     全链路闭环且全部开源免费可商用。
    ///
    /// 网络结构（NCHW，彩色 3 通道输入）：
    ///   Conv3x3(3→8, pad1) + ReLU + MaxPool2
    ///   → Conv3x3(8→16, pad1) + ReLU + MaxPool2
    ///   → Flatten → 全连接(→类别数) → Logits → Softmax + 交叉熵
    /// 尺寸 S=32：卷积后 32→16→8，展平 16*8*8=1024；S=64：展平 16*16*16=4096。
    /// 优化器：Adam（m/v 每参数，默认 lr=0.001）。确定性：种子固定，冒烟可复现。
    ///
    /// 训练目录约定：一级子目录 = 类别（目录名即类别名，按名称排序），
    /// 图片支持 png/jpg/jpeg/bmp/tif/tiff，统一缩放/归一化后进网络。
    /// 防御：目录不存在/没有可读图片/类别少于 2 → 抛明确异常，不让脏数据静默训练。
    /// </summary>
    public class DeepTrainer
    {
        /// <summary>类别名（与训练集子目录一一对应，按名称排序）</summary>
        public List<string> Labels { get; private set; } = [];

        /// <summary>训练样本（每项 = 一张图展平后的 RGB float，长度 InputSide*InputSide*3）</summary>
        public List<float[]> Samples { get; private set; } = [];

        /// <summary>每个样本的类别下标（0..Labels.Count-1）</summary>
        public List<int> SampleClasses { get; private set; } = [];

        /// <summary>输入正方形边长（32 或 64；默认 32 参数量小，小样本更稳）</summary>
        public int InputSide = 32;

        /// <summary>训练轮数（小样本默认 100；过拟合早停看日志判断）</summary>
        public int Epochs = 100;

        /// <summary>学习率（Adam 默认 0.003；小样本 CNN 收敛快，loss 震荡就调低）</summary>
        public float Lr = 0.003f;

        /// <summary>批大小（小样本默认 8；每类不足 8 张时自动退化为全批）</summary>
        public int Batch = 8;

        /// <summary>随机种子（固定以便复现/冒烟）</summary>
        public long Seed = 20260926;

        /// <summary>训练日志回调（页面显示进度用；不回调则静默）</summary>
        public Action<string> Log;

        /// <summary>每轮回调（轮号从 1 开始、该轮平均 loss、该轮训练集准确率）——训练监控曲线/进度条用。</summary>
        public Action<int, float, float> OnEpoch;

        /// <summary>请求停止训练（页面"停止"按钮设置；每轮检查一次）</summary>
        public volatile bool StopRequested;

        /// <summary>是否启用数据增强：每轮对样本做随机小平移/亮度抖动。
        /// 实测（NEU 钢材缺陷 90 张/6 类）：增强在此类表面纹理数据上是负收益（acc 91%→71%），
        /// 故默认关；对轻微拍摄抖动/光照变化的场景可自行打开对比。</summary>
        public bool Augment = false;

        /// <summary>是否启用学习率余弦衰减（默认开）：lr 从初始值按余弦降到 0，
        /// 避免固定 lr 后期震荡不收敛（loss 高但 acc 卡住）。</summary>
        public bool LrDecay = true;

        /// <summary>
        /// 从目录加载训练集：一级子目录=类别，每个子目录内的图片=该类样本。
        /// 返回各类别样本数（供页面显示统计）。
        /// </summary>
        public Dictionary<string, int> LoadFolder(string folder)
        {
            Labels = [];
            Samples = [];
            SampleClasses = [];
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("训练目录不存在：" + folder);

            // 一级子目录即类别（排除隐藏目录）
            var dirs = Directory.GetDirectories(folder)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dirs.Count < 2)
                throw new InvalidOperationException("至少需要 2 个类别子目录（例如 训练目录/良品/、训练目录/不良/）");

            var stats = new Dictionary<string, int>();
            foreach (string dir in dirs)
            {
                string cls = Path.GetFileName(dir);
                var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsImageExt(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count == 0)
                    throw new InvalidOperationException($"类别「{cls}」里没有可读图片（支持 png/jpg/jpeg/bmp/tif/tiff）");

                foreach (string f in files)
                {
                    float[] rgb = LoadImageAsRgb(f, InputSide);
                    if (rgb == null) continue;
                    Samples.Add(rgb);
                    SampleClasses.Add(Labels.Count);
                }
                stats[cls] = Samples.Count - stats.Values.Sum();
                Labels.Add(cls);
            }

            // 类别数过多或样本过少直接拒绝，避免训出一个不可靠的模型
            if (Labels.Count < 2)
                throw new InvalidOperationException("至少需要 2 个类别");
            if (Labels.Count > 16)
                throw new InvalidOperationException("类别过多（超过 16 类），小样本训练不可靠");
            if (Samples.Count < Labels.Count * 2)
                throw new InvalidOperationException("样本太少：每个类别至少 2 张图");

            Log?.Invoke($"已加载 {Samples.Count} 张图 / {Labels.Count} 类：" +
                        string.Join(" / ", Labels.Select(l => $"{l}={stats[l]}张")));
            return stats;
        }

        /// <summary>
        /// 从标注文件加载训练集：训练目录里的 标注.json 每项 = { image, x, y, w, h, label }，
        /// 每个标注框区域 = 一个样本（裁框 → 缩放 InputSide），标签名 = 类别。
        /// 用途：人像姓名识别 / 产品 NG 类型识别（框选目标 + 标签）。
        /// 建议额外标注一些"背景"框作负样本，识别时最高分是背景的窗口会被自动忽略。
        /// </summary>
        public Dictionary<string, int> LoadAnnotated(string folder)
        {
            Labels = [];
            Samples = [];
            SampleClasses = [];
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("训练目录不存在：" + folder);
            string annPath = Path.Combine(folder, "标注.json");
            if (!File.Exists(annPath))
                throw new InvalidOperationException("目录里没有 标注.json（先在训练页「标注」区框选目标并保存标注）");

            Newtonsoft.Json.Linq.JArray items;
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(annPath));
                items = (Newtonsoft.Json.Linq.JArray)(root["items"] ?? throw new InvalidOperationException("标注.json 缺少 items 数组"));
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("标注.json 解析失败：" + ex.Message);
            }
            if (items.Count == 0)
                throw new InvalidOperationException("标注.json 里没有标注条目（先在训练页框选目标并添加标注）");

            // 标签名集合 = 类别（含"背景"则作为独立负样本类）
            var labelSet = items
                .Select(i => (string)i["label"])
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct()
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (labelSet.Count < 2)
                throw new InvalidOperationException("至少需要 2 个不同标签（例如 张三/李四，或 良品/划痕；建议加一个「背景」类）");
            if (labelSet.Count > 16)
                throw new InvalidOperationException("标签类别过多（超过 16 类），小样本训练不可靠");
            Labels = labelSet;   // 类别 = 标签名（含「背景」则作为独立负样本类）

            var stats = labelSet.ToDictionary(l => l, _ => 0);
            foreach (var it in items)
            {
                string img = (string)it["image"];
                string lab = (string)it["label"];
                if (string.IsNullOrWhiteSpace(img) || string.IsNullOrWhiteSpace(lab)) continue;
                int x = (int)(it["x"] ?? 0), y = (int)(it["y"] ?? 0);
                int w = (int)(it["w"] ?? 0), h = (int)(it["h"] ?? 0);
                string imgPath = Path.Combine(folder, img);
                if (!File.Exists(imgPath)) continue;
                using var mat = Cv2.ImRead(imgPath, ImreadModes.Color);
                if (mat == null || mat.Empty()) continue;
                var rect = new OpenCvSharp.Rect(x, y, w, h) & new OpenCvSharp.Rect(0, 0, mat.Cols, mat.Rows);
                if (rect.Width < 2 || rect.Height < 2) continue;
                using Mat roi = new Mat(mat, rect);           // 框区域子视图
                float[] rgb = MatToRgb(roi, InputSide);
                if (rgb == null) continue;
                Samples.Add(rgb);
                SampleClasses.Add(labelSet.IndexOf(lab));
                stats[lab]++;
            }
            if (Samples.Count < labelSet.Count * 2)
                throw new InvalidOperationException("样本太少：每个标签至少 2 个标注框（多标一些，建议含「背景」框）");

            Log?.Invoke($"已加载标注样本 {Samples.Count} 个 / {labelSet.Count} 类：" +
                        string.Join(" / ", labelSet.Select(l => $"{l}={stats[l]}个")));
            return stats;
        }

        /// <summary>保存模型检查点到 .vtmodel 文件（JSON：标签/输入边长/轮数 + 全部权重）。
        /// 训练页「保存模型」用；之后可「加载模型继续训练」或复制给其他设备。</summary>
        public static void SaveModel(TrainedModel m, string path)
        {
            if (m == null) throw new InvalidOperationException("模型为空，无法保存");
            var j = new Newtonsoft.Json.Linq.JObject
            {
                ["format"] = "vtmodel-v1",
                ["labels"] = Newtonsoft.Json.Linq.JArray.FromObject(m.Labels),
                ["side"] = m.InputSide,
                ["epochs"] = m.FinalEpoch,
                ["acc"] = m.TrainAcc,
                ["loss"] = m.FinalLoss,
                ["c1w"] = Newtonsoft.Json.Linq.JArray.FromObject(m.Conv1W),
                ["c1b"] = Newtonsoft.Json.Linq.JArray.FromObject(m.Conv1B),
                ["c2w"] = Newtonsoft.Json.Linq.JArray.FromObject(m.Conv2W),
                ["c2b"] = Newtonsoft.Json.Linq.JArray.FromObject(m.Conv2B),
                ["fcw"] = Newtonsoft.Json.Linq.JArray.FromObject(m.FcW),
                ["fcb"] = Newtonsoft.Json.Linq.JArray.FromObject(m.FcB),
            };
            File.WriteAllText(path, j.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>从 .vtmodel 文件加载模型检查点（与 SaveModel 配套）；损坏/格式不符时抛错提示。</summary>
        public static TrainedModel LoadModel(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("模型文件不存在：" + path);
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            if ((string)j["format"] != "vtmodel-v1")
                throw new InvalidOperationException("不是本软件训练的模型文件（format 不符）");
            var m = new TrainedModel
            {
                Labels = j["labels"]?.ToObject<List<string>>() ?? [],
                InputSide = (int)(j["side"] ?? 0),
                FinalEpoch = (int)(j["epochs"] ?? 0),
                TrainAcc = (float)(j["acc"] ?? 0),
                FinalLoss = (float)(j["loss"] ?? 0),
                Conv1W = j["c1w"]?.ToObject<float[]>() ?? [],
                Conv1B = j["c1b"]?.ToObject<float[]>() ?? [],
                Conv2W = j["c2w"]?.ToObject<float[]>() ?? [],
                Conv2B = j["c2b"]?.ToObject<float[]>() ?? [],
                FcW = j["fcw"]?.ToObject<float[]>() ?? [],
                FcB = j["fcb"]?.ToObject<float[]>() ?? [],
            };
            if (m.InputSide <= 0 || m.Labels.Count < 2 || m.Conv1W.Length == 0)
                throw new InvalidOperationException("模型文件内容不完整");
            return m;
        }

        /// <summary>是否图片扩展名</summary>
        public static bool IsImageExt(string ext) => ext?.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" => true,
            _ => false,
        };

        /// <summary>读取图片 → RGB float（InputSide×InputSide，0~1）</summary>
        public static float[] LoadImageAsRgb(string path, int side)
        {
            try
            {
                using var mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat == null || mat.Empty()) return null;
                return MatToRgb(mat, side);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>任意 Mat（彩色）→ RGB float（side×side，0~1，NCHW）。标注框区域样本复用。</summary>
        public static float[] MatToRgb(Mat mat, int side)
        {
            if (mat == null || mat.Empty() || side <= 0) return null;
            using var resized = new Mat();
            Cv2.Resize(mat, resized, new OpenCvSharp.Size(side, side), 0, 0, InterpolationFlags.Area);
            float[] rgb = new float[side * side * 3];
            unsafe
            {
                byte* p = (byte*)resized.Data;
                int step = (int)resized.Step();
                int idx = 0;
                for (int y = 0; y < side; y++)
                {
                    byte* row = p + y * step;
                    for (int x = 0; x < side; x++)
                    {
                        int b = row[x * 3 + 0];
                        int g = row[x * 3 + 1];
                        int r = row[x * 3 + 2];
                        rgb[idx] = r / 255f;
                        rgb[idx + side * side] = g / 255f;
                        rgb[idx + side * side * 2] = b / 255f;
                        idx++;
                    }
                }
            }
            return rgb;
        }

        /// <summary>数据增强：随机平移（图内 ±S/8 像素）+ 亮度/对比抖动 + 水平翻转。
        /// 在像素空间做（NCHW 平面布局），越界区填 0（黑边），增强强度固定、确定性由 rnd 控制。</summary>
        public float[] AugmentSample(float[] rgb, Random rnd, int S)
        {
            if (rgb == null || rgb.Length != S * S * 3) return rgb;
            float[] a = new float[rgb.Length];
            int plane = S * S;
            // 轻量增强（实测结论：翻转/大幅平移会掉点，小平移+轻微亮度最稳）：
            // 平移 ±2px（S/16，黑边影响小）、亮度增益 0.92~1.08、偏移 ±0.03；不做翻转。
            int shift = Math.Max(1, S / 16);
            int dx = rnd.Next(-shift, shift + 1);
            int dy = rnd.Next(-shift, shift + 1);
            float gain = (float)(0.92 + 0.16 * rnd.NextDouble());   // 0.92~1.08 亮度
            float bias = (float)((rnd.NextDouble() - 0.5) * 0.06); // ±0.03 偏移
            for (int ch = 0; ch < 3; ch++)
            {
                int off = ch * plane;
                for (int y = 0; y < S; y++)
                {
                    int sy = y + dy;
                    if (sy < 0 || sy >= S) continue;
                    int rw = y * S;
                    int sw = sy * S;
                    for (int x = 0; x < S; x++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= S) continue;
                        a[off + rw + x] = Math.Clamp(rgb[off + sw + sx] * gain + bias, 0f, 1f);
                    }
                }
            }
            return a;
        }

        // ============================== 训练 ==============================

        /// <summary>训练结束返回的模型（权重 + 元信息，可导出 ONNX / 页面临时推理）</summary>
        public class TrainedModel
        {
            public List<string> Labels;
            public int InputSide;
            /// <summary>Conv1 权重 [8,3,3,3]（NCHW）+ 偏置 [8]</summary>
            public float[] Conv1W, Conv1B;
            /// <summary>Conv2 权重 [16,8,3,3] + 偏置 [16]</summary>
            public float[] Conv2W, Conv2B;
            /// <summary>全连接权重 [C, D]（D=16*(S/4)^2）+ 偏置 [C]</summary>
            public float[] FcW, FcB;
            /// <summary>最终训练轮次 / 训练集准确率（日志用）</summary>
            public int FinalEpoch; public float TrainAcc; public float FinalLoss;
            /// <summary>混淆矩阵 Confusion[真实类别][预测类别]（训练集全量统计；训练完成后自动生成，供界面展示）</summary>
            public int[][] Confusion;

            /// <summary>单张 RGB float 图（side*side*3）→ 各类别概率（softmax 后，已降序由调用方排）</summary>
            public float[] Predict(float[] rgb)
            {
                if (rgb == null || rgb.Length != InputSide * InputSide * 3) return null;
                // 前向（复用训练器静态前向，权重已训好）
                return DeepTrainer.Forward(this, rgb);
            }
        }

        /// <summary>开始训练（阻塞；页面放后台线程调用）。中途 StopRequested=true 时保存当前模型返回。
        /// 传入 seed 模型时=继续训练：以已训好的权重为初始值接着跑，而不是从头随机初始化。</summary>
        public TrainedModel Train(TrainedModel seed = null)
        {
            if (Labels.Count < 2 || Samples.Count < Labels.Count * 2)
                throw new InvalidOperationException("先加载训练集（至少 2 类、每类 2 张）");
            int C = Labels.Count;
            int S = InputSide;
            int D = 16 * (S / 4) * (S / 4);
            int N = Samples.Count;

            // 初始化权重：默认 He 初始化卷积 / Xavier 全连接（小随机）；
            // seed 模型结构一致（输入边长/类别数/全连接维度都对得上）时复制其权重=继续训练
            float[] c1w, c1b, c2w, c2b, fcw, fcb;
            bool continuing = seed != null && seed.InputSide == S && seed.Labels.Count == C
                              && seed.FcW != null && seed.FcW.Length == C * D;
            var rnd = new Random((int)(Seed + (continuing ? 7 : 0)));   // 继续训练用不同打乱顺序，避免原地打转
            if (continuing)
            {
                c1w = (float[])seed.Conv1W.Clone();
                c1b = (float[])seed.Conv1B.Clone();
                c2w = (float[])seed.Conv2W.Clone();
                c2b = (float[])seed.Conv2B.Clone();
                fcw = (float[])seed.FcW.Clone();
                fcb = (float[])seed.FcB.Clone();
                Log?.Invoke($"继续训练：从已训模型（{seed.FinalEpoch} 轮）接着跑 {Epochs} 轮，不重头开始");
            }
            else
            {
                c1w = Init((r) => (float)(r.NextDouble() * 2 - 1) * (float)Math.Sqrt(2.0 / (3 * 9)), 8 * 3 * 3 * 3);
                c1b = new float[8];
                c2w = Init((r) => (float)(r.NextDouble() * 2 - 1) * (float)Math.Sqrt(2.0 / (8 * 9)), 16 * 8 * 3 * 3);
                c2b = new float[16];
                fcw = Init((r) => (float)(r.NextDouble() * 2 - 1) / (float)Math.Sqrt(D), C * D);
                fcb = new float[C];
                Log?.Invoke(seed != null
                    ? "继续训练：已加载模型与当前训练集结构不一致（输入边长/类别数变了），改为从头训练"
                    : "开始训练（从头初始化权重）");
            }

            // Adam 状态
            var adam = new AdamState(new float[][] { c1w, c1b, c2w, c2b, fcw, fcb }, Lr);

            // 每轮数据顺序（确定性打乱：Fisher-Yates 用固定种子）
            int[] order = Enumerable.Range(0, N).ToArray();

            float lastLoss = 0f, lastAcc = 0f;
            int finalEpoch = 0;
            for (int ep = 1; ep <= Epochs; ep++)
            {
                if (StopRequested)
                {
                    Log?.Invoke($"已停止（用户请求），保存第 {ep - 1} 轮结果");
                    break;
                }
                // 学习率余弦衰减：lr_ep = lr * (1+cos(pi*(ep-1)/(Epochs-1)))/2，
                // 前期大步收敛、后期小步精调，避免固定 lr 震荡
                if (LrDecay && Epochs > 1)
                    adam.SetLr(Lr * 0.5f * (1f + (float)Math.Cos(Math.PI * (ep - 1) / (Epochs - 1))));
                Shuffle(rnd, order);
                // 数据增强：每轮对全部样本做一次随机几何/光度扰动（小样本泛化关键）
                List<float[]> batchSamples = Samples;
                if (Augment)
                {
                    batchSamples = new List<float[]>(N);
                    for (int i = 0; i < N; i++) batchSamples.Add(AugmentSample(Samples[i], rnd, S));
                }
                float lossSum = 0f;
                int steps = 0, correct = 0;
                int bsz = Math.Min(Batch, N);
                for (int start = 0; start < N; start += bsz)
                {
                    int cnt = Math.Min(bsz, N - start);
                    // 小批前向
                    float[] logits = new float[cnt * C];
                    ForwardBatch(batchSamples, SampleClasses, order, start, cnt, c1w, c1b, c2w, c2b, fcw, fcb, S, C, logits);
                    // 交叉熵 loss：-log(softmax[g])，逐样本累加
                    float lsum = 0f;
                    for (int i = 0; i < cnt; i++)
                    {
                        int g = SampleClasses[order[start + i]];
                        float[] p = SoftmaxSlice(logits, i, C);
                        lsum += -(float)Math.Log(Math.Max(p[g], 1e-12f));
                        int best = 0;
                        for (int c = 1; c < C; c++) if (logits[i * C + c] > logits[i * C + best]) best = c;
                        if (best == g) correct++;
                    }
                    lossSum += lsum / cnt;
                    // 反向（小批：逐样本反向后累加梯度）
                    var grads = BackwardBatch(batchSamples, logits, cnt, C, order, start, SampleClasses,
                        c1w, c1b, c2w, c2b, fcw, fcb, S);
                    adam.Step(grads);
                    steps++;
                }
                lastLoss = lossSum / Math.Max(1, steps);
                lastAcc = (float)correct / N;
                finalEpoch = ep;
                Log?.Invoke($"轮 {ep}/{Epochs}  loss={lastLoss:F4}  训练准确率={lastAcc:P0}");
                OnEpoch?.Invoke(ep, lastLoss, lastAcc);   // 训练监控（进度条/损失/准确率曲线）
                // 提前结束要求"既分对又分得稳"：acc=100% 但 loss 还高（置信度低）就继续训
                if (lastAcc >= 0.999f && lastLoss < 0.2f && ep >= 3)
                {
                    Log?.Invoke("训练集已 100% 收敛且损失很低，提前结束");
                    break;
                }
            }

            // 训练收尾：对训练集全量前向，统计混淆矩阵 Confusion[真实][预测]（界面 5 混淆矩阵卡片用）
            var conf = new int[C][];
            for (int ci = 0; ci < C; ci++) conf[ci] = new int[C];
            var orderAll = new int[N];
            for (int i = 0; i < N; i++) orderAll[i] = i;
            var allLogits = new float[Batch * C];
            for (int s0 = 0; s0 < N; s0 += Batch)
            {
                int cnt = Math.Min(Batch, N - s0);
                ForwardBatch(Samples, SampleClasses, orderAll, s0, cnt, c1w, c1b, c2w, c2b, fcw, fcb, S, C, allLogits);
                for (int k = 0; k < cnt; k++)
                {
                    int pred = 0;
                    for (int j = 1; j < C; j++) if (allLogits[k * C + j] > allLogits[k * C + pred]) pred = j;
                    int truth = SampleClasses[orderAll[s0 + k]];
                    conf[truth][pred]++;
                }
            }

            return new TrainedModel
            {
                Labels = new List<string>(Labels),
                InputSide = S,
                Conv1W = c1w, Conv1B = c1b,
                Conv2W = c2w, Conv2B = c2b,
                FcW = fcw, FcB = fcb,
                FinalEpoch = finalEpoch,
                TrainAcc = lastAcc,
                FinalLoss = lastLoss,
                Confusion = conf,
            };
        }

        private static float[] Init(Func<Random, float> gen, int len)
        {
            var r = new Random(1);
            var a = new float[len];
            for (int i = 0; i < len; i++) a[i] = gen(r);
            return a;
        }

        private static void Shuffle(Random rnd, int[] arr)
        {
            for (int i = arr.Length - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (arr[i], arr[j]) = (arr[j], arr[i]);
            }
        }

        // ============================== 前向 ==============================

        /// <summary>单样本前向（给页面验证与导出推理用）：返回 softmax 概率</summary>
        public static float[] Forward(TrainedModel m, float[] rgb)
        {
            int S = m.InputSide, C = m.Labels.Count, D = m.FcW.Length / C;
            // Conv1 → ReLU → Pool → Conv2 → ReLU → Pool → FC
            float[] c1 = new float[8 * S * S];
            ConvForward(rgb, m.Conv1W, m.Conv1B, 3, 8, S, S, c1);
            for (int i = 0; i < c1.Length; i++) if (c1[i] < 0) c1[i] = 0;
            float[] p1 = new float[8 * (S / 2) * (S / 2)];
            MaxPoolForward(c1, 8, S, p1, out var _);
            float[] c2 = new float[16 * (S / 2) * (S / 2)];
            ConvForward(p1, m.Conv2W, m.Conv2B, 8, 16, S / 2, S / 2, c2);
            for (int i = 0; i < c2.Length; i++) if (c2[i] < 0) c2[i] = 0;
            float[] p2 = new float[16 * (S / 4) * (S / 4)];
            MaxPoolForward(c2, 16, S / 2, p2, out var _);
            float[] flat = new float[D];
            for (int i = 0; i < D; i++) flat[i] = p2[i];
            float[] logits = new float[C];
            for (int c = 0; c < C; c++)
            {
                float s = m.FcB[c];
                for (int d = 0; d < D; d++) s += m.FcW[c * D + d] * flat[d];
                logits[c] = s;
            }
            return Softmax(logits);
        }

        /// <summary>批前向：输出 logits（N*C）。临时缓冲复用（内部 new 无妨，小批）</summary>
        private static void ForwardBatch(IList<float[]> samples, List<int> cls,
            int[] order, int start, int cnt, float[] c1w, float[] c1b, float[] c2w, float[] c2b,
            float[] fcw, float[] fcb, int S, int C, float[] logits)
        {
            int D = 16 * (S / 4) * (S / 4);
            for (int bi = 0; bi < cnt; bi++)
            {
                float[] rgb = samples[order[start + bi]];
                float[] p2 = ForwardToPool2(rgb, c1w, c1b, c2w, c2b, S);
                for (int c = 0; c < C; c++)
                {
                    float s = fcb[c];
                    for (int d = 0; d < D; d++) s += fcw[c * D + d] * p2[d];
                    logits[bi * C + c] = s;
                }
            }
        }

        /// <summary>前向到第二个池化层输出（展平 D 维），供训练与推理共用</summary>
        private static float[] ForwardToPool2(float[] rgb, float[] c1w, float[] c1b, float[] c2w, float[] c2b, int S)
        {
            float[] c1 = new float[8 * S * S];
            ConvForward(rgb, c1w, c1b, 3, 8, S, S, c1);
            for (int i = 0; i < c1.Length; i++) if (c1[i] < 0) c1[i] = 0;
            float[] p1 = new float[8 * (S / 2) * (S / 2)];
            MaxPoolForward(c1, 8, S, p1, out var _);
            float[] c2 = new float[16 * (S / 2) * (S / 2)];
            ConvForward(p1, c2w, c2b, 8, 16, S / 2, S / 2, c2);
            for (int i = 0; i < c2.Length; i++) if (c2[i] < 0) c2[i] = 0;
            float[] p2 = new float[16 * (S / 4) * (S / 4)];
            MaxPoolForward(c2, 16, S / 2, p2, out var _);
            return p2;
        }

        /// <summary>3×3 卷积（pad1，stride1），输入 NCHW 单图 [inC*H*W] → 输出 [outC*H*W]</summary>
        private static void ConvForward(float[] input, float[] w, float[] b, int inC, int outC, int H, int W, float[] output)
        {
            int outIdx = 0;
            for (int oc = 0; oc < outC; oc++)
            {
                for (int oh = 0; oh < H; oh++)
                {
                    for (int ow = 0; ow < W; ow++)
                    {
                        float s = b[oc];
                        for (int ic = 0; ic < inC; ic++)
                        {
                            for (int kh = -1; kh <= 1; kh++)
                            {
                                int ih = oh + kh;
                                if (ih < 0 || ih >= H) continue;
                                for (int kw = -1; kw <= 1; kw++)
                                {
                                    int iw = ow + kw;
                                    if (iw < 0 || iw >= W) continue;
                                    s += input[ic * H * W + ih * W + iw] *
                                         w[((oc * inC + ic) * 3 + (kh + 1)) * 3 + (kw + 1)];
                                }
                            }
                        }
                        output[outIdx++] = s;
                    }
                }
            }
        }

        /// <summary>2×2 MaxPool（stride2），输入 [C*H*W] → 输出 [C*(H/2)*(W/2)]；记录 argmax 索引供反向</summary>
        private static void MaxPoolForward(float[] input, int C, int H, float[] output, out int[] argmax)
        {
            int H2 = H / 2, W2 = H / 2;
            argmax = new int[output.Length];
            int oi = 0;
            for (int c = 0; c < C; c++)
            {
                for (int y = 0; y < H2; y++)
                {
                    for (int x = 0; x < W2; x++)
                    {
                        int b0 = c * H * H + (y * 2) * H + (x * 2);
                        int best = b0;
                        float bv = input[b0];
                        if (input[b0 + 1] > bv) { bv = input[b0 + 1]; best = b0 + 1; }
                        if (input[b0 + H] > bv) { bv = input[b0 + H]; best = b0 + H; }
                        if (input[b0 + H + 1] > bv) { bv = input[b0 + H + 1]; best = b0 + H + 1; }
                        output[oi] = bv;
                        argmax[oi] = best;
                        oi++;
                    }
                }
            }
        }

        private static float[] Softmax(float[] logits)
        {
            float m = logits.Max();
            double sum = 0;
            for (int i = 0; i < logits.Length; i++) sum += Math.Exp(logits[i] - m);
            var p = new float[logits.Length];
            for (int i = 0; i < logits.Length; i++) p[i] = (float)(Math.Exp(logits[i] - m) / sum);
            return p;
        }

        // ============================== 反向 ==============================

        /// <summary>小批反向：逐样本反向累加 6 个梯度数组（与参数一一对应）。</summary>
        private static float[][] BackwardBatch(IList<float[]> samples, float[] logits, int cnt, int C,
            int[] order, int start, List<int> cls, float[] c1w, float[] c1b, float[] c2w, float[] c2b,
            float[] fcw, float[] fcb, int S)
        {
            int D = 16 * (S / 4) * (S / 4);
            float[] dc1w = new float[c1w.Length], dc1b = new float[8];
            float[] dc2w = new float[c2w.Length], dc2b = new float[16];
            float[] dfcw = new float[fcw.Length], dfcb = new float[C];

            for (int bi = 0; bi < cnt; bi++)
            {
                int n = order[start + bi];
                float[] rgb = samples[n];
                // 前向缓存
                float[] c1 = new float[8 * S * S];
                ConvForward(rgb, c1w, c1b, 3, 8, S, S, c1);
                int[] a1; float[] p1 = new float[8 * (S / 2) * (S / 2)];
                MaxPoolForward(c1, 8, S, p1, out a1);
                float[] c2 = new float[16 * (S / 2) * (S / 2)];
                ConvForward(p1, c2w, c2b, 8, 16, S / 2, S / 2, c2);
                int[] a2; float[] p2 = new float[16 * (S / 4) * (S / 4)];
                MaxPoolForward(c2, 16, S / 2, p2, out a2);

                int g = cls[n];
                // dLogits = softmax - onehot
                float[] dLogits = SoftmaxSlice(logits, bi, C);
                dLogits[g] -= 1f;

                // FC 梯度
                for (int c = 0; c < C; c++)
                {
                    dfcb[c] += dLogits[c];
                    for (int d = 0; d < D; d++)
                        dfcw[c * D + d] += dLogits[c] * p2[d];
                }
                // dP2 = dLogits · FcW
                float[] dp2 = new float[D];
                for (int d = 0; d < D; d++)
                {
                    float s = 0;
                    for (int c = 0; c < C; c++) s += dLogits[c] * fcw[c * D + d];
                    dp2[d] = s;
                }
                // 池化1反向：散射到 c2 的 argmax 位置
                float[] dc2 = new float[c2.Length];
                for (int i = 0; i < p2.Length; i++) dc2[a2[i]] += dp2[i];
                // ReLU2 反向 + Conv2 反向
                for (int i = 0; i < dc2.Length; i++)
                    if (c2[i] <= 0) dc2[i] = 0;
                ConvBackward(p1, dc2, c2w, c2b, 8, 16, S / 2, S / 2, dc2w, dc2b, out float[] dp1Raw);
                // 池化0反向 → ReLU1 反向 → Conv1 反向
                float[] dp1 = new float[p1.Length];
                for (int i = 0; i < p1.Length; i++)
                {
                    // dp1Raw 是 p1 的梯度（Conv2 输入）；池化把 c1 中非 argmax 的梯度归零
                    int a = a1[i];
                    dp1[i] = dp1Raw[i];
                }
                float[] dc1 = new float[c1.Length];
                for (int i = 0; i < p1.Length; i++) dc1[a1[i]] += dp1[i];
                for (int i = 0; i < dc1.Length; i++)
                    if (c1[i] <= 0) dc1[i] = 0;
                ConvBackward(rgb, dc1, c1w, c1b, 3, 8, S, S, dc1w, dc1b, out _);
            }
            return new[] { dc1w, dc1b, dc2w, dc2b, dfcw, dfcb };
        }

        private static float[] SoftmaxSlice(float[] logits, int bi, int C)
        {
            float m = float.MinValue;
            for (int c = 0; c < C; c++) m = Math.Max(m, logits[bi * C + c]);
            double sum = 0;
            for (int c = 0; c < C; c++) sum += Math.Exp(logits[bi * C + c] - m);
            var p = new float[C];
            for (int c = 0; c < C; c++) p[c] = (float)(Math.Exp(logits[bi * C + c] - m) / sum);
            return p;
        }

        /// <summary>卷积反向：累加 dW/dB，并输出 dInput（[inC*H*W]）</summary>
        private static void ConvBackward(float[] input, float[] dOut, float[] w, float[] b,
            int inC, int outC, int H, int W, float[] dW, float[] dB, out float[] dInput)
        {
            dInput = new float[inC * H * W];
            int idx = 0;
            for (int oc = 0; oc < outC; oc++)
            {
                for (int oh = 0; oh < H; oh++)
                {
                    for (int ow = 0; ow < W; ow++)
                    {
                        float d = dOut[idx++];
                        if (d == 0f) continue;
                        dB[oc] += d;
                        for (int ic = 0; ic < inC; ic++)
                        {
                            for (int kh = -1; kh <= 1; kh++)
                            {
                                int ih = oh + kh;
                                if (ih < 0 || ih >= H) continue;
                                for (int kw = -1; kw <= 1; kw++)
                                {
                                    int iw = ow + kw;
                                    if (iw < 0 || iw >= W) continue;
                                    float inVal = input[ic * H * W + ih * W + iw];
                                    int widx = ((oc * inC + ic) * 3 + (kh + 1)) * 3 + (kw + 1);
                                    dW[widx] += d * inVal;
                                    dInput[ic * H * W + ih * W + iw] += d * w[widx];
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Adam 优化器：m/v 一阶/二阶矩，按参数数组逐一更新</summary>
        private sealed class AdamState
        {
            private readonly float[][] _pars;
            private readonly float[][] _m;
            private readonly float[][] _v;
            private float _lr;
            private const float Beta1 = 0.9f, Beta2 = 0.999f, Eps = 1e-8f;
            private int _t;

            public AdamState(float[][] pars, float lr)
            {
                _pars = pars;
                _m = new float[pars.Length][];
                _v = new float[pars.Length][];
                for (int i = 0; i < pars.Length; i++)
                {
                    _m[i] = new float[pars[i].Length];
                    _v[i] = new float[pars[i].Length];
                }
                _lr = lr;
            }

            /// <summary>更新学习率（余弦衰减用）</summary>
            public void SetLr(float lr) => _lr = lr;

            public void Step(float[][] grads)
            {
                _t++;
                float bc1 = 1f - (float)Math.Pow(Beta1, _t);
                float bc2 = 1f - (float)Math.Pow(Beta2, _t);
                for (int i = 0; i < _pars.Length; i++)
                {
                    float[] p = _pars[i], g = grads[i], m = _m[i], v = _v[i];
                    for (int j = 0; j < p.Length; j++)
                    {
                        m[j] = Beta1 * m[j] + (1f - Beta1) * g[j];
                        v[j] = Beta2 * v[j] + (1f - Beta2) * g[j] * g[j];
                        float mh = m[j] / bc1, vh = v[j] / bc2;
                        p[j] -= _lr * mh / ((float)Math.Sqrt(vh) + Eps);
                    }
                }
            }
        }
    }
}
