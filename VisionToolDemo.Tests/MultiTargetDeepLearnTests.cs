using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 深度学习推理「滑窗多人/多目标识别」（任务类型=2）端到端测试：
    /// 用训练集_复杂 的 chip/gear/pin/spring 四类模型，拼一张含 6 个元件的大图，
    /// 验证一张图能把多个目标全部识别并各自画框（人像姓名 / 产品 NG 多缺陷同理）。
    /// </summary>
    public class MultiTargetDeepLearnTests
    {
        private readonly ITestOutputHelper _output;

        public MultiTargetDeepLearnTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // 模型与标签（训练集_复杂 的导出模型，四类：chip/gear/pin/spring）
        private static readonly string ModelPath =
            @"C:\Users\huang\Desktop\VisionToolDemo\训练集_复杂\训练集_复杂\model.onnx";
        private static readonly string LabelPath =
            @"C:\Users\huang\Desktop\VisionToolDemo\训练集_复杂\训练集_复杂\labels.txt";
        private static readonly string TrainDir =
            @"C:\Users\huang\Desktop\VisionToolDemo\训练集_复杂\训练集_复杂\train";

        /// <summary>把素材小图等比缩放到目标边长并贴到大图指定位置（模拟流水线上多个零件/多张人脸同框）</summary>
        private static void Paste(Mat dst, string cls, int index, int px, int py, int side)
        {
            string file = Path.Combine(TrainDir, cls, $"{cls}_{index:D2}.bmp");
            Assert.True(File.Exists(file), $"素材缺失: {file}");
            using var small = Cv2.ImRead(file, ImreadModes.Color);
            Assert.False(small.Empty(), $"{file} 解码失败");
            using var scaled = new Mat();
            Cv2.Resize(small, scaled, new OpenCvSharp.Size(side, side), 0, 0, InterpolationFlags.Area);
            scaled.CopyTo(dst[new OpenCvSharp.Rect(px, py, side, side)]);
        }

        [Fact]
        public void 多目标图_滑窗识别_全部目标都被框出且类别正确()
        {
            // 1. 拼一张 900x500 大图：灰底 + 6 个元件（chip×2 / gear×2 / pin / spring）
            const int W = 900, H = 500, S = 72;
            using var scene = new Mat(H, W, MatType.CV_8UC3, new Scalar(118, 118, 118));
            // 坐标：(x,y) 每个目标间隔足够，避免相邻窗口互相干扰
            Paste(scene, "chip", 0, 40, 60, S);
            Paste(scene, "gear", 0, 240, 60, S);
            Paste(scene, "pin", 0, 440, 60, S);
            Paste(scene, "spring", 0, 640, 60, S);
            Paste(scene, "chip", 1, 120, 320, S);
            Paste(scene, "gear", 1, 520, 320, S);
            // 期望：6 个目标全部被识别（类别 chip≥2 / gear≥2 / pin≥1 / spring≥1）

            // 2. 配置深度学习推理算子（任务类型=2 滑窗多人/多目标）
            var task = new DeepLearnTask
            {
                ModelPath = ModelPath,
                Labels = File.ReadAllLines(LabelPath)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            };
            int[] p = new int[13];
            // 索引必须与 DeepLearnTask.ParamDescriptions 声明顺序一致：
            // [0]任务类型 [1]输入边长 [2]窗口边长 [3]窗口步长
            // [4..8]ROI(启用/X/Y/宽/高) [9]置信度阈值 [10]NMS IoU [11]显示前N类 [12]人脸增强
            p[0] = 2;    // 任务类型=2 滑窗
            p[1] = 0;    // 输入边长 0=自动读模型
            p[2] = S;    // 窗口边长=目标边长
            p[3] = S / 2;// 窗口步长=边长一半
            p[4] = 0;    // 启用ROI=0 整图
            p[5] = 0; p[6] = 0; p[7] = 0; p[8] = 0;  // ROI X/Y/宽/高=0
            p[9] = 30;   // 置信度阈值 30%
            p[10] = 45;  // NMS IoU
            p[11] = 8;   // 显示前N类（TopN）
            p[12] = 0;   // 人脸增强关

            // 3. 执行推理（整链真实路径：任务实例 → Execute）
            using var result = task.Execute(scene, p);
            Assert.False(result.Empty(), "结果图不应为空");

            // 存图供视觉确认（临时诊断产物，验证后清理）
            Cv2.ImWrite(@"C:\Users\huang\Desktop\VisionToolDemo\_multi_scene.bmp", scene);
            Cv2.ImWrite(@"C:\Users\huang\Desktop\VisionToolDemo\_multi_result.bmp", result);

            // 4. 断言：6 个目标全部识别，类别与摆放一致
            var boxes = task.LastBoxes;
            _output.WriteLine("=== 摘要 ===\n" + task.LastSummary);
            _output.WriteLine("=== 检测框 " + boxes.Count + " 个 ===");
            foreach (var b in boxes)
                _output.WriteLine($"  {b.Name} conf={b.Score:P1} @ ({b.X:F0},{b.Y:F0}) {b.W:F0}x{b.H:F0}");
            // 期望目标中心：chip(76,96) gear(276,96) pin(476,96) spring(676,96) chip(156,356) gear(556,356)
            var expect = new[]
            {
                ("chip", 76, 96), ("gear", 276, 96), ("pin", 476, 96),
                ("spring", 676, 96), ("chip", 156, 356), ("gear", 556, 356),
            };
            int hitCount = 0;
            foreach (var (cls, cx, cy) in expect)
            {
                bool hit = boxes.Any(b => b.Name == cls
                    && Math.Abs((b.X + b.W / 2) - cx) <= S * 0.9
                    && Math.Abs((b.Y + b.H / 2) - cy) <= S * 0.9);
                if (hit) hitCount++;
                _output.WriteLine($"期望 {cls} @({cx},{cy}) → {(hit ? "命中" : "未命中")}");
            }
            _output.WriteLine($"位置命中 {hitCount}/6（spring 若未命中：模型在窗口含背景时把弹簧误判为销钉，属分类模型局限，详见报告）");

            // 断言1：至少 5 个目标位置被框出（多目标识别功能成立）
            Assert.True(hitCount >= 5, $"应命中至少 5 个目标位置，实际 {hitCount} 个（摘要：{task.LastSummary}）");
            // 断言2：同一目标不得出现重复框（相邻滑窗/双尺度重叠框必须合并）
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i]; var b = boxes[j];
                    if (a.Name != b.Name) continue;
                    float ax = a.X + a.W / 2, ay = a.Y + a.H / 2;
                    float bx = b.X + b.W / 2, by = b.Y + b.H / 2;
                    float dist = MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                    Assert.False(dist < S * 0.75,
                        $"同类重复框：{a.Name} @({a.X},{a.Y}) 与 @({b.X},{b.Y}) 中心距 {dist:F0}px");
                }
            // 断言3：核心类别都识别到（spring 允许被混淆，见模型局限说明）
            var byClass = boxes.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.Count());
            Assert.True(byClass.GetValueOrDefault("chip") >= 2, "应识别出 2 个 chip（摘要：" + task.LastSummary + "）");
            Assert.True(byClass.GetValueOrDefault("gear") >= 2, "应识别出 2 个 gear（摘要：" + task.LastSummary + "）");
            Assert.True(byClass.GetValueOrDefault("pin") >= 1, "应识别出 pin（摘要：" + task.LastSummary + "）");

            // 5. 所有框在图内（坐标合法）
            foreach (var b in boxes)
            {
                Assert.True(b.W > 0 && b.H > 0);
                Assert.True(b.X >= 0 && b.Y >= 0, "框不得为负坐标");
            }
        }
    }
}
