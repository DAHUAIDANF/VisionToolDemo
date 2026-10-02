using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    // 点阵（dot-peen）Data Matrix 定位与采样管线，为 Ecc200Decoder 供图：
    //   顶帽/黑帽提点 → Otsu 二值（失败自动降档捞弱点）→ 码点点集提取 →
    //   最近邻距离直方图取多个节距候选 → 投影圆均值锐度全角度搜向（[0,90°) 粗搜+细搜，
    //   金属件任意摆放角可解）→ 整数格点固定后最小二乘精化节距/原点 →
    //   受支持尺寸枚举 → 格点响应自适应判暗采样出暗模块矩阵。
    // 金属表面（DPM，实拍图回归得到）兼容增强：
    //   · 大尺度光照不均（弧面/反光）时原图分支失败后，自动退回"高斯背景除法拉平"
    //     的归一化图重试全档位；
    //   · 码点点集双路：A=连通域质心（点分离良好，快路径）；A 全档失败后走
    //     B=粘连链按距离变换局部极大拆分（点径接近节距时相邻点连成链，质心落在链中点、
    //     最近邻距离塌到碎片尺度）。B 先按轮廓层级填连通域内孔，解决"亮点环+暗心"
    //     的环形点（距离变换峰落在环中线而非点心）；
    //   · 节距多假设：杂散纹理（连接器针脚/焊盘/划痕/文字）在整图上会提供一个偏小的
    //     最近邻众数，单一众数会把码点云切成碎片而漏检；改为取直方图前 8 个峰逐个尝试，
    //     按"点面积 ∝ 节距²"筛出该假设下的码点，命中即返回；
    //   · 面积窗按节距自适应：原"面积中位数"会被海量噪斑拉低，真码点被 2.5×中位数
    //     上限整体滤除（实拍大图常见）；现按假设节距取 [0.05p², 0.95p²]；
    //   · 尺寸枚举：外沿多一圈杂点会把范围撑成 17×16 这类非受支持尺寸，原"逐条裁最稀疏
    //     边缘"会在一次重拟合里裁掉两条线（16×17 → 14×16），把正确的 16×16 跳过；
    //     现按"各边裁 0~2 线"枚举受支持尺寸并逐个采样+RS 校验；
    //   · 采样判暗改为"格点邻域形态学响应 + 格点级 Otsu"：原"二值图方窗白占比"在一个
    //     全局阈值偏松时会整片判暗（整幅矩阵全 1，RS 必然失败），响应量本身保持双峰；
    //     低对比浅打点仍由质心命中判据兜底。
    // 性能：最近邻/最大簇用均匀网格空间索引（原 O(n²)），锐度搜向按步长抽样，
    // 尺寸枚举限量，整体搜索带墙钟预算（SearchBudgetMs）——无码/极难图最坏耗时可控。
    // 几何量全部由点云拟合而来，不识别定位边——只要求采样点落在网格节点上；
    // 朝向/尺寸/极性的最终裁决交给 Ecc200Decoder 的 RS 校验。
    public static class DotMatrix
    {
        /// <summary>参与网格拟合的最少单点数</summary>
        private const int MinFitPoints = 24;

        /// <summary>采样方窗半径系数（×节距）</summary>
        private const double SampleRadiusRatio = 0.32;

        /// <summary>判暗的方窗白色占比阈值（默认，同时作为格点级 Otsu 的偏置基准）</summary>
        private const double DarkRatio = 0.30;

        /// <summary>圆均值锐度下限（两轴之和，上限 2）——低于此说明网格不成立（默认）</summary>
        private const double MinSharpness = 0.9;

        /// <summary>顶帽核边长序列：小核留住小点，大核覆盖大点（点径应小于核、大于面积窗下限）</summary>
        private static readonly int[] HatKernels = { 11, 21, 41, 61 };

        /// <summary>节距候选数上限：多假设搜索，防止全图穷举耗时失控。
        /// 取 8 而非 4：整图杂散纹理（针脚/焊盘/划痕）会占满前几个峰，真节距常排在第 5~8 位</summary>
        private const int MaxPitchCandidates = 8;

        /// <summary>码点面积窗相对节距的系数（点径 ≈ 0.25~1.1 节距 → 面积 ≈ 0.05~0.95 节距²）</summary>
        private const double DotAreaLoRatio = 0.05;
        private const double DotAreaHiRatio = 0.95;

        /// <summary>最近邻距离统计的有效半径上限（px）：超过此值视为孤立点，不参与节距直方图。
        /// 取 64 足够覆盖到 64px 节距（更大的点阵在实拍图上已不可分辨），
        /// 同时把"孤立噪点"的网格扫描环数减半</summary>
        private const double NnMaxRadius = 64.0;

        /// <summary>距离变换局部极大判为码点的最小半径（px）：滤掉 1~4px 噪斑峰</summary>
        private const float MinDotRadius = 1.5f;

        /// <summary>角度/节距搜向时参与锐度统计的点数上限（抽样，不影响主峰位置）</summary>
        private const int SharpnessSamples = 256;

        /// <summary>单次 TryDecode 的搜索墙钟上限（ms）：无码/极难图不允许把
        /// 32 组（极性×核×阈值档）× 多节距假设 × 多尺寸穷举跑满（实测可到分钟级）</summary>
        private const int SearchBudgetMs = 5000;

        /// <summary>局部轮自带的独立时间片（ms）：全局轮可能因单组大核形态学超时很长，
        /// 局部轮必须有自己的预算才不会"一进去就超时"（见 TryDecode）</summary>
        private const int LocalSearchBudgetMs = 7000;

        /// <summary>局部轮单个窗口的时间片（ms）与最多尝试的窗口数</summary>
        private const int WinTimeSliceMs = 2000;
        private const int MaxLocalWindows = 6;

        /// <summary>粘连拆分点集超过此点数即视为噪声主导，直接跳过（省掉最贵的一路搜索）</summary>
        private const int MaxSplitPoints = 4000;

        /// <summary>质心点集超过此点数即视为纯噪（无码图）：直接放弃，避免无效穷举</summary>
        private const int MaxPoints = 40000;

        /// <summary>每次拟合允许尝试的符号尺寸数（按边框吻合率降序取前若干个）</summary>
        private const int MaxExtentTries = 6;

        /// <summary>RS 解码前的 ECC200 边框最低吻合率。这是**误检闸门**，不是筛选器：
        /// 实测正确解码的边框吻合率 0.77~0.81（弱边/丢点会拉低），而 a2 全图上的
        /// 假阳性只有 0.32 —— 取 0.55 既能挡住假阳性，又给真符号留足余量。
        /// 注意不要往上调：早先误设 0.85 会把 0.77 的正确尺寸直接判死（a1/a113 全挂）。</summary>
        private const double BorderMinScore = 0.55;

        /// <summary>每次拟合参与边框打分的候选尺寸数上限</summary>
        private const int MaxExtentCandidates = 24;



        // 单次 TryDecode 的搜索截止时刻与拟合计数（ThreadStatic：算子可能被多线程并行调用）
        [ThreadStatic] private static long _searchDeadline;
        [ThreadStatic] private static long _searchStart;

        // 可调参数集：null 的字段用默认值。UI（BarcodeTask 参数面板）与探针按需覆盖。
        public class Options
        {
            /// <summary>判暗阈值偏置 ×100（30=默认，即直接采用格点级 Otsu 门限；
            /// 调低=门限降低=更容易判暗，调高=更严）</summary>
            public int DarkRatioPercent = 30;

            /// <summary>圆均值锐度下限 ×100（90=默认 0.9；低对比实图可调低放宽）</summary>
            public int MinSharpnessPercent = 90;

            /// <summary>最小节距（像素）——小于此判为非点阵纹理（默认 5）</summary>
            public int MinPitch = 5;

            /// <summary>搜索墙钟预算覆盖（ms，≤0 用默认）。仅诊断/离线批量用，
            /// UI 侧不应放宽——它决定无码/极难图的最坏耗时</summary>
            public int SearchBudgetOverrideMs = 0;

            /// <summary>诊断日志回调（默认 null 零开销）：打点报告各档位失败原因，供排障/测试</summary>
            public Action<string> DebugLog;

            /// <summary>采样矩阵诊断回调（默认 null 零开销）：每次送入 RS 解码前回调
            /// （行×列 bool，true=判暗=1）与上下文标签，供排障/测试定位采样偏差</summary>
            public Action<bool[,], string> DebugMatrix;
        }

        // 对灰度图尝试点阵 Data Matrix 解码。成功输出文本与符号四角（输入图像坐标）。
        // 两种极性 × 多档核尺寸各试一遍：暗模块 = 亮点（金属暗底打点，TopHat）或暗点（BlackHat）。
        public static bool TryDecode(Mat gray, out string text, out Point2f[] quad)
        {
            return TryDecode(gray, null, out text, out quad);
        }

        // 二值化阈值档（×Otsu）：必须**双向**扫，不能只往下调。
        //   1.00 = Otsu 原阈值；
        //   0.55 = 降档捞低对比浅打点；
        //   1.50 / 2.00 / 2.60 = **升档**分开"在 Otsu 下粘成一片的点"。
        // 升档这一侧是实测出来的：整图 Otsu 会被大面积背景/面板压低，码点响应整体高于该阈值，
        // 于是相邻点连成一片、连通域面积中位数被抬到几百（a114 实测 639px，只有 59 个可用域），
        // 网格拟合拿不到足够格点。把阈值升到 1.6×Otsu 后，同一码区恰好析出 **146 个**约 187px
        // 的独立圆斑——正是 16×16 DataMatrix 的暗模块数，lattice 立刻可拟合。
        // 升档会带来更多噪斑，仍由连通域过滤/最大簇/网格拟合/RS+边框校验层层兜底。
        private static readonly double[] ThresholdScales = { 1.0, 0.55, 1.5, 2.0, 2.6 };

        public static bool TryDecode(Mat gray, Options opt, out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            if (gray == null || gray.Empty()) return false;

            double darkRatio = (opt?.DarkRatioPercent ?? 30) / 100.0;
            double minSharp = (opt?.MinSharpnessPercent ?? 90) / 100.0;
            int minPitch = opt?.MinPitch ?? 5;

            // 两条预处理分支：原图直提（对比良好的打点）→ 光照归一化图（金属弧面/斜射
            // 反光/大尺度亮度梯度下顶帽响应被压制，除法拉平后全局阈值恢复可用）。
            // 每分支内：极性 × 核尺寸 × 阈值档，任一命中即返回。
            Action<string> log = opt?.DebugLog;
            Action<bool[,], string> matLog = opt?.DebugMatrix;
            // 本次调用的搜索预算（见 BudgetExpired / TryHat）
            int budget = opt?.SearchBudgetOverrideMs > 0 ? opt.SearchBudgetOverrideMs : SearchBudgetMs;
            StartBudget(budget);

            // —— 全局轮：整图一次 ——
            // 码只占整图一小块时，整图的最大簇/节距直方图被针脚、焊盘、丝印淹没，
            // 即使真节距进了候选，簇里也混满杂点，网格拟合出的采样矩阵整体错位、RS 必失败。
            // 因此全局轮只分到一半预算，剩下的留给下面的"局部轮"。
            long totalDeadline = _searchDeadline;
            long half = _searchStart + (System.Diagnostics.Stopwatch.Frequency * (budget / 2) / 1000);

            // 大图优先走局部轮：码只占一小块时，全局轮既慢（大核形态学 O(像素×k²) 跑满全档）
            // 又难命中（簇被杂点淹没）。实拍 a114 全局轮 10s 失败、同一窗口局部 0.6s 命中。
            // 小图（截图/裁片）码本来就占大半，全局轮一次就中，保持原顺序最省时。
            // 局部轮：全局轮失败后，在高密度/网格窗口上重跑整条管线。
            // 默认关闭——窗口排序仍未收敛：按点密度排会选中连接器（比码点更密），
            // 按 FFT 点阵证据排也会被条码/丝印的强周期抢走，真正含码的窗排不进前几名，
            // 而单窗全档穷举要 1~2s，预算内试不了几个。实现完整保留，供后续继续调。
            // 现状：带 ROI 时（用户在界面上框选）算子已能正确解出（见 LocateCodeRegion 注释），
            // 只有"不框选、整图直接跑"这一种情形会漏 a114。
            const bool localSearchEnabled = false;
            bool localFirst = false;
            if (localFirst)
            {
                _searchDeadline = half;   // 局部轮先拿走一半
                if (TryDecodeLocal(gray, log, matLog, out text, out quad)) { _searchDeadline = totalDeadline; return true; }
            }

            // —— 全局轮：整图一次 ——
            _searchDeadline = localFirst ? totalDeadline : (localSearchEnabled ? half : totalDeadline);
            bool hit = TryAll(gray, "raw", darkRatio, minSharp, minPitch, log, matLog, out text, out quad);
            if (!hit)
                using (Mat flat = FlattenIllumination(gray))
                    hit = TryAll(flat, "flat", darkRatio, minSharp, minPitch, log, matLog, out text, out quad);
            if (hit) { _searchDeadline = totalDeadline; return true; }
            if (localFirst || !localSearchEnabled) return false;

            // —— 局部轮：在定位到的码区上重跑整条管线 ——
            // 关键：局部轮用**从此刻起算的独立时间片**，而不是沿用整次调用的绝对截止时刻。
            // 全局轮的预算检查只在每个 (极性,核) 组合开始时生效，单组大核形态学（8MP 上
            // k61 椭圆核）本身就可能超时很长，于是全局轮经常一路吃到总预算耗尽，
            // 局部轮一开始就"no budget"（a114 实测）。独立切片才能保证局部轮真正跑起来。
            _searchDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
                + (System.Diagnostics.Stopwatch.Frequency * (budget * 3 / 5) / 1000);
            if (TryDecodeLocal(gray, log, matLog, out text, out quad)) return true;
            return false;
        }

        /// <summary>局部轮：取"候选码点最密"的若干窗口，在各窗口上重跑整条点阵管线。
        /// 码只占画面一小块时，窗口内码点占比大幅提升，节距/簇/网格拟合恢复可用。
        /// 窗口按密度降序、互不重叠，数量与单窗口预算均受剩余预算约束。</summary>
        private static bool TryDecodeLocal(Mat gray, Action<string> log, Action<bool[,], string> matLog,
            out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            // 窗口边长取短边 30%（≈800px @2679）：实测这个尺度上码点占比足够高、
            // 单窗全档穷举也只要几百毫秒——窗口开大（45%）单窗就能吃掉半个预算，轮次被压到 1~2 次。
            // 边长取短边 30%（≈800px @2679）：与实测"能解出 a114 的手工裁窗"同尺度——
            // 再大就把相邻杂点一并框进来（964 窗实测失败），再小则可能切掉符号本身。
            int side = Math.Max(320, (int)(Math.Min(gray.Cols, gray.Rows) * 0.30));
            if (side >= Math.Min(gray.Cols, gray.Rows)) return false;   // 图太小，全局轮已等价
            // 窗口集 = 高密度窗 ∪ 全覆盖网格（按密度降序、去重）：
            // 只取密度最高的少数窗会在"码不是最密的纹理"时整体漏掉（a1 实测），
            // 而纯网格顺序又会在无关区域烧掉预算——两者合并即可兼顾覆盖与优先级。
            var wins = new List<Rect>();
            // 先给一个"紧贴码区"的 ROI：密点最多的格心 + 其邻域点的包围盒（外扩 12%）。
            // 网格窗边长固定，符号接近窗口大小时必然混进大量相邻杂点（a114 实测：
            // 完整覆盖码的 964 窗失败，而同一位置 800 的紧窗 0.6s 命中）——所以紧窗优先。
            // 紧窗只作为候选之一（密度代理在"连接器/丝印比码点更密"时会指错，
            // 实测 a114 指到了连接器区），不能只押它一个
            // 主候选：FFT 周期分析 + 格点相位外推定出的码区。
            // 旧版用"二值质心点云的规律性"定位，实测在能解出的裁片上簇仍是碎片
            // （包围盒 116×435、残差达节距 20~40%），因为全局 Otsu 的质心点云
            // 本身不含可靠格点结构——定位必须建立在"响应场确实周期性"这个证据上。
            Rect codeRoi = LocateByLattice(gray, log);
            if (codeRoi.Width > 0) wins.Add(codeRoi);
            // 备选：定位失败时，用"窗口内是否存在一致的方格点阵"（FFT 频谱峰值信噪比）
            // 给全覆盖网格窗排序，再逐个试。
            // 不能按"点密度"排序：连接器针脚/丝印的点比码点更密更集中，a114 实测密度
            // 前两名都落在连接器区，真正的码窗排在后面且预算已尽。而"存在周期一致的
            // 方格点阵"才是码的判据，也正好是 FFT 能直接量出来的东西。
            if (wins.Count == 0)
            {
                int gstep = Math.Max(64, side / 2);
                // 评分必须覆盖**整个窗口**，不能只取窗心：码不一定会落在网格窗的中央，
                // 只测窗心会把"窗内有码但码偏一侧"的窗判低分（a114 实测：真正含码的
                // 窗因码偏在窗心外而落选，选中的全是连接器/边框区）。
                // 全窗 803² 做 48 次 FFT 太贵，故先整体降采样到 1/2：半尺度下每窗 401²，
                // 48 次约 0.4s；节距同步减半，仍在 WindowLattice 的 8~90px 识别带内。
                double ds = 0.5;
                int sh = Math.Max(64, (int)Math.Round(side * ds));
                int gsh = Math.Max(32, (int)Math.Round(gstep * ds));
                using var small = new Mat();
                Cv2.Resize(gray, small, new OpenCvSharp.Size((int)(gray.Cols * ds), (int)(gray.Rows * ds)),
                    0, 0, InterpolationFlags.Area);
                var cands = new List<(double Score, Rect R)>();
                for (int ys = 0; ys + sh <= small.Rows; ys += gsh)
                    for (int xs = 0; xs + sh <= small.Cols; xs += gsh)
                    {
                        using var win = new Mat(small, new Rect(xs, ys, sh, sh));
                        double score = WindowLattice(win, out _, out _);
                        cands.Add((score, new Rect((int)(xs / ds), (int)(ys / ds), side, side)));
                    }
                cands.Sort((p, q) => q.Score.CompareTo(p.Score));
                foreach ((double sc, Rect rr) in cands)
                {
                    if (wins.Count >= 12) break;
                    wins.Add(rr);
                }
            }
            if (wins.Count == 0) return false;
            // 窗口集构建本身（定位/顶帽/连通域）可能已花掉数秒，故单窗时间片一律
            // **从"开始处理该窗的时刻"起算**，而不是从整次调用起点起算——后者会让
            // 每个窗一进去就已超时（a114 实测：局部轮每次都立刻 no budget）。
            long localDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
                + (System.Diagnostics.Stopwatch.Frequency * LocalSearchBudgetMs / 1000);
            _searchDeadline = localDeadline;
            int winTried = 0;
            foreach (Rect w in wins)
            {
                if (BudgetExpired()) { log?.Invoke("local: budget exhausted"); return false; }
                // 单窗时间片：单窗失败不许拖垮后续窗口（否则轮到第 2 个窗口预算就没了）
                long saved = _searchDeadline;
                long sliceEnd = System.Diagnostics.Stopwatch.GetTimestamp()
                    + (System.Diagnostics.Stopwatch.Frequency * WinTimeSliceMs / 1000);
                _searchDeadline = Math.Min(saved, sliceEnd);
                if (++winTried > MaxLocalWindows) break;
                using (Mat roi = new Mat(gray, w))
                {
                    log?.Invoke($"[local {w.X},{w.Y} {w.Width}x{w.Height}] begin");
                    try
                    {
                        if (TryAll(roi, "raw", 0.30, 0.90, 5, null, null, out text, out quad)
                            || TryAll(roi, "flat", 0.30, 0.90, 5, null, null, out text, out quad))
                        {
                            // 窗口局部坐标 → 全图坐标
                            if (quad != null)
                                for (int i = 0; i < quad.Length; i++)
                                    quad[i] = new Point2f(quad[i].X + w.X, quad[i].Y + w.Y);
                            log?.Invoke($"[local {w.X},{w.Y}] hit");
                            return true;
                        }
                    }
                    finally { _searchDeadline = saved; }
                }
            }
            return false;
        }

        /// <summary>用 FFT 周期分析 + 格点相位外推定位码区。
        /// 思路：码的本质是"响应场在 (节距, 角度) 上强周期"，这是可靠证据；
        /// 而二值化质心点云的"规律性"不是——实测在能解出的裁片上簇仍是碎片
        /// （包围盒 116×435、残差达节距 20~40%），据此外推必然偏。
        /// 步骤：① 在若干窗口上做顶帽响应的 2D FFT，取窄带内最强峰得到 (节距, 角度)，
        /// 以"峰/带内均值"为置信度选最佳窗口；② 用响应加权的圆均值锁定格点相位；
        /// ③ 沿格点逐列/逐行走出去，节点响应塌到格间水平即符号边界——
        /// 边界由相位外推得到，不再依赖经验外扩系数。</summary>
        private static Rect LocateByLattice(Mat gray, Action<string> log)
        {
            // 窗口取短边 30%（≈800px @2679）：与"手工裁窗能解出 a114"同尺度。
            // 窗口过大（42%）时码与窗口同量级，FFT 被面板/标签大结构主导（实测全窗
            // 最强峰落在 360~415px 的整幅轮廓上，而不是点阵节距）。
            int side = Math.Max(192, (int)(Math.Min(gray.Cols, gray.Rows) * 0.30));
            if (side >= Math.Min(gray.Cols, gray.Rows)) return new Rect();
            int step = Math.Max(32, side / 2);
            double bestScore = 0;
            Rect bestRoi = new Rect();
            double bestPitch = 0, bestAng = 0;
            using (Mat resp = new())
            using (Mat ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(21, 21)))
            {
                Cv2.MorphologyEx(gray, resp, MorphTypes.TopHat, ker);
                for (int y = 0; y + side <= gray.Rows; y += step)
                    for (int x = 0; x + side <= gray.Cols; x += step)
                    {
                        if (BudgetExpired()) return bestRoi;
                        using (Mat win = new Mat(resp, new Rect(x, y, side, side)))
                        {
                            double score = WindowLattice(win, out double pitch, out double ang);
                            if (score <= bestScore) continue;
                            // 相位锁定 + 外推边界（在窗口内做，再换算回全图）
                            Rect local = PhaseExtent(win, pitch, ang, out double conf);
                            if (local.Width < 48 || conf < 0.25) continue;
                            bestScore = score; bestPitch = pitch; bestAng = ang;
                            bestRoi = new Rect(x + local.X, y + local.Y, local.Width, local.Height);
                        }
                    }
            }
            if (bestRoi.Width == 0) return bestRoi;
            // 留静区：向外扩 1 个节距（RS 采样需要符号完整含边框）
            int pad = (int)Math.Max(8, bestPitch * 1.2);
            int bx = Math.Max(0, bestRoi.X - pad), by = Math.Max(0, bestRoi.Y - pad);
            int bw = Math.Min(gray.Cols - bx, bestRoi.Width + 2 * pad);
            int bh = Math.Min(gray.Rows - by, bestRoi.Height + 2 * pad);
            log?.Invoke($"locateFFT pitch={bestPitch:F1} ang={bestAng:F1} roi=({bx},{by},{bw},{bh}) score={bestScore:F1}");
            return new Rect(bx, by, bw, bh);
        }

        /// <summary>窗口内 2D FFT：返回窄带内最强峰的信噪比，并输出该峰对应的节距与角度。
        /// 方格点阵的基频在频率轴/对角线上；只取"对应节距 8~90px"的峰，
        /// 避开图像大结构（面板边框等）占主导的低频。</summary>
        private static double WindowLattice(Mat win, out double pitch, out double ang)
        {
            pitch = 0; ang = 0;
            using (Mat f = new())
            {
                win.ConvertTo(f, MatType.CV_32FC1);
                double mean = Cv2.Mean(f).Val0;
                using (Mat meanM = new Mat(f.Size(), MatType.CV_32FC1, Scalar.All(mean)))
                    Cv2.Subtract(f, meanM, f);
                using (Mat hann = new())
                {
                    Cv2.CreateHanningWindow(hann, f.Size(), MatType.CV_32FC1);
                    Cv2.Multiply(f, hann, f);
                }
                using (Mat zeros = Mat.Zeros(f.Size(), MatType.CV_32FC1))
                using (Mat complex = new())
                using (Mat dft = new())
                {
                    Cv2.Merge(new Mat[] { f, zeros }, complex);
                    Cv2.Dft(complex, dft, DftFlags.ComplexOutput);
                    Mat[] ch = dft.Split();
                    using (Mat mag = new())
                    {
                        Cv2.Magnitude(ch[0], ch[1], mag);
                        int W = mag.Cols, H = mag.Rows, mn = Math.Min(W, H);
                        double sum = 0;
                        int nIn = 0;
                        // 收集带内所有局部峰：方格点阵的基频在对角线上给出强峰（其幅值
                        // 常高于轴基频），但它对应的是"对角次晶格"（节距 = a/√2），
                        // 据此外推只能圈出符号的 ~一半（a114 实测 427 vs 真实 ~800）。
                        // 轴基频 (1/a,0)/(0,1/a) 才是符号真实格距，故在同量级峰里优先取轴向。
                        var cand = new System.Collections.Generic.List<(double V, int X, int Y)>();
                        for (int yy = 0; yy < H; yy++)
                            for (int xx = 0; xx < W; xx++)
                            {
                                int dx = xx <= W / 2 ? xx : xx - W, dy = yy <= H / 2 ? yy : yy - H;
                                if (dx == 0 && dy == 0) continue;
                                double rr = Math.Sqrt((double)dx * dx + (double)dy * dy);
                                double pxc = mn / Math.Max(1e-6, rr);
                                if (pxc < 8 || pxc > 90) continue;   // 只认点阵尺度
                                double v = mag.At<float>(yy, xx);
                                sum += v; nIn++;
                                cand.Add((v, dx, dy));
                            }
                        if (nIn == 0) return 0;
                        double band = sum / nIn;
                        cand.Sort((p, q) => q.V.CompareTo(p.V));
                        double peakV = cand[0].V;
                        int px = cand[0].X, py = cand[0].Y;
                        // 轴向优先：若最强峰是斜向（|dx|,|dy| 都显著），而同量级内存在
                        // 轴向峰（其中一个分量接近 0），改取轴向峰
                        bool bestIsAxis = cand[0].X == 0 || cand[0].Y == 0;
                        if (!bestIsAxis)
                        {
                            foreach ((double v, int dx, int dy) in cand)
                            {
                                if (v < peakV * 0.45) break;
                                if (dx == 0 || dy == 0) { px = dx; py = dy; peakV = v; break; }
                            }
                        }
                        double rf = Math.Sqrt((double)px * px + (double)py * py);
                        if (rf < 1e-6) return 0;
                        pitch = mn / rf;
                        ang = Math.Atan2((double)py, (double)px) * 180 / Math.PI;
                        if (ang < 0) ang += 180;
                        return peakV / Math.Max(1e-6, band);
                    }
                }
            }
        }

        /// <summary>锁定格点相位后沿格点外推符号边界：节点响应显著高于格间水平即"还在码内"，
        /// 塌下去即边界。返回窗口内 ROI 与外推置信度（有效节点占比）。
        /// 两个关键稳健性处理：
        ///   ① 节点采样取邻域最大值（±0.22 节距）而不是单像素——单像素在相位偏
        ///      几分之一格时就落到点与点之间，响应直接塌掉，实测节距差 1.7% 就整体失败；
        ///   ② 节距由"节点对比度最大化"精化——FFT 的频率分辨率是离散 bin，
        ///      直接取 bin 会有 ~1% 误差，16 格累积就是 1/4 格，足以让外推边界跑偏。</summary>
        private static Rect PhaseExtent(Mat resp, double pitch0, double angDeg, out double conf)
        {
            conf = 0;
            // —— ② 节距/角度精化：在 FFT 结果附近搜索"节点响应 - 格间响应"最大者 ——
            double bestP = pitch0, bestA = angDeg, bestV = double.MinValue;
            for (int ai = -2; ai <= 2; ai++)
                for (int pi = -3; pi <= 3; pi++)
                {
                    double pp = pitch0 * (1 + 0.006 * pi);
                    double aa = angDeg + 0.5 * ai;
                    double v = NodeContrast(resp, pp, aa, out _, out _);
                    if (v > bestV) { bestV = v; bestP = pp; bestA = aa; }
                }
            if (bestV <= 0) return new Rect();
            double pitch = bestP, angDeg2 = bestA;
            double v2 = NodeContrast(resp, pitch, angDeg2, out double ou, out double ov);
            if (v2 <= 0) return new Rect();

            double a = angDeg2 * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
            int rad = Math.Max(1, (int)Math.Round(pitch * 0.22));
            int NS(int x, int y)
            {
                int r0 = Math.Max(0, y - rad), r1 = Math.Min(resp.Rows - 1, y + rad);
                int c0 = Math.Max(0, x - rad), c1 = Math.Min(resp.Cols - 1, x + rad);
                int m = 0;
                for (int yy = r0; yy <= r1; yy++)
                    for (int xx = c0; xx <= c1; xx++)
                    {
                        byte v = resp.At<byte>(yy, xx);
                        if (v > m) m = v;
                    }
                return m;
            }
            // 节点 - 格间 的响应差（邻域最大，抗相位小幅偏差）
            double NodeDiff(int kk, int ll)
            {
                double nu = ou + kk * pitch, nv = ov + ll * pitch;
                double nx = (nu * ca) + (nv * -sa), ny = (nu * sa) + (nv * ca);
                if (nx < 0 || ny < 0 || nx >= resp.Cols || ny >= resp.Rows) return double.NaN;
                double mu = nu + (pitch / 2 * ca), mv = nv + (pitch / 2 * -sa);
                double mx = (mu * ca) + (mv * -sa), my = (mu * sa) + (mv * ca);
                double on = NS((int)Math.Round(nx), (int)Math.Round(ny));
                double off = 0;
                int mxi = (int)Math.Round(mx), myi = (int)Math.Round(my);
                if (mxi >= 0 && myi >= 0 && mxi < resp.Cols && myi < resp.Rows) off = NS(mxi, myi);
                return on - off;
            }
            int kmin = (int)Math.Floor(-ou / pitch) - 1,
                kmax = (int)Math.Ceiling((resp.Cols - ou) / pitch) + 1;
            int lmin = (int)Math.Floor(-ov / pitch) - 1,
                lmax = (int)Math.Ceiling((resp.Rows - ov) / pitch) + 1;
            var colN = new System.Collections.Generic.Dictionary<int, int>();
            var rowN = new System.Collections.Generic.Dictionary<int, int>();
            for (int kk = kmin; kk <= kmax; kk++)
                for (int ll = lmin; ll <= lmax; ll++)
                {
                    double d = NodeDiff(kk, ll);
                    if (double.IsNaN(d) || d < 15) continue;
                    colN[kk] = (colN.TryGetValue(kk, out int c1) ? c1 : 0) + 1;
                    rowN[ll] = (rowN.TryGetValue(ll, out int c2) ? c2 : 0) + 1;
                }
            // 取"最长的高密度连续段"：真符号的每一列/行都有大量暗模块（≥45%），
            // 而窗口背景里的零散命中是稀疏的——按比例门槛即可把符号段与背景区分开。
            // 段内允许 ≤2 的空档（丢点/弱模块）。
            bool Span(System.Collections.Generic.Dictionary<int, int> m, out int lo, out int hi)
            {
                lo = 0; hi = -1;
                if (m.Count == 0) return false;
                var ks = m.Keys.OrderBy(v => v).ToList();
                int maxFill = m.Values.Max();
                int need = Math.Max(3, (int)(maxFill * 0.45));
                int bl = 0, bh = -1;
                for (int i = 0; i < ks.Count; i++)
                {
                    if (m[ks[i]] < need) continue;
                    int j = i, gaps = 0;
                    while (j + 1 < ks.Count)
                    {
                        int step = ks[j + 1] - ks[j];
                        if (step <= 2 && m[ks[j + 1]] >= need) { j++; continue; }
                        if (step <= 2 && gaps < 2) { j++; gaps++; continue; }
                        break;
                    }
                    if (ks[j] - ks[i] > bh - bl) { bl = ks[i]; bh = ks[j]; }
                    i = j;
                }
                if (bh < bl) return false;
                lo = bl; hi = bh;
                return true;
            }
            if (!Span(colN, out int k0, out int k1)) return new Rect();
            if (!Span(rowN, out int l0, out int l1)) return new Rect();
            int nk = k1 - k0 + 1, nl = l1 - l0 + 1;
            if (nk < 8 || nl < 8 || nk > 160 || nl > 160) return new Rect();
            // 置信度：①节点对比度（越接近 1 越好）②形状接近方形（Data Matrix 是方的）
            // 实测（a114 已知可解裁片）：正确锁定对比度 ≈5.2，邻近错配 ≤1.9，错角度 ≤1.4。
            // 因此以 2.5 为"可用锁定"基准换算置信度，再乘形状因子。
            double shape = (double)Math.Min(nk, nl) / Math.Max(nk, nl);
            conf = Math.Min(1.0, Math.Max(0.0, (bestV - 1.8) / 2.5)) * shape;
            double X(double nu, double nv) => (nu * ca) + (nv * -sa);
            double Y(double nu, double nv) => (nu * sa) + (nv * ca);
            double u0 = ou + (k0 - 0.5) * pitch, u1 = ou + (k1 + 0.5) * pitch;
            double v0 = ov + (l0 - 0.5) * pitch, v1 = ov + (l1 + 0.5) * pitch;
            double[] xs = { X(u0, v0), X(u1, v0), X(u1, v1), X(u0, v1) };
            double[] ys = { Y(u0, v0), Y(u1, v0), Y(u1, v1), Y(u0, v1) };
            int rx0 = Math.Max(0, (int)Math.Floor(xs.Min()));
            int ry0 = Math.Max(0, (int)Math.Floor(ys.Min()));
            int rx1 = Math.Min(resp.Cols, (int)Math.Ceiling(xs.Max()));
            int ry1 = Math.Min(resp.Rows, (int)Math.Ceiling(ys.Max()));
            if (rx1 - rx0 < 48 || ry1 - ry0 < 48) return new Rect();
            return new Rect(rx0, ry0, rx1 - rx0, ry1 - ry0);
        }

        /// <summary>给定 (节距, 角度) 时的"节点 - 格间"平均对比度，同时输出锁定的相位。</summary>
        private static double NodeContrast(Mat resp, double pitch, double angDeg, out double ou, out double ov)
        {
            ou = 0; ov = 0;
            double a = angDeg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
            double cu = 0, su = 0, cv = 0, sv = 0;
            double thr = Cv2.Mean(resp).Val0 + 8;
            for (int y = 0; y < resp.Rows; y += 2)
                for (int x = 0; x < resp.Cols; x += 2)
                {
                    double w = resp.At<byte>(y, x);
                    if (w < thr) continue;
                    double u = (x * ca) + (y * sa), v = (-x * sa) + (y * ca);
                    cu += w * Math.Cos(2 * Math.PI * u / pitch); su += w * Math.Sin(2 * Math.PI * u / pitch);
                    cv += w * Math.Cos(2 * Math.PI * v / pitch); sv += w * Math.Sin(2 * Math.PI * v / pitch);
                }
            double magU = Math.Sqrt((cu * cu) + (su * su)), magV = Math.Sqrt((cv * cv) + (sv * sv));
            if (magU < 1e-6 || magV < 1e-6) return 0;
            ou = Math.Atan2(su, cu) / (2 * Math.PI) * pitch;
            ov = Math.Atan2(sv, cv) / (2 * Math.PI) * pitch;
            // 对比度 = 圆均值合成向量长度 / 权重和（0~1），越接近 1 说明响应越集中在格点上
            double wsum = 0;
            for (int y = 0; y < resp.Rows; y += 2)
                for (int x = 0; x < resp.Cols; x += 2)
                {
                    double w = resp.At<byte>(y, x);
                    if (w >= thr) wsum += w;
                }
            if (wsum <= 0) return 0;
            return 100.0 * Math.Min(magU, magV) / wsum;
        }



        /// <summary>点级斑块质心（密度/规律性代理，顶帽+Otsu 一次）。
        /// 核尺寸必须匹配点径：核小于点径时码点整体被顶帽响应"填平"、连片后质心落在
        /// 粘连块中点，规律性格点结构消失（a114 的点径 ≈30px，用 k21 即此症状）。</summary>
        private static List<Point2f> DotCentroids(Mat gray, int kerSize = 21)
        {
            var pts = new List<Point2f>();
            using (Mat resp = new())
            using (Mat ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(kerSize, kerSize)))
            using (Mat bin = new())
            using (Mat labels = new())
            using (Mat stats = new())
            using (Mat cents = new())
            {
                Cv2.MorphologyEx(gray, resp, MorphTypes.TopHat, ker);
                Cv2.Threshold(resp, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                int nc = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cents,
                    PixelConnectivity.Connectivity4, MatType.CV_32SC1);
                for (int i = 1; i < nc; i++)
                {
                    double a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (a < 12 || a > 4000) continue;
                    pts.Add(new Point2f((float)cents.At<double>(i, 0), (float)cents.At<double>(i, 1)));
                }
            }
            return pts;
        }

        /// <summary>候选码点密度最高的若干正方形窗口（降序、互不重叠）：
        /// 用一次顶帽+Otsu 的连通域质心当密度代理，比逐窗重跑管线便宜两个数量级。</summary>
        private static List<Rect> DenseWindows(Mat gray, int side, int maxWins,
            out List<(int Count, Rect R)> allWindows)
        {
            allWindows = new List<(int Count, Rect R)>();
            var res = new List<Rect>();
            List<Point2f> pts = DotCentroids(gray);
            if (pts.Count < MinFitPoints) return res;

            // 步长取 1/4 边长（75% 重叠）：符号尺寸常常接近窗口本身，窗口必须"码完整
            // 落在内且四周留出静区"才解得出。实测 a114 的码约 800×800 = 一整个窗口，
            // 步长 1/3 时相邻窗一个切右一个切左、全部失败，步长 1/4 才能命中像
            // (1000,2100) 那样对齐良好的窗口。
            int step = Math.Max(16, side / 4);
            // 全覆盖网格（含边界对齐），保证码无论落在哪都被某个窗口完整包含
            var grid = new List<Rect>();
            for (int y = 0; y < gray.Rows; y += step)
                for (int x = 0; x < gray.Cols; x += step)
                {
                    int w = Math.Min(side, gray.Cols - x), h = Math.Min(side, gray.Rows - y);
                    int x0 = Math.Max(0, gray.Cols - side), y0 = Math.Max(0, gray.Rows - side);
                    grid.Add(new Rect(Math.Min(x, x0), Math.Min(y, y0),
                        Math.Min(side, gray.Cols), Math.Min(side, gray.Rows)));
                }
            // 点数积分图：任意窗口密度 O(1) 查询（逐窗扫点在 8MP 上要几百万次比较）
            // 掩膜必须是 8U（cv::integral 只吃 8U/16U/32F/64F），积分和用 32S：
            // 每点置 1，窗口内点数为 4 个角点的和差
            using (Mat mask = new Mat(gray.Rows, gray.Cols, MatType.CV_8UC1, Scalar.All(0)))
            {
                foreach (Point2f p in pts)
                {
                    int px = Math.Clamp((int)p.X, 0, gray.Cols - 1);
                    int py = Math.Clamp((int)p.Y, 0, gray.Rows - 1);
                    mask.Set(py, px, (byte)1);
                }
                using (Mat ii = new())
                {
                    Cv2.Integral(mask, ii, MatType.CV_32SC1);
                    int Count(Rect r)
                    {
                        int x1 = Math.Min(gray.Cols, r.X + r.Width), y1 = Math.Min(gray.Rows, r.Y + r.Height);
                        return ii.At<int>(y1, x1) - ii.At<int>(r.Y, x1) - ii.At<int>(y1, r.X) + ii.At<int>(r.Y, r.X);
                    }
                    var seen = new HashSet<long>();
                    var merged = new List<(int Count, Rect R)>();
                    foreach (Rect r in grid)
                    {
                        long key = ((long)r.X << 32) ^ (uint)r.Y;
                        if (!seen.Add(key)) continue;
                        merged.Add((Count(r), r));
                    }
                    merged.Sort((a, b) => b.Count.CompareTo(a.Count));
                    allWindows = merged;
                    foreach ((int c, Rect r) in merged)
                    {
                        if (res.Count >= maxWins) break;
                        if (c < MinFitPoints) break;
                        bool overlap = false;
                        foreach (Rect o in res)
                            if (r.Intersect(o).Width > r.Width / 3 && r.Intersect(o).Height > r.Height / 3) { overlap = true; break; }
                        if (!overlap) res.Add(r);
                    }
                }
            }
            return res;
        }

        /// <summary>开始一次搜索预算（墙钟）</summary>
        private static void StartBudget(int budgetMs)
        {
            _searchStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _searchDeadline = _searchStart + (System.Diagnostics.Stopwatch.Frequency * budgetMs / 1000);
        }

        /// <summary>搜索预算是否已耗尽</summary>
        private static bool BudgetExpired()
            => _searchDeadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > _searchDeadline;

        private static bool TryAll(Mat img, string branch, double darkRatio, double minSharp, int minPitch,
            Action<string> log, Action<bool[,], string> matLog, out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            if (_searchDeadline == 0) StartBudget(SearchBudgetMs);   // 供直接调用 TryAll 的诊断路径兜底
            foreach (MorphTypes hat in new[] { MorphTypes.TopHat, MorphTypes.BlackHat })
                foreach (int ker in HatKernels)
                    if (TryHat(img, hat, ker, branch, darkRatio, minSharp, minPitch, log, matLog, out text, out quad))
                        return true;
            return false;
        }

        // 大尺度光照拉平：大核高斯估计背景亮度场，除法归一化到 128 基准。
        // 核边长取短边 1/6（≥31 且为奇数）：远大于节距才能只留光照、不抹掉码点。
        private static Mat FlattenIllumination(Mat gray)
        {
            int k = Math.Max(31, Math.Min(gray.Cols, gray.Rows) / 6) | 1;
            using (Mat bg = new())
            {
                Cv2.GaussianBlur(gray, bg, new OpenCvSharp.Size(k, k), 0);
                // 背景近黑处除法会把噪声放大成假点，亮度场先钳到 ≥8
                using (Mat lo = new Mat(bg.Size(), MatType.CV_8UC1, new Scalar(8)))
                    Cv2.Max(bg, lo, bg);
                Mat norm = new();
                Cv2.Divide(gray, bg, norm, 128.0);
                return norm;
            }
        }

        // 同一 (极性, 核) 组合的形态学只做一次，多个阈值档共享顶帽图——
        // MorphologyEx 是全管线最贵的操作，按阈值档重复调用会让大图全档穷举耗时翻倍。
        // resp 为形态学响应（灰度，供格点级自适应判暗），bin 为全局阈值二值图（仅供质心提取）。
        private static bool TryHat(Mat gray, MorphTypes hat, int kerSize, string branch, double darkRatio,
            double minSharp, int minPitch, Action<string> log, Action<bool[,], string> matLog, out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            // 逐组合检查预算：形态学/连通域/距离变换在 8MP 实拍图上是"秒级"开销，
            // 只在拟合处检查会让无码图跑满 32 组 × 双点集（实测分钟级）
            if (BudgetExpired())
            {
                log?.Invoke($"[{branch}/{hat}/k{kerSize}] search-budget exhausted");
                return false;
            }
            // 结构元保留椭圆（与打点的圆斑形状匹配，换成方窗实测会在旋转合成用例上误检）；
            // 椭圆核不可分离、形态学是 O(像素 × k²)，因此大核只在大图穷举时才贵——
            // 逐组合的预算检查 + 搜索墙钟上限把这一路的最坏耗时兜住（见 BudgetExpired）。
            using (Mat resp = new())
            using (Mat ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(kerSize, kerSize)))
            using (Mat bin = new())
            {
                Cv2.MorphologyEx(gray, resp, hat, ker);
                double otsu = Cv2.Threshold(resp, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                foreach (double thScale in ThresholdScales)
                {
                    // thScale=1.0 档直接用上面 Otsu 的结果；其余档复用同一顶帽图重阈值化。
                    // 注意条件必须是"≠1.0"而不是"<1.0"——后者会把**升档**（分粘连点用）
                    // 静默吃掉，等于升档从未生效（本项修复前就是如此）。
                    // 降档捞低对比浅打点；升档分开在 Otsu 下粘成一片的点，
                    // 两者引入的噪斑都由连通域过滤/最大簇/网格拟合/RS+边框校验层层兜底。
                    if (Math.Abs(thScale - 1.0) > 0.001)
                        Cv2.Threshold(resp, bin, Math.Max(1.0, otsu * thScale), 255, ThresholdTypes.Binary);
                    if (TryFromBinary(resp, bin, kerSize, darkRatio, minSharp, minPitch,
                        s => log?.Invoke($"[{branch}/{hat}/k{kerSize}/t{thScale:0.00}] {s}"),
                        (mm, tag) => matLog?.Invoke(mm, $"[{branch}/{hat}/k{kerSize}/t{thScale:0.00}] {tag}"),
                        out text, out quad))
                        return true;
                }
            }
            return false;
        }

        private static bool TryFromBinary(Mat resp, Mat bin, int kerSize, double darkRatio, double minSharp, int minPitch,
            Action<string> log, Action<bool[,], string> matLog, out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            if (BudgetExpired()) return false;

            // —— 候选码点点集 ——
            // 面积窗上限随核尺寸放大：大核服务大点阵，固定 900 会把整个码点排除在外；
            // 下限 6px：小节距点阵（节距 6~10px，点面积 10~30px）是高分辨率金属打点的常态，
            // 原下限 30 会把它们整体滤除；1~4px 噪斑仍被挡在门外。
            // 这里不再用"面积中位数"过滤：实拍图里噪斑数量远多于码点，中位数被拉低到
            // 噪斑尺度，真码点会被 2.5×中位数上限整体剔除（节距假设阶段按 p² 重新筛选）。
            int areaMax = Math.Max(900, (int)(kerSize * kerSize * 0.6));

            // 点集 1：连通域质心（点与点分离良好的常规情形，快路径）
            var pts = new List<Point2f>();
            var areas = new List<double>();
            ExtractCentroids(bin, areaMax, pts, areas);
            log?.Invoke($"cand={pts.Count}");
            if (pts.Count > MaxPoints) return false;   // 纯噪点集：后续全是无效穷举
            if (TryPointSet(resp, pts, areas, darkRatio, minSharp, minPitch, log, matLog, out text, out quad))
                return true;

            // 点集 2：粘连链按距离变换峰拆分（点径接近节距、相邻点连成链的高填充打点）
            var pts2 = new List<Point2f>();
            var areas2 = new List<double>();
            ExtractSplit(bin, areaMax, pts2, areas2);
            if (pts2.Count == pts.Count) return false;
            log?.Invoke($"candSplit={pts2.Count}");
            if (pts2.Count > MaxSplitPoints) return false;   // 噪声主导，跳过（最贵的一路）
            return TryPointSet(resp, pts2, areas2, darkRatio, minSharp, minPitch, log, matLog, out text, out quad);
        }

        /// <summary>对一组候选码点做"节距多假设 → 最大簇 → 网格拟合 → 采样 → RS"完整搜索</summary>
        private static bool TryPointSet(Mat resp, List<Point2f> pts, List<double> areas, double darkRatio,
            double minSharp, int minPitch, Action<string> log, Action<bool[,], string> matLog,
            out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            if (pts.Count < MinFitPoints) return false;

            // —— 节距多假设：最近邻距离直方图的各峰值（含众数），按计数降序逐个尝试 ——
            // 全图杂散纹理（针脚/焊盘/文字/划痕）会给出一个偏小的最近邻众数；单一众数下
            // 最大簇阈值 2.3p 太小，码点云被切成碎片 —— 必须把真节距也纳入候选。
            var nn = new List<double>();
            NearestNeighborDistances(pts, NnMaxRadius, nn);
            List<double> pitches = PitchCandidates(nn, minPitch, log);
            if (pitches.Count == 0) return false;

            foreach (double p0 in pitches)
            {
                // 点面积与节距平方同量级：按假设节距筛出真正的码点
                double lo = DotAreaLoRatio * p0 * p0, hi = DotAreaHiRatio * p0 * p0;
                var dotPts = new List<Point2f>();
                for (int i = 0; i < pts.Count; i++)
                    if (areas[i] >= lo && areas[i] <= hi) dotPts.Add(pts[i]);
                if (dotPts.Count < MinFitPoints)
                {
                    log?.Invoke($"p={p0:F1} dots={dotPts.Count} < min");
                    continue;
                }
                if (TryFitDecode(resp, dotPts, p0, darkRatio, minSharp, minPitch, log, matLog, out text, out quad))
                    return true;
            }
            return false;
        }

        /// <summary>给定节距假设下的完整拟合+采样+解码：最大簇 → 全角度搜向 → LS 精化 → 采样 → RS</summary>
        private static bool TryFitDecode(Mat resp, List<Point2f> centersIn, double pitch0, double darkRatio,
            double minSharp, int minPitch, Action<string> log, Action<bool[,], string> matLog,
            out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            if (pitch0 < minPitch) return false;
            // 搜索超时即收手：保证最坏耗时（无码图/极难图）有界
            if (BudgetExpired())
            {
                log?.Invoke("search-budget exhausted");
                return false;
            }

            double pitch = pitch0;
            // —— 孤点剔除：码区外的孤立亮斑会把最小二乘网格范围撑爆（nk/nl 失配）。
            // 以 2.3×节距 为链接阈值取最大连通簇：实点最远与邻点隔 2×节距（稀疏点阵），
            // 仍须保留；孤斑（截图边框/文字/金属划痕）则被整体丢弃。 ——
            List<Point2f> centers = LargestCluster(centersIn, 2.3 * pitch);
            log?.Invoke($"p={pitch:F1} cluster={centers.Count}");
            if (centers.Count < MinFitPoints) return false;

            // —— 角度+节距：投影圆均值锐度联合搜索。金属件摆放朝向任意，原 ±8° 搜角
            // 无法覆盖；网格具 90° 旋转对称，[0,90°) 粗搜+细搜即可覆盖全部朝向。
            // 节距联合扫描：众数估计在插值/丢点/噪声下可偏 ±7%，偏差经 16 格累积
            // >0.5 格会让 LS round 错列（16×16 拟成 17×17），锐度峰直接锁定真实节距 ——
            if (!FindAngle(centers, pitch, minSharp, out double bestAng, out double gridPitch, out double bestScore))
            {
                log?.Invoke($"angle={bestAng:F2} sharp={bestScore:F2} < min");
                return false;
            }
            log?.Invoke($"angle={bestAng:F2} pitch={gridPitch:F2} sharp={bestScore:F2}");

            // —— 精化：整数格点固定后最小二乘（3 轮）；尺寸不符时裁边缘离群点重试 ——
            // 码区外残余杂点会多占行列、把范围撑出支持尺寸（如 16×16 拟成 17×16）；
            // 裁掉点最少的边缘行/列后重拟合，最多 3 轮。
            double aR = bestAng * Math.PI / 180;
            double ux = Math.Cos(aR), uy = Math.Sin(aR), vx = -uy, vy = ux;
            int m = centers.Count;
            var au = new double[m];
            var av = new double[m];
            for (int i = 0; i < m; i++)
            {
                au[i] = (centers[i].X * ux) + (centers[i].Y * uy);
                av[i] = (centers[i].X * vx) + (centers[i].Y * vy);
            }
            var alive = Enumerable.Range(0, m).ToList();
            var k = new int[m];
            var l = new int[m];
            // 初相取投影最小值：au.Min() 让最靠边的点成为 k=0，避免 ou=0 时
            // 半格相位的点阵（如格点在 7+14k）round 出重号/跳号、LS 锁死错晶格；
            // 节距初值用锐度联合扫描的精化值（比众数估计准，防远端格点错列）
            double pu = gridPitch, pv = gridPitch, ou = au.Min(), ov = av.Min();
            double rad = SampleRadiusRatio * Math.Min(pu, pv);
            var triedSizes = new HashSet<long>();

            // —— 尺寸搜索：最少二乘把格点相位锁死后，"外沿多一圈杂点"会把范围撑成
            // 17×16 这类非受支持尺寸。原实现按"最稀疏边缘逐条裁掉"直到尺寸受支持，
            // 但重拟合会让相位微移、一次裁掉两条线（16×17 → 14×16），把正确的 16×16
            // 直接跳过。现改为：每轮在当前晶格上枚举"各边裁 0~2 线"得到的受支持尺寸，
            // 逐个采样+RS 校验（RS 极快，命中即返回）；都不中再按原逻辑裁边重拟合。 ——
            int kmin = 0, kmax = 0, lmin = 0, lmax = 0;
            for (int outer = 0; outer < 4; outer++)
            {
                for (int it = 0; it < 3; it++)
                {
                    foreach (int i in alive)
                    {
                        k[i] = (int)Math.Round((au[i] - ou) / pu);
                        l[i] = (int)Math.Round((av[i] - ov) / pv);
                    }
                    FitLS(au, k, alive, ref pu, ref ou);
                    FitLS(av, l, alive, ref pv, ref ov);
                }
                // —— 内点重拟合（trimmed LS）：最大簇里仍混着码区外的杂点（针脚/焊盘/丝印），
                // 它们照样被 round 成整数格号，但残差大，会把最小二乘的节距/原点拖偏。
                // 实测 a114：16×16 的码被拟成 18×17、簇内 333 点（真码点仅 ~146），
                // 六种裁剪全 RS 失败——问题不在裁剪方向，而在晶格本身被杂点带偏。
                // 按残差剔离群点后重拟合，使节距精度与杂点无关。 ——
                RefitInliers(au, k, ref pu, ref ou, ref alive, m);
                RefitInliers(av, l, ref pv, ref ov, ref alive, m);
                for (int it = 0; it < 2; it++)
                {
                    foreach (int i in alive)
                    {
                        k[i] = (int)Math.Round((au[i] - ou) / pu);
                        l[i] = (int)Math.Round((av[i] - ov) / pv);
                    }
                    FitLS(au, k, alive, ref pu, ref ou);
                    FitLS(av, l, alive, ref pv, ref ov);
                }

                kmin = int.MaxValue; kmax = int.MinValue;
                lmin = int.MaxValue; lmax = int.MinValue;
                foreach (int i in alive)
                {
                    if (k[i] < kmin) kmin = k[i];
                    if (k[i] > kmax) kmax = k[i];
                    if (l[i] < lmin) lmin = l[i];
                    if (l[i] > lmax) lmax = l[i];
                }
                log?.Invoke($"grid={kmax - kmin + 1}x{lmax - lmin + 1} alive={alive.Count}");
                rad = SampleRadiusRatio * Math.Min(pu, pv);
                if (TryExtents(resp, centers, alive, ou, ov, pu, pv, ux, uy, vx, vy,
                        kmin, kmax, lmin, lmax, rad, darkRatio, triedSizes, log, matLog, out text, out quad))
                    return true;

                // 四条边缘行/列中点最少的一条整体裁掉（≤2 点才算离群边）
                int nKmin = 0, nKmax = 0, nLmin = 0, nLmax = 0;
                foreach (int i in alive)
                {
                    if (k[i] == kmin) nKmin++;
                    if (k[i] == kmax) nKmax++;
                    if (l[i] == lmin) nLmin++;
                    if (l[i] == lmax) nLmax++;
                }
                int best = Math.Min(Math.Min(nKmin, nKmax), Math.Min(nLmin, nLmax));
                if (best > 2) { log?.Invoke("edge-trim impossible"); return false; }
                alive.RemoveAll(i =>
                    (best == nKmin && k[i] == kmin) || (best == nKmax && k[i] == kmax) ||
                    (best == nLmin && l[i] == lmin) || (best == nLmax && l[i] == lmax));
                if (alive.Count < MinFitPoints) return false;
            }
            return false;
        }

        /// <summary>在当前晶格上枚举"各边裁 0~2 线"得到的受支持符号尺寸，逐个采样+RS 校验。
        /// 大符号（&gt;64×64 格）只试原尺寸与单边裁 1 线，避免组合爆炸</summary>
        private static bool TryExtents(Mat resp, List<Point2f> centers, List<int> alive,
            double ou, double ov, double pu, double pv, double ux, double uy, double vx, double vy,
            int kmin, int kmax, int lmin, int lmax, double rad, double darkRatio,
            HashSet<long> triedSizes, Action<string> log, Action<bool[,], string> matLog,
            out string text, out Point2f[] quad)
        {
            text = null;
            quad = null;
            int nk = kmax - kmin + 1, nl = lmax - lmin + 1;
            bool big = nk * nl > 4096;
            // 各边裁/扩 -2..+2 线：**必须同时允许"扩"**。拟合出的晶格常比真符号小一圈
            // （最外圈边框点因邻居不足被排除、或边框点弱），此时正确尺寸要靠向外长出来；
            // 原实现只减不加，18×17 永远变不成 18×18，正确尺寸根本进不了候选（a114 即此）。
            var cand = new List<(int A, int B, int C, int D, int Area)>();
            for (int a = -2; a <= 2; a++)
                for (int b = -2; b <= 2; b++)
                    for (int c = -2; c <= 2; c++)
                        for (int d = -2; d <= 2; d++)
                        {
                            if (big && (Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d)) > 1) continue;
                            int nkk = nk - a - b, nll = nl - c - d;
                            if (nkk < 8 || nll < 8) continue;
                            if (!Ecc200Decoder.Supports(nkk, nll)) continue;
                            cand.Add((a, b, c, d, nkk * nll));
                        }
            // —— 用 ECC200 边框几何给各候选尺寸打分，再按分数降序做 RS ——
            // 只按"保留格点数最多"排序是不够的：16×17 裁到 16×16 时，
            // 从上边裁（错，削掉真正的时序边）与从下边裁（对）面积完全相同，
            // 排序不稳定就会挑错边，采样矩阵整体错开一行、RS 必然失败。
            // 真实符号必满足"左列/下行实心 + 上行/右列交替"（ISO 16022 定位边+时序边），
            // 逐候选用该判据打分即可直接锁定正确的裁剪方向；朝向未知 → 8 个二面体取最优。
            // 先按"格点数多"排序再截断：截断必须发生在排序之后。
            // 原实现按嵌套循环的枚举顺序取前 24 个，扩边引入负偏移后，枚举顺序从
            // a=b=c=-2 开始，前 24 个全挤在角落，18×18 这种关键候选根本进不了打分集。
            cand.Sort((x, y) => y.Area.CompareTo(x.Area));
            var scored = new List<(double Score, int A, int B, int C, int D, int Area, bool[,] Mat)>();
            foreach ((int a, int b, int c, int d, int _) in cand)
            {
                if (scored.Count >= MaxExtentCandidates) break;
                int k0 = kmin + a, k1 = kmax - b, l0 = lmin + c, l1 = lmax - d;
                long key = ((long)k0 << 42) ^ ((long)k1 << 28) ^ ((long)l0 << 14) ^ (uint)l1;
                if (!triedSizes.Add(key)) continue;
                double ou2 = ou + (a * pu), ov2 = ov + (c * pv);
                bool[,] mat = SampleGrid(resp, centers, alive, ou2, ov2, pu, pv, ux, uy, vx, vy,
                    k0, k1, l0, l1, rad, darkRatio, out _);
                scored.Add((BorderScore(mat), a, b, c, d, (k1 - k0 + 1) * (l1 - l0 + 1), mat));
            }
            // 排序：先"格点多（尺寸大）"，面积相同时再用边框吻合率打破平局。
            // 真符号几乎总是当前晶格下最大的受支持尺寸——保持这个优先级才不会回归；
            // 而"16×17 到底从上边裁还是下边裁"是同面积之争，只能靠边框几何区分（a114 即此）。
            // 边框分不做硬门槛：实拍打点常有弱边/丢点，真符号也可能只有 0.7~0.8，
            // 卡门槛会把正确尺寸直接判死（a1/a113 实测 0.81/0.77），最终裁决仍交给 RS。
            // 排序：① 原样（各边都不裁不扩）永远第一个试——拟合尺寸本身就是最可能的答案，
            // 扩边候选引入后若仍按面积优先，20×20/18×18 会先吃掉有限的尝试名额，
            // 把正确的原尺寸挤出候选（a114 实测）；② 其余按格点数多优先（真符号通常
            // 是受支持尺寸里较大的那个）；③ 同面积再按 ECC200 边框吻合率打破平局。
            scored.Sort((x, y) =>
            {
                bool xi = x.A == 0 && x.B == 0 && x.C == 0 && x.D == 0;
                bool yi = y.A == 0 && y.B == 0 && y.C == 0 && y.D == 0;
                if (xi != yi) return xi ? -1 : 1;
                return x.Area != y.Area ? y.Area.CompareTo(x.Area) : y.Score.CompareTo(x.Score);
            });
            int tried = 0;
            foreach ((double score, int a, int b, int c, int d, int _, bool[,] mat) in scored)
            {
                if (tried++ >= MaxExtentTries) break;
                // 边框吻合率过低说明采样矩阵与真符号没对齐，RS 即使在上面"解出"也是
                // 撞上的假阳性（a2 实测 border=0.32 解出 !090673）——直接跳过
                if (score < BorderMinScore) { log?.Invoke($"skip 边框={score:F2}"); continue; }
                int k0 = kmin + a, k1 = kmax - b, l0 = lmin + c, l1 = lmax - d;
                double ou2 = ou + (a * pu), ov2 = ov + (c * pv);
                log?.Invoke($"try {k1 - k0 + 1}x{l1 - l0 + 1} border={score:F2}");
                matLog?.Invoke(mat, "sample");
                if (Ecc200Decoder.TryDecode(mat, out text, out int orient))
                {
                    quad = ExtentQuad(ou2, ov2, k0, k1, l0, l1, ux, uy, vx, vy, pu, pv);
                    return true;
                }
                // 低对比浅打点：判暗阈值放宽一档重采样重试
                bool[,] relaxed = SampleGrid(resp, centers, alive, ou2, ov2, pu, pv, ux, uy, vx, vy,
                    k0, k1, l0, l1, rad, darkRatio * 0.6, out _);
                matLog?.Invoke(relaxed, "sample-relaxed");
                if (Ecc200Decoder.TryDecode(relaxed, out text, out int orient2))
                {
                    log?.Invoke("ok(relaxed)");
                    quad = ExtentQuad(ou2, ov2, k0, k1, l0, l1, ux, uy, vx, vy, pu, pv);
                    return true;
                }
                log?.Invoke("rs-fail");
            }
            return false;
        }

        /// <summary>ECC200 边框吻合率（0~1）：左列与下行必须实心（定位边），
        /// 上行与右列必须交替（时序边，偶数位为暗）。符号朝向未知，
        /// 对 8 个二面体变换取最优吻合率——只有采样网格与真实符号对齐时才会接近 1。</summary>
        private static double BorderScore(bool[,] m)
        {
            double best = 0;
            bool[,] cur = m;
            for (int k = 0; k < 4; k++)
            {
                best = Math.Max(best, CanonicalBorderScore(cur));
                best = Math.Max(best, CanonicalBorderScore(FlipHorizontal(cur)));
                if (best >= 1.0) return best;
                cur = Rotate90(cur);
            }
            return best;
        }

        private static double CanonicalBorderScore(bool[,] m)
        {
            int cols = m.GetLength(0), rows = m.GetLength(1);
            if (rows < 8 || cols < 8) return 0;
            int hit = 0, tot = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    bool isBorder = r == 0 || r == rows - 1 || c == 0 || c == cols - 1;
                    if (!isBorder) continue;
                    bool want;
                    if (c == 0) want = true;                    // 左列实心
                    else if (r == rows - 1) want = true;        // 下行实心
                    else if (r == 0) want = (c & 1) == 0;       // 上行时序
                    else want = (r & 1) == 0;                   // 右列时序
                    if (m[c, r] == want) hit++;
                    tot++;
                }
            return tot == 0 ? 0 : (double)hit / tot;
        }

        private static bool[,] Rotate90(bool[,] m)
        {
            int cols = m.GetLength(0), rows = m.GetLength(1);
            var r = new bool[rows, cols];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    r[y, cols - 1 - x] = m[x, y];
            return r;
        }

        private static bool[,] FlipHorizontal(bool[,] m)
        {
            int cols = m.GetLength(0), rows = m.GetLength(1);
            var r = new bool[cols, rows];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    r[cols - 1 - x, y] = m[x, y];
            return r;
        }

        /// <summary>符号网格外沿四角（绝对格点 k0-0.5 … k1+0.5，含半格余量）</summary>
        private static Point2f[] ExtentQuad(double ou, double ov, int k0, int k1, int l0, int l1,
            double ux, double uy, double vx, double vy, double pu, double pv)
        {
            return
            [
                GridPoint(ou, ov, k0 - 0.5, l0 - 0.5, ux, uy, vx, vy, pu, pv),
                GridPoint(ou, ov, k1 + 0.5, l0 - 0.5, ux, uy, vx, vy, pu, pv),
                GridPoint(ou, ov, k1 + 0.5, l1 + 0.5, ux, uy, vx, vy, pu, pv),
                GridPoint(ou, ov, k0 - 0.5, l1 + 0.5, ux, uy, vx, vy, pu, pv)
            ];
        }

        private static Point2f GridPoint(double ou, double ov, double kk, double ll,
            double ux, double uy, double vx, double vy, double pu, double pv)
        {
            double su = ou + (kk * pu), sv = ov + (ll * pv);
            return new Point2f((float)((su * ux) + (sv * vx)), (float)((su * uy) + (sv * vy)));
        }

        // 码点点集提取 A：连通域质心（点与点分离良好时的常规路径，与旧版行为一致）。
        // 码点近似圆斑；金属拉丝/划痕/边缘粘连链是长条（长短轴比 > 3），整体剔除。
        private static void ExtractCentroids(Mat bin, int areaMax, List<Point2f> pts, List<double> areas)
        {
            using (Mat labels = new())
            using (Mat stats = new())
            using (Mat cents = new())
            {
                int nc = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cents,
                    PixelConnectivity.Connectivity4, MatType.CV_32SC1);
                for (int i = 1; i < nc; i++)
                {
                    double a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (a < 6 || a > areaMax) continue;
                    if (!AspectOk(stats, i)) continue;
                    pts.Add(new Point2f((float)cents.At<double>(i, 0), (float)cents.At<double>(i, 1)));
                    areas.Add(a);
                }
            }
        }

        // 填连通域内孔：打点实拍常见"亮点环 + 暗心"（凹陷边缘高光、中心背光），
        // 环状连通域的距离变换峰落在环中线上、且一圈会碎成多个峰，点心反而无峰。
        // 只用轮廓层级里"带父轮廓"的内轮廓填白，不合并彼此分离的域 ——
        // 闭运算会连片，小节距码（点径≈节距）会被整片糊掉。
        private static void FillHoles(Mat bin, Mat filled)
        {
            // 泛洪填孔（O(像素)）：取反后从"外框外一圈"泛洪清掉外背景，
            // 仍为 255 的即被前景包住的内孔，再或回前景。
            // 不用 FindContours + 逐轮廓 DrawContours：噪点图上内孔可达十万级，
            // 逐轮廓填充（含 P/Invoke 与光栅化）会把单次提取拖到分钟级。
            const byte White = 255;
            using (Mat inv = new())
            using (Mat pad = new())
            using (Mat mask = new())
            {
                Cv2.BitwiseNot(bin, inv);
                Cv2.CopyMakeBorder(inv, pad, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(White));
                mask.Create(pad.Rows + 2, pad.Cols + 2, MatType.CV_8UC1);
                mask.SetTo(Scalar.All(0));
                Cv2.FloodFill(pad, mask, new OpenCvSharp.Point(0, 0), Scalar.All(0));
                using (Mat inner = new Mat(pad, new Rect(1, 1, bin.Cols, bin.Rows)))
                    Cv2.BitwiseOr(bin, inner, filled);
            }
        }

        // 码点点集提取 B：粘连链按"距离变换局部极大"拆分。
        // 点径接近节距时（金属打点高填充率）相邻点连成链，整条链并成一个连通域：
        // 质心落在链中点、最近邻距离塌到碎片尺度，节距直方图直接失准（实拍大图漏检主因）。
        // 距离变换在每个点中心形成峰，峰值距离 ≈ 该点半径 → 面积按 πr² 估算，
        // 供节距假设的面积窗筛选。是否真"粘连多个点"用面积比判据（ca ≫ π·rmax²），
        // 不按峰数——不规则单点也会出现多个距离变换峰。
        private static void ExtractSplit(Mat bin, int areaMax, List<Point2f> pts, List<double> areas)
        {
            using (Mat filled = new())
            using (Mat dist = new())
            using (Mat dil = new())
            using (Mat k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
            using (Mat peakMask = new())
            using (Mat minR = new())
            using (Mat minR8 = new())
            using (Mat labels = new())
            using (Mat stats = new())
            using (Mat cents = new())
            using (Mat pLabels = new())
            using (Mat pStats = new())
            using (Mat pCents = new())
            {
                FillHoles(bin, filled);
                Cv2.DistanceTransform(filled, dist, DistanceTypes.L2, DistanceTransformMasks.Mask5);
                Cv2.Dilate(dist, dil, k3);
                Cv2.Compare(dist, dil, peakMask, CmpTypes.GE);              // 3×3 局部极大（CV_8U 掩膜）
                // threshold 的输出类型跟随源图（CV_32F），必须转 CV_8U 才能与掩膜按位与
                Cv2.Threshold(dist, minR, MinDotRadius, 255, ThresholdTypes.Binary);
                minR.ConvertTo(minR8, MatType.CV_8UC1);
                Cv2.BitwiseAnd(peakMask, minR8, peakMask);
                int np = Cv2.ConnectedComponentsWithStats(peakMask, pLabels, pStats, pCents,
                    PixelConnectivity.Connectivity8, MatType.CV_32SC1);

                int nc = Cv2.ConnectedComponentsWithStats(filled, labels, stats, cents,
                    PixelConnectivity.Connectivity4, MatType.CV_32SC1);

                // 峰 → 所属连通域
                var perComp = new Dictionary<int, List<int>>();
                for (int i = 1; i < np; i++)
                {
                    int x = (int)Math.Round(pCents.At<double>(i, 0));
                    int y = (int)Math.Round(pCents.At<double>(i, 1));
                    x = Math.Clamp(x, 0, filled.Cols - 1);
                    y = Math.Clamp(y, 0, filled.Rows - 1);
                    int cid = labels.At<int>(y, x);
                    if (cid <= 0) continue;
                    if (!perComp.TryGetValue(cid, out List<int> lst)) perComp[cid] = lst = new List<int>(2);
                    lst.Add(i);
                }

                for (int c = 1; c < nc; c++)
                {
                    double ca = stats.At<int>(c, (int)ConnectedComponentsTypes.Area);
                    if (ca < 6) continue;
                    perComp.TryGetValue(c, out List<int> pk);
                    // 单连通域内是否真"粘连多个点"：面积极显著大于"单个点应有的圆面积"
                    // π·rmax²（rmax = 域内最大距离变换值 ≈ 点半径）才判为粘连链。
                    // 不规则单点也可能出现多个距离变换峰，看峰数会把一个点拆成两个，
                    // 因此必须用面积比判据，而不是峰数。
                    float rmax = 0;
                    if (pk != null)
                        foreach (int i in pk)
                            rmax = Math.Max(rmax, dist.At<float>((int)Math.Round(pCents.At<double>(i, 1)), (int)Math.Round(pCents.At<double>(i, 0))));
                    bool merged = pk != null && pk.Count >= 2 && ca > 1.6 * Math.PI * rmax * rmax;
                    if (!merged)
                    {
                        // 孤立圆斑：沿用连通域质心 + 原面积/长宽比判据（与旧版行为一致）
                        if (ca > areaMax) continue;
                        if (!AspectOk(stats, c)) continue;
                        pts.Add(new Point2f((float)cents.At<double>(c, 0), (float)cents.At<double>(c, 1)));
                        areas.Add(ca);
                        continue;
                    }
                    // 粘连链：按峰逐个拆成独立码点，面积按 πr² 估
                    foreach (int i in pk)
                    {
                        float x = (float)pCents.At<double>(i, 0), y = (float)pCents.At<double>(i, 1);
                        float r = dist.At<float>((int)Math.Round(y), (int)Math.Round(x));
                        double a = Math.PI * r * r;
                        if (a < 6 || a > areaMax) continue;
                        pts.Add(new Point2f(x, y));
                        areas.Add(a);
                    }
                }
            }
        }

        // 连通域外接框长短轴比 ≤ 3 才算"圆斑"码点（小点经旋转插值略扁，阈值放宽松）
        private static bool AspectOk(Mat stats, int i)
        {
            double w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            double h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            return Math.Max(w, h) <= 3.0 * Math.Max(1.0, Math.Min(w, h));
        }

        // 全角度网格搜向+节距精化：[0,90°) 2° 步长粗搜定位主峰，再主峰 ±1.75° 内
        // 0.25° 细搜；节距在给定初值 ±6% 内联合扫描（粗 3% 步长 5 档，细 ±3% 步长 1.5%）。
        // 锐度 = 投影圆均值合成向量长度（两轴之和，网格对齐 → 接近 2，上限 2），
        // 只有角度与节距同时对齐才出高峰——节距偏差会让远端格点相位累积失锁、锐度塌掉。
        // 总计算 ≈ 285+75 次 Sharpness（每次 O(n)），毫秒级。返回 false 表示
        // 最佳锐度仍低于下限（网格不成立）。
        private static bool FindAngle(List<Point2f> pts, double pitch, double minSharp,
            out double bestAng, out double bestPitch, out double bestScore)
        {
            bestAng = 0; bestPitch = pitch; bestScore = -1;
            for (int pi = 0; pi < 5; pi++)
            {
                double p = pitch * (0.94 + 0.03 * pi);
                int guard = 0;
                for (double ang = 0; ang < 89.999; ang += 2.0)
                {
                    if ((++guard & 63) == 0 && BudgetExpired()) return false;
                    double s = Sharpness(pts, ang * Math.PI / 180, p);
                    if (s > bestScore) { bestScore = s; bestAng = ang; bestPitch = p; }
                }
            }
            double ang0 = bestAng, p0 = bestPitch;
            for (double ang = ang0 - 1.75; ang <= ang0 + 1.7501; ang += 0.25)
            {
                double a = (ang + 90.0) % 90.0;   // 归一化回 [0,90°)，处理 0°/90° 边界环绕
                for (int pi = -2; pi <= 2; pi++)
                {
                    double p = p0 * (1 + 0.015 * pi);
                    double s = Sharpness(pts, a * Math.PI / 180, p);
                    if (s > bestScore) { bestScore = s; bestAng = a; bestPitch = p; }
                }
            }
            return bestScore >= minSharp;
        }

        // 网格采样：格点邻域形态学响应均值构成连续得分场，对全部格点得分做 Otsu
        // 得到随对比度自适应的判暗门限（响应量在"有点/无点"两类间保持双峰，不随
        // 全图二值阈值的松紧整体翻转）；darkRatio 作为门限偏置保留原参数语义
        // （调低更严 = 门限降低更易判暗，用于 RS 失败后的放宽重试）。
        // 另保留质心命中判据：格点 0.4×节距 内存在存活"单点"质心即判暗，捞回碎点/浅点。
        private static bool[,] SampleGrid(Mat resp, List<Point2f> centers, List<int> alive,
            double ou, double ov, double pu, double pv, double ux, double uy, double vx, double vy,
            int kmin, int kmax, int lmin, int lmax, double rad, double darkRatio, out double thr)
        {
            int nk = kmax - kmin + 1, nl = lmax - lmin + 1;
            var score = new double[nk, nl];
            var hit = new bool[nk, nl];
            double hitR2 = 0.4 * Math.Min(pu, pv);
            hitR2 *= hitR2;
            double smin = double.MaxValue, smax = double.MinValue;
            for (int kk = kmin; kk <= kmax; kk++)
                for (int ll = lmin; ll <= lmax; ll++)
                {
                    double px = ((ou + (kk * pu)) * ux) + ((ov + (ll * pv)) * vx);
                    double py = ((ou + (kk * pu)) * uy) + ((ov + (ll * pv)) * vy);
                    double s = MeanResp(resp, px, py, rad);
                    score[kk - kmin, ll - lmin] = s;
                    if (s < smin) smin = s;
                    if (s > smax) smax = s;
                    foreach (int i in alive)
                    {
                        double dx = centers[i].X - px, dy = centers[i].Y - py;
                        if ((dx * dx) + (dy * dy) < hitR2)
                        {
                            hit[kk - kmin, ll - lmin] = true;
                            break;
                        }
                    }
                }
            thr = OtsuScoreThreshold(score, smin, smax);
            if (thr <= 0)
                thr = darkRatio * 255.0;           // 得分无对比度（极性错/全平）→ 回退固定门限
            else
                thr *= darkRatio / DarkRatio;      // 保留"判暗阈值"参数对门限的单调作用
            var mat = new bool[nk, nl];
            for (int kk = 0; kk < nk; kk++)
                for (int ll = 0; ll < nl; ll++)
                    mat[kk, ll] = score[kk, ll] > thr || hit[kk, ll];
            return mat;
        }

        /// <summary>格点得分场的 Otsu 门限；得分几乎无对比度时返回 -1（由调用方回退）</summary>
        private static double OtsuScoreThreshold(double[,] score, double smin, double smax)
        {
            if (smax - smin < 4) return -1;
            var hist = new int[256];
            int total = 0;
            foreach (double s in score)
            {
                int b = (int)Math.Clamp((s - smin) / (smax - smin) * 255.0, 0, 255);
                hist[b]++;
                total++;
            }
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += (double)i * hist[i];
            double sumB = 0;
            int wB = 0, bestT = 0;
            double best = -1;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;
                sumB += (double)t * hist[t];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; bestT = t; }
            }
            return smin + ((bestT + 0.5) * (smax - smin) / 255.0);
        }

        // 以 (x,y) 为中心的方窗内形态学响应均值
        private static double MeanResp(Mat resp, double x, double y, double rad)
        {
            int r0 = Math.Max(0, (int)(y - rad)), r1 = Math.Min(resp.Rows - 1, (int)(y + rad));
            int c0 = Math.Max(0, (int)(x - rad)), c1 = Math.Min(resp.Cols - 1, (int)(x + rad));
            double sum = 0;
            int cnt = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    sum += resp.At<byte>(r, c);
                    cnt++;
                }
            return cnt == 0 ? 0 : sum / cnt;
        }

        // 最近邻距离（网格空间索引，均摊 O(n)）：只统计半径 NnMaxRadius 内的邻居，
        // 孤立点不参与节距直方图。
        private static void NearestNeighborDistances(List<Point2f> pts, double maxR, List<double> outp)
        {
            int n = pts.Count;
            double cell = Math.Max(4.0, maxR / 16.0);
            var map = new Dictionary<long, List<int>>(n);
            for (int i = 0; i < n; i++)
            {
                long key = CellKey(pts[i].X, pts[i].Y, cell);
                if (!map.TryGetValue(key, out List<int> lst)) map[key] = lst = new List<int>(4);
                lst.Add(i);
            }
            double maxR2 = maxR * maxR;
            int rmax = (int)Math.Ceiling(maxR / cell);
            for (int i = 0; i < n; i++)
            {
                // 逐点预算检查：极密噪点集（无码图）上这一步是主要开销之一
                if ((i & 4095) == 0 && BudgetExpired()) break;
                double x = pts[i].X, y = pts[i].Y;
                int cx = (int)Math.Floor(x / cell), cy = (int)Math.Floor(y / cell);
                double best2 = maxR2;
                for (int r = 0; r <= rmax; r++)
                {
                    // 已扫过 0..r-1 环；第 r 环内任意点到查询点的距离 ≥ (r-1)×cell
                    if (r > 0 && (r - 1) * cell > Math.Sqrt(best2)) break;
                    for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                            if (!map.TryGetValue(CellKey(cx + dx, cy + dy, cell), out List<int> lst)) continue;
                            foreach (int j in lst)
                            {
                                if (j == i) continue;
                                double ddx = pts[j].X - x, ddy = pts[j].Y - y;
                                double d2 = (ddx * ddx) + (ddy * ddy);
                                if (d2 > 1e-6 && d2 < best2) best2 = d2;
                            }
                        }
                }
                if (best2 < maxR2) outp.Add(Math.Sqrt(best2));
            }
        }

        private static long CellKey(double x, double y, double cell)
            => CellKey((int)Math.Floor(x / cell), (int)Math.Floor(y / cell), cell);

        private static long CellKey(int cx, int cy, double cell)
            => ((long)cx * 1000003L) ^ cy;

        /// <summary>最近邻直方图的节距候选：取各局部峰的 3-bin 均值，按计数降序，
        /// 相互间隔 ≥15% 视为不同假设，最多 MaxPitchCandidates 个</summary>
        private static List<double> PitchCandidates(List<double> nn, int minPitch, Action<string> log)
        {
            var result = new List<double>();
            if (nn.Count == 0) return result;
            var bins = new SortedDictionary<int, int>();
            foreach (double v in nn)
            {
                // 小于最小节距的距离不是合法节距（重复点/噪声峰的伪最近邻），
                // 计入会把直方图"最高峰"抬到 1~4px、把真节距峰按相对阈值滤掉
                if (v < minPitch) continue;
                int b = (int)Math.Floor(v);
                bins[b] = (bins.TryGetValue(b, out int c) ? c : 0) + 1;
            }
            if (bins.Count == 0) return result;
            var peaks = new List<int>();
            foreach (KeyValuePair<int, int> kv in bins)
            {
                int prev = bins.TryGetValue(kv.Key - 1, out int p) ? p : 0;
                int next = bins.TryGetValue(kv.Key + 1, out int n) ? n : 0;
                if (kv.Value >= prev && kv.Value >= next) peaks.Add(kv.Key);
            }
            peaks.Sort((a, b) => bins[b].CompareTo(bins[a]));
            int top = peaks.Count > 0 ? bins[peaks[0]] : 0;
            int floorCount = Math.Max(3, (int)(top * 0.06));
            foreach (int b in peaks)
            {
                if (result.Count >= MaxPitchCandidates) break;
                if (bins[b] < floorCount) continue;
                var sel = nn.Where(v => v >= b - 1 && v < b + 2).ToList();
                double p = sel.Count > 0 ? sel.Average() : b + 0.5;
                if (p < minPitch) continue;
                if (result.Any(c => Math.Abs(c - p) <= 0.15 * Math.Max(c, p))) continue;
                result.Add(p);
            }
            log?.Invoke("pitchCand=" + string.Join(",", result.Select(p => p.ToString("F1"))));
            return result;
        }

        // 最大连通簇：距离 < linkThresh 的点互连（并查集 + 网格空间索引），返回最大簇
        private static List<Point2f> LargestCluster(List<Point2f> pts, double linkThresh)
        {
            int n = pts.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                    x = parent[x] = parent[parent[x]];
                return x;
            }

            double cell = Math.Max(1.0, linkThresh);
            var map = new Dictionary<long, List<int>>(n);
            for (int i = 0; i < n; i++)
            {
                long key = CellKey(pts[i].X, pts[i].Y, cell);
                if (!map.TryGetValue(key, out List<int> lst)) map[key] = lst = new List<int>(4);
                lst.Add(i);
            }
            double t2 = linkThresh * linkThresh;
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0 && BudgetExpired()) break;
                int cx = (int)Math.Floor(pts[i].X / cell), cy = (int)Math.Floor(pts[i].Y / cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (!map.TryGetValue(CellKey(cx + dx, cy + dy, cell), out List<int> lst)) continue;
                        foreach (int j in lst)
                        {
                            if (j <= i) continue;
                            double ddx = pts[i].X - pts[j].X, ddy = pts[i].Y - pts[j].Y;
                            if ((ddx * ddx) + (ddy * ddy) >= t2) continue;
                            int a = Find(i), b = Find(j);
                            if (a != b) parent[a] = b;
                        }
                    }
            }
            return Enumerable.Range(0, n).GroupBy(Find)
                .OrderByDescending(g => g.Count()).First()
                .Select(i => pts[i]).ToList();
        }

        // 圆均值合成向量长度（两轴之和）：网格对齐 → 接近 2，否则趋 0。
        // 点数超过 SharpnessSamples 时按固定步长抽样：锐度是均值量，抽样不改变主峰位置，
        // 而 FindAngle 一次要做 ~360 次评估，大图上全量点数会让"全档位穷举"变成分钟级。
        private static double Sharpness(List<Point2f> pts, double ang, double pitch)
        {
            double ca = Math.Cos(ang), sa = Math.Sin(ang);
            double cu = 0, su = 0, cv = 0, sv = 0;
            int cnt = pts.Count;
            int step = cnt > SharpnessSamples ? cnt / SharpnessSamples : 1;
            int n = 0;
            for (int i = 0; i < cnt; i += step)
            {
                Point2f p = pts[i];
                double a = ((p.X * ca) + (p.Y * sa)) / pitch;
                double b = ((-p.X * sa) + (p.Y * ca)) / pitch;
                cu += Math.Cos(2 * Math.PI * a); su += Math.Sin(2 * Math.PI * a);
                cv += Math.Cos(2 * Math.PI * b); sv += Math.Sin(2 * Math.PI * b);
                n++;
            }
            if (n == 0) return 0;
            return (Math.Sqrt((cu * cu) + (su * su)) + Math.Sqrt((cv * cv) + (sv * sv))) / n;
        }

        // 按残差剔除离群点后保留内点：格号已由 round 给出，残差 = 点到最近格点的距离
        // （归一化到节距）。杂点/误检点残差普遍 > 0.3 格，真码点远小于它。
        // 剔除只作用于 alive（是否参与拟合），不影响采样阶段的质心命中判据。
        private static void RefitInliers(double[] a, int[] idx, ref double p, ref double o,
            ref List<int> alive, int total)
        {
            if (alive.Count < MinFitPoints) return;
            var keep = new List<int>(alive.Count);
            foreach (int i in alive)
            {
                double res = a[i] - (o + (idx[i] * p));
                if (Math.Abs(res) < 0.3 * p) keep.Add(i);
            }
            // 内点太少说明晶格没锁住，保留原集合（后续还有裁边重拟合兜底）
            if (keep.Count >= MinFitPoints) alive = keep;
        }

        // a = i*p + o 的闭式最小二乘（只统计 alive 中存活点）
        private static void FitLS(double[] a, int[] i, List<int> alive, ref double p, ref double o)
        {
            int n = alive.Count;
            if (n < 2) return;
            double si = 0, sa = 0, sii = 0, sia = 0;
            foreach (int j in alive)
            {
                int v = i[j];
                si += v; sa += a[j];
                sii += (double)v * v;
                sia += (double)v * a[j];
            }
            double den = (n * sii) - (si * si);
            if (Math.Abs(den) < 1e-9) return;
            double pNew = ((n * sia) - (si * sa)) / den;
            double oNew = (sa - (pNew * si)) / n;
            if (pNew > 1) { p = pNew; o = oNew; }
        }
    }
}