using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 自动化节点图 JSON 序列化（格式权威实现，从旧版 WinForms 编辑器 AutomationNodeEditor 抽出）。
    ///
    /// 格式：{ version, nodes[], templates { "节点id": "base64 PNG" }, texts, keys, ops, extra }。
    /// 旧版本只写了 nodes 数组，Import 两种都能读。
    /// WPF 自动化页与无界面运行入口共用这里，保证与已有 .autograph.json 文件互通。
    /// </summary>
    public static class AutomationGraphIO
    {
        /// <summary>把节点图导出为 JSON 文本（含每个匹配节点的模板图，base64 PNG）</summary>
        public static string Export(AutomationGraph graph)
        {
            var templates = new Dictionary<string, string>();
            foreach (var kv in graph.NodeTemplates)
            {
                string b64 = VisionHelper.SaveTemplateState(kv.Value);
                if (!string.IsNullOrEmpty(b64)) templates[kv.Key.ToString()] = b64;
            }
            // 键盘输入节点的内容：id -> 字符串。空的不写，文件干净些
            var texts = new Dictionary<string, string>();
            var keys = new Dictionary<string, string>();
            foreach (var kv in graph.NodeTexts)
                if (!string.IsNullOrEmpty(kv.Value)) texts[kv.Key.ToString()] = kv.Value;
            foreach (var kv in graph.NodeKeys)
                if (!string.IsNullOrEmpty(kv.Value)) keys[kv.Key.ToString()] = kv.Value;

            // 通用视觉算子节点挑的算子名
            var ops = new Dictionary<string, string>();
            foreach (var kv in graph.NodeOps)
                if (!string.IsNullOrEmpty(kv.Value)) ops[kv.Key.ToString()] = kv.Value;

            // 第 2 个以上的字符串槽（命令行/表达式/等待条件/窗口 用），键是 "节点id:槽号"
            var extra = new Dictionary<string, string>();
            foreach (var kv in graph.NodeExtraStrings)
                if (!string.IsNullOrEmpty(kv.Value)) extra[kv.Key] = kv.Value;

            return JsonConvert.SerializeObject(
                new { version = 5, nodes = graph.Nodes, templates, texts, keys, ops, extra }, Formatting.Indented);
        }

        /// <summary>从 JSON 文本整图装载到目标图（兼容旧版"只有节点数组"的文件）。</summary>
        public static void Import(AutomationGraph graph, string json)
        {
            var root = JToken.Parse(json);
            List<AutoNode> nodes;
            var templates = new Dictionary<string, string>();
            var texts = new Dictionary<string, string>();
            var keys = new Dictionary<string, string>();
            var ops = new Dictionary<string, string>();
            var extra = new Dictionary<string, string>();

            if (root.Type == JTokenType.Array)
            {
                nodes = root.ToObject<List<AutoNode>>();     // 旧格式：顶层就是节点数组
            }
            else
            {
                nodes = root["nodes"]?.ToObject<List<AutoNode>>();
                if (root["templates"] is JObject t)
                    foreach (var p in t.Properties()) templates[p.Name] = p.Value.ToString();
                if (root["texts"] is JObject tx)
                    foreach (var p in tx.Properties()) texts[p.Name] = p.Value.ToString();
                if (root["keys"] is JObject ky)
                    foreach (var p in ky.Properties()) keys[p.Name] = p.Value.ToString();
                if (root["ops"] is JObject op)
                    foreach (var p in op.Properties()) ops[p.Name] = p.Value.ToString();
                if (root["extra"] is JObject ex)
                    foreach (var p in ex.Properties()) extra[p.Name] = p.Value.ToString();
            }
            if (nodes == null) throw new InvalidOperationException("文件里没有节点");

            // 整图替换：先清空（连模板一起）再装载，避免新旧节点/模板混在一起
            graph.Clear();
            int maxId = 0;
            foreach (var n in nodes)
            {
                graph.Nodes.Add(n);
                if (n.Id > maxId) maxId = n.Id;
            }
            graph.SetNextId(maxId + 1);
            graph.NormalizeParams();

            foreach (var kv in templates)
            {
                if (!int.TryParse(kv.Key, out int id)) continue;
                if (graph.Get(id) == null) continue;        // 模板没有对应节点就丢弃
                var m = VisionHelper.LoadTemplateState(kv.Value);
                if (m != null && !m.Empty()) graph.SetNodeTemplate(id, m);
            }
            foreach (var kv in texts)
                if (int.TryParse(kv.Key, out int id) && graph.Get(id) != null) graph.SetNodeText(id, kv.Value);
            foreach (var kv in keys)
                if (int.TryParse(kv.Key, out int id) && graph.Get(id) != null) graph.SetNodeKey(id, kv.Value);
            foreach (var kv in ops)
            {
                if (!int.TryParse(kv.Key, out int id)) continue;
                var node = graph.Get(id);
                if (node == null) continue;
                graph.SetNodeOp(id, kv.Value);
                node.OpName = kv.Value;
            }
            // 额外字符串槽："节点id:槽号" → 值（没有对应节点就丢弃）
            foreach (var kv in extra)
            {
                int colon = kv.Key.IndexOf(':');
                if (colon <= 0) continue;
                if (!int.TryParse(kv.Key.Substring(0, colon), out int id)) continue;
                if (!int.TryParse(kv.Key.Substring(colon + 1), out int slot)) continue;
                if (graph.Get(id) == null) continue;
                graph.SetNodeString(id, slot, kv.Value);
            }
        }
    }
}
