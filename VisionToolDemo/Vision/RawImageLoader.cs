using System;
using System.IO;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// RAW 原始像素数据参数（工业相机/采集卡导出的裸数据，如 .raw 文件）。
    /// 支持 灰度 / Bayer / RGB，8 / 16 / 24 位；16 位先按满量程归一化到 8 位，
    /// 24 位按 3 字节/像素解包，保证与现有 8 位图像处理流水线兼容；Bayer 自动去马赛克为 BGR。
    /// </summary>
    public sealed class RawImageParams
    {
        // 默认按常见工业相机幅面 3264x2448（约 800 万像素）
        public int Width { get; set; } = 3264;
        public int Height { get; set; } = 2448;
        public int BitDepth { get; set; } = 8;      // 8 / 16 / 24
        public string Format { get; set; } = "灰度"; // 灰度 / BayerRG / BayerGR / BayerGB / BayerBG / RGB / BGR
        public long Offset { get; set; }             // 文件头字节数（像素数据从该偏移开始）
        public int FullScale { get; set; } = 65535;  // 16 位归一化满量程（1023/4095/65535），非 16 位时忽略
        public bool BigEndian { get; set; }          // 16 位数据的字节序（默认小端）
        /// <summary>24 位数据在内存中的排列：true = BGR（默认），false = RGB</summary>
        public bool Is24BitBgr { get; set; } = true;

        /// <summary>单像素字节数。24 位固定按 3 字节/像素（每通道 8 位）。</summary>
        public int BytesPerPixel => BitDepth switch
        {
            24 => 3,
            16 => 2 * (Format is "RGB" or "BGR" ? 3 : 1),
            _ => Format is "RGB" or "BGR" ? 3 : 1,
        };

        /// <summary>像素数据区所需字节数（不含偏移）</summary>
        public long ExpectedBytes => (long)Width * Height * BytesPerPixel;

        public bool Is16Bit => BitDepth == 16;

        /// <summary>24 位彩色（每通道 8 位，共 3 字节/像素）</summary>
        public bool Is24Bit => BitDepth == 24;

        public bool IsBayer => Format is "BayerRG" or "BayerGR" or "BayerGB" or "BayerBG";
    }

    public static class RawImageLoader
    {
        /// <summary>
        /// 按文件大小推断最可能的位深，避免"16 位文件按 8 位读"这种**不报错但结果是坏的**情况：
        /// 8 位只需要字节数的一半，尺寸检查会通过，于是把 16 位像素的低字节当整像素，
        /// 图像被撕裂成噪声（实测同一文件：按 8 位读 std=81.9 的噪声，按 16 位读才是 std=44.0 的正常图）。
        ///
        /// 判据：在候选位深里找"期望字节数与文件大小完全相等"的那个；都不完全相等时，
        /// 退回"能装下且余量最小"的位深（允许文件尾有多余字节，如附加段/对齐填充）。
        /// </summary>
        /// <param name="fileLength">文件总字节数</param>
        /// <param name="width">图像宽度（像素）</param>
        /// <param name="height">图像高度（像素）</param>
        /// <param name="offset">数据起始偏移（字节）</param>
        /// <param name="isColor">是否按 3 通道彩色计算（影响 8/16 位的每像素字节数）</param>
        /// <param name="candidates">候选位深，默认 8/16/24</param>
        /// <returns>推断出的位深；无法判断时返回 candidates 里的第一个</returns>
        public static int DetectBitDepth(long fileLength, int width, int height, long offset = 0,
            bool isColor = false, int[] candidates = null)
        {
            if (width <= 0 || height <= 0) return 8;
            candidates ??= new[] { 8, 16, 24 };

            long avail = fileLength - offset;
            if (avail <= 0) return candidates[0];

            long px = (long)width * height;
            long bestFit = 0, bestSlack = long.MaxValue;
            foreach (int depth in candidates)
            {
                long need = px * BytesFor(depth, isColor);
                if (need == avail) return depth;      // 完全匹配，最可信
                if (need <= avail && avail - need < bestSlack)
                {
                    bestSlack = avail - need;
                    bestFit = depth;
                }
            }
            return bestFit != 0 ? (int)bestFit : candidates[0];
        }

        /// <summary>
        /// 采样 16 位数据，按实际最大值推断归一化满量程（1023=10bit / 4095=12bit / 65535=16bit）。
        /// 为什么必须自动推断：raw 常是 10/12 位相机数据，但对齐成 16 位存放。若满量程按 65535 算，
        /// 164~1004 的实际范围会被压到 0~4 灰阶——图像全黑，后续算子全部失效。
        /// 实测同一文件：满量程 65535 → 均值 2.1；满量程 1023 → 均值 137.1。
        /// </summary>
        /// <returns>1023 / 4095 / 65535</returns>
        public static int DetectFullScale16(string path, RawImageParams p, int samples = 200000)
        {
            try
            {
                long need = p.ExpectedBytes;
                using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < p.Offset + need) return 65535;
                fs.Seek(p.Offset, SeekOrigin.Begin);

                long px = (long)p.Width * p.Height;
                int channels = p.Format is "RGB" or "BGR" ? 3 : 1;
                long stride = Math.Max(1, (px * channels) / samples);

                byte[] pair = new byte[2];
                int maxVal = 0;
                for (long i = 0; i < px; i += stride)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        fs.Seek(p.Offset + ((i * channels) + c) * 2, SeekOrigin.Begin);
                        if (fs.Read(pair, 0, 2) < 2) break;
                        int v = p.BigEndian ? (pair[0] << 8) | pair[1] : pair[1] << 8 | pair[0];
                        if (v > maxVal) maxVal = v;
                    }
                }

                if (maxVal <= 0) return 65535;
                if (maxVal <= 1023) return 1023;
                if (maxVal <= 4095) return 4095;
                return 65535;
            }
            catch
            {
                return 65535;
            }
        }

        /// <summary>按满量程反推的等效位深（仅用于界面提示）</summary>
        public static int BitsForFullScale(int fullScale) => fullScale switch
        {
            1023 => 10,
            4095 => 12,
            _ => 16,
        };

        /// <summary>指定位深与通道类型下的每像素字节数</summary>
        private static int BytesFor(int bitDepth, bool isColor) => bitDepth switch
        {
            24 => 3,
            16 => 2 * (isColor ? 3 : 1),
            _ => isColor ? 3 : 1,
        };

        /// <summary>
        /// 从 .raw 裸数据文件构造 Mat（输出为 8UC1 或 8UC3，Bayer 已转 BGR）。
        /// 失败时返回 false，并给出具体原因。
        /// </summary>
        public static bool TryLoad(string path, RawImageParams p, out Mat mat, out string error)
        {
            mat = null;
            error = null;
            if (p == null || p.Width <= 0 || p.Height <= 0 || p.Offset < 0)
            {
                error = "参数无效（宽高必须大于 0，偏移不能为负）";
                return false;
            }

            long need = p.ExpectedBytes;
            try
            {
                long fileLen = new FileInfo(path).Length;
                if (fileLen < p.Offset + need)
                {
                    error = $"文件大小不足：需要 {p.Offset + need} 字节（偏移 {p.Offset} + 数据 {need}），实际 {fileLen} 字节";
                    return false;
                }

                byte[] buf = new byte[need];
                using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(p.Offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < buf.Length)
                    {
                        error = "读取数据不完整";
                        return false;
                    }
                }

                if (p.Is24Bit)
                {
                    // 24 位 = 每像素 3 字节、每通道 8 位，无需归一化，直接按 8UC3 解释。
                    // 内存排列由 Is24BitBgr 决定：BGR 直接可用，RGB 需换成 BGR 供后续算子使用。
                    using Mat raw24 = Mat.FromPixelData(p.Height, p.Width, MatType.CV_8UC3, buf);
                    if (p.Is24BitBgr)
                        mat = raw24.Clone();
                    else
                    {
                        mat = new Mat();
                        Cv2.CvtColor(raw24, mat, ColorConversionCodes.RGB2BGR);
                    }
                }
                else if (p.Is16Bit)
                {
                    if (p.BigEndian)
                        SwapBytes(buf);

                    double alpha = 255.0 / Math.Max(1, p.FullScale);
                    if (p.Format is "RGB" or "BGR")
                    {
                        using Mat raw16 = Mat.FromPixelData(p.Height, p.Width, MatType.CV_16UC3, buf);
                        using Mat rgb8 = new();
                        Cv2.ConvertScaleAbs(raw16, rgb8, alpha, 0);
                        if (p.Format == "RGB")
                        {
                            mat = new Mat();
                            Cv2.CvtColor(rgb8, mat, ColorConversionCodes.RGB2BGR);
                        }
                        else
                            mat = rgb8.Clone();
                    }
                    else
                    {
                        using Mat raw16 = Mat.FromPixelData(p.Height, p.Width, MatType.CV_16UC1, buf);
                        using Mat gray8 = new();
                        Cv2.ConvertScaleAbs(raw16, gray8, alpha, 0);
                        if (p.IsBayer)
                        {
                            mat = new Mat();
                            Cv2.CvtColor(gray8, mat, BayerCode(p.Format));
                        }
                        else
                            mat = gray8.Clone();
                    }
                }
                else
                {
                    if (p.Format is "RGB" or "BGR")
                    {
                        using Mat raw8 = Mat.FromPixelData(p.Height, p.Width, MatType.CV_8UC3, buf);
                        if (p.Format == "RGB")
                        {
                            mat = new Mat();
                            Cv2.CvtColor(raw8, mat, ColorConversionCodes.RGB2BGR);
                        }
                        else
                            mat = raw8.Clone();
                    }
                    else
                    {
                        using Mat raw8 = Mat.FromPixelData(p.Height, p.Width, MatType.CV_8UC1, buf);
                        if (p.IsBayer)
                        {
                            mat = new Mat();
                            Cv2.CvtColor(raw8, mat, BayerCode(p.Format));
                        }
                        else
                            mat = raw8.Clone();
                    }
                }

                if (mat.Empty())
                {
                    mat.Dispose();
                    mat = null;
                    error = "图像数据为空";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                mat?.Dispose();
                mat = null;
                error = ex.Message;
                return false;
            }
        }

        private static ColorConversionCodes BayerCode(string fmt) => fmt switch
        {
            "BayerRG" => ColorConversionCodes.BayerRG2BGR,
            "BayerGR" => ColorConversionCodes.BayerGR2BGR,
            "BayerGB" => ColorConversionCodes.BayerGB2BGR,
            _ => ColorConversionCodes.BayerBG2BGR,
        };

        /// <summary>相邻两字节交换（大端 → 小端）</summary>
        private static void SwapBytes(byte[] buf)
        {
            for (int i = 0; i + 1 < buf.Length; i += 2)
                (buf[i], buf[i + 1]) = (buf[i + 1], buf[i]);
        }
    }
}
