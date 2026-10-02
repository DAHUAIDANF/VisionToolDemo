using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 节点系统全覆盖测试：
    /// 1) 内置流程示例全部可执行（干跑，无真实副作用）
    /// 2) 24 种自动化节点（AutoNodeKind）逐一可执行
    /// 3) 全部视觉算子（111 个）作为"视觉算子"节点逐一可执行
    /// 运行：dotnet test VisionToolDemo.Tests --filter NodeCoverageTests
    /// </summary>
    public class NodeCoverageTests
    {
        /// <summary>构造 开始 → 目标节点 → 结束 的最小图，返回目标节点 id</summary>
        private static int MinGraph(AutomationGraph g, AutoNodeKind kind, AutoNode node)
        {
            var start = new AutoNode { Id = 1, Kind = AutoNodeKind.Start, X = 100, Y = 100, NextId = 2 };
            node.Id = 2;
            node.X = 200;
            node.Y = 200;
            var end = new AutoNode { Id = 3, Kind = AutoNodeKind.End, X = 300, Y = 300 };
            node.NextId = 3;
            g.Nodes.Add(start);
            g.Nodes.Add(node);
            g.Nodes.Add(end);
            return 2;
        }

        private static AutomationRunResult DryRun(AutomationGraph g, int maxSteps = 2000)
        {
            AutomationContext.DryRun = true;
            AutomationContext.BeginRound();
            return AutomationRunner.Run(g, maxSteps, null);
        }

        /// <summary>执行后收集日志（用于失败时诊断）</summary>
        private static string Dump(AutomationRunResult r)
        {
            var lines = r?.Lines;
            if (lines == null) return "(无日志)";
            return string.Join("\n", lines.TakeLast(12));
        }

        // ==================== 1. 内置流程示例 ====================

        public static IEnumerable<object[]> SampleCases()
        {
            foreach (var s in AutomationSamples.Items)
                yield return new object[] { s.Title };
        }

        [Theory]
        [MemberData(nameof(SampleCases))]
        public void BuiltinSamples_ExecuteWithoutCrash(string title)
        {
            var sample = AutomationSamples.Items.First(s => s.Title == title);
            var g = new AutomationGraph();
            AutomationGraphIO.Import(g, sample.Json);
            Assert.NotEmpty(g.Nodes);

            AutomationRunResult r = null;
            var ex = Record.Exception(() => r = DryRun(g));
            Assert.Null(ex);   // 运行器本身不允许抛异常
            Assert.NotNull(r);
            Assert.True(r.Steps > 0, title + " 应有执行步数。日志：\n" + Dump(r));
        }

        // ==================== 2. 24 种自动化节点 ====================

        public static IEnumerable<object[]> AllKinds()
        {
            foreach (AutoNodeKind k in Enum.GetValues<AutoNodeKind>())
            {
                if (k is AutoNodeKind.Start or AutoNodeKind.End or AutoNodeKind.VisionOp) continue;
                yield return new object[] { k };
            }
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void EveryAutoNodeKind_ExecutesWithoutCrash(AutoNodeKind kind)
        {
            using var tpl = new Mat(20, 30, MatType.CV_8UC3, new Scalar(60, 90, 120));
            var g = new AutomationGraph();
            var node = new AutoNode { Kind = kind, Params = AutoNodeInfo.DefaultParams(kind) };
            int id = MinGraph(g, kind, node);

            // 各种类需要的字符串槽/连接，按运行器契约给全
            switch (kind)
            {
                case AutoNodeKind.Loop:
                    node.Params = new[] { 1, 0 };            // 固定 1 次
                    break;
                case AutoNodeKind.Condition:
                    node.Params = new[] { 0, 0 };            // 判断"上次匹配"（无匹配 → 走否分支）
                    node.AltNextId = 3;                       // 否 → 结束
                    break;
                case AutoNodeKind.BreakLoop:
                    // 结束循环必须处于循环体内；构造 开始→循环→结束循环→结束
                    g.Nodes.Clear();
                    var start = new AutoNode { Id = 1, Kind = AutoNodeKind.Start, X = 100, Y = 100, NextId = 2 };
                    var loop = new AutoNode { Id = 2, Kind = AutoNodeKind.Loop, X = 200, Y = 200,
                        NextId = 3, AltNextId = 3, Params = new[] { 1, 0 } };
                    var brk = new AutoNode { Id = 3, Kind = AutoNodeKind.BreakLoop, X = 300, Y = 300, NextId = 5 };
                    var end = new AutoNode { Id = 5, Kind = AutoNodeKind.End, X = 400, Y = 400 };
                    g.Nodes.Add(start); g.Nodes.Add(loop); g.Nodes.Add(brk); g.Nodes.Add(end);
                    id = 3;
                    break;
                case AutoNodeKind.Match:
                    // 模板匹配节点必须有模板图（Validate 会拦）；模板生命周期覆盖整个 Run
                    g.SetNodeTemplate(id, tpl);
                    break;
                case AutoNodeKind.Popup:
                    g.SetNodeText(id, "测试弹窗");
                    break;
                case AutoNodeKind.SetVar:
                    g.SetNodeText(id, "var1");
                    g.SetNodeKey(id, "123");
                    break;
                case AutoNodeKind.Key:
                    g.SetNodeKey(id, "abc");
                    break;
                case AutoNodeKind.Capture:
                    g.SetNodeText(id, "");                   // 保存目录留空 = 默认
                    break;
                case AutoNodeKind.Command:
                    g.SetNodeString(id, 0, "cmd");
                    g.SetNodeString(id, 1, "/c echo ok");
                    break;
                case AutoNodeKind.Expression:
                    g.SetNodeString(id, 0, "1+1");
                    g.SetNodeString(id, 1, "结果");
                    break;
                case AutoNodeKind.WaitCondition:
                    g.SetNodeString(id, 0, "var1");
                    g.SetNodeString(id, 1, "1");
                    break;
                case AutoNodeKind.Window:
                    g.SetNodeString(id, 0, "不存在的窗口_测试");
                    break;
                case AutoNodeKind.Rule:
                    g.SetNodeString(id, 0, "var1");
                    g.SetNodeString(id, 1, "10");
                    break;
                case AutoNodeKind.Aggregate:
                    break;
                case AutoNodeKind.Calibrate:
                    break;
                case AutoNodeKind.Http:
                    g.SetNodeString(id, 0, "http://127.0.0.1:1/none");   // 干跑不真实请求
                    break;
                case AutoNodeKind.Roi:
                    g.SetNodeString(id, 0, "10,10,50,50");
                    break;
                case AutoNodeKind.Browser:
                    g.SetNodeString(id, 0, "https://example.com");
                    break;
                case AutoNodeKind.BrowserElement:
                    g.SetNodeString(id, 0, "登录");
                    break;
            }

            AutomationRunResult r = null;
            var ex = Record.Exception(() => r = DryRun(g));
            Assert.Null(ex);   // 运行器不允许抛异常
            Assert.NotNull(r);

            // 预期前置条件缺失类错误（缺模板/没截图/没目标窗口/请求失败）不算节点 bug：
            // 只断言"执行走到结束或有步数、且错误是已知前置缺失"。
            string[] allowed = { "模板", "截图", "窗口", "匹配", "目标", "区域", "变量",
                "请求", "网络", "失败", "未命中", "找不到", "没有", "URL", "OCR", "识别" };
            if (!string.IsNullOrEmpty(r.Error))
            {
                Assert.True(allowed.Any(k => r.Error.Contains(k)),
                    kind + " 出现未知错误：" + r.Error + "\n日志：\n" + Dump(r));
            }
            Assert.True(r.Steps > 0, kind + " 应有执行步数。日志：\n" + Dump(r));
        }

        // ==================== 3. 全部视觉算子作为节点 ====================

        public static IEnumerable<object[]> AllVisionOps()
        {
            foreach (var name in VisionTaskRegistry.GetVisionToolNames())
                yield return new object[] { name };
        }

        [Theory]
        [MemberData(nameof(AllVisionOps))]
        public void EveryVisionOpNode_ExecutesWithoutCrash(string opName)
        {
            var g = new AutomationGraph();
            var node = new AutoNode { Kind = AutoNodeKind.VisionOp, OpName = opName };
            int id = MinGraph(g, AutoNodeKind.VisionOp, node);

            var t = VisionTaskRegistry.GetTask(opName);
            Assert.NotNull(t);
            node.Params = t.ParamDescriptions.Select(d => d.DefaultValue).ToArray();

            AutomationRunResult r = null;
            var ex = Record.Exception(() => r = DryRun(g));
            Assert.Null(ex);   // 运行器不允许抛异常
            Assert.NotNull(r);
            Assert.True(r.Steps > 0, opName + " 应有执行步数。日志：\n" + Dump(r));

            // 允许预期前置缺失类错误，禁止未知崩溃错误
            if (!string.IsNullOrEmpty(r.Error))
            {
                string[] allowed = { "模板", "模型", "截图", "匹配", "文件", "目录", "读取",
                    "参数", "失败", "未", "找不到", "没有", "不能", "不支持", "无效",
                    "范围", "为空", "OCR", "识别", "网络", "URL", "请求", "无法", "空" };
                Assert.True(allowed.Any(k => r.Error.Contains(k)),
                    opName + " 出现未知错误：" + r.Error + "\n日志：\n" + Dump(r));
            }
        }

        // ==================== 4. 视觉算子直接 Execute 冒烟 ====================

        public static IEnumerable<object[]> SmokeOps()
        {
            // 挑一批有代表性的算子直接跑 Execute（有真实图像输入）
            string[] picks = {
                "二值化", "灰度化", "高斯滤波", "Canny边缘", "膨胀", "腐蚀",
                "仿射配准", "图像旋转", "图像缩放", "直方图均衡", "锐化",
                "形态学开运算", "轮廓查找", "轮廓面积", "模板匹配", "颜色识别",
                "字符识别OCR", "条码/二维码识别", "DPM二维码", "深度学习推理",
                "深度学习相似度", "像素统计", "图像拼接", "透视校正", "裁剪矩形",
            };
            foreach (var p in picks)
                if (VisionTaskRegistry.GetTask(p) != null)
                    yield return new object[] { p };
        }

        [Theory]
        [MemberData(nameof(SmokeOps))]
        public void RepresentativeOps_ExecuteOnRealImage(string opName)
        {
            var t = VisionTaskRegistry.GetTask(opName);
            Assert.NotNull(t);
            using var img = new Mat(120, 160, MatType.CV_8UC3, new Scalar(90, 120, 160));
            Cv2.Circle(img, new Point(60, 40), 18, new Scalar(20, 40, 200), -1);
            Cv2.Rectangle(img, new Rect(20, 80, 40, 30), new Scalar(240, 220, 40), 2);
            Cv2.PutText(img, "AB12", new Point(70, 100), HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);

            int[] ps = t.ParamDescriptions.Select(d => d.DefaultValue).ToArray();
            Mat outMat = null;
            var ex = Record.Exception(() =>
            {
                outMat = t.Execute(img, ps);
            });
            // 有图输入时算子不允许抛异常（缺模型/模板/文件类前置缺失由调用方处理）
            if (ex != null)
            {
                string[] allowed = { "模型", "模板", "文件", "目录", "读取", "参数", "网络", "URL",
                    "模型文件", "请先", "加载", "不能", "无效", "范围", "为空", "不支持", "OCR" };
                Assert.True(allowed.Any(k => ex.Message.Contains(k)),
                    opName + " 抛未知异常：" + ex.Message);
            }
            outMat?.Dispose();
        }
    }
}
