using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;
using ZXing.Multi;
using ZXing.QrCode;
using ZXing.Datamatrix;
using ZXing.Aztec;
using ZXing.PDF417;
using System.Drawing.Imaging;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 条码/二维码识别算子，不依赖额外的 NuGet 库：
    /// 二维码两级引擎：先 QRCodeDetector（单码，小图 1/2 倍两档），未命中再退回
    /// WeChatQRCode（不加载 CNN 模型文件，走其内置传统检测+zxing 解码，
    /// 对小尺寸/密集/带 Logo 的艺术化码明显更鲁棒）；
    /// 一维码用项目内置的纯 C# 解码器 OneDBarcode（EAN-13 / UPC-A / EAN-8 / Code128 / Code39）——
    /// OpenCvSharp 的 BarcodeDetector 原生实现在当前构建中调用即崩溃，不可用；
    /// 点阵 Data Matrix（金属打点 DMC，QR 引擎与 ZXing 检测器均无法识别）由
    /// 自研 DotMatrix（网格拟合采样）+ Ecc200Decoder（RS 纠错解码）兜底。
    /// 点阵引擎金属表面（DPM）兼容：任意摆放角（[0,90°) 锐度全角度+节距联合搜索）、
    /// 反光/光照梯度（高斯背景除法拉平预处理分支）、浅打点低对比（Otsu 降档阈值+
    /// 判暗阈值放宽重采样）、划痕/拉丝纹理（连通域长宽比剔除）、碎点弱模块
    /// （格点响应 Otsu + 质心命中双判据）。
    /// 实拍图增强（整图杂散纹理 + 高填充率打点）：节距多假设（直方图前 8 个峰逐个试，
    /// 防整图杂散纹理的偏小众数把码点云切碎）、点面积窗按节距² 自适应、粘连点按距离
    /// 变换拆分（含环状点填孔）、外沿杂点用"受支持尺寸枚举"兜住、搜索带墙钟预算，
    /// 无码/极难图最坏耗时可控（见 DotMatrix 文件头）。
    /// ZXing.Net（Apache-2.0，开源免费可商用）增强层：
    ///   · 多码 QR：GenericMultipleBarcodeReader（ZXing Java QRCodeMultiReader 同实现），
    ///     同一画面多个二维码可同时解出（小图 1/2 倍两档）；已检出码时追加多码补充并按外接框去重；
    ///   · 印刷 2D 兜底：未检出任何码时试 DataMatrix / Aztec / PDF417（MultiFormatReader，
    ///     同一缩放档共享一次灰度拷贝与二值化）；
    ///   · 1D 格式兜底：Code128/Code39/Code93/ITF/Codabar/MSI/EAN/UPC/RSS 系列，
    ///     补足自研 OneDBarcode 之外的格式，与既有结果按外接框去重；
    ///   · 误检门槛：结果外接框宽高同时小于 max(6px, 原图短边 1%) 的极小碎片丢弃
    ///     （不能用"短边"判定：ITF 等 1D 码定位点为同一条线上的两点，外接框高度天然 ≈4px）。
    /// 识别性能受"检测边长上限"约束；整个 ZXing 层由参数"ZXing增强"(参数[4]) 整体开关，
    /// 关=回到纯 OpenCV+自研引擎（用于排查疑似 ZXing 误检）。
    /// 结果图标注改用 GDI+ 绘制：中文等非 ASCII 字符正常显示（不再以 ? 代替）。
    /// 金属点阵打码仍由自研 DotMatrix 处理（ZXing 之后最后兜底）。
    /// 性能：大图先整体降采样到 DetMaxSide 再识别（微信引擎传统检测在大图上耗时
    /// 随面积超线性增长，噪声图 8MP 可达分钟级），微信引擎输入再限制到 WeChatMaxSide，
    /// 检出角点按缩放倍率映射回原图坐标。
    /// 检出位置在结果图上以序号标注，解码内容标在序号旁（红色），
    /// 完整内容同时写入提示标签。
    /// 注意：ZXing.Net 0.16.10 net8.0 构建的解码方法为小写命名（decode/decodeMultiple/reset），
    /// 与 ZXing Java 上游一致；属性/编码 API（BarcodeWriterGeneric.Encode 等）仍为大写。
    /// </summary>
    public class BarcodeTask : IVisionTask, IResultReporter
    {
        public string TaskName => "条码/二维码识别";

        /// <summary>整体检测的默认输入边长上限（像素），大图按此缩小；参数[0] 可覆盖（0=不缩放）</summary>
        private const float DetMaxSide = 1600f;

        /// <summary>微信引擎输入的边长上限（其传统检测器在大图上耗时失控）</summary>
        private const float WeChatMaxSide = 600f;

        /// <summary>跳过 2 倍放大档的输入边长阈值（再放大就太耗时/无意义）</summary>
        private const float Tier2MaxSide = 1200f;

        private const float WeChatTier2MaxSide = 400f;

        /// <summary>单个识别结果：解码内容、类型（QR/EAN-13/…）、四角坐标（输入图像局部坐标）</summary>
        public class CodeResult
        {
            public string Text = "";
            public string TypeName = "";
            public Point2f[] Corners = [];
        }

        /// <summary>最近一次 Execute 的识别结果，供 UI 层读取显示</summary>
        public List<CodeResult> LastResults { get; } = [];

        /// <summary>最近一次 Execute 的结果摘要（条码识别: …），提示标签与运行完成弹窗共用</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次 Execute 中 ZXing 层未预期异常的信息（常规"未命中"不计入），供排障</summary>
        public string LastZxingError { get; private set; } = "";

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                // 整体检测的输入边长上限（像素）：0=不缩放；越大越能保住小码细节，
                // 微信 QR 引擎在大图上耗时越长。点阵兜底不受此值影响（固定原分辨率）。
                ParamName = "检测边长上限",
                Min = 0,
                Max = 4000,
                DefaultValue = 1600,
                DisplayFormat = "上限:{0}px",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 点阵判暗方窗白色占比阈值（%）:调高=更容易判暗（对比度差的打点图）
                ParamName = "点阵判暗阈值",
                Min = 10,
                Max = 80,
                DefaultValue = 30,
                DisplayFormat = "判暗:{0}%",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 点阵网格锐度下限（%）：低对比/畸变实图可调低放宽网格判定
                ParamName = "点阵锐度下限",
                Min = 50,
                Max = 150,
                DefaultValue = 90,
                DisplayFormat = "锐度:{0}%",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 点阵最小节距（px）：小于此判为非点阵纹理；图里码很小时调低
                ParamName = "点阵最小节距",
                Min = 2,
                Max = 50,
                DefaultValue = 5,
                DisplayFormat = "节距≥{0}px",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // ZXing 增强层总开关（默认开）：多码 QR 补充 + 印刷 DataMatrix/Aztec/PDF417 兜底
                // + 1D 格式兜底（Code128/Code39/ITF/Codabar 等）。关=回到纯 OpenCV+自研引擎，
                // 用于排查疑似 ZXing 误检的场景。
                ParamName = "ZXing增强",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "ZXing:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 识别格式收窄：默认全部。只测单一码型时把范围收窄可显著降低误检
                // （例如产线只打 QR，把"仅QR"打开后 1D 与 DataMatrix 都不会再去试）。
                ParamName = "识别格式 0全部1仅2D2仅1D3仅QR",
                Min = 0,
                Max = 3,
                DefaultValue = 0,
                DisplayFormat = "格式:{0}",
                ForceOdd = false,
                Tip = "0 全部（默认）：二维码+一维码+点阵；\n" +
                      "1 仅2D：QR/DataMatrix/Aztec/PDF417/点阵（跳过一维码）；\n" +
                      "2 仅1D：EAN/UPC/Code128/Code39/ITF/Codabar 等一维码（跳过二维码）；\n" +
                      "3 仅QR：只识别二维码（最不易误检）。"
            },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastResults.Clear();
            LastSummary = "";
            LastZxingError = "";
            if (srcMat?.Empty() != false)
                return new Mat();

            // 参数（UI 滑条/流水线均传入；越界或空数组回退默认，兼容旧调用）
            float detMaxSide = paramValues?.Length > 0 ? paramValues[0] : DetMaxSide;
            // ZXing 增强层开关（参数[4]，默认开）：1=启用（多码 QR/印刷 2D/1D 兜底），0=回到纯 OpenCV+自研引擎
            bool zxEnabled = !(paramValues?.Length > 4) || paramValues[4] != 0;
            // 识别格式收窄（参数[5]，默认全部）：0全部 1仅2D 2仅1D 3仅QR —— 按场景跳段，
            // 少试的段不会产生误检；检测入口如下：QR 段 / 1D 段 / ZXing 兜底(2D+多码) / 点阵
            int formatSel = paramValues?.Length > 5 ? Math.Clamp(paramValues[5], 0, 3) : 0;
            bool wantQr = formatSel == 0 || formatSel == 1 || formatSel == 3;
            bool want1D = formatSel == 0 || formatSel == 2;
            bool want2D = formatSel == 0 || formatSel == 1;
            var dmOpt = new DotMatrix.Options
            {
                DarkRatioPercent = paramValues?.Length > 1 ? paramValues[1] : 30,
                MinSharpnessPercent = paramValues?.Length > 2 ? paramValues[2] : 90,
                MinPitch = paramValues?.Length > 3 ? paramValues[3] : 5,
            };

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            using (Mat gray = VisionHelper.ToGray(srcMat))
            {
                // 大图先整体降采样再识别，角点按 detScale 映射回原图（0=不缩放）
                float detScale = detMaxSide <= 0
                    ? 1f
                    : Math.Max(1f, Math.Max(gray.Cols, gray.Rows) / detMaxSide);
                using Mat det = Shrink(gray, detScale);
                Mat baseImg = det ?? gray;
                if (wantQr) DetectQr(baseImg, detScale);
                if (want1D) Detect1D(baseImg, detScale);
                if (zxEnabled)
                {
                    if (want1D) DetectZxing1D(baseImg, detScale);   // 1D 格式兜底（Code128/Code39/ITF 等，去重）
                    if (wantQr && LastResults.Count > 0)
                    {
                        DetectZxingQrMore(baseImg, detScale);       // 已检出时追加多码 QR 补充（去重）
                    }
                    else if (LastResults.Count == 0)
                    {
                        // 点阵兜底固定用原分辨率：码点质心数随分辨率骤减（1600 边长下
                        // 16×16 符号仅剩 ~60 点，网格拟合失败），且全图直解 <100ms
                        if (want2D) DetectZxing(baseImg, detScale); // ZXing 兜底（多码 QR + 印刷 2D 码）
                        if (want2D && LastResults.Count == 0)
                            DetectDotMatrix(gray, 1f, dmOpt);
                    }
                }
                else if (want2D && LastResults.Count == 0)
                {
                    DetectDotMatrix(gray, 1f, dmOpt);
                }
            }

            DrawResults(dst);

            // 结果摘要（与提示标签同格式）：解码内容超 40 字符截断
            if (LastResults.Count == 0)
            {
                LastSummary = "条码识别: 未识别到条码/二维码";
            }
            else
            {
                string parts = string.Join(" | ", LastResults.Select((r, i) =>
                {
                    string text = string.IsNullOrEmpty(r.Text) ? "(未解出)" : r.Text;
                    if (text.Length > 40) text = $"{text.AsSpan(0, 40)}…";
                    return $"{i + 1}[{r.TypeName}]{text}";
                }));
                LastSummary = "条码识别: " + parts;
            }
            return dst;
        }

        // —— 二维码：先 QRCodeDetector（小图 1/2 倍两档），未命中退回 WeChatQRCode 传统模式 ——
        private void DetectQr(Mat gray, float detScale)
        {
            try
            {
                using QRCodeDetector qcd = new();
                foreach (float scale in TierScales(gray, Tier2MaxSide))
                {
                    DetectQrAtScale(qcd, gray, scale, detScale);
                    if (LastResults.Count > 0)
                        return;
                }
            }
            catch
            {
                // 二维码检测异常时退回微信引擎
            }
            DetectQrWeChat(gray, detScale);
        }

        /// <summary>微信二维码引擎（不加载模型文件）：对小码/密集码/带 Logo 码更鲁棒，可多码。
        /// 输入先限制到 WeChatMaxSide，避免其传统检测器在大图上耗时失控</summary>
        private void DetectQrWeChat(Mat gray, float detScale)
        {
            try
            {
                float wcScale = Math.Max(1f, Math.Max(gray.Cols, gray.Rows) / WeChatMaxSide);
                using Mat wbase = Shrink(gray, wcScale);
                Mat wimg = wbase ?? gray;
                using WeChatQRCode wc = new();
                foreach (float scale in TierScales(wimg, WeChatTier2MaxSide))
                {
                    using Mat scaled = Prepare(wimg, scale);
                    Mat input = scaled ?? wimg;
                    string[] texts = wc.DetectAndDecode(input, out Point2f[][] ptsArr);
                    if (texts == null)
                        continue;
                    for (int i = 0; i < texts.Length; i++)
                    {
                        if (string.IsNullOrEmpty(texts[i]))
                            continue;
                        Point2f[] pts = (ptsArr != null && i < ptsArr.Length) ? ptsArr[i] : null;
                        if (pts == null || pts.Length < 4)
                            continue;
                        AddResult(texts[i], "QR", pts, scale * wcScale * detScale);
                    }
                    if (LastResults.Count > 0)
                        return;
                }
            }
            catch
            {
                // 微信引擎异常时静默跳过
            }
        }

        private void DetectQrAtScale(QRCodeDetector qcd, Mat gray, float scale, float detScale)
        {
            using Mat scaled = Prepare(gray, scale);
            Mat input = scaled ?? gray;
            using Mat straight = new();
            string text = qcd.DetectAndDecode(input, out Point2f[] pts, straight);
            if (!string.IsNullOrEmpty(text) && pts?.Length >= 4)
                AddResult(text, "QR", pts, scale * detScale);
        }

        /// <summary>放大档序列：1 倍一档；输入边长小于上限时才追加 2 倍档</summary>
        private static float[] TierScales(Mat gray, float tier2MaxSide)
        {
            float maxSide = Math.Max(gray.Cols, gray.Rows);
            return maxSide < tier2MaxSide ? [1f, 2f] : [1f];
        }

        // —— 一维码：纯 C# 解码器（EAN-13 / UPC-A / EAN-8 / Code128 / Code39，横竖两个方向）——
        private void Detect1D(Mat gray, float detScale)
        {
            try
            {
                foreach (OneDBarcode.Result r in OneDBarcode.Decode(gray))
                {
                    Rect b = r.Bounds;
                    var corners = new Point2f[]
                    {
                        new(b.X * detScale, b.Y * detScale),
                        new((b.X + b.Width) * detScale, b.Y * detScale),
                        new((b.X + b.Width) * detScale, (b.Y + b.Height) * detScale),
                        new(b.X * detScale, (b.Y + b.Height) * detScale)
                    };
                    LastResults.Add(new CodeResult { Text = r.Text, TypeName = r.TypeName, Corners = corners });
                }
            }
            catch
            {
                // 一维码解码异常时静默跳过
            }
        }

        // —— 点阵 Data Matrix：自研定位+解码（网格拟合 + RS 校验），兜底引擎 ——
        private void DetectDotMatrix(Mat gray, float detScale, DotMatrix.Options opt)
        {
            try
            {
                if (DotMatrix.TryDecode(gray, opt, out string text, out Point2f[] quad))
                {
                    var corners = new Point2f[quad.Length];
                    for (int i = 0; i < quad.Length; i++)
                        corners[i] = new Point2f(quad[i].X * detScale, quad[i].Y * detScale);
                    LastResults.Add(new CodeResult { Text = text, TypeName = "DataMatrix", Corners = corners });
                }
            }
            catch
            {
                // 点阵解码异常时静默跳过
            }
        }

        // —— ZXing 增强层（Apache-2.0，可商用）：多码 QR + 印刷 DataMatrix/Aztec/PDF417 + 1D 格式兜底 ——
        /// <summary>ZXing 1D 格式兜底：Code128/Code39/Code93/ITF/Codabar/MSI/EAN-13/EAN-8/UPC-A/UPC-E/
        /// RSS-14/RSS-Expanded（OneDBarcode 只覆盖 EAN-13/UPC-A/EAN-8/Code128/Code39），
        /// 与既有结果按外接框去重</summary>
        private void DetectZxing1D(Mat gray, float detScale)
        {
            try
            {
                foreach (float scale in TierScales(gray, Tier2MaxSide))
                {
                    using Mat scaled = Prepare(gray, scale);
                    Mat input = scaled ?? gray;
                    // 多码 1D：GenericMultiple 解出一个即涂黑该条再解，直至无码
                    Result[] results = TryDecode1D(ToZxingBitmap(input));
                    foreach (Result r in results)
                        AddZxingResult(r, scale * detScale, true, gray.Cols, gray.Rows);
                }
            }
            catch (Exception ex)
            {
                LastZxingError = ex.Message;
            }
        }

        /// <summary>ZXing 2D 兜底：未检出任何码时优先多码 QR（1x/2x 两档），未命中再试
        /// 印刷 DataMatrix/Aztec/PDF417（同一缩放档共享一次灰度拷贝与二值化）</summary>
        private void DetectZxing(Mat gray, float detScale)
        {
            try
            {
                foreach (float scale in TierScales(gray, Tier2MaxSide))
                {
                    using Mat scaled = Prepare(gray, scale);
                    Mat input = scaled ?? gray;
                    if (DecodeZxingQr(input, scale * detScale, false, gray.Cols, gray.Rows) > 0)
                        return;
                }
                foreach (float scale in TierScales(gray, Tier2MaxSide))
                {
                    using Mat scaled = Prepare(gray, scale);
                    Mat input = scaled ?? gray;
                    // 三种印刷 2D 码共用一次二值化：MultiFormatReader 按 POSSIBLE_FORMATS 顺序串试
                    Result r = TryDecode2D(ToZxingBitmap(input));
                    if (AddZxingResult(r, scale * detScale, false, gray.Cols, gray.Rows) > 0)
                        return;
                }
            }
            catch (Exception ex)
            {
                LastZxingError = ex.Message;
            }
        }

        /// <summary>已检出 ≥1 个码时补充解码更多二维码（多码场景），与已有结果按外接框去重</summary>
        private void DetectZxingQrMore(Mat gray, float detScale)
        {
            try
            {
                foreach (float scale in TierScales(gray, Tier2MaxSide))
                {
                    using Mat scaled = Prepare(gray, scale);
                    Mat input = scaled ?? gray;
                    DecodeZxingQr(input, scale * detScale, true, gray.Cols, gray.Rows);
                }
            }
            catch (Exception ex)
            {
                LastZxingError = ex.Message;
            }
        }

        /// <summary>ZXing 多码 QR 解码入口：返回新增结果数；dedup=true 时与既有结果去重</summary>
        private int DecodeZxingQr(Mat gray, float pointsScale, bool dedup, int refCols, int refRows)
        {
            // QRCodeReader 本身只解单码；多码 = 包一层 GenericMultipleBarcodeReader
            // （ZXing Java 的 QRCodeMultiReader 即此实现：解出一个码后涂黑该区域再解，直至无码）
            // 注：0.16.10 net8.0 构建的公开方法为小写命名（decode/decodeMultiple/reset）
            Result[] results = TryDecodeQrMulti(ToZxingBitmap(gray));
            int added = 0;
            foreach (Result r in results)
                added += AddZxingResult(r, pointsScale, dedup, refCols, refRows);
            return added;
        }

        /// <summary>灰度 Mat → ZXing BinaryBitmap（Gray8 亮度源 + Hybrid 二值化）</summary>
        private static BinaryBitmap ToZxingBitmap(Mat gray)
        {
            byte[] bytes = new byte[(int)gray.Total()];
            Marshal.Copy(gray.Data, bytes, 0, bytes.Length);
            var source = new RGBLuminanceSource(bytes, gray.Cols, gray.Rows, RGBLuminanceSource.BitmapFormat.Gray8);
            return new BinaryBitmap(new HybridBinarizer(source));
        }

        private static Result[] TryDecodeQrMulti(BinaryBitmap bmp)
        {
            try { return new GenericMultipleBarcodeReader(new QRCodeReader()).decodeMultiple(bmp, _zxingQrHints) ?? []; }
            catch { return []; } // ReaderException 等 = 常规未命中
        }

        private static Result[] TryDecode1D(BinaryBitmap bmp)
        {
            try { return new GenericMultipleBarcodeReader(new MultiFormatReader()).decodeMultiple(bmp, _zxing1DHints) ?? []; }
            catch { return []; }
        }

        private static Result TryDecode2D(BinaryBitmap bmp)
        {
            try { return new MultiFormatReader().decode(bmp, _zxing2DHints); }
            catch { return null; }
        }

        /// <summary>ZXing 结果落地：外接框外扩映射回原图 → 最小尺寸门槛（防误检）→ 可选去重 → 加入结果</summary>
        private int AddZxingResult(Result r, float pointsScale, bool dedupExisting, int refCols, int refRows)
        {
            if (r == null || string.IsNullOrEmpty(r.Text) || r.ResultPoints == null || r.ResultPoints.Length < 2)
                return 0;
            Point2f[] box = ResultPointsToBox(r.ResultPoints, pointsScale);
            Rect2f bounds = BoxBounds(box);
            // 误检门槛：宽高都小于原图短边 1%（且 <6px）的极小结果丢弃（TRY_HARDER 下的碎片误检）。
            // 注意不能用"短边"判定：ITF 等 1D 码定位点为同一条线上的两点，外接框高度天然 ≈4px，
            // 但宽度合法；只有宽高同时极小才是碎片误检。
            float minSide = Math.Max(6f, Math.Min(refCols, refRows) * 0.01f);
            if (bounds.Width < minSide && bounds.Height < minSide)
                return 0;
            if (dedupExisting && OverlapsExisting(box))
                return 0;
            LastResults.Add(new CodeResult { Text = r.Text, TypeName = FormatName(r.BarcodeFormat), Corners = box });
            return 1;
        }

        /// <summary>ZXing BarcodeFormat → 中文类型名</summary>
        private static string FormatName(BarcodeFormat f) => f switch
        {
            BarcodeFormat.QR_CODE => "QR",
            BarcodeFormat.DATA_MATRIX => "DataMatrix",
            BarcodeFormat.AZTEC => "Aztec",
            BarcodeFormat.PDF_417 => "PDF417",
            BarcodeFormat.CODE_128 => "Code128",
            BarcodeFormat.CODE_39 => "Code39",
            BarcodeFormat.CODE_93 => "Code93",
            BarcodeFormat.ITF => "ITF",
            BarcodeFormat.CODABAR => "Codabar",
            BarcodeFormat.EAN_13 => "EAN-13",
            BarcodeFormat.EAN_8 => "EAN-8",
            BarcodeFormat.UPC_A => "UPC-A",
            BarcodeFormat.UPC_E => "UPC-E",
            BarcodeFormat.MSI => "MSI",
            BarcodeFormat.RSS_14 => "RSS-14",
            BarcodeFormat.RSS_EXPANDED => "RSS-Expanded",
            BarcodeFormat.UPC_EAN_EXTENSION => "UPC/EAN附加码",
            _ => f.ToString()
        };

        /// <summary>ZXing 1D 兜底尝试的格式（自研 OneDBarcode 之外的补充）</summary>
        private static readonly List<BarcodeFormat> _zxing1DFormats =
        [
            BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93,
            BarcodeFormat.ITF, BarcodeFormat.CODABAR, BarcodeFormat.EAN_13,
            BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
            BarcodeFormat.MSI, BarcodeFormat.RSS_14, BarcodeFormat.RSS_EXPANDED,
            BarcodeFormat.UPC_EAN_EXTENSION
        ];

        private static readonly Dictionary<DecodeHintType, object> _zxingQrHints = new()
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
        };

        private static readonly Dictionary<DecodeHintType, object> _zxing1DHints = new()
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
            [DecodeHintType.POSSIBLE_FORMATS] = _zxing1DFormats
        };

        private static readonly Dictionary<DecodeHintType, object> _zxing2DHints = new()
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417 }
        };

        /// <summary>ZXing 定位点（QR 寻像图形心等）→ 略外扩的外接框四角，映射回原图坐标</summary>
        private static Point2f[] ResultPointsToBox(ZXing.ResultPoint[] pts, float scale)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (ZXing.ResultPoint p in pts)
            {
                if (p == null) continue;
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            float padX = Math.Max(2f, (maxX - minX) * 0.12f);
            float padY = Math.Max(2f, (maxY - minY) * 0.12f);
            return
            [
                new Point2f((minX - padX) / scale, (minY - padY) / scale),
                new Point2f((maxX + padX) / scale, (minY - padY) / scale),
                new Point2f((maxX + padX) / scale, (maxY + padY) / scale),
                new Point2f((minX - padX) / scale, (maxY + padY) / scale)
            ];
        }

        /// <summary>新结果外接框与任一既有结果重叠比例 &gt; 25% 视为同一码</summary>
        private bool OverlapsExisting(Point2f[] box)
        {
            Rect2f r = BoxBounds(box);
            foreach (CodeResult c in LastResults)
            {
                Rect2f e = BoxBounds(c.Corners);
                float ix = Math.Max(0, Math.Min(r.X + r.Width, e.X + e.Width) - Math.Max(r.X, e.X));
                float iy = Math.Max(0, Math.Min(r.Y + r.Height, e.Y + e.Height) - Math.Max(r.Y, e.Y));
                float inter = ix * iy;
                float minArea = Math.Min(r.Width * r.Height, e.Width * e.Height);
                if (minArea > 0 && inter / minArea > 0.25f)
                    return true;
            }
            return false;
        }

        private static Rect2f BoxBounds(Point2f[] pts)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Point2f p in pts)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            return new Rect2f(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>缩小倍率 &gt; 1 时返回面积插值缩小副本，否则返回 null（直接用原图）</summary>
        private static Mat Shrink(Mat gray, float factor)
        {
            if (factor <= 1f + 1e-3f)
                return null;
            int w = Math.Max(1, (int)Math.Round(gray.Cols / factor));
            int h = Math.Max(1, (int)Math.Round(gray.Rows / factor));
            Mat resized = new();
            Cv2.Resize(gray, resized, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        /// <summary>非 1 倍缩放时双三次放大，返回 null 表示直接用原图</summary>
        private static Mat Prepare(Mat gray, float scale)
        {
            if (scale <= 1f + 1e-3f)
                return null;
            Mat resized = new();
            Cv2.Resize(gray, resized, new OpenCvSharp.Size(0, 0), scale, scale, InterpolationFlags.Cubic);
            return resized;
        }

        private void AddResult(string text, string typeName, Point2f[] corners, float scale)
        {
            if (corners == null || corners.Length < 4)
                return;
            var mapped = new Point2f[corners.Length];
            for (int i = 0; i < corners.Length; i++)
                mapped[i] = new Point2f(corners[i].X / scale, corners[i].Y / scale);
            LastResults.Add(new CodeResult { Text = text ?? "", TypeName = typeName, Corners = mapped });
        }

        /// <summary>
        /// 安全创建字体：优先微软雅黑，失败回退 Arial/通用字体，避免
        /// Font 构造在部分环境抛 "Parameter is not valid"。
        /// </summary>
        private static Font MakeSafeFont(float size)
        {
            foreach (var family in new[] { "Microsoft YaHei", "Arial", "Microsoft Sans Serif" })
            {
                try { return new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel); }
                catch { /* 换下一个字体 */ }
            }
            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        /// <summary>在结果图上标注序号+解码内容：GDI+ 绘制，中文等非 ASCII 字符正常显示。
        /// 通过 LockBits 逐行把 Mat(BGR) 拷入 Format24bppRgb Bitmap（内存序同为 BGR）画完再拷回，
        /// 兼容任意 stride，不依赖 OpenCvSharp.Extensions</summary>
        private void DrawResults(Mat dst)
        {
            if (dst?.Empty() != false || LastResults.Count == 0)
                return;
            int w = dst.Cols, h = dst.Rows;
            // 32bppArgb：24bpp RGB 在部分显示环境（16 位色/远程桌面）下
            // Graphics.FromImage/DrawString 会抛 "Parameter is not valid"，32bpp 兼容性最好
            const PixelFormat fmt = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            using var bmp = new Bitmap(w, h, fmt);
            byte[] row = new byte[w * 4];
            byte[] srcRow = new byte[w * 3];
            int step = (int)dst.Step();

            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, fmt);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(dst.Data + (y * step), srcRow, 0, srcRow.Length);
                    for (int x = 0; x < w; x++)
                    {
                        row[x * 4 + 0] = srcRow[x * 3 + 0];
                        row[x * 4 + 1] = srcRow[x * 3 + 1];
                        row[x * 4 + 2] = srcRow[x * 3 + 2];
                        row[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(row, 0, bd.Scan0 + (y * bd.Stride), row.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float fs = Math.Clamp(w / 90f, 11f, 18f);
                using var font = MakeSafeFont(fs);
                // 码框：把识别到的码（QR/一维码/DataMatrix/Aztec/PDF417…）用黄色闭合多边形圈出来，
                // 角点贴合码的四角（透视/点阵多角也适用），与 DPM 码的框选效果一致
                using var framePen = new Pen(Color.Yellow, Math.Max(2f, w / 600f));
                for (int i = 0; i < LastResults.Count; i++)
                {
                    CodeResult r = LastResults[i];
                    if (r.Corners == null || r.Corners.Length < 1)
                        continue;
                    if (r.Corners.Length >= 3)
                    {
                        var poly = new PointF[r.Corners.Length];
                        for (int k = 0; k < r.Corners.Length; k++)
                            poly[k] = new PointF(r.Corners[k].X, r.Corners[k].Y);
                        g.DrawPolygon(framePen, poly);
                    }
                    float tx = r.Corners[0].X + 4;
                    float ty = r.Corners[0].Y - fs - 5;
                    if (ty < 2)
                        ty = r.Corners[0].Y + 4;
                    string label = (i + 1).ToString();
                    string text = string.IsNullOrEmpty(r.Text) ? "(未解出)" : r.Text;
                    if (text.Length > 40)
                        text = $"{text.AsSpan(0, 40)}…";
                    // 序号（黑描边+白字）
                    g.DrawString(label, font, Brushes.Black, tx + 1, ty + 1);
                    g.DrawString(label, font, Brushes.White, tx, ty);
                    // 内容（黑描边+红字），贴着序号右侧；超右缘整体左移
                    SizeF labelSize = g.MeasureString(label, font);
                    float textX = tx + labelSize.Width + 8;
                    float fw = g.MeasureString(text, font).Width;
                    if (textX + fw > w - 4)
                        textX = Math.Max(4, w - 4 - fw);
                    g.DrawString(text, font, Brushes.Black, textX + 1, ty + 1);
                    g.DrawString(text, font, Brushes.Red, textX, ty);
                }
                g.Flush();
            }

            bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, fmt);
            try
            {
                byte[] bgr = new byte[w * 3];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(bd.Scan0 + (y * bd.Stride), row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        bgr[x * 3 + 0] = row[x * 4 + 0];
                        bgr[x * 3 + 1] = row[x * 4 + 1];
                        bgr[x * 3 + 2] = row[x * 4 + 2];
                    }
                    Marshal.Copy(bgr, 0, dst.Data + (y * step), bgr.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
        }
    }
}