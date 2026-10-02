using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ZXing.Common.ReedSolomon;
using ZXing.Datamatrix.Encoder;

namespace VisionToolDemo.Vision.Tasks
{
    // 自研 Data Matrix（ECC200）解码核心，支持 ISO 16022 全 48 个版本：
    //   方形 10×10..26×26、32×32..144×144 + 矩形 8×18 / 8×32 / 12×26 / 12×36 / 16×36 / 16×48
    //   + 矩形扩展 8×48..26×64（含 90° 旋转采样，行×列与列×行两个次序都入表）。
    // 链路：暗模块矩阵 → 拆数据区（多区符号剔除区间隔行/列，拼接成连续映射矩阵）
    //   → 8 二面体朝向逐一尝试（矩形换维朝向由行/列互换的表项负责）→
    //   cell→(码字,位) 映射反排布（DefaultPlacement 单点置位探测法构建，缓存在映射矩阵尺寸上）→
    //   RS 纠错（DATA_MATRIX_FIELD_256，多块符号按 interleave 拆块独立纠错后合并）→ 位流解析。
    // 朝向、尺寸、极性的正确性全部由 RS 校验兜底，无需识别定位边，
    // 因此天然适配点阵（dot-peen）渲染——ZXing 自带的 DM 检测器对点阵符号失效。
    // 输入约定：modules[r,c] = true 表示该模块为"暗"（图像里打印的圆点）。
    public static class Ecc200Decoder
    {
        // 全 48 版本规格（ZXing Internal.Version 表反编译，ISO 16022:2006 Table 7 一致）：
        // 符号(行,列) → 数据区(行,列)、总码字、纠错块序列 (块数,每块数据码字)、每块纠错码字数
        readonly record struct Spec(int Rows, int Cols, int RegionR, int RegionC, int Total, (int Count, int Data)[] Blocks, int EcPerBlock);

        static readonly Spec[] Specs =
        {
            new(10, 10, 8, 8, 8, new[]{ (1,3) }, 5),
            new(12, 12, 10, 10, 12, new[]{ (1,5) }, 7),
            new(14, 14, 12, 12, 18, new[]{ (1,8) }, 10),
            new(16, 16, 14, 14, 24, new[]{ (1,12) }, 12),
            new(18, 18, 16, 16, 32, new[]{ (1,18) }, 14),
            new(20, 20, 18, 18, 40, new[]{ (1,22) }, 18),
            new(22, 22, 20, 20, 50, new[]{ (1,30) }, 20),
            new(24, 24, 22, 22, 60, new[]{ (1,36) }, 24),
            new(26, 26, 24, 24, 72, new[]{ (1,44) }, 28),
            new(32, 32, 14, 14, 98, new[]{ (1,62) }, 36),
            new(36, 36, 16, 16, 128, new[]{ (1,86) }, 42),
            new(40, 40, 18, 18, 162, new[]{ (1,114) }, 48),
            new(44, 44, 20, 20, 200, new[]{ (1,144) }, 56),
            new(48, 48, 22, 22, 242, new[]{ (1,174) }, 68),
            new(52, 52, 24, 24, 288, new[]{ (2,102) }, 42),
            new(64, 64, 14, 14, 392, new[]{ (2,140) }, 56),
            new(72, 72, 16, 16, 512, new[]{ (4,92) }, 36),
            new(80, 80, 18, 18, 648, new[]{ (4,114) }, 48),
            new(88, 88, 20, 20, 800, new[]{ (4,144) }, 56),
            new(96, 96, 22, 22, 968, new[]{ (4,174) }, 68),
            new(104, 104, 24, 24, 1152, new[]{ (6,136) }, 56),
            new(120, 120, 18, 18, 1458, new[]{ (6,175) }, 68),
            new(132, 132, 20, 20, 1800, new[]{ (8,163) }, 62),
            new(144, 144, 22, 22, 2178, new[]{ (8,156), (2,155) }, 62),
            // 矩形
            new(8, 18, 6, 16, 12, new[]{ (1,5) }, 7),
            new(8, 32, 6, 14, 21, new[]{ (1,10) }, 11),
            new(12, 26, 10, 24, 30, new[]{ (1,16) }, 14),
            new(12, 36, 10, 16, 40, new[]{ (1,22) }, 18),
            new(16, 36, 14, 16, 56, new[]{ (1,32) }, 24),
            new(16, 48, 14, 22, 77, new[]{ (1,49) }, 28),
            new(8, 48, 6, 22, 33, new[]{ (1,18) }, 15),
            new(8, 64, 6, 14, 43, new[]{ (1,24) }, 18),
            new(8, 80, 6, 18, 54, new[]{ (1,32) }, 22),
            new(8, 96, 6, 22, 66, new[]{ (1,38) }, 28),
            new(8, 120, 6, 18, 81, new[]{ (1,49) }, 32),
            new(8, 144, 6, 22, 99, new[]{ (1,63) }, 36),
            new(12, 64, 10, 14, 70, new[]{ (1,43) }, 27),
            new(12, 88, 10, 20, 100, new[]{ (1,64) }, 36),
            new(16, 64, 14, 14, 98, new[]{ (1,62) }, 36),
            new(20, 36, 18, 16, 72, new[]{ (1,44) }, 28),
            new(20, 44, 18, 20, 90, new[]{ (1,56) }, 34),
            new(20, 64, 18, 14, 126, new[]{ (1,84) }, 42),
            new(22, 48, 20, 22, 110, new[]{ (1,72) }, 38),
            new(24, 48, 22, 22, 122, new[]{ (1,80) }, 41),
            new(24, 64, 22, 14, 154, new[]{ (1,108) }, 46),
            new(26, 40, 24, 18, 108, new[]{ (1,70) }, 38),
            new(26, 48, 24, 22, 132, new[]{ (1,90) }, 42),
            new(26, 64, 24, 14, 168, new[]{ (1,118) }, 50),
        };

        // 行×列 → 规格（两个行列次序都登记，覆盖旋转 90° 采样）
        static readonly Dictionary<(int rows, int cols), Spec> ByDim = BuildByDim();
        static Dictionary<(int rows, int cols), Spec> BuildByDim()
        {
            var d = new Dictionary<(int rows, int cols), Spec>();
            foreach (Spec s in Specs)
            {
                if (!d.ContainsKey((s.Rows, s.Cols))) d[(s.Rows, s.Cols)] = s;
                if (!d.ContainsKey((s.Cols, s.Rows))) d[(s.Cols, s.Rows)] = s;
            }
            return d;
        }

        static readonly Dictionary<(int rows, int cols), int[,]> MapCache = [];

        // 该行×列（或其互换）是否受支持
        public static bool Supports(int rows, int cols)
        {
            return ByDim.ContainsKey((rows, cols));
        }

        // 尝试解码。成功返回 true 并输出文本与命中朝向（0..7，二面体枚举序）。
        // 8 个二面体变换逐个试：变换后是"映射矩阵"（已剔除定位边与区间隔）尺寸，
        // 直接按 (映射行+0, 映射列+0) 找规格——注意规格键是完整符号尺寸，
        // 映射矩阵 = (symR - 2*vRegions) × (symC - 2*hRegions)，逐版本算出再入字典。
        // log 可选：诊断回调，报告各朝向失败原因（排布探测异常不再静默吞掉）。
        public static bool TryDecode(bool[,] modules, out string text, out int orient, Action<string> log = null)
        {
            text = null;
            orient = -1;
            int rows = modules.GetLength(0), cols = modules.GetLength(1);
            if (!ByDim.TryGetValue((rows, cols), out Spec spec)) return false;

            // 矩形符号的换维匹配（如采样得 [32,8]、规格为 8×32）：
            // 采样矩阵第一维是网格 u 向（对齐符号"列"轴时为 32），解码器要求第一维=符号行数。
            // 换维命中时先转置成规格方向再拆数据区——否则 ExtractDataRegion 会越界。
            bool[,] m0 = modules;
            if (spec.Rows != rows || spec.Cols != cols)
                m0 = Transpose(modules);

            // 拆数据区：剔除全部定位边/分隔线，得到连续映射矩阵
            bool[,] mapping = ExtractDataRegion(m0, spec);

            int ti = 0;
            foreach (bool[,] t in Dihedral(mapping))
            {
                int rR = t.GetLength(0), rC = t.GetLength(1);
                if (LookupByMapping(rR, rC, out Spec s2))
                {
                    try
                    {
                        int[,] map = GetMap(rR, rC, s2.Total);
                        int[] raw = Unplace(t, map, s2.Total);

                        bool rs = DecodeInterleaved(raw, s2);
                        if (rs)
                        {
                            // 位流解析优先委托 ZXing 官方 DecodedBitStreamParser（处理全部
                            // latch/ECI/Macro/FNC1/UpperShift 控制码字——自研版对 240 ECI 等
                            // 控制码字的载荷长度处理不全会在长内容上错位），自研解析兜底
                            string s = ParseBitStreamZxing(raw, DataCount(s2)) ?? ParseBitStream(raw, DataCount(s2));
                            if (Plausible(s)) { text = s; orient = ti; return true; }
                            // 诊断：转义非可打印字符，暴露乱码位置
                            log?.Invoke($"orient{ti}: rs-ok but implausible len={s?.Length} '{Escape(s)}");
                        }
                        else
                        {
                            log?.Invoke($"orient{ti}: rs-fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 排布探测异常（该尺寸位图不适用）→ 跳过此朝向
                        log?.Invoke($"orient{ti}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                ti++;
            }
            return false;
        }

        // 数据码字总数 = Σ(块数据容量)
        static int DataCount(Spec s)
        {
            int n = 0;
            foreach (var b in s.Blocks) n += b.Count * b.Data;
            return n;
        }

        // (映射行, 映射列) → 规格：symbolR - 2*行向区数, symbolC - 2*列向区数
        static readonly Dictionary<(int, int), Spec> ByMapping = BuildByMapping();
        static Dictionary<(int, int), Spec> BuildByMapping()
        {
            var d = new Dictionary<(int, int), Spec>();
            foreach (Spec s in Specs)
            {
                int vR = s.Rows / (s.RegionR + 2);
                int vC = s.Cols / (s.RegionC + 2);
                var key = (s.Rows - 2 * vR, s.Cols - 2 * vC);
                if (!d.ContainsKey(key)) d[key] = s;
            }
            return d;
        }

        static bool LookupByMapping(int mapR, int mapC, out Spec spec)
        {
            return ByMapping.TryGetValue((mapR, mapC), out spec);
        }

        // 拆数据区：多区符号剔除区间隔行/列 + 符号外圈定位边，拼成连续映射矩阵。
        // 区结构：行向 = symR/(regionR+2) 区、列向 = symC/(regionC+2) 区；
        // 每区数据区尺寸 regionR×regionC，区与区之间 1 行/列间隔，符号外圈 1 圈。
        // 单区符号（v1-9, 25-30）：退化为去 1 圈边框。
        static bool[,] ExtractDataRegion(bool[,] m, Spec s)
        {
            int symR = s.Rows, symC = s.Cols;
            int vR = symR / (s.RegionR + 2), vC = symC / (s.RegionC + 2);
            var outM = new bool[vR * s.RegionR, vC * s.RegionC];
            for (int i = 0; i < vR; i++)
                for (int j = 0; j < vC; j++)
                    for (int k = 0; k < s.RegionR; k++)
                        for (int l = 0; l < s.RegionC; l++)
                            outM[i * s.RegionR + k, j * s.RegionC + l] =
                                m[i * (s.RegionR + 2) + 1 + k, j * (s.RegionC + 2) + 1 + l];
            return outM;
        }

        // 交织多块 RS 纠错：按 ZXing DataBlock.getDataBlocks 逆过程拆块，
        // 每块独立 RS decode，再按块序合并数据码字（原地写入 raw）。
        // 48 号版本（144×144）特例：10 块中 8 块 156 数据 + 2 块 155 数据，
        // 最后一个数据码字只发给长块（ZXing version==24 分支）。
        static bool DecodeInterleaved(int[] raw, Spec s)
        {
            int blocks = 0;
            foreach (var b in s.Blocks) blocks += b.Count;
            if (blocks == 1)
            {
                var rec = new int[raw.Length];
                for (int i = 0; i < raw.Length; i++) rec[i] = raw[i];
                bool ok = new ReedSolomonDecoder(GenericGF.DATA_MATRIX_FIELD_256).decode(rec, raw.Length - DataCount(s));
                if (!ok) return false;
                for (int i = 0; i < raw.Length; i++) raw[i] = rec[i];
                return true;
            }

            int dataLen0 = s.Blocks[0].Data; // 长块数据容量
            var dest = new int[blocks][];
            for (int b = 0; b < blocks; b++) dest[b] = new int[BlockData(b, s) + s.EcPerBlock];

            // 阶段 1: 前 dataLen0-1 个数据位逐块轮转
            int src = 0;
            for (int k = 0; k < dataLen0 - 1; k++)
                for (int b = 0; b < blocks; b++)
                    if (k < BlockData(b, s)) dest[b][k] = raw[src++];
            // 长块的最后一个数据位（短块 155 无第 156 位；ZXing: version 24 只给前 8 块）
            for (int b = 0; b < blocks; b++)
                if (BlockData(b, s) == dataLen0)
                    dest[b][dataLen0 - 1] = raw[src++];
            // 纠错段（从 dataLen0 起按块容量轮转）
            for (int n = dataLen0; n < dataLen0 + s.EcPerBlock; n++)
                for (int b = 0; b < blocks; b++)
                    dest[b][n] = raw[src++];

            // 先逐块 RS 纠错（原地修正 dest）
            for (int b = 0; b < blocks; b++)
            {
                var rec = dest[b];
                bool ok = new ReedSolomonDecoder(GenericGF.DATA_MATRIX_FIELD_256).decode(rec, s.EcPerBlock);
                if (!ok) return false;
            }

            // 合并 = 拆块的精确逆操作（交错合并）：编码侧把数据流按块轮转分配
            // （块 b 持有原流中下标 ≡ b (mod blocks) 的码字），raw 数据段即原流顺序；
            // 若按块整块顺序拼接，会得到 [D0,D2,…,D1,D3,…] 乱序流（RS 仍自洽但内容全乱）
            var merged = new int[DataCount(s)];
            int w = 0;
            for (int k = 0; k < dataLen0 - 1; k++)
                for (int b = 0; b < blocks; b++)
                    if (k < BlockData(b, s)) merged[w++] = dest[b][k];
            // 长块尾位（144×144 短块 155 无第 156 位）
            for (int b = 0; b < blocks; b++)
                if (BlockData(b, s) == dataLen0) merged[w++] = dest[b][dataLen0 - 1];
            Array.Copy(merged, raw, merged.Length);
            return true;
        }

        static int BlockData(int b, Spec s)
        {
            foreach (var (count, data) in s.Blocks)
            {
                if (b < count) return data;
                b -= count;
            }
            return s.Blocks[^1].Data;
        }

        // 像"正常载荷"才算命中（可打印、非空），过滤 RS 假阳性
        static bool Plausible(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return false;
            foreach (char ch in s)
                if (ch < 32 || ch > 126) return false;
            return true;
        }

        // 诊断用：可打印字符原样，其余转 \xNN，截断 40 字符
        static string Escape(string s)
        {
            if (s == null) return "(null)";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (sb.Length >= 40) { sb.Append('…'); break; }
                sb.Append(ch >= 32 && ch <= 126 ? ch : $"\\x{(int)ch:X2}");
            }
            return sb.ToString();
        }

        // DefaultPlacement 单点置位探测：cell(r,c) → 码字 pos 的第 p 位（p∈1..8，掩码 1<<(8-p)）。
        // 全零基线排除固定图案格，每个探测位恰好命中 1 格。
        // 探测在"映射矩阵"（拆区后的连续网格，宽 gridC×高 gridH）上进行——
        // 大符号与单区符号排布一致（都是 DefaultPlacement 蛇形），无需再分区。
        static int[,] GetMap(int gridH, int gridC, int total)
        {
            var key = (gridH, gridC);
            if (MapCache.TryGetValue(key, out int[,] cached)) return cached;

            byte[] baseBits = Place(new byte[total], gridH, gridC);
            var map = new int[gridH, gridC];
            for (int i = 0; i < gridH * gridC; i++) map[i / gridC, i % gridC] = -1;

            for (int pos = 0; pos < total; pos++)
            {
                for (int p = 1; p <= 8; p++)
                {
                    var cw = new byte[total];
                    cw[pos] = (byte)(1 << (8 - p));
                    byte[] bits = Place(cw, gridH, gridC);
                    int hits = 0;
                    for (int i = 0; i < bits.Length && hits <= 1; i++)
                        if (bits[i] != 0 && baseBits[i] == 0)
                        {
                            map[i / gridC, i % gridC] = (pos * 8) + (p - 1);
                            hits++;
                        }
                    if (hits != 1)
                        throw new InvalidOperationException("placement probe pos=" + pos + " p=" + p + " hits=" + hits);
                }
            }

            MapCache[key] = map;
            return map;
        }

        static byte[] Place(byte[] codewords, int regionR, int regionC)
        {
            var sb = new StringBuilder(codewords.Length);
            foreach (byte b in codewords) sb.Append((char)b);
            var dp = new DefaultPlacement(sb.ToString(), regionC, regionR);
            dp.place();   // ZXing.Net 端口把 place() 做成公开方法，Bits 只是取数组
            byte[] bits = dp.Bits;
            if (bits.Length != regionR * regionC)
                throw new InvalidOperationException("Bits length " + bits.Length + " != " + (regionR * regionC));
            return bits;
        }

        // 映射矩阵位 → 码字（Unplace：提取）
        static int[] Unplace(bool[,] m, int[,] map, int total)
        {
            var cw = new int[total];
            int regionR = map.GetLength(0), regionC = map.GetLength(1);
            for (int r = 0; r < regionR; r++)
                for (int c = 0; c < regionC; c++)
                {
                    int v = map[r, c];
                    if (v >= 0 && m[r, c]) cw[v / 8] |= 1 << (7 - (v % 8));
                }
            return cw;
        }

        static IEnumerable<bool[,]> Dihedral(bool[,] m)
        {
            for (int rot = 0; rot < 4; rot++)
            {
                bool[,] r0 = m;
                for (int k = 0; k < rot; k++) r0 = Rot90(r0);
                yield return r0;
                yield return Flip(r0);
            }
        }

        // 行列互换（矩形符号换维采样时先转置回规格方向）
        static bool[,] Transpose(bool[,] a)
        {
            int R = a.GetLength(0), C = a.GetLength(1);
            var b = new bool[C, R];
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    b[c, r] = a[r, c];
            return b;
        }

        static bool[,] Rot90(bool[,] a)
        {
            int R = a.GetLength(0), C = a.GetLength(1);
            var b = new bool[C, R];
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    b[c, R - 1 - r] = a[r, c];
            return b;
        }

        static bool[,] Flip(bool[,] a)
        {
            int R = a.GetLength(0), C = a.GetLength(1);
            var b = new bool[R, C];
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    b[r, C - 1 - c] = a[r, c];
            return b;
        }

        // —— 数据流解析（ASCII / C40 / Text / X12 / Base256） ——
        // ZXing 官方 DataMatrix 位流解析器（internal，反射一次缓存）：latch/ECI/Macro/
        // FNC1/UpperShift 等控制码字语义完整；不可用时返回 null，调用方回退自研解析
        private static readonly MethodInfo _zxingDmParse = typeof(DefaultPlacement).Assembly.GetTypes()
            .Where(t => t.Name == "DecodedBitStreamParser" && t.Namespace != null && t.Namespace.Contains("atamatrix"))
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .OrderBy(mi => mi.GetParameters().Length)
            .FirstOrDefault(mi => mi.Name == "decode"
                && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(byte[]));

        static string ParseBitStreamZxing(int[] cw, int dataCount)
        {
            if (_zxingDmParse == null) return null;
            try
            {
                var ps = _zxingDmParse.GetParameters();
                var args = new object[ps.Length];
                var bytes = new byte[dataCount];
                for (int i = 0; i < dataCount; i++) bytes[i] = (byte)cw[i];
                args[0] = bytes;
                for (int i = 1; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                object res = _zxingDmParse.Invoke(null, args);
                return res?.GetType().GetProperty("Text")?.GetValue(res) as string;
            }
            catch
            {
                return null;   // ReaderException 等 → 调用方回退自研解析
            }
        }

        static string ParseBitStream(int[] cw, int dataCount)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < dataCount)
            {
                int v = cw[i];
                if (v >= 1 && v <= 128) { sb.Append((char)(v - 1)); i++; }
                else if (v == 129) break;
                else if (v >= 130 && v <= 229)
                {
                    int d = v - 130;
                    sb.Append((char)('0' + (d / 10))).Append((char)('0' + (d % 10)));
                    i++;
                }
                else if (v == 230) i = ParseC40Text(cw, dataCount, i + 1, sb, text: false);
                else if (v == 231) i = ParseBase256(cw, dataCount, i, sb);
                else if (v == 238) i = ParseX12(cw, dataCount, i + 1, sb);
                else if (v == 239) i = ParseC40Text(cw, dataCount, i + 1, sb, text: true);
                else if (v == 235)
                {
                    if (i + 1 < dataCount) { sb.Append((char)(cw[i + 1] + 127)); i += 2; }
                    else break;
                }
                else i++;
            }
            return sb.ToString();
        }

        // C40 / Text 段：码对 → 16 位值 = v1*256 + v2 − 1，三连值 = 1600/40/1 分解；
        // 0/1/2 为移位 1/2/3（作用于紧随的下一个值）；254 Unlatch 占码对首位，下一位回 ASCII。
        // 基本集差异：C40 14-39 = A-Z；Text 14-39 = a-z；移位 3 分别为小写/大写。
        static int ParseC40Text(int[] cw, int count, int i, StringBuilder sb, bool text)
        {
            int shift = 0;
            while (i + 1 < count)
            {
                int v1 = cw[i], v2 = cw[i + 1];
                i += 2;
                if (v1 == 254) return i - 1;
                int value = (v1 * 256) + v2 - 1;
                int[] tri = { value / 1600, value % 1600 / 40, value % 40 };
                foreach (int t in tri)
                {
                    if (shift > 0)
                    {
                        char c = ShiftChar(shift, t, text);
                        if (c != '\0') sb.Append(c);
                        shift = 0;
                        continue;
                    }
                    if (t <= 2) { shift = t + 1; continue; }
                    sb.Append(BasicChar(t, text));
                }
            }
            return i;
        }

        // 基本集（两模式相同）：3 空，4-13 数字；C40 大写、Text 小写
        static char BasicChar(int t, bool text)
        {
            if (t == 3) return ' ';
            if (t >= 4 && t <= 13) return (char)('0' + t - 4);
            return text ? (char)('a' + t - 14) : (char)('A' + t - 14);
        }

        // 移位集：1=ASCII 0..39；2='!'..'/'+':'..'@'+'['..'_'；
        // 3：C40=小写+{|}~，Text=0 加重音 + 大写 + {|}~ + DEL
        static char ShiftChar(int shift, int v, bool text)
        {
            if (shift == 1) return v <= 39 ? (char)v : '\0';
            if (shift == 2)
            {
                if (v <= 14) return (char)(33 + v);
                if (v <= 21) return (char)(58 + v - 15);
                if (v <= 26) return (char)(69 + v);
                return '\0';
            }
            if (text)
            {
                if (v == 0) return '`';
                if (v >= 1 && v <= 26) return (char)('A' + v - 1);
                if (v >= 27 && v <= 30) return (char)(123 + v - 27);
                if (v == 31) return (char)127;
                return '\0';
            }
            if (v >= 1 && v <= 26) return (char)('a' + v - 1);
            if (v >= 27 && v <= 30) return (char)(123 + v - 27);
            return '\0';
        }

        // X12 集：0 回车，1 '>'，2 '*'，3 空格，4-13 数字，14-39 大写；无移位
        static int ParseX12(int[] cw, int count, int i, StringBuilder sb)
        {
            while (i + 1 < count)
            {
                int v1 = cw[i], v2 = cw[i + 1];
                i += 2;
                if (v1 == 254) return i - 1;
                int value = (v1 * 256) + v2 - 1;
                int[] tri = { value / 1600, value % 1600 / 40, value % 40 };
                foreach (int t in tri)
                {
                    if (t == 0) sb.Append('\r');
                    else if (t == 1) sb.Append('>');
                    else if (t == 2) sb.Append('*');
                    else if (t == 3) sb.Append(' ');
                    else if (t <= 13) sb.Append((char)('0' + t - 4));
                    else if (t <= 39) sb.Append((char)('A' + t - 14));
                }
            }
            return i;
        }

        static int ParseBase256(int[] cw, int count, int i, StringBuilder sb)
        {
            if (i >= count) return i;
            int len = Unrand(cw[i], i + 1);
            i++;
            if (len == 0 || len > 1555) return i;
            for (int k = 0; k < len && i < count; k++, i++)
                sb.Append((char)Unrand(cw[i], i + 1));
            return i;
        }

        static int Unrand(int v, int pos)
        {
            int pseudo = (149 * pos % 255) + 1;
            int r = v - pseudo;
            if (r < 0) r += 255;
            return r % 255;
        }
    }
}
