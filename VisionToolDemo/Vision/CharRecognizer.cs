using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 轻量字符识别内核（无外部依赖）。
    ///
    /// 为什么不用 Tesseract：本项目离线运行、且工业场景多为**固定字库**
    /// （批次号/日期/型号喷码），通用 OCR 引擎既要额外 30MB+ 语言包，
    /// 又会把 "0/O"、"1/I"、"8/B" 这类工业码认错——在公差/序列号场景是致命的。
    ///
    /// 本内核的做法：
    ///   1. 用 6 种内置矢量字模（ASCII 大写字母 + 数字 + 常用符号）生成参考图，
    ///      对每张参考图算归一化特征；
    ///   2. 对每个待识别字符，规范化到 20x28 的二值图，算同样的特征；
    ///   3. 用"特征加权欧氏距离"匹配最近字模。
    ///
    /// 特征不是逐像素比对，而是 7 类**形状矩**：
    ///   · 行/列投影重心  —— 区分 T/L/F（横竖笔画分布不同）
    ///   · 水平/垂直穿越数 —— 区分 O/0（无穿越）与 8/B（多次穿越）
    ///   · 孔洞数          —— 区分 0/O/D（1个孔）与 C/G（0个孔）
    ///   · 端点/交点计数   —— 区分 7/1、5/S
    /// 逐像素比对对 1px 的笔画抖动过于敏感（工业喷码必然抖动），
    /// 形状矩对抖动稳、对结构差异敏感，正好匹配这个场景。
    /// </summary>
    public static class CharRecognizer
    {
        /// <summary>字模位图尺寸：宽 x 高</summary>
        public const int GlyphW = 20;
        public const int GlyphH = 28;

        /// <summary>可选字符集：数字 / 大写字母 / 两者</summary>
        public const string Digits = "0123456789";
        public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public const string Common = "-.:/";

        private static readonly Dictionary<string, double[]> _cache = [];

        /// <summary>一次识别结果</summary>
        public readonly struct Match
        {
            public readonly char Ch;
            public readonly double Score;      // 越小越像
            public readonly double Confidence; // 0~1，越大越可信
            public Match(char ch, double score, double confidence)
            { Ch = ch; Score = score; Confidence = confidence; }
        }

        /// <summary>
        /// 识别单个字符位图（白字黑底，已二值化 0/255）。
        /// charset 为空时用数字+大写字母。
        /// </summary>
        public static Match Recognize(Mat bin, string charset, out double[] features)
        {
            features = NormalizeAndFeature(bin);
            if (charset == null || charset.Length == 0) charset = Digits + Upper;

            char bestCh = '?';
            double bestScore = double.MaxValue;
            double secondScore = double.MaxValue;
            foreach (char c in charset)
            {
                double[] refF = GetGlyphFeature(c);
                if (refF == null) continue;
                double s = WeightedDistance(features, refF);
                if (s < bestScore) { secondScore = bestScore; bestScore = s; bestCh = c; }
                else if (s < secondScore) secondScore = s;
            }
            if (bestScore >= double.MaxValue / 2)
                return new Match('?', double.NaN, 0);

            // 置信度：与次优的分离度。两个候选一样像 -> 低置信
            double conf = secondScore >= double.MaxValue / 2
                ? 1.0
                : Math.Clamp((secondScore - bestScore) / Math.Max(1e-6, secondScore), 0.0, 1.0);
            return new Match(bestCh, bestScore, conf);
        }

        /// <summary>取（并缓存）某字符的标准特征</summary>
        public static double[] GetGlyphFeature(char c)
        {
            string key = c.ToString();
            if (_cache.TryGetValue(key, out double[] cached)) return cached;
            using Mat g = RenderGlyph(c);
            if (g == null || g.Empty())
            {
                _cache[key] = null;
                return null;
            }
            double[] f = NormalizeAndFeature(g);
            _cache[key] = f;
            return f;
        }

        /// <summary>
        /// 规范化为 GlyphW x GlyphH 的二值图并提取特征向量。
        /// 先裁到字符的外接框再缩放——否则字符在图块里的位置/大小差异会主导距离。
        /// </summary>
        public static double[] NormalizeAndFeature(Mat bin)
        {
            using Mat norm = Normalize(bin);
            return ExtractFeatures(norm);
        }

        /// <summary>裁剪到内容外接框后缩放到 GlyphW x GlyphH</summary>
        public static Mat Normalize(Mat bin)
        {
            var dst = new Mat(GlyphH, GlyphW, MatType.CV_8UC1, Scalar.Black);
            if (bin == null || bin.Empty()) return dst;

            using Mat b = new();
            if (bin.Channels() != 1) Cv2.CvtColor(bin, b, ColorConversionCodes.BGR2GRAY);
            else bin.CopyTo(b);

            using Mat bw = new();
            Cv2.Threshold(b, bw, 127, 255, ThresholdTypes.Binary);

            Cv2.FindContours(bw, out Point[][] cs, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            Rect box = new();
            bool found = false;
            foreach (Point[] c in cs)
            {
                if (Cv2.ContourArea(c) < 2) continue;
                Rect r = Cv2.BoundingRect(c);
                if (!found) { box = r; found = true; }
                else
                {
                    int x1 = Math.Min(box.X, r.X), y1 = Math.Min(box.Y, r.Y);
                    int x2 = Math.Max(box.Right, r.Right), y2 = Math.Max(box.Bottom, r.Bottom);
                    box = new Rect(x1, y1, x2 - x1, y2 - y1);
                }
            }
            if (!found) return dst;

            box = new Rect(
                Math.Max(0, box.X), Math.Max(0, box.Y),
                Math.Min(bw.Cols - Math.Max(0, box.X), box.Width),
                Math.Min(bw.Rows - Math.Max(0, box.Y), box.Height));
            if (box.Width < 2 || box.Height < 2) return dst;

            using Mat crop = new(bw, box);
            // 等比缩放到内框（留 2px 边距），保持纵横比——把 "1" 拉成方块会毁掉特征
            double sx = (GlyphW - 4.0) / box.Width;
            double sy = (GlyphH - 4.0) / box.Height;
            double s = Math.Min(sx, sy);
            int nw = Math.Max(1, (int)Math.Round(box.Width * s));
            int nh = Math.Max(1, (int)Math.Round(box.Height * s));
            nw = Math.Min(nw, GlyphW - 2);
            nh = Math.Min(nh, GlyphH - 2);

            using Mat resized = new();
            Cv2.Resize(crop, resized, new Size(nw, nh), 0, 0, InterpolationFlags.Area);
            Cv2.Threshold(resized, resized, 127, 255, ThresholdTypes.Binary);

            int ox = (GlyphW - nw) / 2, oy = (GlyphH - nh) / 2;
            using Mat roi = new(dst, new Rect(ox, oy, nw, nh));
            resized.CopyTo(roi);
            return dst;
        }

        /// <summary>
        /// 特征向量（7 类形状矩，共 14 维），每维归一化到 0~1。
        /// 权重体现在 WeightedDistance 里，不在这里改数值。
        /// </summary>
        public static double[] ExtractFeatures(Mat norm)
        {
            var f = new double[14];
            if (norm == null || norm.Empty()) return f;

            int w = norm.Cols, h = norm.Rows;
            int area = 0;
            int[] rowSum = new int[h];
            int[] colSum = new int[w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (norm.At<byte>(y, x) <= 127) continue;
                    area++; rowSum[y]++; colSum[x]++;
                }
            if (area == 0) return f;

            double inv = 1.0 / area;

            // 0: 面积占比（区分 1 与 8）
            f[0] = (double)area / (w * h);

            // 1~2: 行/列投影重心的归一化位置（区分 T/L、F/E）
            double cgY = 0, cgX = 0;
            for (int y = 0; y < h; y++) cgY += y * rowSum[y];
            for (int x = 0; x < w; x++) cgX += x * colSum[x];
            f[1] = cgY * inv / Math.Max(1, h - 1);
            f[2] = cgX * inv / Math.Max(1, w - 1);

            // 3: 投影二阶矩（行），反映笔画的纵向分布集中度（区分 8/0 与 1/7）
            double my = f[1] * (h - 1), mx = f[2] * (w - 1), vy = 0, vx = 0;
            for (int y = 0; y < h; y++) { double d = y - my; vy += rowSum[y] * d * d; }
            for (int x = 0; x < w; x++) { double d = x - mx; vx += colSum[x] * d * d; }
            f[3] = Math.Sqrt(vy * inv) / Math.Max(1, h - 1);
            f[4] = Math.Sqrt(vx * inv) / Math.Max(1, w - 1);

            // 5~6: 水平/垂直穿越数（归一化）。0 无穿越(1,-)，8 三穿越(水平)
            f[5] = Math.Min(3, CountHorizontalCrossings(norm, h, w)) / 3.0;
            f[6] = Math.Min(3, CountVerticalCrossings(norm, h, w)) / 3.0;

            // 7: 孔洞数（区分 0/O/D[1] 与 C/G[0]，8/B[2]）
            f[7] = Math.Min(2, CountHoles(norm)) / 2.0;

            // 8~11: 四象限面积占比（区分 5/S、2/Z 的笔画落点）
            double q00 = 0, q10 = 0, q01 = 0, q11 = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (norm.At<byte>(y, x) <= 127) continue;
                    bool right = x >= w / 2, bottom = y >= h / 2;
                    if (!right && !bottom) q00++;
                    else if (right && !bottom) q10++;
                    else if (!right && bottom) q01++;
                    else q11++;
                }
            f[8] = q00 * inv; f[9] = q10 * inv; f[10] = q01 * inv; f[11] = q11 * inv;

            // 12: 端点+交点密度（对笔画数敏感，区分 1 与 4/7）
            f[12] = Math.Min(12, CountJunctions(norm)) / 12.0;

            // 13: 上下半面积比（区分 5/S、6/9、2/5 这类镜像对）
            double top = 0, bot = 0;
            for (int y = 0; y < h; y++)
            {
                double s = rowSum[y];
                if (y < h / 2) top += s; else bot += s;
            }
            f[13] = (top + bot) > 0 ? top / (top + bot) : 0.5;

            return f;
        }

        /// <summary>
        /// 加权距离。孔洞数和穿越数权重最高——它们是"结构"特征，
        /// 正是 0/O、8/B、1/7 这些易混对的判别依据；面积/重心权重低，
        /// 因为它们对喷码的粗细变化太敏感。
        /// </summary>
        private static readonly double[] Weights =
        [
            0.6,  // 0 面积占比
            0.8,  // 1 行重心
            0.8,  // 2 列重心
            0.6,  // 3 行二阶矩
            0.6,  // 4 列二阶矩
            2.5,  // 5 水平穿越
            2.5,  // 6 垂直穿越
            3.0,  // 7 孔洞数
            1.0, 1.0, 1.0, 1.0,  // 8~11 四象限
            1.5,  // 12 端点/交点
            1.2,  // 13 上下半比
        ];

        public static double WeightedDistance(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < Weights.Length; i++)
            {
                double d = a[i] - b[i];
                sum += Weights[i] * d * d;
            }
            return Math.Sqrt(sum);
        }

        // ------------------------------------------------------------------ 结构测量

        private static int CountHorizontalCrossings(Mat m, int h, int w)
        {
            int best = 0;
            for (int y = 1; y < h - 1; y++)
            {
                int c = 0; bool inRun = false;
                for (int x = 0; x < w; x++)
                {
                    bool on = m.At<byte>(y, x) > 127;
                    if (on && !inRun) c++;
                    inRun = on;
                }
                if (c > best) best = c;
            }
            return best;
        }

        private static int CountVerticalCrossings(Mat m, int h, int w)
        {
            int best = 0;
            for (int x = 1; x < w - 1; x++)
            {
                int c = 0; bool inRun = false;
                for (int y = 0; y < h; y++)
                {
                    bool on = m.At<byte>(y, x) > 127;
                    if (on && !inRun) c++;
                    inRun = on;
                }
                if (c > best) best = c;
            }
            return best;
        }

        /// <summary>孔洞数：反转后数"不与边界连通"的独立前景块</summary>
        private static int CountHoles(Mat m)
        {
            using Mat inv = new();
            Cv2.BitwiseNot(m, inv);
            Cv2.FindContours(inv, out Point[][] cs, out _, RetrievalModes.CComp,
                ContourApproximationModes.ApproxSimple);
            int holes = 0;
            foreach (Point[] c in cs)
            {
                if (Cv2.ContourArea(c) < 2) continue;
                Rect r = Cv2.BoundingRect(c);
                // 贴边的反色块是"背景"不是"孔"
                if (r.X <= 0 || r.Y <= 0 || r.Right >= m.Cols || r.Bottom >= m.Rows) continue;
                holes++;
            }
            return holes;
        }

        /// <summary>端点数 + 交点数的粗略估计：邻域连通分支数变化</summary>
        private static int CountJunctions(Mat m)
        {
            int count = 0;
            for (int y = 1; y < m.Rows - 1; y++)
                for (int x = 1; x < m.Cols - 1; x++)
                {
                    if (m.At<byte>(y, x) <= 127) continue;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (m.At<byte>(y + dy, x + dx) > 127) n++;
                        }
                    if (n == 1 || n >= 5) count++;
                }
            return count;
        }

        // ------------------------------------------------------------------ 内置字模

        /// <summary>
        /// 用一组线段描绘字符（坐标系 0~1），再栅格化到 GlyphW x GlyphH。
        /// 用矢量笔画而不是点阵字库：同一套定义能缩放到任意尺寸，
        /// 且笔画宽度可直接控制，便于与实拍字符的笔画粗细匹配。
        /// 每个字符是若干折线，坐标 (x, y) 均为 0~1，y 向下。
        /// </summary>
        private static readonly Dictionary<char, float[][]> Strokes = new()
        {
            // 横竖为主（0/1/7 是易混对，笔画定义必须准确）
            ['0'] = [[0.5f,0.06f, 0.78f,0.18f, 0.9f,0.5f, 0.78f,0.82f, 0.5f,0.94f, 0.22f,0.82f, 0.1f,0.5f, 0.22f,0.18f, 0.5f,0.06f]],
            ['1'] = [[0.3f,0.25f, 0.5f,0.06f, 0.5f,0.94f]],
            ['2'] = [[0.15f,0.25f, 0.3f,0.08f, 0.6f,0.08f, 0.8f,0.25f, 0.78f,0.45f, 0.14f,0.94f, 0.86f,0.94f]],
            ['3'] = [[0.15f,0.15f, 0.7f,0.08f, 0.85f,0.28f, 0.55f,0.46f, 0.85f,0.62f, 0.8f,0.85f, 0.3f,0.94f, 0.14f,0.8f]],
            ['4'] = [[0.7f,0.06f, 0.14f,0.66f, 0.9f,0.66f], [0.7f,0.06f, 0.7f,0.94f]],
            ['5'] = [[0.82f,0.08f, 0.25f,0.08f, 0.2f,0.44f, 0.6f,0.4f, 0.86f,0.6f, 0.76f,0.88f, 0.3f,0.94f, 0.14f,0.82f]],
            ['6'] = [[0.78f,0.12f, 0.4f,0.1f, 0.16f,0.5f, 0.16f,0.8f, 0.4f,0.94f, 0.68f,0.9f, 0.8f,0.68f, 0.6f,0.48f, 0.3f,0.5f, 0.16f,0.62f]],
            ['7'] = [[0.12f,0.08f, 0.88f,0.08f, 0.42f,0.94f]],
            ['8'] = [[0.5f,0.06f, 0.24f,0.18f, 0.28f,0.44f, 0.5f,0.5f, 0.74f,0.44f, 0.78f,0.18f, 0.5f,0.06f],
                    [0.5f,0.5f, 0.2f,0.62f, 0.18f,0.84f, 0.5f,0.94f, 0.82f,0.84f, 0.8f,0.62f, 0.5f,0.5f]],
            ['9'] = [[0.78f,0.5f, 0.6f,0.38f, 0.34f,0.42f, 0.22f,0.62f, 0.36f,0.86f, 0.66f,0.9f, 0.86f,0.66f, 0.84f,0.3f, 0.6f,0.1f, 0.28f,0.12f]],
            ['A'] = [[0.08f,0.94f, 0.5f,0.06f, 0.92f,0.94f], [0.24f,0.6f, 0.76f,0.6f]],
            ['B'] = [[0.2f,0.06f, 0.2f,0.94f], [0.2f,0.06f, 0.62f,0.1f, 0.75f,0.28f, 0.62f,0.46f, 0.2f,0.5f],
                    [0.2f,0.5f, 0.68f,0.54f, 0.82f,0.74f, 0.66f,0.92f, 0.2f,0.94f]],
            ['C'] = [[0.84f,0.2f, 0.6f,0.07f, 0.28f,0.14f, 0.12f,0.5f, 0.28f,0.86f, 0.62f,0.94f, 0.86f,0.8f]],
            ['D'] = [[0.2f,0.06f, 0.2f,0.94f], [0.2f,0.06f, 0.6f,0.12f, 0.82f,0.36f, 0.84f,0.64f, 0.62f,0.9f, 0.2f,0.94f]],
            ['E'] = [[0.82f,0.08f, 0.22f,0.08f, 0.22f,0.94f, 0.82f,0.94f], [0.22f,0.5f, 0.7f,0.5f]],
            ['F'] = [[0.82f,0.08f, 0.22f,0.08f, 0.22f,0.94f], [0.22f,0.5f, 0.7f,0.5f]],
            ['G'] = [[0.84f,0.2f, 0.6f,0.07f, 0.28f,0.14f, 0.12f,0.5f, 0.28f,0.86f, 0.62f,0.94f, 0.86f,0.8f, 0.86f,0.56f, 0.58f,0.54f]],
            ['H'] = [[0.18f,0.06f, 0.18f,0.94f], [0.82f,0.06f, 0.82f,0.94f], [0.18f,0.5f, 0.82f,0.5f]],
            ['I'] = [[0.3f,0.08f, 0.7f,0.08f], [0.5f,0.08f, 0.5f,0.94f], [0.3f,0.94f, 0.7f,0.94f]],
            ['J'] = [[0.7f,0.08f, 0.7f,0.76f], [0.7f,0.76f, 0.5f,0.94f, 0.26f,0.86f, 0.2f,0.66f]],
            ['K'] = [[0.2f,0.06f, 0.2f,0.94f], [0.8f,0.08f, 0.2f,0.56f], [0.42f,0.44f, 0.84f,0.94f]],
            ['L'] = [[0.24f,0.06f, 0.24f,0.94f, 0.84f,0.94f]],
            ['M'] = [[0.1f,0.94f, 0.1f,0.06f, 0.5f,0.6f, 0.9f,0.06f, 0.9f,0.94f]],
            ['N'] = [[0.16f,0.94f, 0.16f,0.06f, 0.84f,0.94f, 0.84f,0.06f]],
            ['O'] = [[0.5f,0.06f, 0.78f,0.18f, 0.9f,0.5f, 0.78f,0.82f, 0.5f,0.94f, 0.22f,0.82f, 0.1f,0.5f, 0.22f,0.18f, 0.5f,0.06f]],
            ['P'] = [[0.2f,0.94f, 0.2f,0.06f, 0.66f,0.1f, 0.8f,0.3f, 0.64f,0.52f, 0.2f,0.54f]],
            ['Q'] = [[0.5f,0.06f, 0.78f,0.18f, 0.9f,0.5f, 0.78f,0.82f, 0.5f,0.94f, 0.22f,0.82f, 0.1f,0.5f, 0.22f,0.18f, 0.5f,0.06f],
                    [0.6f,0.66f, 0.92f,0.96f]],
            ['R'] = [[0.2f,0.94f, 0.2f,0.06f, 0.66f,0.1f, 0.8f,0.3f, 0.64f,0.52f, 0.2f,0.54f], [0.5f,0.54f, 0.84f,0.94f]],
            ['S'] = [[0.84f,0.2f, 0.6f,0.07f, 0.3f,0.12f, 0.2f,0.3f, 0.4f,0.46f, 0.66f,0.52f, 0.82f,0.7f, 0.7f,0.9f, 0.36f,0.94f, 0.14f,0.8f]],
            ['T'] = [[0.14f,0.08f, 0.86f,0.08f], [0.5f,0.08f, 0.5f,0.94f]],
            ['U'] = [[0.16f,0.06f, 0.16f,0.7f, 0.34f,0.92f, 0.66f,0.92f, 0.84f,0.7f, 0.84f,0.06f]],
            ['V'] = [[0.1f,0.06f, 0.5f,0.94f, 0.9f,0.06f]],
            ['W'] = [[0.06f,0.06f, 0.26f,0.94f, 0.5f,0.36f, 0.74f,0.94f, 0.94f,0.06f]],
            ['X'] = [[0.14f,0.06f, 0.86f,0.94f], [0.86f,0.06f, 0.14f,0.94f]],
            ['Y'] = [[0.14f,0.06f, 0.5f,0.5f, 0.86f,0.06f], [0.5f,0.5f, 0.5f,0.94f]],
            ['Z'] = [[0.14f,0.08f, 0.86f,0.08f, 0.14f,0.94f, 0.86f,0.94f]],
            ['-'] = [[0.25f,0.52f, 0.75f,0.52f]],
            ['.'] = [[0.48f,0.9f, 0.52f,0.9f, 0.52f,0.94f, 0.48f,0.94f, 0.48f,0.9f]],
            [':'] = [[0.48f,0.34f, 0.52f,0.34f, 0.52f,0.38f, 0.48f,0.38f, 0.48f,0.34f],
                    [0.48f,0.68f, 0.52f,0.68f, 0.52f,0.72f, 0.48f,0.72f, 0.48f,0.68f]],
            ['/'] = [[0.82f,0.06f, 0.18f,0.94f]],
        };

        /// <summary>把某字符的矢量笔画栅格化成 GlyphW x GlyphH 的二值图</summary>
        public static Mat RenderGlyph(char c)
        {
            if (!Strokes.TryGetValue(c, out float[][] segs)) return new Mat();
            var m = new Mat(GlyphH, GlyphW, MatType.CV_8UC1, Scalar.Black);
            // 笔画宽度：字模归一化坐标下的 0.11 -> 约 2px（GlyphW=20）
            int thick = Math.Max(2, (int)Math.Round(GlyphW * 0.115));
            foreach (float[] pl in segs)
            {
                for (int i = 0; i + 3 < pl.Length; i += 2)
                {
                    var p1 = new Point(
                        (int)Math.Round((pl[i] * (GlyphW - 1)) + 0.5),
                        (int)Math.Round((pl[i + 1] * (GlyphH - 1)) + 0.5));
                    var p2 = new Point(
                        (int)Math.Round((pl[i + 2] * (GlyphW - 1)) + 0.5),
                        (int)Math.Round((pl[i + 3] * (GlyphH - 1)) + 0.5));
                    Cv2.Line(m, p1, p2, Scalar.White, thick, LineTypes.AntiAlias);
                }
            }
            Cv2.Threshold(m, m, 127, 255, ThresholdTypes.Binary);
            // 统一裁到内容框再规范化，保证与实拍字符一样"填满"图块
            using Mat norm = Normalize(m);
            norm.CopyTo(m);
            return m;
        }
    }
}
