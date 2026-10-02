using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VisionToolDemo.Vision;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 参数的"人话"显示。
    ///
    /// 背景：算子的取值含义一直写在 TaskParamDesc.ParamName 里，
    /// 形如「区域 0全屏1主屏2自定」「方式 0单击1双击2移动」「超时秒 0=不限」。
    /// 但界面之前只显示 DisplayFormat（"区域:0"），**图例被吞掉了**，
    /// 于是用户看到 "区域:0" 完全不知道 0/1/2 是什么；有些参数 Tip 还是空的，悬停也没提示。
    ///
    /// 这里做两件事：
    ///   1. 从 ParamName 尾部解析出 { 取值 → 名字 } 的图例（保守解析，解析不出就不显示，绝不乱猜）；
    ///   2. 生成"永远有内容"的说明文本（范围/默认/取值图例/提示），界面上做标签与悬停提示。
    /// </summary>
    public static class ParamDisplay
    {
        /// <summary>参数名（去掉尾部图例后的部分），例如「区域」</summary>
        public static string NameOf(TaskParamDesc d)
        {
            string n = d?.ParamName ?? "";
            int cut = LegendStart(n);
            if (cut >= 0) n = n.Substring(0, cut).Trim();
            return n.Length > 0 ? n : (d?.ParamName ?? "参数");
        }

        /// <summary>
        /// 解析取值图例：「区域 0全屏1主屏2自定」→ {0:全屏, 1:主屏, 2:自定}。
        /// 解析不出返回空字典（宁可不显示，也不要给出错的东西）。
        /// </summary>
        public static Dictionary<int, string> ParseLegend(string paramName)
        {
            var map = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(paramName)) return map;

            int start = LegendStart(paramName);
            if (start < 0) return map;
            string tail = paramName.Substring(start).Trim();
            if (tail.Length == 0 || tail[0] != '0') return map;

            // 按序号递增扫描：先找 0，再找 1、2、3…；两个序号之间的文字就是那个取值的名字。
            // 这样即使标签里本身带数字也能正确解析
            // （例：值来源 0上节点文字…6固定文本7计数器+1 → 7 的名字就是「计数器+1」）。
            int pos = 0, expect = 0;
            while (true)
            {
                string token = expect.ToString(CultureInfo.InvariantCulture);
                int at = IndexOfToken(tail, token, pos);
                if (at < 0) break;                       // 没有下一个序号 → 图例到此为止
                int labelStart = at + token.Length;
                if (labelStart < tail.Length && tail[labelStart] == '=') labelStart++;

                string nextToken = (expect + 1).ToString(CultureInfo.InvariantCulture);
                int next = IndexOfToken(tail, nextToken, labelStart);
                string label = next < 0 ? tail.Substring(labelStart)
                                        : tail.Substring(labelStart, next - labelStart);
                label = label.Trim().Trim('=', ' ', '、', ',', '，', '|', '/');
                if (label.Length == 0 || label.Length > 24) break;             // 标签为空/过长 → 不是图例
                map[expect] = label;
                if (next < 0) break;
                pos = next;
                expect++;
            }
            if (map.Count == 0 || !map.ContainsKey(0)) return new Dictionary<int, string>();
            return map;
        }

        /// <summary>找一个"独立"的序号（前后都不能是数字，避免在 10 里命中 1）</summary>
        private static int IndexOfToken(string s, string token, int from)
        {
            for (int i = Math.Max(0, from); i + token.Length <= s.Length; i++)
            {
                if (string.CompareOrdinal(s, i, token, 0, token.Length) != 0) continue;
                bool okLeft = i == 0 || !char.IsDigit(s[i - 1]);
                int rightEdge = i + token.Length;
                bool okRight = rightEdge >= s.Length || !char.IsDigit(s[rightEdge]);
                if (okLeft && okRight) return i;
            }
            return -1;
        }

        /// <summary>图例在参数名里的起始位置（-1 = 没有图例）。要求是空白/行首后的独立 "0"</summary>
        private static int LegendStart(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] != '0') continue;
                bool atBoundary = i == 0 || char.IsWhiteSpace(name[i - 1]) || name[i - 1] == '=';
                if (!atBoundary) continue;
                // "0" 后面必须是名字或 '='，不能是数字/小数字（避免把 0.001 这种当图例）
                if (i + 1 < name.Length)
                {
                    char c = name[i + 1];
                    if (char.IsDigit(c) || c == '.' || c == 'x' || c == 'X' || c == '%') continue;
                }
                return i;
            }
            return -1;
        }

        /// <summary>当前值的显示文本：「0（全屏）」；没有图例就只是「0」</summary>
        public static string ValueText(TaskParamDesc d, int value)
        {
            var legend = ParseLegend(d?.ParamName);
            if (legend.TryGetValue(value, out string label)) return value + "（" + label + "）";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>标签行：「区域   0=全屏 1=主屏 2=自定」（不用悬停就知道每个取值的含义）</summary>
        public static string LabelText(TaskParamDesc d)
        {
            string name = NameOf(d);
            var legend = ParseLegend(d?.ParamName);
            if (legend.Count == 0) return name;
            var sb = new StringBuilder(name);
            sb.Append("   ");
            foreach (var kv in Sorted(legend))
            {
                if (kv.Key > 0) sb.Append(' ');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// "永远有内容"的说明文本（悬停提示与详情用）：
        /// 取值图例 + 取值范围与默认值 + 算子自己写的 Tip（如果有）。
        /// </summary>
        public static string HelpText(TaskParamDesc d)
        {
            if (d == null) return "";
            var sb = new StringBuilder();

            var legend = ParseLegend(d.ParamName);
            if (legend.Count > 0)
            {
                sb.Append("取值：");
                var parts = new List<string>();
                foreach (var kv in Sorted(legend)) parts.Add(kv.Key + " = " + kv.Value);
                sb.Append(string.Join("，", parts));
                sb.AppendLine();
            }

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "范围 {0} ~ {1}，默认 {2}", d.Min, d.Max, d.DefaultValue));

            if (!string.IsNullOrWhiteSpace(d.Tip)) sb.Append(d.Tip.Trim());
            else if (legend.Count == 0 && !string.IsNullOrWhiteSpace(d.ParamName))
                sb.Append("（这个参数没写详细说明，可参考参数名：").Append(d.ParamName).Append("）");

            return sb.ToString().TrimEnd();
        }

        private static List<KeyValuePair<int, string>> Sorted(Dictionary<int, string> map)
        {
            var list = new List<KeyValuePair<int, string>>(map);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }
    }
}
