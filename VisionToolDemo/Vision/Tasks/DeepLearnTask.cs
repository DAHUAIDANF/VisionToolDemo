using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 深度学习推理算子：加载用户自备的 .onnx 模型做「图像分类」或「目标检测」。
    ///
    /// 引擎：ONNX Runtime（Microsoft.ML.OnnxRuntime，MIT 许可，开源免费可商用）。
    /// 不绑定任何模型家族 / 训练框架：任何「单图像输入」的 .onnx 导出模型都可用。
    ///
    /// 两种后端（参数[0] 选择）：
    ///   · 0 图像分类：输入 [1,3,H,W]（RGB 归一化 0~1）→ 输出 [1,C] logits →
    ///     softmax 取前 5 类，结果图按得分从高到低标注类别名（中文不乱码，GDI+ 绘制）。
    ///   · 1 目标检测：兼容 YOLO 系导出格式——YOLOv5 风格输出 [1,N,5+C]
    ///     （N 个框：cx,cy,w,h,score,class_scores）与 YOLOv8 风格输出 [1,4+C,N]
    ///     （转置后同构）均支持；置信度过滤 → NMS（IoU 阈值可调）→ 框映射回原图
    ///     （letterbox 黑边补偿）→ 图上画框 + 类别 + 置信度。
    ///
    /// 模型与标签的加载入口在视觉页参数区（"深度学习推理"专用行）：
    ///   · 选择 .onnx 模型文件；
    ///   · 选择 .txt 标签文件（每行一个类别名，与模型输出通道顺序一致）。
    /// 状态（ModelPath / Labels / 会话缓存）存在任务实例上，链步骤复用同一实例，
    /// 多次执行只加载一次会话；模型文件时间戳变化时自动重载（改模型不用重启）。
    ///
    /// 安全契约：模型未加载 / 文件不存在 / 加载失败 / 推理异常 → 一律防御性返回
    /// 「原图 BGR 拷贝」并在摘要里写明原因，绝不让一条异常炸掉整条算子链
    /// （本项目 OpenCV BarcodeDetector 曾因"原生实现调用即崩"不可用，防御性是硬要求）。
    ///
    /// 许可提示：模型文件由用户自备。ONNX Runtime 与 .onnx 格式本身无许可问题，
    /// 但请选用可商用许可的开源模型（如 Apache-2.0 的 NanoDet / YOLOX / MobileNet /
    /// ONNX Model Zoo 系列）；Ultralytics YOLOv8/v11 导出模型为 AGPL-3.0，商用需谨慎。
    /// </summary>
    public class DeepLearnTask : IVisionTask, IResultReporter
    {
        public string TaskName => "深度学习推理";

        // ============================== 状态（任务实例上，链步复用） ==============================

        /// <summary>.onnx 模型文件路径（视觉页参数区"选择模型"设置；空=未加载）</summary>
        public string ModelPath = "";

        /// <summary>类别名列表（标签文件每行一个；顺序与模型输出通道一一对应）</summary>
        public List<string> Labels = [];

        /// <summary>模型文件写入时间戳（用于检测模型被替换后自动重载会话）</summary>
        private DateTime _modelStamp = DateTime.MinValue;

        /// <summary>惰性缓存的 ONNX 推理会话（Execute 首次使用时才创建）</summary>
        private InferenceSession _session;

        /// <summary>最近一次加载/推理失败的原因（空=正常），用于摘要与排障</summary>
        private string _loadError = "";

        /// <summary>最近一次 Execute 的摘要（提示标签 / 运行完成弹窗共用）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>IResultReporter 兼容占位（本算子无 ZXing 类扩展信息）</summary>
        public string LastZxingError => "";

        /// <summary>单个检测框（原图坐标，左上+宽高）</summary>
        public class DetectBox
        {
            public float X, Y, W, H;
            public int Class = -1;
            public float Score;
            public string Name = "";
        }

        /// <summary>最近一次检测的结果框（供 UI/结果图读取）</summary>
        public List<DetectBox> LastBoxes { get; } = [];

        /// <summary>最近一次分类的 Top-N（类别名 + 得分，已降序）</summary>
        public List<(string Name, float Score)> LastTop { get; } = [];

        // ============================== 参数 ==============================

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                // 任务类型：0=图像分类（输出前 5 类），1=目标检测（YOLO 系导出格式）
                ParamName = "任务类型",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "类型:{0}",
                ForceOdd = false,
                Tip = "0 图像分类：softmax 取前 5 类标注在图上。\n" +
                      "1 目标检测：YOLOv5/YOLOv8 导出格式，画框+类别+置信度。\n" +
                      "2 滑窗多人/多目标识别：训练页自研模型（框选标注+标签），一张图识别出多个目标" +
                      "（人像姓名 / 产品NG类型）并各自画框+标签；建议训练时标注含「背景」类抑制误检。"
            },
            new TaskParamDesc
            {
                // 模型输入的正方形边长：0=自动读取模型输入尺寸（推荐）；分类/检测模型通常固定（YOLO 系 640/416/320…）
                ParamName = "输入边长",
                Min = 0,
                Max = 1280,
                DefaultValue = 0,
                DisplayFormat = "边:{0}px",
                ForceOdd = false,
                Tip = "模型输入边长。0 = 自动读取模型输入尺寸（推荐，训练页导出模型直接可用）；\n" +
                      "若自动读取失败或模型输入非正方形，请手动填（如 YOLO 640）。"
            },
            new TaskParamDesc
            {
                // 任务类型=2 滑窗检测的窗口边长（像素）。0=按图片短边 1/6 自动。
                ParamName = "窗口边长",
                Min = 0,
                Max = 2000,
                DefaultValue = 0,
                DisplayFormat = "窗口:{0}px",
                ForceOdd = false,
                Group = "滑窗识别",
                Tip = "任务类型=2 时：滑窗边长（图像像素）。0=按图片短边的 1/6 自动；" +
                      "目标小/人多就调小（如 64~96）。"
            },
            new TaskParamDesc
            {
                // 任务类型=2 滑窗检测的步长。0=窗口边长的一半。
                ParamName = "窗口步长",
                Min = 0,
                Max = 2000,
                DefaultValue = 0,
                DisplayFormat = "步长:{0}px",
                ForceOdd = false,
                Group = "滑窗识别",
                Tip = "任务类型=2 时：滑窗步长（像素）。0=窗口边长的一半；想快就调大（漏检风险升高）。"
            },
            // 算子级 ROI：框选识别区域，识别只在该区域内做（不需要扫全图）。
            // 参数面板会自动出现「用图上框选填充 ROI」按钮，把图上拉的框直接写入这里。
            ..RoiRegion.ParamDescs(),
            new TaskParamDesc
            {
                // 置信度阈值（%）：低于此的检测框/类别丢弃；分类也用它过滤低置信度输出
                ParamName = "置信度阈值",
                Min = 1,
                Max = 99,
                DefaultValue = 50,
                DisplayFormat = "阈值:{0}%",
                ForceOdd = false,
                Tip = "低于此得分的检测框/类别不显示。误检多就调高，漏检多就调低。"
            },
            new TaskParamDesc
            {
                // NMS 的 IoU 阈值（%，仅目标检测用）：重叠超过此比例的重复框被抑制
                ParamName = "NMS IoU",
                Min = 10,
                Max = 95,
                DefaultValue = 45,
                DisplayFormat = "IoU:{0}%",
                ForceOdd = false,
                Tip = "目标检测专用：NMS 的 IoU 阈值。同一目标多个重叠框时，\n" +
                      "IoU 越高保留越少（默认 45% 偏保守）。"
            },
            new TaskParamDesc
            {
                // 分类模式在图上显示的前 N 类（默认 5，可调 1~10）
                ParamName = "显示前N类",
                Min = 1,
                Max = 10,
                DefaultValue = 5,
                DisplayFormat = "Top:{0}",
                ForceOdd = false,
                Tip = "图像分类专用：结果图上从高到低标注前几个类别（默认 5）。"
            },
            new TaskParamDesc
            {
                // 人脸识别增强：任务类型=2 滑窗时，先自动检测人脸，只对每张人脸区域识别（不扫全图）。
                // 适合人像姓名/打卡/人脸分类场景：更快更准；多人脸自动各画一框。
                ParamName = "人脸增强 0关1开",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "人脸:{0}",
                ForceOdd = false,
                Group = "人脸增强",
                Tip = "任务类型=2 滑窗时生效：先用人脸检测引擎（YuNet 优先，Haar 回退，均开源可商用）自动检测人脸，" +
                      "再只对每张人脸区域（外扩15%）做滑窗识别——比全图滑窗快且准，多人脸自动各出一框。\n" +
                      "启用 ROI 时只检测 ROI 区域内的人脸。\n" +
                      "模型文件 face_detection_yunet_2023mar.onnx / haarcascade_frontalface_default.xml 随程序输出（程序目录自动查找）；" +
                      "缺失/图中无人脸时自动降级为普通滑窗并在摘要说明。"
            },
        ];

        // ============================== 执行 ==============================

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            // 每次执行先清空上次结果，避免旧框/旧类残留
            LastBoxes.Clear();
            LastTop.Clear();
            LastSummary = "";
            _loadError = "";
            if (srcMat?.Empty() != false)
                return new Mat();

            // 参数（越界/空数组回退默认，兼容旧调用）
            // 注意：索引必须与 ParamDescriptions 的声明顺序一致：
            //   [0]任务类型 [1]输入边长 [2]窗口边长 [3]窗口步长
            //   [4..8]ROI(启用/X/Y/宽/高) [9]置信度阈值 [10]NMS IoU [11]显示前N类 [12]人脸增强
            int taskType = paramValues?.Length > 0 ? Math.Clamp(paramValues[0], 0, 2) : 0;
            int inputSide = paramValues?.Length > 1 ? Math.Clamp(paramValues[1], 0, 1280) : 0;
            int winSide = paramValues?.Length > 2 ? Math.Max(0, paramValues[2]) : 0;   // 任务类型2 窗口边长
            int winStep = paramValues?.Length > 3 ? Math.Max(0, paramValues[3]) : 0;   // 任务类型2 窗口步长
            // 算子级 ROI（固定索引 4..8）：0=整图；1=只在框选区域识别（与图求交防越界）
            bool roiEnabled = paramValues?.Length > 4 && paramValues[4] == 1;
            var roiRect = default(Rect);
            if (roiEnabled)
            {
                var raw = new Rect(Math.Max(0, paramValues[5]), Math.Max(0, paramValues[6]),
                                   Math.Max(0, paramValues[7]), Math.Max(0, paramValues[8]));
                if (raw.Width > 0 && raw.Height > 0)
                    roiRect = raw & new Rect(0, 0, srcMat.Cols, srcMat.Rows);
                if (roiRect.Width < 1 || roiRect.Height < 1) roiEnabled = false;   // 框不在图内 → 退回整图
            }
            float conf = (paramValues?.Length > 9 ? Math.Clamp(paramValues[9], 1, 99) : 50) / 100f;
            float iou = (paramValues?.Length > 10 ? Math.Clamp(paramValues[10], 10, 95) : 45) / 100f;
            int topN = paramValues?.Length > 11 ? Math.Clamp(paramValues[11], 1, 10) : 5;
            int faceOn = paramValues?.Length > 12 ? Math.Clamp(paramValues[12], 0, 1) : 0;   // 人脸增强（滑窗前置）

            // 输出图：默认原图 BGR 拷贝；任何失败路径都返回它（防御）
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 模型未加载 / 文件不存在：不执行推理，明确提示 ——
            if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
            {
                LastSummary = string.IsNullOrWhiteSpace(ModelPath)
                    ? "深度学习推理: 未加载模型（先在参数区选择 .onnx 模型文件）"
                    : "深度学习推理: 模型文件不存在（" + ModelPath + "）";
                return dst;
            }

            try
            {
                // 惰性加载会话：模型路径或文件时间戳变化时自动重建（改模型不用重启程序）
                var stamp = File.GetLastWriteTimeUtc(ModelPath);
                if (_session == null || !string.Equals(_modelPathLoaded, ModelPath, StringComparison.OrdinalIgnoreCase)
                    || stamp != _modelStamp)
                {
                    _session?.Dispose();
                    _session = new InferenceSession(ModelPath);
                    _modelStamp = stamp;
                    _modelPathLoaded = ModelPath;
                }

                // 模型输入元数据（输入名/元素类型/维度）：推理张量严格按模型声明构造，
                // 不再假设 float32+固定 640——模型输入是 Double/Int8 或边长非 640 时自动匹配，
                // 彻底避免 "Tensor element data" / shape 不匹配类 InvalidArgument 错误。
                var inMeta = _session.InputMetadata.FirstOrDefault();
                string inName = inMeta.Key;
                var inType = inMeta.Value.ElementDataType;
                int[] inDims = inMeta.Value.Dimensions.ToArray();

                // 输入边长：0=自动读取模型固定边长；手动填错时自动纠正为模型实际边长
                if (inDims.Length == 4 && inDims[2] > 0 && inDims[3] > 0)
                {
                    if (inDims[2] != inDims[3])
                    {
                        LastSummary = "深度学习推理: 模型输入非正方形 [" + inDims[2] + "x" + inDims[3] +
                                      "]，请手动设置「输入边长」";
                        return dst;
                    }
                    if (inputSide <= 0)
                    {
                        inputSide = inDims[2];   // 自动读取（推荐）
                    }
                    else if (inputSide != inDims[2])
                    {
                        inputSide = inDims[2];   // 用户手动填错 → 以模型实际输入为准
                    }
                }
                else if (inputSide <= 0)
                {
                    LastSummary = "深度学习推理: 自动读取模型输入尺寸失败（模型输入 [" +
                                  string.Join("x", inDims) + "]），请手动设置「输入边长」";
                    return dst;
                }

                {
                    // 推理源：启用 ROI 时取框选子图（分类/YOLO 只在该区域上推理，滑窗只在区域内扫）
                    Mat inferSrc = srcMat;
                    using Mat roiSub = roiEnabled ? new Mat(srcMat, roiRect) : null;
                    if (roiEnabled) inferSrc = roiSub;

                    if (taskType == 2)
                    {
                        // 滑窗多人/多目标识别：逐窗口推理（每窗一次 session.Run，不再预处理整图），
                        // 双尺度（窗口边长 ×1 / ×1.5）+ 背景类抑制 + NMS + TopN，只在 ROI 区域内扫；
                        // 摘要反馈「窗口 → 阈值保留 → NMS/TopN」，阈值调整效果可见
                        // 人脸增强开：先人脸检测（YuNet 优先，Haar 回退），只对每张人脸区域滑窗（更快更准，多人脸各一框）
                        if (faceOn == 1)
                            RunFaceDetection(dst, srcMat, inputSide, conf, iou, topN, roiEnabled, roiRect);
                        else
                            RunSlidingDetection(dst, srcMat, inputSide, conf, iou, winSide, winStep,
                                                topN, roiEnabled, roiRect);
                    }
                    else
                    {
                        // 预处理：letterbox（等比缩放 + 灰边填充到正方形）→ BGR→RGB → 归一化 0~1 → NCHW
                        float padRatio = 1f;
                        float[] input = BuildInput(inferSrc, inputSide, out padRatio);
                        if (input == null)
                        {
                            LastSummary = "深度学习推理: 输入预处理失败";
                            return dst;
                        }

                        // 推理：输入张量按模型声明的元素类型构造（float32/double/int8…自动匹配）
                        dynamic inputTensor = BuildInputTensor(input, inType, new[] { 1, 3, inputSide, inputSide });
                        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inName, inputTensor) };
                        using var results = _session.Run(inputs);
                        var outTensor = results.First().AsTensor<float>();
                        int[] outDims = outTensor.Dimensions.ToArray();

                        if (taskType == 0)
                            RunClassification(dst, outTensor, outDims, conf, topN, roiEnabled, roiRect);
                        else
                            RunDetection(dst, outTensor, outDims, conf, iou, inputSide, padRatio,
                                         roiEnabled ? roiRect.X : 0, roiEnabled ? roiRect.Y : 0);
                    }
                }
            }
            catch (Exception ex)
            {
                // 任何加载/推理异常都不能炸链：记录原因，返回原图
                _loadError = ex.Message;
                LastSummary = "深度学习推理: 失败（" + ex.Message + "）";
            }

            // 摘要兜底（成功且无结果时）
            if (LastSummary.Length == 0)
            {
                if (taskType == 0)
                {
                    LastSummary = LastTop.Count == 0
                        ? "深度学习推理: 分类完成，无超过阈值的类别"
                        : "深度学习推理: " + string.Join(" | ", LastTop.Select(t => $"{t.Name} {t.Score:P1}"));
                }
                else
                {
                    LastSummary = LastBoxes.Count == 0
                        ? "深度学习推理: 检测完成，未命中目标"
                        : "深度学习推理: " + string.Join(" | ", LastBoxes.Select(b => $"{b.Name} {b.Score:P1}"));
                }
            }

            return dst;
        }

        /// <summary>会话对应已加载的模型路径（配合时间戳判断是否需要重载）</summary>
        private string _modelPathLoaded = "";

        /// <summary>释放缓存的推理会话（视觉页"清除模型"调用，改模型/换任务时释放内存）</summary>
        public void UnloadModel()
        {
            _session?.Dispose();
            _session = null;
            _modelPathLoaded = "";
            _modelStamp = DateTime.MinValue;
        }

        // ============================== 预处理 ==============================

        /// <summary>
        /// letterbox 预处理：原图等比缩放后灰边填充到 inputSide×inputSide 正方形，
        /// 输出 [inputSide*inputSide*3] 的 RGB float 数组（0~1 归一化，NCHW 顺序）。
        /// padRatio = 原图边长 / 输入边长（检测后映射回原图坐标用）。
        /// </summary>
        private static float[] BuildInput(Mat src, int inputSide, out float padRatio)
        {
            padRatio = 1f;
            if (src == null || src.Empty() || inputSide <= 0)
                return null;
            float scale = Math.Min((float)inputSide / src.Cols, (float)inputSide / src.Rows);
            int nw = Math.Max(1, (int)Math.Round(src.Cols * scale));
            int nh = Math.Max(1, (int)Math.Round(src.Rows * scale));
            padRatio = 1f / scale;   // 原图边长/输入边长：框坐标乘它回到原图

            using Mat resized = new Mat();
            Cv2.Resize(src, resized, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Linear);
            using Mat canvas = new Mat(inputSide, inputSide, MatType.CV_8UC3, new Scalar(114, 114, 114));
            resized.CopyTo(canvas[new OpenCvSharp.Rect(0, 0, nw, nh)]);

            // BGR → RGB float NCHW：OpenCV Mat 按行连续，直接按 BGR 顺序读出再重排
            float[] data = new float[inputSide * inputSide * 3];
            unsafe
            {
                byte* p = (byte*)canvas.Data;
                int step = (int)canvas.Step();
                int idx = 0;
                for (int y = 0; y < inputSide; y++)
                {
                    byte* row = p + y * step;
                    for (int x = 0; x < inputSide; x++)
                    {
                        int b = row[x * 3 + 0];
                        int g = row[x * 3 + 1];
                        int r = row[x * 3 + 2];
                        // NCHW：R 平面 / G 平面 / B 平面
                        data[idx] = r / 255f;
                        data[idx + inputSide * inputSide] = g / 255f;
                        data[idx + inputSide * inputSide * 2] = b / 255f;
                        idx++;
                    }
                }
            }
            return data;
        }

        /// <summary>
        /// 按模型输入声明的元素类型构建 DenseTensor：float32 / double / uint8 / int8 自动匹配，
        /// 避免 ONNX Runtime "Tensor element data"（输入类型不匹配）类错误。
        /// uint8/int8 按 0~1 归一化数据回转到 0~255 / -128~127（ONNX 量化模型的常见输入约定）。
        /// 其他类型（float16 等）抛出明确提示，不静默传错类型。
        /// </summary>
        private static object BuildInputTensor(float[] data, TensorElementType type, int[] shape)
        {
            switch (type)
            {
                case TensorElementType.Float:
                    return new DenseTensor<float>(data, shape);
                case TensorElementType.Double:
                    {
                        var d = new double[data.Length];
                        for (int i = 0; i < data.Length; i++) d[i] = data[i];
                        return new DenseTensor<double>(d, shape);
                    }
                case TensorElementType.UInt8:
                    {
                        var d = new byte[data.Length];
                        for (int i = 0; i < data.Length; i++) d[i] = (byte)Math.Clamp(MathF.Round(data[i] * 255f), 0, 255);
                        return new DenseTensor<byte>(d, shape);
                    }
                case TensorElementType.Int8:
                    {
                        var d = new sbyte[data.Length];
                        for (int i = 0; i < data.Length; i++) d[i] = (sbyte)Math.Clamp(MathF.Round(data[i] * 255f) - 128f, -128, 127);
                        return new DenseTensor<sbyte>(d, shape);
                    }
                default:
                    throw new NotSupportedException("模型输入元素类型 " + type + " 暂不支持（常见 Float/Double/UInt8/Int8；Float16 请先转换为 float32 再导出）");
            }
        }

        // ============================== 分类后端 ==============================

        /// <summary>分类：softmax 取前 N 类标注（类别名来自标签文件；无标签时显示 "class N"）</summary>
        private void RunClassification(Mat dst, Tensor<float> outTensor, int[] dims, float conf, int topN,
                                         bool roiEnabled, Rect roi)
        {
            float[] logits = outTensor.ToArray();
            int nClasses = logits.Length;
            // softmax（数值稳定：减最大值再 exp）
            float max = logits.Max();
            double sum = 0;
            for (int i = 0; i < nClasses; i++) sum += Math.Exp(logits[i] - max);
            var top = new List<(int idx, float score)>();
            for (int i = 0; i < nClasses; i++)
            {
                float p = (float)(Math.Exp(logits[i] - max) / sum);
                if (p >= conf) top.Add((i, p));
            }
            top.Sort((a, b) => b.score.CompareTo(a.score));
            top = top.Take(Math.Max(1, topN)).ToList();

            LastTop.Clear();
            foreach (var (idx, score) in top)
            {
                string name = idx >= 0 && idx < Labels.Count ? Labels[idx] : "class " + idx;
                LastTop.Add((name, score));
            }

            // 图上标注（GDI+，中文不乱码）：
            // · 左上角 Top-N 列表（只显示 ≥ 置信度阈值的类别）；
            // · 分类模型没有检测框，把识别区域圈出来（虚线框 + 中心十字 + 中心坐标）：
            //   启用了 ROI 就圈框选区域，否则圈整图。
            if (top.Count > 0)
            {
                // 类别标签只在左上角 Top-N 列表标一次（白底黑字），不再在识别区域顶部重复叠加，
                // 避免同一最高类别出现两个重复标签；虚线框/中心十字/中心坐标仍由 DrawClassRegion 画
                DrawTextList(dst, LastTop.Select(t => $"{t.Name} {t.Score:P1}").ToList());
                DrawClassRegion(dst, roiEnabled, roi, null, 0f);
            }
            else
            {
                // 全部类别都低于置信度阈值：不显示任何标注，并明确提示阈值生效
                LastSummary = string.Format("深度学习推理(分类): 所有类别低于阈值 {0:P0}，未标注（调低阈值或换更强模型）", conf);
            }
        }

        // ============================== 检测后端 ==============================

        /// <summary>
        /// 检测：兼容 YOLOv5（[1,N,5+C] cx,cy,w,h,score,class_scores）与
        /// YOLOv8（[1,4+C,N]，先转置为 [1,N,4+C]）。置信度过滤 → NMS → 画框。
        /// 框坐标乘 padRatio 并扣除 letterbox 偏移后映射回原图。
        /// </summary>
        private void RunDetection(Mat dst, Tensor<float> outTensor, int[] dims, float conf, float iou,
            int inputSide, float padRatio, int roiX, int roiY)
        {
            // 统一为 [N, 5+C] 的框行
            // YOLOv5：[1,N,5+C]（N 锚框数大、5+C 列数小）；YOLOv8：[1,4+C,N]（4+C 列数小、N 锚框数大）。
            // 用「列数 vs N 谁小」判别格式，不写死通道上限：
            // COCO 80 类模型 85/84 列、自定义多类模型列数更大，按列数 ≤40 判断会把合法模型误拒。
            List<float[]> boxes = new();
            if (dims.Length == 3 && dims[0] == 1)
            {
                int small = Math.Min(dims[1], dims[2]);
                int large = Math.Max(dims[1], dims[2]);
                if (small >= 5 && large >= 1)
                {
                    float[] flat = outTensor.ToArray();
                    if (dims[2] == small)
                    {
                        // YOLOv5：输出 [1,N,5+C]，行序天然（行数=N 锚框数、列数=5+C）
                        int n = dims[1], cols = dims[2];
                        for (int i = 0; i < n; i++)
                        {
                            var row = new float[cols];
                            Array.Copy(flat, i * cols, row, 0, cols);
                            boxes.Add(row);
                        }
                    }
                    else
                    {
                        // YOLOv8：输出 [1,4+C,N]（4+C 在 dim1、N 锚框数在 dim2），转置成行
                        int cols = dims[1], n = dims[2];
                        for (int i = 0; i < n; i++)
                        {
                            var row = new float[cols];
                            for (int c = 0; c < cols; c++)
                                row[c] = flat[c * n + i];
                            boxes.Add(row);
                        }
                    }
                }
            }

            if (boxes.Count == 0)
            {
                // 未知输出形状：不能硬猜，防御返回。
                // [1,C]（2 维）是训练页导出的分类模型输出（logits），提示把任务类型改为 0=分类；
                // 其余形状提示期望的 YOLO 格式。
                string shape = string.Join("x", dims);
                if (dims.Length == 2 && dims[0] == 1 && dims[1] >= 2 && dims[1] <= 64)
                {
                    _loadError = "输出形状 [1," + dims[1] + "] 是分类模型的 logits（" + dims[1] + " 类）：" +
                                 "请把「任务类型」改为 0=图像分类；YOLO 检测需要 [1,N,5+C] 或 [1,4+C,N] 输出";
                }
                else
                {
                    _loadError = "未知输出形状 " + shape + "（期望 YOLOv5 [1,N,5+C] 或 YOLOv8 [1,4+C,N]）";
                }
                LastSummary = "深度学习推理: 失败（" + _loadError + "）";
                return;
            }

            int nCols = boxes.Count == 0 ? 0 : boxes[0].Length;
            int nClasses = Math.Max(0, nCols - 5);

            // 逐框：取类别置信度，过滤低分
            var cands = new List<(float[] row, float score, int cls)>();
            for (int i = 0; i < boxes.Count; i++)
            {
                float[] row = boxes[i];
                float objScore = row[4];
                // 类别置信度 = 目标分数 × 最高类分数（YOLOv5 语义）或直接取行内最高类分数
                float bestCls = 0;
                int bestIdx = -1;
                for (int c = 5; c < nCols; c++)
                {
                    if (row[c] > bestCls) { bestCls = row[c]; bestIdx = c - 5; }
                }
                float score = nClasses > 1 ? objScore * bestCls : Math.Max(objScore, bestCls);
                if (score < conf || bestIdx < 0) continue;
                cands.Add((row, score, bestIdx));
            }

            // 按得分降序后做 NMS（重叠 IoU > 阈值则抑制）
            cands.Sort((a, b) => b.score.CompareTo(a.score));
            var keep = new List<(float[] row, float score, int cls)>();
            foreach (var c in cands)
            {
                float cx = c.row[0], cy = c.row[1], bw = c.row[2], bh = c.row[3];
                bool sup = false;
                foreach (var k in keep)
                {
                    if (k.cls != c.cls) continue;   // 不同类不互相抑制（跨类重叠仍各自保留）
                    float kcx = k.row[0], kcy = k.row[1], kbw = k.row[2], kbh = k.row[3];
                    float ix = Math.Max(0, Math.Min(cx + bw / 2, kcx + kbw / 2) - Math.Max(cx - bw / 2, kcx - kbw / 2));
                    float iy = Math.Max(0, Math.Min(cy + bh / 2, kcy + kbh / 2) - Math.Max(cy - bh / 2, kcy - kbh / 2));
                    float inter = ix * iy;
                    float union = bw * bh + kbw * kbh - inter;
                    if (union > 0 && inter / union > iou) { sup = true; break; }
                }
                if (!sup) keep.Add(c);
            }

            // 映射回原图并画框：letterbox 时 x 无偏移（灰边只在下方/右侧），
            // 但为稳妥统一按中心坐标反算（padRatio = 原图/输入 的放大倍率）；
            // ROI 模式下推理源是框选子图，检测框中心要加回 ROI 左上角偏移才回到原图坐标
            LastBoxes.Clear();
            foreach (var (row, score, cls) in keep)
            {
                float cx = row[0] * padRatio + roiX;
                float cy = row[1] * padRatio + roiY;
                float bw = row[2] * padRatio;
                float bh = row[3] * padRatio;
                string name = cls >= 0 && cls < Labels.Count ? Labels[cls] : "class " + cls;
                LastBoxes.Add(new DetectBox
                {
                    X = cx - bw / 2f,
                    Y = cy - bh / 2f,
                    W = bw,
                    H = bh,
                    Class = cls,
                    Score = score,
                    Name = name,
                });
            }

            // 图上画框 + 类别标签
            DrawBoxes(dst);
        }

        // ============================== 滑窗多人/多目标检测（训练页自研模型） ==============================

        /// <summary>
        /// 滑窗多人/多目标识别：对整图做双尺度滑动窗口（窗口边长 ×1 / ×1.5），每个窗口
        /// 缩放到模型输入 → 推理 → softmax → 最高类别得分 ≥ 置信度且不是「背景」类 → 记录候选框；
        /// 最后 NMS 合并重叠框，一张图框出多个目标（人像姓名 / 产品 NG 类型）。
        /// 性能说明：每窗口一次 session.Run，窗口数多时耗时秒级；可用「窗口步长」提速。
        /// </summary>
        private void RunSlidingDetection(Mat dst, Mat srcMat, int inputSide, float conf, float iou,
                                           int win, int step, int topN, bool roiEnabled, Rect roi)
        {
            // 扫描范围：启用 ROI 只在框选区域内滑；否则整图
            int W = srcMat.Cols, H = srcMat.Rows;
            int sx = roiEnabled ? roi.X : 0, sy = roiEnabled ? roi.Y : 0;
            int sw = roiEnabled ? roi.Width : W, sh = roiEnabled ? roi.Height : H;
            if (sw < 1 || sh < 1) { sw = W; sh = H; sx = sy = 0; }
            if (win <= 0) win = Math.Max(32, Math.Min(sw, sh) / 6);   // 默认按区域短边 1/6
            win = Math.Min(win, Math.Min(sw, sh));                     // 不超过区域短边
            if (step <= 0) step = Math.Max(8, win / 2);              // 默认步长=边长一半

            var cands = ScanWindows(srcMat, new OpenCvSharp.Rect(sx, sy, sw, sh), win, step,
                                    inputSide, conf, out int outClasses, out int winCount);
            var final = DedupAndTop(cands, iou, topN);
            LastBoxes.Clear();
            foreach (var b in final) LastBoxes.Add(b);
            DrawBoxes(dst);

            // 摘要反馈阈值/数量在起作用：窗口数 → 过阈值候选 → NMS/TopN 后实际画框数
            LastSummary = string.Format(
                "深度学习推理(滑窗): 窗口 {0} 个 → 过阈值 {1} 个 → NMS/Top{2} 后 {3} 框（阈值 {4:P0}，窗口 {5}px）",
                winCount, cands.Count, topN, final.Count, conf, win)
                + (_labelMismatch ? "；⚠ 标签数 " + Labels.Count + " ≠ 模型输出 " + outClasses + " 类，请核对模型与标签文件" : "");
        }

        /// <summary>
        /// 人脸增强识别（任务类型=2 + 人脸增强开）：
        /// 人脸检测引擎（YuNet 优先，Haar 回退）自动检测人脸 → 每张人脸外扩 15% 做局部滑窗
        /// （窗口=人脸短边 0.7、步长=窗口 1/3），汇总所有候选 → NMS/TopN → 统一画框；
        /// 多人脸各出一框，不扫全图（更快更准）。
        /// 启用 ROI 时只检测 ROI 区域内的人脸（人脸框坐标偏移回原图），ROI 外不作业。
        /// </summary>
        private void RunFaceDetection(Mat dst, Mat srcMat, int inputSide, float conf, float iou, int topN,
                                      bool roiEnabled, Rect roi)
        {
            Rect[] faces;
            int offX = 0, offY = 0;
            if (roiEnabled && roi.Width > 0 && roi.Height > 0)
            {
                // ROI 子图（与原图共享像素内存）上做人脸检测，框坐标需偏移回原图
                using var roiSub = new Mat(srcMat, roi);
                faces = FaceDetector.Detect(roiSub);
                offX = roi.X; offY = roi.Y;
            }
            else
            {
                faces = FaceDetector.Detect(srcMat);
            }

            if (faces.Length == 0)
            {
                LastBoxes.Clear();
                LastSummary = "深度学习推理(人脸增强): 未检测到人脸（模型文件缺失或图中无人脸）；可关闭人脸增强参数改回全图滑窗";
                return;
            }

            var cands = new List<DetectBox>();
            int winCount = 0;
            foreach (var f in faces)
            {
                // 人脸框外扩 15%（覆盖额头/下巴/发际），裁剪到图内；ROI 模式下坐标偏移回原图
                var face = new Rect(f.X + offX, f.Y + offY, f.Width, f.Height);
                int padX = (int)(face.Width * 0.15f), padY = (int)(face.Height * 0.15f);
                int rx = Math.Max(0, face.X - padX), ry = Math.Max(0, face.Y - padY);
                int rw = Math.Min(face.Width + padX * 2, srcMat.Cols - rx);
                int rh = Math.Min(face.Height + padY * 2, srcMat.Rows - ry);
                if (rw < 8 || rh < 8) continue;
                // 人脸内滑窗：窗口=人脸短边 0.7（人脸居中，少量窗口即可），步长=窗口 1/3
                int win = Math.Min(Math.Min(rw, rh), Math.Max(16, (int)(Math.Min(rw, rh) * 0.7f)));
                int step = Math.Max(8, win / 3);
                cands.AddRange(ScanWindows(srcMat, new OpenCvSharp.Rect(rx, ry, rw, rh), win, step,
                                           inputSide, conf, out _, out int wc));
                winCount += wc;
            }

            var final = DedupAndTop(cands, iou, topN);
            LastBoxes.Clear();
            foreach (var b in final) LastBoxes.Add(b);
            DrawBoxes(dst);

            LastSummary = string.Format(
                "深度学习推理(人脸增强): 检测到人脸 {0} 张 → 窗口 {1} 个 → 过阈值 {2} 个 → NMS/Top{3} 后 {4} 框",
                faces.Length, winCount, cands.Count, topN, final.Count);
        }

        /// <summary>
        /// 在 roi 区域内做双尺度滑窗扫描，返回过阈值候选（含类别/置信度/框）。
        /// 供全图滑窗与人脸增强局部滑窗共用；outClasses=最后一次窗口的模型输出类别数（摘要诊断用）。
        /// </summary>
        private List<DetectBox> ScanWindows(Mat srcMat, OpenCvSharp.Rect roi, int win, int step,
                                            int inputSide, float conf, out int outClasses, out int winCount)
        {
            outClasses = -1;
            winCount = 0;
            int W = srcMat.Cols, H = srcMat.Rows;
            int sx = Math.Max(0, Math.Min(roi.X, W - 1)), sy = Math.Max(0, Math.Min(roi.Y, H - 1));
            int sw = Math.Min(roi.Width, W - sx), sh = Math.Min(roi.Height, H - sy);
            if (sw < 1 || sh < 1) return new List<DetectBox>();
            if (win <= 0) win = Math.Max(32, Math.Min(sw, sh) / 6);
            win = Math.Min(win, Math.Min(sw, sh));
            if (step <= 0) step = Math.Max(8, win / 2);

            // 双尺度：目标大小不一（人像大头/全身、不同尺寸缺陷）时提高召回
            int[] sizes = { win, (int)Math.Round(win * 1.5f) };
            var cands = new List<DetectBox>();
            foreach (int sz in sizes)
            {
                if (sz <= 0 || sz > Math.Min(sw, sh)) continue;
                for (int y = sy; y + sz <= sy + sh; y += step)
                {
                    for (int x = sx; x + sz <= sx + sw; x += step)
                    {
                        winCount++;
                        using Mat crop = new Mat(srcMat, new OpenCvSharp.Rect(x, y, sz, sz));
                        using Mat resized = new Mat();
                        Cv2.Resize(crop, resized, new OpenCvSharp.Size(inputSide, inputSide), 0, 0, InterpolationFlags.Area);
                        // 正方形窗口 → 直接 letterbox（等比 scale=1，无灰边），BGR→RGB 归一化 NCHW
                        float[] input = BuildInput(resized, inputSide, out _);
                        if (input == null) continue;
                        // 输入张量按模型声明类型构造（与主推理路径一致，避免类型不匹配）
                        var inMeta = _session.InputMetadata.FirstOrDefault();
                        dynamic tensor = BuildInputTensor(input, inMeta.Value.ElementDataType,
                                                          new[] { 1, 3, inputSide, inputSide });
                        var ins = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inMeta.Key, tensor) };
                        using var results = _session.Run(ins);
                        float[] logits = results.First().AsTensor<float>().ToArray();
                        int C = Math.Max(1, logits.Length);
                        outClasses = C;
                        float mx = logits.Max();
                        double sum = 0;
                        for (int c = 0; c < C; c++) sum += Math.Exp(logits[c] - mx);
                        int best = 0; float bestP = 0f;
                        for (int c = 0; c < C; c++)
                        {
                            float p = (float)(Math.Exp(logits[c] - mx) / sum);
                            if (p > bestP) { bestP = p; best = c; }
                        }
                        if (C != Labels.Count) _labelMismatch = true;   // 标签数与模型输出不一致（换错模型/标签）
                        if (bestP < conf) continue;                     // 置信度过低：不认
                        string bname = best < Labels.Count ? Labels[best] : "class " + best;
                        if (IsBackgroundName(bname)) continue;          // 最高分是「背景」：拒绝
                        cands.Add(new DetectBox
                        {
                            X = x, Y = y, W = sz, H = sz,
                            Class = best, Score = bestP, Name = bname,
                        });
                    }
                }
            }
            return cands;
        }

        /// <summary>上次执行是否发现标签文件类别数与模型输出不一致（换错模型/标签时提醒）</summary>
        private bool _labelMismatch;

        /// <summary>
        /// 滑窗结果收尾：NMS（IoU）→ 同类中心距离去重（双尺度 ×1/×1.5 同一目标会出两个重叠框，
        /// 相邻滑窗步长=窗口一半时中心距≤窗口一半，也必须合并）→ 按分数截断 TopN。
        /// 中心距离判定：≤ 小框短边 60%（覆盖相邻滑窗命中与双尺度重叠，避免同一目标重复框）。
        /// </summary>
        private static List<DetectBox> DedupAndTop(List<DetectBox> cands, float iou, int topN)
        {
            var nms = NmsFilter(cands, iou);
            var keep = new List<DetectBox>();
            foreach (var b in nms.OrderByDescending(x => x.Score))
            {
                bool dup = false;
                foreach (var k in keep)
                {
                    if (k.Class != b.Class) continue;   // 不同类不互斥（并排多人/多 NG 类型各留一框）
                    float cdx = (b.X + b.W / 2f) - (k.X + k.W / 2f);
                    float cdy = (b.Y + b.H / 2f) - (k.Y + k.H / 2f);
                    float dist = MathF.Sqrt(cdx * cdx + cdy * cdy);
                    // 中心距离 ≤ 小框短边 75% → 视为同一目标，保留得分高者（同标签只画一次）。
                    // 75% 覆盖滑窗网格最远相邻命中（步长=窗口一半时，对角相邻中心距=窗口/2×√2
                    // ≈0.71 窗口），避免同一目标出重复框；两个真实目标中心距<0.75 窗口时
                    // 本就在滑窗分辨极限内（窗口本身 ≥ 目标），漏合并风险可接受。
                    if (dist <= Math.Min(b.W, b.H) * 0.75f) { dup = true; break; }
                }
                if (!dup) keep.Add(b);
            }
            return keep.OrderByDescending(b => b.Score).Take(Math.Max(1, topN)).ToList();
        }

        /// <summary>是否为「背景」标签（训练时标注的背景负样本框；最高分为它即拒绝该窗口）</summary>
        private static bool IsBackgroundName(string name)
            => name == "背景" || string.Equals(name, "background", StringComparison.OrdinalIgnoreCase);

        /// <summary>通用 NMS（按得分降序，同类重叠 IoU>阈值 抑制；不同类不互斥）</summary>
        private static List<DetectBox> NmsFilter(List<DetectBox> cands, float iou)
        {
            var keep = new List<DetectBox>();
            foreach (var c in cands.OrderByDescending(b => b.Score))
            {
                bool sup = false;
                foreach (var k in keep)
                {
                    if (k.Class != c.Class) continue;   // 不同类不互相抑制（跨类重叠仍各自保留）
                    float ix = Math.Max(0, Math.Min(c.X + c.W, k.X + k.W) - Math.Max(c.X, k.X));
                    float iy = Math.Max(0, Math.Min(c.Y + c.H, k.Y + k.H) - Math.Max(c.Y, k.Y));
                    float inter = ix * iy;
                    float union = c.W * c.H + k.W * k.H - inter;
                    if (union > 0 && inter / union > iou) { sup = true; break; }
                }
                if (!sup) keep.Add(c);
            }
            return keep;
        }

        // ============================== GDI+ 标注（中文不乱码） ==============================

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

        /// <summary>把 Mat 拷贝成 GDI+ Bitmap 供绘制（与 BarcodeTask 同一套防乱码方案）</summary>
        private static Bitmap MatToBitmap(Mat dst)
        {
            int w = dst.Cols, h = dst.Rows;
            // 32bppArgb：24bpp RGB 在部分显示环境（16 位色/远程桌面）下
            // Graphics.FromImage/DrawString 会抛 "Parameter is not valid"，32bpp 兼容性最好
            const PixelFormat fmt = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var bmp = new Bitmap(w, h, fmt);
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
            return bmp;
        }

        /// <summary>顶部列表标注（分类 Top-N 用）：白底黑字单次绘制，与检测标签同风格</summary>
        private void DrawTextList(Mat dst, List<string> lines)
        {
            using var bmp = MatToBitmap(dst);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float fs = Math.Clamp(dst.Cols / 90f, 11f, 18f);
                using var font = MakeSafeFont(fs);
                float y = 4;
                foreach (string line in lines.Take(5))
                {
                    SizeF sz = g.MeasureString(line, font);
                    using var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255));   // 白底保证任何底图清晰
                    g.FillRectangle(bg, 2, y - 1, sz.Width + 6, sz.Height + 2);
                    g.DrawString(line, font, Brushes.Black, 4, y);
                    y += fs + 6;
                }
            }
            CopyBitmapBack(bmp, dst);
        }

        /// <summary>检测框 + 类别标签（黄框 + 黑底白字标签条）</summary>
        private void DrawBoxes(Mat dst)
        {
            if (LastBoxes.Count == 0) return;
            using var bmp = MatToBitmap(dst);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float fs = Math.Clamp(dst.Cols / 110f, 10f, 16f);
                using var font = MakeSafeFont(fs);
                // 标准检测样式（单目标/多目标一致）：紫色矩形框 + 框上方白底黑字「类别: 置信度」
                using var pen = new Pen(Color.FromArgb(255, 168, 85, 247), Math.Max(2f, dst.Cols / 400f));
                foreach (var b in LastBoxes)
                {
                    float x = b.X, y = b.Y, w = Math.Max(1, b.W), h = Math.Max(1, b.H);
                    // 框（可能部分出图，裁剪到图内显示）
                    float cx = Math.Clamp(x, 0, dst.Cols), cy = Math.Clamp(y, 0, dst.Rows);
                    float cw = Math.Min(x + w, dst.Cols) - cx;
                    float ch = Math.Min(y + h, dst.Rows) - cy;
                    g.DrawRectangle(pen, cx, cy, Math.Max(1, cw), Math.Max(1, ch));
                    // 标签「person: 99.30%」贴在框上方；白底黑字保证任何底图都清晰
                    string label = $"{b.Name}: {b.Score:P2}";
                    SizeF sz = g.MeasureString(label, font);
                    float bx = cx, by = cy - sz.Height - 2;
                    if (by < 0) by = cy + 2;                       // 出图则放框内上沿
                    if (bx + sz.Width + 6 > dst.Cols) bx = Math.Max(0, dst.Cols - sz.Width - 6);
                    using var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                    g.FillRectangle(bg, bx, by, sz.Width + 6, sz.Height + 2);
                    g.DrawString(label, font, Brushes.Black, bx + 3, by + 1);
                }
            }
            CopyBitmapBack(bmp, dst);
        }

        /// <summary>
        /// 分类结果在图上圈选：分类模型只输出类别、没有检测框，这里把整图作为"识别区域"
        /// 用虚线框圈出来，并画出中心十字与中心坐标（像素），让结果位置在图上可见。
        /// </summary>
        private void DrawClassRegion(Mat dst, bool roiEnabled, Rect roi, string topName, float topScore)
        {
            if (dst?.Empty() != false) return;
            try
            {
                using var bmp = MatToBitmap(dst);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float fs = Math.Clamp(dst.Cols / 110f, 10f, 16f);
                    using var pen = new Pen(Color.Lime, Math.Max(2f, dst.Cols / 500f))
                    {
                        DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
                    };
                    // 识别区域：启用 ROI 圈框选区域，否则圈整图
                    int rx, ry, rw, rh;
                    if (roiEnabled && roi.Width > 0 && roi.Height > 0)
                    {
                        rx = roi.X; ry = roi.Y; rw = roi.Width; rh = roi.Height;
                    }
                    else { rx = 0; ry = 0; rw = dst.Cols; rh = dst.Rows; }
                    g.DrawRectangle(pen, rx + 2, ry + 2, Math.Max(1, rw - 4), Math.Max(1, rh - 4));
                    // 中心十字（识别区域中心）
                    int cx = rx + rw / 2, cy = ry + rh / 2;
                    g.DrawLine(pen, cx - 12, cy, cx + 12, cy);
                    g.DrawLine(pen, cx, cy - 12, cx, cy + 12);
                    // 识别区域顶部大标签已移除（类别只由左上角 Top-N 列表标注，避免重复标签）；
                    // 保留中心坐标文字（黑底白字，避免与十字/底图混）
                    string txt = string.Format("识别区域中心 ({0},{1})", cx, cy);
                    using var font = MakeSafeFont(fs);
                    SizeF sz = g.MeasureString(txt, font);
                    float tx = cx - sz.Width / 2f, ty = cy + 14;
                    if (tx < 4) tx = 4;
                    if (tx + sz.Width > dst.Cols - 4) tx = Math.Max(4, dst.Cols - 4 - sz.Width);
                    if (ty + sz.Height > dst.Rows - 2) ty = Math.Max(4, cy - sz.Height - 4);
                    using var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                    g.FillRectangle(bg, tx - 2, ty - 2, sz.Width + 6, sz.Height + 4);
                    g.DrawString(txt, font, Brushes.White, tx, ty);
                }
                CopyBitmapBack(bmp, dst);
            }
            catch
            {
                // 标注失败不影响识别结果本身（结果文字与摘要仍有效）
            }
        }

        /// <summary>把 GDI+ Bitmap 画回 Mat（结果图交付）</summary>
        private static void CopyBitmapBack(Bitmap bmp, Mat dst)
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
                    Marshal.Copy(bd.Scan0 + (y * bd.Stride), row, 0, row.Length);
                    for (int x = 0; x < bmp.Width; x++)
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

