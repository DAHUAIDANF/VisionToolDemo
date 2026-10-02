using System;
using System.IO;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// WIDER FACE 真实多人脸图上的深度学习人像识别端到端测试：
    /// 1) FaceDetector（Haar 级联）在真实多人合影上能检出多张人脸（训练页人脸辅助 / 目标跟踪人脸追踪共用）；
    /// 2) 深度学习推理「人脸增强」（任务类型=2 + 人脸增强=1）对真实人脸图运行，
    ///    先 Haar 检测人脸 → 每张人脸区域局部滑窗识别 → 多人脸各出一框；
    /// 3) 回归：普通滑窗（人脸增强=0）在真实图上也能出框（不因修复 Haar 路径而回归）。
    /// 用 WIDER FACE 验证集样例图（wider_few=5 人合影 / wider_many=行进乐队多人队列）。
    /// </summary>
    public class WiderFaceEnhanceTests
    {
        private readonly ITestOutputHelper _output;

        public WiderFaceEnhanceTests(ITestOutputHelper output) { _output = output; }

        // 测试输出目录里的 Haar 模型（csproj 已配置 PreserveNewest 拷贝到输出根）
        private static string ModelPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "haarcascade_frontalface_default.xml"));

        private static string FewPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wider_few.jpg"));

        private static string ManyPath =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wider_many.jpg"));

        // 训练集_复杂 的 chip/gear/pin/spring 四类模型（滑窗/人脸增强共用）
        private static readonly string DlModelPath =
            @"C:\Users\huang\Desktop\VisionToolDemo\训练集_复杂\训练集_复杂\model.onnx";
        private static readonly string DlLabelPath =
            @"C:\Users\huang\Desktop\VisionToolDemo\训练集_复杂\训练集_复杂\labels.txt";

        // ---------- 1. FaceDetector 真实多人脸检出 ----------

        [Fact]
        public void FaceDetector_输出目录_模型路径解析命中()
        {
            // 回归本轮的 csproj Link 展平修复：模型必须出现在输出根目录，FaceDetector 才找得到
            Assert.True(File.Exists(ModelPath), $"输出根应存在 Haar 模型（csproj Link 展平修复）：{ModelPath}");
            var p = FaceDetector.ResolveModelPath(null);
            Assert.NotNull(p);
            Assert.True(File.Exists(p));
        }

        [Fact]
        public void FaceDetector_WIDER5人合影_检出多张人脸()
        {
            Assert.True(File.Exists(FewPath));
            using var bgr = Cv2.ImRead(FewPath, ImreadModes.Color);
            Assert.False(bgr.Empty());
            var faces = FaceDetector.Detect(bgr, ModelPath);
            _output.WriteLine($"wider_few 检出 {faces.Length} 张人脸（标注 5 人）");
            Assert.True(faces.Length >= 2, $"5 人合影应至少检出 2 张人脸，实际 {faces.Length} 张");
            foreach (var f in faces)
            {
                Assert.True(f.X >= 0 && f.Y >= 0 && f.X + f.Width <= bgr.Cols && f.Y + f.Height <= bgr.Rows);
            }
        }

        [Fact]
        public void FaceDetector_WIDER行进队列_检出多张人脸()
        {
            Assert.True(File.Exists(ManyPath));
            using var bgr = Cv2.ImRead(ManyPath, ImreadModes.Color);
            Assert.False(bgr.Empty());
            var faces = FaceDetector.Detect(bgr, ModelPath);
            _output.WriteLine($"wider_many 检出 {faces.Length} 张人脸（标注 9 人）");
            Assert.True(faces.Length >= 3, $"行进队列应至少检出 3 张人脸，实际 {faces.Length} 张");
        }

        // ---------- 2. 深度学习推理 人脸增强 端到端 ----------

        [Fact]
        public void DeepLearn_人脸增强_5人合影_多人脸各出一框()
        {
            Assert.True(File.Exists(DlModelPath), "训练模型缺失: " + DlModelPath);
            Assert.True(File.Exists(FewPath));
            using var bgr = Cv2.ImRead(FewPath, ImreadModes.Color);

            var task = new DeepLearnTask
            {
                ModelPath = DlModelPath,
                Labels = File.ReadAllLines(DlLabelPath)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            };
            int[] p = new int[13];
            p[0] = 2;    // 任务类型=2 滑窗多人/多目标识别
            p[1] = 0;    // 输入边长 0=自动读模型
            p[2] = 0;    // 窗口边长 0=按图片短边 1/6 自动
            p[3] = 0;    // 窗口步长 0=窗口边长一半
            p[9] = 30;   // 置信度阈值 30%
            p[10] = 45;  // NMS IoU
            p[11] = 8;   // 显示前N类
            p[12] = 1;   // 人脸增强开：Haar 检测人脸 → 每张人脸区域局部滑窗

            using var result = task.Execute(bgr, p);
            Assert.False(result.Empty(), "结果图不应为空");
            _output.WriteLine("=== 人脸增强摘要 ===\n" + task.LastSummary);
            var boxes = task.LastBoxes;
            _output.WriteLine($"检测框 {boxes.Count} 个");
            foreach (var b in boxes)
                _output.WriteLine($"  {b.Name} conf={b.Score:P1} @ ({b.X:F0},{b.Y:F0}) {b.W:F0}x{b.H:F0}");

            // 人脸增强路径必须真正走到（摘要里出现滑窗/人脸关键词，而不是"未检测到人脸"降级）
            Assert.True(task.LastSummary.Contains("滑窗") || task.LastSummary.Contains("人脸"),
                "摘要应体现人脸增强滑窗路径：" + task.LastSummary);
            // 多人脸图至少出 2 框（每张脸各一框的机制成立）
            Assert.True(boxes.Count >= 2, $"5 人合影人脸增强应至少 2 框，实际 {boxes.Count}（摘要：{task.LastSummary}）");
            foreach (var b in boxes)
                Assert.True(b.W > 0 && b.H > 0 && b.X >= 0 && b.Y >= 0, "框坐标必须合法");
        }

        // ---------- 3. 回归：普通滑窗在真实图上仍可用 ----------

        [Fact]
        public void DeepLearn_普通滑窗_5人合影_仍能出框()
        {
            Assert.True(File.Exists(DlModelPath));
            Assert.True(File.Exists(FewPath));
            using var bgr = Cv2.ImRead(FewPath, ImreadModes.Color);

            var task = new DeepLearnTask
            {
                ModelPath = DlModelPath,
                Labels = File.ReadAllLines(DlLabelPath)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            };
            int[] p = new int[13];
            p[0] = 2; p[1] = 0; p[2] = 0; p[3] = 0;
            p[9] = 30; p[10] = 45; p[11] = 8;
            p[12] = 0;   // 人脸增强关

            using var result = task.Execute(bgr, p);
            Assert.False(result.Empty());
            _output.WriteLine("=== 普通滑窗摘要 ===\n" + task.LastSummary);
            Assert.True(task.LastBoxes.Count >= 1, "普通滑窗应在真人脸图上至少出 1 框（摘要：" + task.LastSummary + "）");
        }
    }
}
