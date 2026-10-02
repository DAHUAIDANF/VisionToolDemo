using System;
using System.IO;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// Tesseract OCR 引擎的薄封装（可选增强路径）。
    ///
    /// 设计原则：**Tesseract 不可用时不能让算子崩**。
    /// 这个类把所有失败（没装原生库、缺 traineddata、语言包不存在）
    /// 收敛成 TryInit 返回 false + 一条可读原因，由算子回退到内置字模引擎。
    /// 工业现场经常出现"开发机能跑、产线机器少拷了 tessdata"的情况，
    /// 直接抛异常会让整条流水线停摆，而那往往不是使用者的错。
    ///
    /// tessdata 目录的查找顺序：
    ///   1. 环境变量 TESSDATA_PREFIX
    ///   2. 程序目录下的 tessdata\
    ///   3. 程序目录的上一级放 tessdata\（从 IDE 运行时常见）
    /// </summary>
    public sealed class TesseractEngine : IDisposable
    {
        private Tesseract.TesseractEngine _engine;
        private readonly object _lock = new();

        /// <summary>是否已成功初始化</summary>
        public bool Ready { get; private set; }

        /// <summary>初始化失败/成功的原因说明（供 UI 显示）</summary>
        public string Status { get; private set; } = "未初始化";

        /// <summary>实际使用的 tessdata 目录</summary>
        public string DataPath { get; private set; } = "";

        /// <summary>语言（如 "eng"、"chi_sim"、"eng+chi_sim"）</summary>
        public string Language { get; private set; } = "";

        /// <summary>
        /// 解析 tessdata 目录。找不到返回 null。
        /// 判定标准是"目录里至少有一个 .traineddata"，只看目录存在会误判。
        /// </summary>
        public static string ResolveDataPath()
        {
            var cands = new System.Collections.Generic.List<string>();

            string env = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
            if (!string.IsNullOrWhiteSpace(env))
            {
                cands.Add(env);
                cands.Add(Path.Combine(env, "tessdata"));
            }

            string baseDir = AppContext.BaseDirectory;
            cands.Add(Path.Combine(baseDir, "tessdata"));
            cands.Add(baseDir);

            var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            if (parent != null)
            {
                cands.Add(Path.Combine(parent.FullName, "tessdata"));
                var gp = parent.Parent;
                if (gp != null) cands.Add(Path.Combine(gp.FullName, "tessdata"));
            }

            foreach (string c in cands)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(c) || !Directory.Exists(c)) continue;
                    if (Directory.GetFiles(c, "*.traineddata").Length > 0) return c;
                }
                catch { /* 权限/路径异常直接跳过 */ }
            }
            return null;
        }

        /// <summary>
        /// 尝试初始化。失败时不抛异常，只记录 Status。
        /// </summary>
        public bool TryInit(string language)
        {
            if (Ready) return true;
            Language = string.IsNullOrWhiteSpace(language) ? "eng" : language;

            DataPath = ResolveDataPath();
            if (DataPath == null)
            {
                Status = "未找到 tessdata 目录（缺少 *.traineddata 语言包）。" +
                         "请把 eng.traineddata 放到程序目录的 tessdata\\ 下，或设置环境变量 TESSDATA_PREFIX。";
                return false;
            }

            // 校验请求的语言包是否都在
            var missing = new System.Collections.Generic.List<string>();
            foreach (string lang in Language.Split('+'))
            {
                string f = Path.Combine(DataPath, lang.Trim() + ".traineddata");
                if (!File.Exists(f)) missing.Add(lang.Trim());
            }
            if (missing.Count > 0)
            {
                Status = string.Format("tessdata 目录 {0} 中缺少语言包: {1}",
                    DataPath, string.Join(", ", missing));
                return false;
            }

            try
            {
                _engine = new Tesseract.TesseractEngine(DataPath, Language, Tesseract.EngineMode.Default);
                _engine.DefaultPageSegMode = Tesseract.PageSegMode.SingleLine;   // 单行兜底，Recognize 按 multiLine 每次覆盖
                Ready = true;
                Status = string.Format("已启用 Tesseract ({0}, 语言 {1})", DataPath, Language);
                return true;
            }
            catch (Exception ex)
            {
                // 真实原因可能被 TargetInvocationException 等包装，逐层展开记全，
                // 否则排障时只看到 "target of an invocation" 一头雾水
                var chain = new System.Text.StringBuilder();
                Exception cur = ex;
                while (cur != null)
                {
                    if (chain.Length > 0) chain.Append(" <- ");
                    chain.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                    cur = cur.InnerException;
                }
                Status = "Tesseract 初始化失败: " + chain;
                _engine?.Dispose();
                _engine = null;
                Ready = false;
                return false;
            }
        }

        /// <summary>
        /// 识别一张已预处理好的图（白字黑底或黑字白底均可，Tesseract 自己二值化）。
        /// 返回文本与平均置信度（0~1）；未就绪时返回 null。
        /// multiLine=true：PageSegMode 切到 SingleBlock，按文本块内自动分行，
        /// 返回文本**保留换行符**（多行识别用，行与行用 \n 分隔）；
        /// multiLine=false：SingleLine 单行模式，抹掉换行符。
        /// </summary>
        public string Recognize(Mat gray, out double confidence, bool multiLine)
        {
            confidence = double.NaN;
            if (!Ready || _engine == null || gray == null || gray.Empty()) return null;

            // Tesseract 需要单通道 8 位。PixConverter 只吃 8U，先确保类型正确。
            Mat work = gray;
            bool dispose = false;
            if (gray.Type() != MatType.CV_8UC1)
            {
                work = new Mat();
                if (gray.Channels() == 3) Cv2.CvtColor(gray, work, ColorConversionCodes.BGR2GRAY);
                else gray.ConvertTo(work, MatType.CV_8UC1);
                dispose = true;
            }

            try
            {
                // 这个版本的托管封装**没有** PixConverter（早期版本才有，
                // 现在只提供 Pix.LoadFromMemory / LoadFromFile）。
                // 所以走"编码成 PNG 再让 leptonica 解码"这条路：
                // 多一次编解码，但这是该版本唯一可用的内存传递方式。
                byte[] png = work.ImEncode(".png");

                // 引擎不是线程安全的，且可能被 UI 多次触发，串行化保护
                lock (_lock)
                {
                    // 版面模式按多行开关切换：多行=SingleBlock（块内自动分行），单行=SingleLine
                    _engine.DefaultPageSegMode = multiLine
                        ? Tesseract.PageSegMode.SingleBlock
                        : Tesseract.PageSegMode.SingleLine;

                    using Tesseract.Pix pix = Tesseract.Pix.LoadFromMemory(png);
                    using Tesseract.Page page = _engine.Process(pix);
                    string text = page.GetText() ?? "";
                    float conf = page.GetMeanConfidence();
                    confidence = float.IsNaN(conf) ? double.NaN : conf;
                    // 多行：保留换行符，行与行用 \n 分隔；单行：抹掉换行（原行为不变）
                    if (multiLine) return text.Replace("\r", "");
                    return text.Replace("\r", "").Replace("\n", "").Trim();
                }
            }
            catch (Exception ex)
            {
                Status = "Tesseract 识别异常: " + ex.Message;
                return null;
            }
            finally
            {
                if (dispose) work.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _engine?.Dispose();
                _engine = null;
                Ready = false;
            }
        }
    }
}
