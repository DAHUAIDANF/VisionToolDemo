using System;
using OpenCvSharp;
using VisionToolDemo.Vision.Automation;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 节点图 JSON 序列化回归测试（覆盖 2026-09 从 WinForms 编辑器抽出 AutomationGraphIO 的清理改动）。
    /// 运行：在 Windows 上 `dotnet test VisionToolDemo.Tests`（或 VS 测试资源管理器）。
    /// </summary>
    public class AutomationGraphIOTests
    {
        [Fact]
        public void ExportImport_RoundTrip_KeepsAllFields()
        {
            var g = new AutomationGraph();
            g.Nodes.Add(new AutoNode { Id = 1, Kind = AutoNodeKind.Match, X = 10, Y = 20, Params = new[] { 100, 0 } });
            g.Nodes.Add(new AutoNode { Id = 2, Kind = AutoNodeKind.Click, X = 30, Y = 40 });
            g.SetNodeText(1, "你好,文本");
            g.SetNodeKey(1, "ENTER");
            g.SetNodeOp(2, "模板匹配");
            g.SetNodeString(2, 3, "窗口标题");
            using var tpl = new Mat(6, 8, MatType.CV_8UC3, new Scalar(12, 34, 56));
            g.SetNodeTemplate(1, tpl);

            string json = AutomationGraphIO.Export(g);

            var g2 = new AutomationGraph();
            AutomationGraphIO.Import(g2, json);

            Assert.Equal(2, g2.Nodes.Count);
            Assert.Equal(1, g2.Nodes[0].Id);
            Assert.Equal(AutoNodeKind.Match, g2.Nodes[0].Kind);
            Assert.Equal(100, g2.Nodes[0].Params[0]);
            Assert.Equal("你好,文本", g2.GetNodeText(1));
            Assert.Equal("ENTER", g2.GetNodeKey(1));
            Assert.Equal("模板匹配", g2.GetNodeOp(2));
            Assert.Equal("窗口标题", g2.GetNodeString(2, 3));

            var t2 = g2.GetNodeTemplate(1);
            Assert.NotNull(t2);
            Assert.False(t2.Empty());
            Assert.Equal(8, t2.Cols);
            Assert.Equal(6, t2.Rows);
            t2.Dispose();
        }

        [Fact]
        public void Import_OldFormat_TopLevelArray()
        {
            const string oldJson =
                "[{\"Id\":7,\"Kind\":0,\"X\":1,\"Y\":2,\"NextId\":-1,\"AltNextId\":-1," +
                "\"Params\":[5],\"Enabled\":true,\"OpName\":\"\"}]";
            var g = new AutomationGraph();
            AutomationGraphIO.Import(g, oldJson);

            Assert.Single(g.Nodes);
            Assert.Equal(7, g.Nodes[0].Id);
        }

        [Fact]
        public void Import_EmptyGraph_ExportJsonIsValid()
        {
            var g = new AutomationGraph();
            string json = AutomationGraphIO.Export(g);
            var g2 = new AutomationGraph();
            AutomationGraphIO.Import(g2, json);
            Assert.Empty(g2.Nodes);
        }

        [Fact]
        public void Import_EmptyTextsAreSkipped_OrphanAndCorruptTemplateDropped()
        {
            const string bad =
                "{\"version\":5,\"nodes\":[{\"Id\":3,\"Kind\":0,\"X\":0,\"Y\":0,\"NextId\":-1," +
                "\"AltNextId\":-1,\"Params\":[],\"Enabled\":true,\"OpName\":\"\"}]," +
                "\"templates\":{\"9\":\"aW52YWxpZA==\",\"3\":\"not-base64!!\"}," +
                "\"texts\":{\"9\":\"孤儿\",\"3\":\"\"},\"ops\":{\"9\":\"孤操作\",\"3\":\"\"}}";
            var g = new AutomationGraph();
            AutomationGraphIO.Import(g, bad);

            Assert.Empty(g.NodeTemplates);            // 孤儿模板(9)与损坏 base64(3)都被丢弃
            Assert.Equal("", g.GetNodeText(9));       // 孤儿文本丢弃
            Assert.Equal("", g.GetNodeOp(9));         // 孤儿算子丢弃
        }

        [Fact]
        public void Import_NullNodes_ThrowsInvalidOperation()
        {
            var g = new AutomationGraph();
            Assert.Throws<InvalidOperationException>(() => AutomationGraphIO.Import(g, "{\"nodes\": null}"));
        }

        [Fact]
        public void Import_CorruptJson_Throws()
        {
            var g = new AutomationGraph();
            Assert.ThrowsAny<Exception>(() => AutomationGraphIO.Import(g, "{ not json !!"));
        }
    }
}
