using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 纯 C# 一维码解码器（EAN-13 / UPC-A / EAN-8 / Code 128 / Code 39），不依赖
    /// OpenCvSharp 的 BarcodeDetector（其原生实现在当前构建中调用即崩溃，不可用）。
    /// 原理：自适应二值化（条=255）→ 逐行游程分析 → 从每个条(黑)游程锚点尝试：
    /// EAN：定位左护线（三段近似等宽的黑白黑）→ 每位数字固定 4 游程、宽度总和 7 模块
    /// （局部自校准，抗模糊/打印变形），查 L/G/R 游程宽度表，奇偶序列定首位 + 校验和验证。
    /// Code 128：起如符 103~105 锚定，逐符号 6 游程整数归一化（和恒 11 模块）查 zxing
    /// 官方宽度表，A/B/C 码集切换 + mod-103 校验，终止符 7 游程 13 模块。
    /// Code 39：'*'（0x094 窄宽位图）锚定，每字符 9 游程窄宽聚类（恰 3 宽）查表，'*' 收尾。
    /// EAN-13/UPC-A 全码 59 个游程、EAN-8 为 43 个，游程颜色结构固定、无同色相邻合并。
    /// 另扫描 90° 旋转图以支持竖直条码。
    /// </summary>
    public static class OneDBarcode
    {
        public class Result
        {
            public string Text = "";
            public string TypeName = "";
            public Rect Bounds;
        }

        private static readonly string[] LCodes =
        {
            "0001101", "0011001", "0010011", "0111101", "0100011",
            "0110001", "0101111", "0111011", "0110111", "0001011"
        };

        private static readonly string[] GCodes =
        {
            "0100111", "0110011", "0011011", "0100001", "0011101",
            "0111001", "0000101", "0010001", "0001001", "0010111"
        };

        private static readonly string[] RCodes =
        {
            "1110010", "1100110", "1101100", "1000010", "1011100",
            "1001110", "1010000", "1000100", "1001000", "1110100"
        };

        /// <summary>EAN-13 首位数字由左侧 6 位字符的 L/G 奇偶序列编码</summary>
        private static readonly string[] ParityFirst =
        {
            "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG",
            "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"
        };

        // 游程宽度键（如 "3211"）→ 数字。左侧字符以空开始（空黑白黑），右侧以条开始（黑白黑白）
        private static readonly Dictionary<string, int> LeftMapL;
        private static readonly Dictionary<string, int> LeftMapG;
        private static readonly Dictionary<string, int> RightMapR;

        // ---------- Code 39（zxing 官方表：9 位游程 = 5 条 4 空，恰 3 宽，1=宽） ----------
        private const string Code39Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
        private static readonly int[] Code39Encodings =
        {
            0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064, // 0-9
            0x109, 0x049, 0x148, 0x019, 0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C, // A-J
            0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016, // K-T
            0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8, // U-$
            0x0A2, 0x08A, 0x02A                                                   // /-%
        };
        private const int Code39Asterisk = 0x094;

        // ---------- Code 128（zxing 官方表：符号 0~105 各 6 游程共 11 模块，终止符 106 为 7 游程 13 模块） ----------
        private static readonly int[][] Code128Patterns =
        {
            new[] {2,1,2,2,2,2}, new[] {2,2,2,1,2,2}, new[] {2,2,2,2,2,1}, new[] {1,2,1,2,2,3},
            new[] {1,2,1,3,2,2}, new[] {1,3,1,2,2,2}, new[] {1,2,2,2,1,3}, new[] {1,2,2,3,1,2},
            new[] {1,3,2,2,1,2}, new[] {2,2,1,2,1,3}, new[] {2,2,1,3,1,2}, new[] {2,3,1,2,1,2},
            new[] {1,1,2,2,3,2}, new[] {1,2,2,1,3,2}, new[] {1,2,2,2,3,1}, new[] {1,1,3,2,2,2},
            new[] {1,2,3,1,2,2}, new[] {1,2,3,2,2,1}, new[] {2,2,3,2,1,1}, new[] {2,2,1,1,3,2},
            new[] {2,2,1,2,3,1}, new[] {2,1,3,2,1,2}, new[] {2,2,3,1,1,2}, new[] {3,1,2,1,3,1},
            new[] {3,1,1,2,2,2}, new[] {3,2,1,1,2,2}, new[] {3,2,1,2,2,1}, new[] {3,1,2,2,1,2},
            new[] {3,2,2,1,1,2}, new[] {3,2,2,2,1,1}, new[] {2,1,2,1,2,3}, new[] {2,1,2,3,2,1},
            new[] {2,3,2,1,2,1}, new[] {1,1,1,3,2,3}, new[] {1,3,1,1,2,3}, new[] {1,3,1,3,2,1},
            new[] {1,1,2,3,1,3}, new[] {1,3,2,1,1,3}, new[] {1,3,2,3,1,1}, new[] {2,1,1,3,1,3},
            new[] {2,3,1,1,1,3}, new[] {2,3,1,3,1,1}, new[] {1,1,2,1,3,3}, new[] {1,1,2,3,3,1},
            new[] {1,3,2,1,3,1}, new[] {1,1,3,1,2,3}, new[] {1,1,3,3,2,1}, new[] {1,3,3,1,2,1},
            new[] {3,1,3,1,2,1}, new[] {2,1,1,3,3,1}, new[] {2,3,1,1,3,1}, new[] {2,1,3,1,1,3},
            new[] {2,1,3,3,1,1}, new[] {2,1,3,1,3,1}, new[] {3,1,1,1,2,3}, new[] {3,1,1,3,2,1},
            new[] {3,3,1,1,2,1}, new[] {3,1,2,1,1,3}, new[] {3,1,2,3,1,1}, new[] {3,3,2,1,1,1},
            new[] {3,1,4,1,1,1}, new[] {2,2,1,4,1,1}, new[] {4,3,1,1,1,1}, new[] {1,1,1,2,2,4},
            new[] {1,1,1,4,2,2}, new[] {1,2,1,1,2,4}, new[] {1,2,1,4,2,1}, new[] {1,4,1,1,2,2},
            new[] {1,4,1,2,2,1}, new[] {1,1,2,2,1,4}, new[] {1,1,2,4,1,2}, new[] {1,2,2,1,1,4},
            new[] {1,2,2,4,1,1}, new[] {1,4,2,1,1,2}, new[] {1,4,2,2,1,1}, new[] {2,4,1,2,1,1},
            new[] {2,2,1,1,1,4}, new[] {4,1,3,1,1,1}, new[] {2,4,1,1,1,2}, new[] {1,3,4,1,1,1},
            new[] {1,1,1,2,4,2}, new[] {1,2,1,1,4,2}, new[] {1,2,1,2,4,1}, new[] {1,1,4,2,1,2},
            new[] {1,2,4,1,1,2}, new[] {1,2,4,2,1,1}, new[] {4,1,1,2,1,2}, new[] {4,2,1,1,1,2},
            new[] {4,2,1,2,1,1}, new[] {2,1,2,1,4,1}, new[] {2,1,4,1,2,1}, new[] {4,1,2,1,2,1},
            new[] {1,1,1,1,4,3}, new[] {1,1,1,3,4,1}, new[] {1,3,1,1,4,1}, new[] {1,1,4,1,1,3},
            new[] {1,1,4,3,1,1}, new[] {4,1,1,1,1,3}, new[] {4,1,1,3,1,1}, new[] {1,1,3,1,4,1},
            new[] {1,1,4,1,3,1}, new[] {3,1,1,1,4,1}, new[] {4,1,1,1,3,1}, new[] {2,1,1,4,1,2},
            new[] {2,1,1,2,1,4}, new[] {2,1,1,2,3,2}, new[] {2,3,3,1,1,1,2}
        };

        /// <summary>归一化游程键（如 "2,1,2,2,2,2"）→ 符号值 0~105 / 终止符 106</summary>
        private static readonly Dictionary<string, int> Code128Map = [];

        static OneDBarcode()
        {
            LeftMapL = BuildMap(LCodes, '0');
            LeftMapG = BuildMap(GCodes, '0');
            RightMapR = BuildMap(RCodes, '1');
            for (int v = 0; v < Code128Patterns.Length; v++)
            {
                int[] p = Code128Patterns[v];
                Code128Map[string.Join(",", p)] = v;
            }
        }

        private static Dictionary<string, int> BuildMap(string[] table, char firstColor)
        {
            var map = new Dictionary<string, int>();
            for (int d = 0; d < 10; d++)
            {
                int[] runs = RunKey(table[d], firstColor);
                map[Key(runs)] = d;
            }
            return map;
        }

        /// <summary>把 7 位编码串拆成 4 段游程宽度（首段颜色由 firstColor 指定）</summary>
        private static int[] RunKey(string bits, char firstColor)
        {
            int[] runs = new int[4];
            int ri = 0;
            char cur = firstColor;
            int len = 1;
            for (int k = 1; k < 7; k++)
            {
                if (bits[k] == cur)
                {
                    len++;
                }
                else
                {
                    runs[ri++] = len;
                    cur = bits[k];
                    len = 1;
                }
            }
            runs[ri] = len;
            return runs;
        }

        private static string Key(int[] runs)
        {
            return "" + runs[0] + runs[1] + runs[2] + runs[3];
        }

        public static List<Result> Decode(Mat gray)
        {
            List<Result> results = [];
            Scan(gray, results);

            // 90° 旋转再扫一遍（竖直条码），包围框映射回原图
            using (Mat rot = new())
            {
                Cv2.Rotate(gray, rot, RotateFlags.Rotate90Counterclockwise);
                List<Result> rotResults = [];
                Scan(rot, rotResults);
                int w = gray.Cols;
                foreach (Result r in rotResults)
                {
                    Rect b = r.Bounds;
                    // 逆时针90°：旋转图(x',y') ↔ 原图(W-1-y', x')
                    r.Bounds = new Rect(w - b.Y - b.Height, b.X, b.Height, b.Width);
                    results.Add(r);
                }
            }

            // 同文本去重并合并包围框（多行/双向命中）
            List<Result> dedup = [];
            foreach (Result r in results)
            {
                Result ex = dedup.Find(d => d.Text == r.Text && d.TypeName == r.TypeName);
                if (ex == null)
                    dedup.Add(r);
                else
                {
                    int x1 = Math.Min(ex.Bounds.X, r.Bounds.X);
                    int y1 = Math.Min(ex.Bounds.Y, r.Bounds.Y);
                    int x2 = Math.Max(ex.Bounds.X + ex.Bounds.Width, r.Bounds.X + r.Bounds.Width);
                    int y2 = Math.Max(ex.Bounds.Y + ex.Bounds.Height, r.Bounds.Y + r.Bounds.Height);
                    ex.Bounds = new Rect(x1, y1, x2 - x1, y2 - y1);
                }
            }
            return dedup;
        }

        private static void Scan(Mat gray, List<Result> results)
        {
            int w = gray.Cols, h = gray.Rows;
            if (w < 80 || h < 30)
                return; // 尺寸不足以容纳 95 模块条码

            using (Mat bin = new())
            {
                // 自适应阈值 + 反二值：深色条 → 255
                Cv2.AdaptiveThreshold(gray, bin, 255, AdaptiveThresholdTypes.MeanC,
                    ThresholdTypes.BinaryInv, 31, 10);

                byte[] data = new byte[w * h];
                Marshal.Copy(bin.Data, data, 0, w * h);

                int yStart = h / 6, yEnd = h * 5 / 6;
                int step = Math.Max(2, (yEnd - yStart) / 80);
                for (int y = yStart; y <= yEnd; y += step)
                    TryDecodeRow(data, w, y, results);
            }
        }

        private static void TryDecodeRow(byte[] data, int w, int y, List<Result> results)
        {
            int baseIdx = y * w;

            // 游程编码
            List<int> runVal = [];
            List<int> runLen = [];
            List<int> runStart = [];
            int cur = data[baseIdx] >= 128 ? 1 : 0;
            int len = 1, start = 0;
            for (int x = 1; x < w; x++)
            {
                int v = data[baseIdx + x] >= 128 ? 1 : 0;
                if (v == cur)
                {
                    len++;
                }
                else
                {
                    runVal.Add(cur);
                    runLen.Add(len);
                    runStart.Add(start);
                    cur = v;
                    len = 1;
                    start = x;
                }
            }
            runVal.Add(cur);
            runLen.Add(len);
            runStart.Add(start);

            // 遍历条(黑)游程锚点：依次尝试 EAN-13/8、Code 128、Code 39
            int i = 0;
            while (i < runVal.Count)
            {
                if (runVal[i] != 1)
                {
                    i++;
                    continue;
                }
                int before = results.Count;
                TryDecodeFrom(runVal, runLen, runStart, i, y, results);
                TryCode128From(runVal, runLen, runStart, i, y, results);
                TryCode39From(runVal, runLen, runStart, i, y, results);
                if (results.Count > before)
                {
                    // 该游程序列被一个条码占用：跳过其覆盖范围，避免重复解码
                    Rect b = results[results.Count - 1].Bounds;
                    int xEnd = b.X + b.Width + 2;
                    while (i < runStart.Count && runStart[i] < xEnd)
                        i++;
                    continue;
                }
                i++;
            }
        }

        /// <summary>从条游程序列起解码 EAN-13/UPC-A（59 游程）或 EAN-8（43 游程）</summary>
        private static void TryDecodeFrom(List<int> runVal, List<int> runLen, List<int> runStart,
            int i, int y, List<Result> results)
        {
            int total = runVal.Count;

            // EAN 左护线预检：黑/白/黑 三段近似等宽
            if (i + 2 >= total)
                return;
            int g1 = runLen[i], g2 = runLen[i + 1], g3 = runLen[i + 2];
            if (g2 > g1 * 2.5 || g2 * 2.5 < g1 || g3 > g1 * 2.5 || g3 * 2.5 < g1
                || g3 > g2 * 2.5 || g3 * 2.5 < g2 || (g1 + g2 + g3) / 3.0 < 1.0)
                return;

            foreach (int kind in new[] { 13, 8 })
            {
                int leftDigits = kind == 13 ? 6 : 4;
                int lo = kind == 13 ? 1 : 0;              // 数字在 digits[] 中的起始偏移
                int centerIdx = i + 3 + (leftDigits * 4);   // 中心护线 5 游程
                int rightIdx = centerIdx + 5;             // 右侧数据 4×N 游程
                int lastIdx = rightIdx + (leftDigits * 4) + 2; // 右护线最后一个游程
                if (lastIdx >= total)
                    continue;

                // 游程颜色结构：护线 101 / 中心 01010 / 右护线 101
                if (runVal[i] != 1 || runVal[i + 1] != 0 || runVal[i + 2] != 1)
                    continue;
                if (runVal[centerIdx] != 0 || runVal[centerIdx + 1] != 1 || runVal[centerIdx + 2] != 0
                    || runVal[centerIdx + 3] != 1 || runVal[centerIdx + 4] != 0)
                    continue;
                if (runVal[lastIdx - 2] != 1 || runVal[lastIdx - 1] != 0 || runVal[lastIdx] != 1)
                    continue;

                char[] digits = new char[kind];
                string parity = "";
                double lastMod = 0;
                bool ok = true;

                // 左侧数据：每 4 个游程（空黑白黑）一位数字，宽度和恒为 7 模块
                for (int d = 0; d < leftDigits && ok; d++)
                {
                    int idx = i + 3 + (d * 4);
                    double mod = (runLen[idx] + runLen[idx + 1] + runLen[idx + 2] + runLen[idx + 3]) / 7.0;
                    string key = NormKey(runLen, idx, mod);
                    if (key == null)
                    {
                        ok = false;
                        break;
                    }
                    if (LeftMapL.TryGetValue(key, out int lv))
                    {
                        digits[lo + d] = (char)('0' + lv);
                        parity += "L";
                    }
                    else if (kind == 13 && LeftMapG.TryGetValue(key, out int gv))
                    {
                        digits[lo + d] = (char)('0' + gv);
                        parity += "G";
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                    lastMod = mod;
                }
                if (!ok)
                    continue;

                // 中心护线每段 ≈1 模块
                bool centerOk = true;
                for (int k = 0; k < 5; k++)
                    if (runLen[centerIdx + k] > lastMod * 2.2)
                        centerOk = false;
                if (!centerOk)
                    continue;

                // 右侧数据：每 4 个游程（黑白黑白）一位数字
                for (int d = 0; d < leftDigits && ok; d++)
                {
                    int idx = rightIdx + (d * 4);
                    double mod = (runLen[idx] + runLen[idx + 1] + runLen[idx + 2] + runLen[idx + 3]) / 7.0;
                    string key = NormKey(runLen, idx, mod);
                    int rv;
                    if (key == null || !RightMapR.TryGetValue(key, out rv))
                    {
                        ok = false;
                        break;
                    }
                    digits[lo + leftDigits + d] = (char)('0' + rv);
                    lastMod = mod;
                }
                if (!ok)
                    continue;

                // 右护线每段 ≈1 模块
                if (runLen[lastIdx - 2] > lastMod * 2.2 || runLen[lastIdx] > lastMod * 2.2)
                    continue;

                string text;
                string typeName;
                if (kind == 13)
                {
                    int first = Array.IndexOf(ParityFirst, parity);
                    if (first < 0)
                        continue;
                    digits[0] = (char)('0' + first);

                    // EAN-13 校验和：从左起第 1 位 ×1、第 2 位 ×3 交替
                    int sum = 0;
                    for (int k = 0; k < 12; k++)
                        sum += (digits[k] - '0') * (k % 2 == 0 ? 1 : 3);
                    if ((10 - (sum % 10)) % 10 != digits[12] - '0')
                        continue;

                    text = new string(digits);
                    typeName = "EAN-13";
                    if (first == 0)
                    {
                        // 首位 0 的 EAN-13 即 UPC-A，按 12 位呈现
                        typeName = "UPC-A";
                        text = text[1..];
                    }
                }
                else
                {
                    // EAN-8 校验和：从左起第 1 位 ×3、第 2 位 ×1 交替
                    int sum = 0;
                    for (int k = 0; k < 7; k++)
                        sum += (digits[k] - '0') * (k % 2 == 0 ? 3 : 1);
                    if ((10 - (sum % 10)) % 10 != digits[7] - '0')
                        continue;

                    text = new string(digits);
                    typeName = "EAN-8";
                }

                int x0 = runStart[i];
                int x1 = runStart[lastIdx] + runLen[lastIdx];
                results.Add(new Result
                {
                    Text = text,
                    TypeName = typeName,
                    Bounds = new Rect(x0, y, x1 - x0, 1)
                });
            }
        }

        /// <summary>4 个游程宽度按模块宽归一化（各 1~4 且总和 7），返回宽度键；非法返回 null</summary>
        private static string NormKey(List<int> runLen, int idx, double mod)
        {
            int[] nr = new int[4];
            int sum = 0;
            for (int k = 0; k < 4; k++)
            {
                int v = (int)Math.Round(runLen[idx + k] / mod);
                if (v < 1 || v > 4)
                    return null;
                nr[k] = v;
                sum += v;
            }
            if (sum != 7)
                return null;
            return "" + nr[0] + nr[1] + nr[2] + nr[3];
        }

        // ==================== Code 128 ====================

        /// <summary>
        /// 从条游程锚点起解码 Code 128：起如符 103~105 锚定，逐符号查宽度表，
        /// 最后一个数据符号为 mod-103 校验符，终止符 106（7 游程）收尾。
        /// </summary>
        private static void TryCode128From(List<int> runVal, List<int> runLen, List<int> runStart,
            int i, int y, List<Result> results)
        {
            int total = runVal.Count;
            if (i + 6 > total)
                return;

            int sum6 = 0;
            for (int k = 0; k < 6; k++) sum6 += runLen[i + k];
            // 安静区：起如符前空白 ≥ 起如符宽度一半
            if (i > 0 && runLen[i - 1] * 2 < sum6)
                return;

            int startCode = MatchCode128At(runLen, i, 6, 11);
            if (startCode < 103 || startCode > 105)
                return;

            List<int> values = [startCode];
            int pos = i + 6;
            int endRun = -1;
            while (pos + 6 <= total)
            {
                // 先试终止符（7 游程 13 模块）
                if (pos + 7 <= total && MatchCode128At(runLen, pos, 7, 13) == 106)
                {
                    endRun = pos + 6;
                    break;
                }
                int v = MatchCode128At(runLen, pos, 6, 11);
                if (v < 0 || v >= 103)
                    return;
                values.Add(v);
                pos += 6;
            }
            if (endRun < 0 || values.Count < 2)
                return;

            // 终止符后空白 ≥ 终止符宽度一半（行尾则免）
            int stopSum = 0;
            for (int k = 0; k < 7; k++) stopSum += runLen[endRun - 6 + k];
            if (endRun + 1 < total && runLen[endRun + 1] * 2 < stopSum)
                return;

            // mod-103 校验：values 最后一个符号是校验符
            int mult = 0, sum = startCode;
            for (int k = 1; k < values.Count; k++)
            {
                mult++;
                sum += mult * values[k];
            }
            int check = values[values.Count - 1];
            sum -= mult * check;
            if (sum % 103 != check)
                return;

            // 文本解码（起始符与校验符之间的数据符号）
            string text = DecodeCode128Text(values, startCode);
            if (string.IsNullOrEmpty(text))
                return;

            int x0 = runStart[i];
            int x1 = runStart[endRun] + runLen[endRun];
            results.Add(new Result
            {
                Text = text,
                TypeName = "CODE-128",
                Bounds = new Rect(x0, y, x1 - x0, 1)
            });
        }

        /// <summary>
        /// 把 idx 起 n 个游程宽度整数归一化到 modules 模块（取整余数补偿到偏差最大的
        /// 游程）并查 Code 128 宽度表；无匹配返回 -1。
        /// </summary>
        private static int MatchCode128At(List<int> runLen, int idx, int n, int modules)
        {
            int sum = 0;
            for (int k = 0; k < n; k++) sum += runLen[idx + k];
            if (sum <= 0)
                return -1;

            double unit = sum / (double)modules;
            int[] v = new int[n];
            double[] frac = new double[n];
            int totalV = 0;
            for (int k = 0; k < n; k++)
            {
                double exact = runLen[idx + k] / unit;
                v[k] = (int)Math.Round(exact);
                if (v[k] < 1 || v[k] > 6)
                    return -1;
                frac[k] = exact - v[k];
                totalV += v[k];
            }

            int diff = modules - totalV;
            while (diff > 0)
            {
                int best = 0;
                for (int k = 1; k < n; k++)
                    if (frac[k] > frac[best]) best = k;
                v[best]++; frac[best] -= 1; totalV++; diff--;
            }
            while (diff < 0)
            {
                int best = -1;
                for (int k = 0; k < n; k++)
                    if (v[k] > 1 && (best < 0 || frac[k] < frac[best])) best = k;
                if (best < 0)
                    return -1;
                v[best]--; frac[best] += 1; totalV--; diff++;
            }

            return Code128Map.TryGetValue(string.Join(",", v), out int val) ? val : -1;
        }

        /// <summary>
        /// 按起始码集翻译数据符号（values[0]=起始符、末位=校验符，均不在翻译范围）：
        /// 99/100/101 切换 C/B/A 码集，102/96/97 为功能符跳过，98 单字符切换 A/B。
        /// </summary>
        private static string DecodeCode128Text(List<int> values, int startCode)
        {
            System.Text.StringBuilder sb = new();
            int set = startCode - 103;   // 0=A 1=B 2=C
            bool shift = false;
            for (int k = 1; k < values.Count - 1; k++)
            {
                int v = values[k];
                if (shift)
                {
                    // SHIFT：仅下一位换 A/B 码集
                    shift = false;
                    int use = set == 0 ? 1 : 0;
                    if (use == 1)
                    {
                        if (v < 96) sb.Append((char)(32 + v));
                        else return null;
                    }
                    else
                    {
                        if (v < 64) sb.Append((char)(32 + v));
                        else return null;
                    }
                    continue;
                }
                if (v == 99) { set = 2; continue; }
                if (v == 100) { set = 1; continue; }
                if (v == 101) { set = 0; continue; }
                if (v == 102 || v == 96 || v == 97) continue;   // FNC1/2/3 功能符
                if (v == 98 && set != 2) { shift = true; continue; }

                if (set == 2)
                {
                    if (v >= 100) return null;
                    sb.Append(v.ToString("00"));
                }
                else if (set == 1)
                {
                    if (v >= 96) return null;
                    sb.Append((char)(32 + v));
                }
                else
                {
                    if (v < 64) sb.Append((char)(32 + v));
                    else if (v >= 96) return null;
                    // 64~95 为控制字符，跳过
                }
            }
            return sb.ToString();
        }

        // ==================== Code 39 ====================

        /// <summary>
        /// 从条游程锚点起解码 Code 39：'*' 起始锚定，每字符 9 游程（条起条止）+
        /// 窄间隔，'*' 收尾，收尾后要求安静区。
        /// </summary>
        private static void TryCode39From(List<int> runVal, List<int> runLen, List<int> runStart,
            int i, int y, List<Result> results)
        {
            int total = runVal.Count;
            if (i + 9 > total)
                return;

            int[] counters = new int[9];
            int sum9 = 0;
            for (int k = 0; k < 9; k++)
            {
                counters[k] = runLen[i + k];
                sum9 += counters[k];
            }
            // 安静区：起始 '*' 前空白 ≥ 星号宽度一半
            if (i > 0 && runLen[i - 1] * 2 < sum9)
                return;
            if (NarrowWidePattern39(counters) != Code39Asterisk)
                return;

            System.Text.StringBuilder sb = new();
            int pos = i + 10;   // 跳过起始 '*'（9 游程）+ 字符间隔空
            int endRun = -1;
            int stopSum = sum9;
            while (pos + 9 <= total)
            {
                for (int k = 0; k < 9; k++)
                    counters[k] = runLen[pos + k];
                int pat = NarrowWidePattern39(counters);
                if (pat == Code39Asterisk)
                {
                    endRun = pos + 8;
                    stopSum = 0;
                    for (int k = 0; k < 9; k++) stopSum += runLen[pos + k];
                    break;
                }
                char c = Pattern39ToChar(pat);
                if (c == '\0')
                    return;
                sb.Append(c);
                pos += 10;
            }
            if (endRun < 0 || sb.Length == 0)
                return;

            // 结束安静区：'*' 后空白 ≥ 星号宽度一半（行尾则免）
            if (endRun + 1 < total && runLen[endRun + 1] * 2 < stopSum)
                return;

            int x0 = runStart[i];
            int x1 = runStart[endRun] + runLen[endRun];
            results.Add(new Result
            {
                Text = sb.ToString(),
                TypeName = "CODE-39",
                Bounds = new Rect(x0, y, x1 - x0, 1)
            });
        }

        /// <summary>
        /// zxing 窄宽聚类：迭代收窄阈值直至恰有 3 个宽游程；任一宽游程 ≥ 1.5×宽均值
        /// 判失败。返回 9 位窄宽位图（MSB 对应首游程）或 -1。
        /// </summary>
        private static int NarrowWidePattern39(int[] counters)
        {
            int maxNarrow = 0;
            while (true)
            {
                int minCounter = int.MaxValue;
                foreach (int c in counters)
                    if (c < minCounter && c > maxNarrow)
                        minCounter = c;
                if (minCounter == int.MaxValue)
                    return -1;
                maxNarrow = minCounter;

                int pattern = 0, wide = 0, wideSum = 0;
                for (int k = 0; k < counters.Length; k++)
                {
                    if (counters[k] > maxNarrow)
                    {
                        pattern |= 1 << (counters.Length - 1 - k);
                        wide++;
                        wideSum += counters[k];
                    }
                }
                if (wide == 3)
                {
                    for (int k = 0; k < counters.Length; k++)
                        if (counters[k] > maxNarrow && counters[k] * 2 >= wideSum)
                            return -1;
                    return pattern;
                }
                if (wide < 3)
                    return -1;
            }
        }

        /// <summary>9 位窄宽位图 → 字母表字符；'*' 或无匹配返回（'\0' 表示失败）</summary>
        private static char Pattern39ToChar(int pattern)
        {
            for (int k = 0; k < Code39Encodings.Length; k++)
                if (Code39Encodings[k] == pattern)
                    return Code39Alphabet[k];
            if (pattern == Code39Asterisk)
                return '*';
            return '\0';
        }
    }
}
