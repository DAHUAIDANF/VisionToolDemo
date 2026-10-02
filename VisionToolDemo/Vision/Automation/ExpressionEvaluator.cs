using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>表达式求值失败（带可读原因，直接写进日志）</summary>
    public sealed class ExpressionException : Exception
    {
        public ExpressionException(string message) : base(message) { }
    }

    /// <summary>
    /// 一个很小的表达式求值器：给"表达式节点"用，**零依赖**（不引入 Roslyn/PowerShell）。
    ///
    /// 支持：
    ///   · 数字、字符串（双引号）、true/false
    ///   · 变量引用 {变量名}（取 AutomationContext 里的全局变量；不存在当空串）
    ///   · 运算 + - * / %   比较 == != &lt; &lt;= &gt; &gt;=   逻辑 && || !   括号
    ///     （+ 两边只要有一边是字符串就做拼接；比较两边都能当数字就按数值比，"9 &lt; 10" 成立）
    ///   · 函数：取整/INT ROUND ABS MIN MAX 长度/LEN 子串/SUB 替换/REPLACE 包含/CONTAINS
    ///           拼接/CONCAT 去空格/TRIM 大写/UPPER 小写/LOWER 正则/REGEX 现在/NOW IF
    ///
    /// 为什么自己做而不是上 C# 脚本：Roslyn 要额外几个 NuGet 包（离线部署要一起带），
    /// 而且能执行任意代码；这里的语法覆盖"算个坐标、拼个字符串、判个条件"绝大多数场景。
    /// </summary>
    public static class ExpressionEvaluator
    {
        /// <summary>求值并把结果转成字符串（数字去掉多余的小数零）</summary>
        public static string EvaluateToString(string expression)
        {
            var v = Evaluate(expression);
            return v.IsNumber ? FormatNumber(v.Number) : v.Text;
        }

        /// <summary>把结果尽量当数字用；不是数字时抛 ExpressionException（点击/循环这类要数字的地方用）</summary>
        public static double EvaluateToNumber(string expression)
        {
            var v = Evaluate(expression);
            if (v.IsNumber) return v.Number;
            if (double.TryParse((v.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            throw new ExpressionException(string.Format("结果 \"{0}\" 不是数字", v.Text));
        }

        // ---------------------------------------------------------------- 值

        /// <summary>
        /// 表达式的一个值（数字或字符串）。
        /// 必须是 public：它会出现在 public 方法 Evaluate(string) 的返回类型上，
        /// 返回类型可访问性低于方法会直接编译不过（CS0050 系）。
        /// </summary>
        public struct Value
        {
            public bool IsNumber;
            public double Number;
            public string Text;

            public static Value Num(double d) => new() { IsNumber = true, Number = d };
            public static Value Str(string s) => new() { IsNumber = false, Text = s ?? "" };
            public string AsText() => IsNumber ? FormatNumber(Number) : (Text ?? "");
            public bool AsBool()
            {
                if (IsNumber) return Math.Abs(Number) > double.Epsilon;
                string t = (Text ?? "").Trim();
                return t.Length > 0
                    && !string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)
                    && t != "0" && t != "否";
            }
            public bool TryNumber(out double d)
            {
                if (IsNumber) { d = Number; return true; }
                return double.TryParse((Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            }
        }

        private static string FormatNumber(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsInfinity(d)) return d > 0 ? "∞" : "-∞";
            if (Math.Abs(d - Math.Round(d)) < 1e-9) return Math.Round(d).ToString("F0", CultureInfo.InvariantCulture);
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- 求值

        public static Value Evaluate(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ExpressionException("表达式为空");

            // 支持以 ; 分隔的语句序列与变量赋值（如 "int n=1;  n=n+1;"）：
            // C# 类型声明（int/var/double/...）自动剥离，赋值写入 AutomationContext 变量表，
            // 最后一条语句的值就是整个表达式的值。
            Value last = Value.Str("");
            foreach (var stmt in SplitStatements(expression))
            {
                if (string.IsNullOrWhiteSpace(stmt)) continue;
                last = EvaluateStatement(stmt);
            }
            return last;
        }

        /// <summary>按 ; 拆分语句（跳过字符串字面量里的 ;）</summary>
        private static List<string> SplitStatements(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == ';') { list.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        /// <summary>常见的 C# 类型声明前缀：本求值器无类型概念，见到就剥掉</summary>
        private static readonly string[] TypePrefixes =
            { "int", "var", "double", "float", "bool", "string", "short", "long", "byte", "uint", "ulong", "decimal", "char" };

        private static void StripTypePrefix(ref string s)
        {
            foreach (var tp in TypePrefixes)
            {
                if (s.Length > tp.Length
                    && string.Equals(s.Substring(0, tp.Length), tp, StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(s[tp.Length]))
                {
                    s = s.Substring(tp.Length).TrimStart();
                    return;
                }
            }
        }

        /// <summary>求值一条语句：可选类型声明前缀；形如 "变量 = 表达式" 的赋值会写入变量表</summary>
        private static Value EvaluateStatement(string stmt)
        {
            string s = stmt.Trim();
            if (s.Length == 0) return Value.Str("");
            StripTypePrefix(ref s);

            // 变量赋值：标识符 = 表达式（==/!=/&lt;=/&gt;= 里的等号不算赋值）
            int eq = FindAssign(s);
            if (eq > 0)
            {
                string name = s.Substring(0, eq).Trim();
                if (IsIdentifier(name))
                {
                    string right = s.Substring(eq + 1).Trim();
                    if (right.Length == 0) throw new ExpressionException("赋值 " + name + " = 的右边是空的");
                    var assign = new Parser(right);
                    Value v = assign.ParseOr();
                    // 赋值右边也必须完全消费：漏写的右括号/多余字符（如 "n=1+2)"）不能静默接受
                    assign.SkipSpaces();
                    if (!assign.AtEnd)
                        throw new ExpressionException(string.Format("赋值 {0} 的右边第 {1} 个字符处有多余内容: {2}",
                            name, assign.Position + 1, assign.RestSnippet()));
                    AutomationContext.SetVariable(name, v.AsText());
                    return v;
                }
            }

            var p = new Parser(s);
            Value val = p.ParseOr();
            p.SkipSpaces();
            if (!p.AtEnd)
                throw new ExpressionException(string.Format("表达式第 {0} 个字符处有多余内容: {1}",
                    p.Position + 1, p.RestSnippet()));
            return val;
        }

        /// <summary>找赋值等号（跳过字符串字面量、括号深度，排除 == != &lt;= &gt;=）</summary>
        private static int FindAssign(string s)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '(' || c == '{') { depth++; continue; }
                if (c == ')' || c == '}') { depth--; continue; }
                if (c == '=' && depth == 0)
                {
                    if (i > 0 && (s[i - 1] == '=' || s[i - 1] == '!' || s[i - 1] == '<' || s[i - 1] == '>')) continue;
                    if (i + 1 < s.Length && s[i + 1] == '=') continue;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>变量文本转值：能解析成数字就按数字用（"n=n+1" 里 n=1 参与加法而不是拼成 "11"）</summary>
        private static Value FromVariableText(string text)
        {
            if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return Value.Num(d);
            return Value.Str(text ?? "");
        }

        private static bool IsIdentifier(string s)
        {
            if (s.Length == 0) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = i == 0 ? (char.IsLetter(c) || c == '_') : (char.IsLetterOrDigit(c) || c == '_');
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsFunctionName(string name)
        {
            switch (name)
            {
                case "int": case "取整": case "round": case "四舍五入": case "abs": case "绝对值":
                case "min": case "最小": case "max": case "最大": case "len": case "长度":
                case "sub": case "子串": case "replace": case "替换": case "contains": case "包含":
                case "concat": case "拼接": case "trim": case "去空格": case "upper": case "大写":
                case "lower": case "小写": case "regex": case "正则": case "now": case "现在":
                case "mm": case "毫米": case "px": case "像素": case "scale": case "比例尺":
                case "if": case "如果":
                    return true;
                default:
                    return false;
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }
            public bool AtEnd => _i >= _s.Length;
            public int Position => _i;
            public string RestSnippet() => _s.Substring(_i, Math.Min(20, _s.Length - _i));

            public void SkipSpaces() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            private bool Eat(string token)
            {
                SkipSpaces();
                if (_i + token.Length > _s.Length) return false;
                if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0) return false;
                _i += token.Length;
                return true;
            }

            /// <summary>当前位置（已消费完一个值）是否紧跟独立赋值等号（排除 ==）</summary>
            private bool IsAssignHere()
            {
                SkipSpaces();
                if (_i >= _s.Length || _s[_i] != '=') return false;
                if (_i + 1 < _s.Length && _s[_i + 1] == '=') return false;
                return true;
            }

            private bool Peek(string token)
            {
                SkipSpaces();
                return _i + token.Length <= _s.Length
                    && string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;
            }

            public Value ParseOr()
            {
                var left = ParseAnd();
                while (true)
                {
                    if (Eat("||") || Eat("或者")) { var r = ParseAnd(); left = Value.Num((left.AsBool() || r.AsBool()) ? 1 : 0); }
                    else return left;
                }
            }

            private Value ParseAnd()
            {
                var left = ParseCompare();
                while (true)
                {
                    if (Eat("&&") || Eat("并且")) { var r = ParseCompare(); left = Value.Num((left.AsBool() && r.AsBool()) ? 1 : 0); }
                    else return left;
                }
            }

            private Value ParseCompare()
            {
                var left = ParseAdd();
                while (true)
                {
                    string op = null;
                    foreach (string cand in new[] { "==", "!=", "<=", ">=", "<", ">" })
                        if (Peek(cand)) { op = cand; break; }
                    if (op == null) return left;
                    Eat(op);
                    var right = ParseAdd();
                    left = Value.Num(CompareValues(left, right, op) ? 1 : 0);
                }
            }

            private static bool CompareValues(Value a, Value b, string op)
            {
                if (a.TryNumber(out double x) && b.TryNumber(out double y))
                {
                    switch (op)
                    {
                        case "==": return Math.Abs(x - y) < 1e-9;
                        case "!=": return Math.Abs(x - y) >= 1e-9;
                        case "<": return x < y;
                        case "<=": return x <= y;
                        case ">": return x > y;
                        case ">=": return x >= y;
                    }
                }
                string sa = a.AsText(), sb = b.AsText();
                int c = string.Compare(sa, sb, StringComparison.Ordinal);
                switch (op)
                {
                    case "==": return c == 0;
                    case "!=": return c != 0;
                    case "<": return c < 0;
                    case "<=": return c <= 0;
                    case ">": return c > 0;
                    case ">=": return c >= 0;
                }
                return false;
            }

            private Value ParseAdd()
            {
                var left = ParseMul();
                while (true)
                {
                    if (Eat("+"))
                    {
                        var r = ParseMul();
                        // 两边都是数字才做加法，否则拼接（"订单 " + {编号}）
                        if (left.IsNumber && r.IsNumber) left = Value.Num(left.Number + r.Number);
                        else left = Value.Str(left.AsText() + r.AsText());
                    }
                    else if (Eat("-"))
                    {
                        var r = ParseMul();
                        left = Value.Num(ToNum(left, "-") - ToNum(r, "-"));
                    }
                    else return left;
                }
            }

            private Value ParseMul()
            {
                var left = ParseUnary();
                while (true)
                {
                    if (Eat("*")) { var r = ParseUnary(); left = Value.Num(ToNum(left, "*") * ToNum(r, "*")); }
                    else if (Eat("/"))
                    {
                        var r = ParseUnary();
                        double d = ToNum(r, "/");
                        if (Math.Abs(d) < double.Epsilon) throw new ExpressionException("除数为 0");
                        left = Value.Num(ToNum(left, "/") / d);
                    }
                    else if (Eat("%"))
                    {
                        var r = ParseUnary();
                        double d = ToNum(r, "%");
                        if (Math.Abs(d) < double.Epsilon) throw new ExpressionException("取模的除数为 0");
                        left = Value.Num(ToNum(left, "%") % d);
                    }
                    else return left;
                }
            }

            private static double ToNum(Value v, string op)
            {
                if (v.TryNumber(out double d)) return d;
                throw new ExpressionException(string.Format("运算符 {0} 需要数字，实际是 \"{1}\"", op, v.AsText()));
            }

            private Value ParseUnary()
            {
                if (Eat("!")) return Value.Num(ParseUnary().AsBool() ? 0 : 1);
                if (Eat("-")) return Value.Num(-ToNum(ParseUnary(), "-"));
                return ParsePrimary();
            }

            private Value ParsePrimary()
            {
                SkipSpaces();
                if (AtEnd) throw new ExpressionException("表达式在这里就结束了（少了个值）");
                char c = _s[_i];

                if (c == '(')
                {
                    _i++;
                    var v = ParseOr();
                    if (!Eat(")")) throw new ExpressionException("括号没有配对");
                    return v;
                }

                if (c == '{')
                {
                    int close = _s.IndexOf('}', _i + 1);
                    if (close < 0) throw new ExpressionException("变量引用缺少右花括号 }");
                    string name = _s.Substring(_i + 1, close - _i - 1).Trim();
                    _i = close + 1;
                    if (name.Length == 0) throw new ExpressionException("变量名为空");
                    // 赋值目标：{n}=0 / {n}={n}+1（函数参数里也允许，如 如果({n}>10,{n}=0,...)）
                    if (IsAssignHere())
                    {
                        Eat("=");
                        var right = ParseOr();
                        AutomationContext.SetVariable(name, right.AsText());
                        return right;
                    }
                    if (!AutomationContext.TryGetVariable(name, out string val))
                        throw new ExpressionException(string.Format("变量 {0} 还没有值（这一轮还没有节点写过它）", name));
                    return FromVariableText(val);
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    var sb = new StringBuilder();
                    _i++;
                    while (_i < _s.Length && _s[_i] != quote)
                    {
                        if (_s[_i] == '\\' && _i + 1 < _s.Length)
                        {
                            _i++;
                            sb.Append(_s[_i] == 'n' ? '\n' : _s[_i] == 't' ? '\t' : _s[_i]);
                        }
                        else sb.Append(_s[_i]);
                        _i++;
                    }
                    if (_i >= _s.Length) throw new ExpressionException("字符串没有结束的引号");
                    _i++;
                    return Value.Str(sb.ToString());
                }

                if (char.IsDigit(c) || c == '.')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    string num = _s.Substring(start, _i - start);
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        throw new ExpressionException("数字写法不对: " + num);
                    return Value.Num(d);
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                    string name = _s.Substring(start, _i - start);

                    if (string.Equals(name, "true", StringComparison.OrdinalIgnoreCase) || name == "是") return Value.Num(1);
                    if (string.Equals(name, "false", StringComparison.OrdinalIgnoreCase) || name == "否") return Value.Num(0);

                    // 赋值目标：裸标识符 n=0 / n=n+1（在函数参数里也允许，如 如果({n}>10,n=0,n=n+1)）
                    if (IsAssignHere())
                    {
                        Eat("=");
                        var right = ParseOr();
                        AutomationContext.SetVariable(name, right.AsText());
                        return right;
                    }

                    // 函数参数：**只切文本不求值**（惰性）。这样"如果(条件, A, B)"只执行选中的分支，
                    // 分支里的赋值（{n}=0 / {n}={n}+1）不会两边都跑。
                    var argTexts = new List<string>();
                    if (Peek("("))
                    {
                        Eat("(");
                        SkipSpaces();
                        if (!Peek(")"))
                        {
                            argTexts.Add(ReadArgumentText());
                            while (Eat(",")) argTexts.Add(ReadArgumentText());
                        }
                        if (!Eat(")")) throw new ExpressionException("函数 " + name + " 的括号没有配对");
                    }
                    else
                    {
                        // 无括号的裸标识符：优先当变量读（支持 "n=n+1" 这类赋值里引用上一句赋的值）；
                        // 不是变量、但名字是已知函数 → 按函数调用报参数错误；都不是 → 提示变量未定义。
                        if (AutomationContext.TryGetVariable(name, out string varVal))
                            return FromVariableText(varVal);
                        if (!IsFunctionName(name.ToLowerInvariant()))
                            throw new ExpressionException(
                                string.Format("变量 {0} 还没有值（这一轮还没有节点写过它；要先赋值才能引用）", name));
                    }
                    return CallFunction(name, argTexts);
                }

                throw new ExpressionException(string.Format("看不懂的字符 '{0}'", c));
            }

            /// <summary>读取一个函数参数（顶层逗号切分，跟踪括号/引号深度，**不解析不求值**）</summary>
            private string ReadArgumentText()
            {
                SkipSpaces();
                int start = _i;
                int depth = 0;
                char quote = '\0';
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (quote != '\0')
                    {
                        if (c == '\\' && _i + 1 < _s.Length) { _i += 2; continue; }
                        if (c == quote) quote = '\0';
                        _i++;
                        continue;
                    }
                    if (c == '"' || c == '\'') { quote = c; _i++; continue; }
                    if (c == '(' || c == '{') { depth++; _i++; continue; }
                    if (c == ')' || c == '}') { if (depth == 0) break; depth--; _i++; continue; }
                    if (c == ',' && depth == 0) break;
                    _i++;
                }
                return _s.Substring(start, _i - start).Trim();
            }

            private static Value CallFunction(string rawName, List<string> argTexts)
            {
                string name = rawName.ToLowerInvariant();
                // 惰性求值：按需把参数文本解析成值（"如果"只解析选中的分支，其余函数全部解析）
                var vals = new List<Value>();
                Value A(int i) { while (vals.Count <= i) vals.Add(new Parser(argTexts[i]).ParseOr()); return vals[i]; }
                double N(int i) => A(i).TryNumber(out double d) ? d
                    : throw new ExpressionException(string.Format("{0} 的第 {1} 个参数需要数字", rawName, i + 1));
                string S(int i) => A(i).AsText();
                void Need(int n, int max = 0) { if (argTexts.Count < n || (max > 0 && argTexts.Count > max))
                    throw new ExpressionException(string.Format("{0} 需要 {1}{2} 个参数，实际 {3} 个",
                        rawName, n, max == 0 ? "" : "~" + max, argTexts.Count)); }

                switch (name)
                {
                    case "int": case "取整": Need(1, 1); return Value.Num(Math.Truncate(N(0)));
                    case "round": case "四舍五入": Need(1, 2);
                        return Value.Num(Math.Round(N(0), argTexts.Count > 1 ? (int)N(1) : 0, MidpointRounding.AwayFromZero));
                    case "abs": case "绝对值": Need(1, 1); return Value.Num(Math.Abs(N(0)));
                    case "min": case "最小": Need(2); { double m = N(0); for (int i = 1; i < argTexts.Count; i++) m = Math.Min(m, N(i)); return Value.Num(m); }
                    case "max": case "最大": Need(2); { double m = N(0); for (int i = 1; i < argTexts.Count; i++) m = Math.Max(m, N(i)); return Value.Num(m); }
                    case "len": case "长度": Need(1, 1); return Value.Num(S(0).Length);
                    case "sub": case "子串": Need(2, 3);
                        {
                            string s = S(0);
                            int start = (int)N(1);
                            if (start < 0) start = 0;
                            if (start > s.Length) return Value.Str("");
                            int len = argTexts.Count > 2 ? (int)N(2) : s.Length - start;
                            len = Math.Max(0, Math.Min(len, s.Length - start));
                            return Value.Str(s.Substring(start, len));
                        }
                    case "replace": case "替换": Need(3, 3); return Value.Str(S(0).Replace(S(1), S(2)));
                    case "contains": case "包含": Need(2, 2);
                        return Value.Num(S(0).Contains(S(1), StringComparison.Ordinal) ? 1 : 0);
                    case "concat": case "拼接": Need(1); { var sb = new StringBuilder(); foreach (var t in argTexts) sb.Append(new Parser(t).ParseOr().AsText()); return Value.Str(sb.ToString()); }
                    case "trim": case "去空格": Need(1, 1); return Value.Str(S(0).Trim());
                    case "upper": case "大写": Need(1, 1); return Value.Str(S(0).ToUpperInvariant());
                    case "lower": case "小写": Need(1, 1); return Value.Str(S(0).ToLowerInvariant());
                    case "regex": case "正则": Need(2, 3);
                        {
                            var m = Regex.Match(S(0), S(1));
                            if (!m.Success) return Value.Str("");
                            int g = argTexts.Count > 2 ? (int)N(2) : 0;
                            if (g < 0 || g >= m.Groups.Count) return Value.Str("");
                            return Value.Str(m.Groups[g].Value);
                        }
                    case "now": case "现在": Need(0, 1); return Value.Str(DateTime.Now.ToString(argTexts.Count > 0 ? S(0) : "yyyy-MM-dd HH:mm:ss"));
                    case "mm": case "毫米": Need(1, 1);
                        {
                            double mm = AutomationContext.PixelToMm(N(0));
                            if (double.IsNaN(mm)) throw new ExpressionException("还没标定比例尺：请先用“标定”节点设定 1 像素 = 多少毫米");
                            return Value.Num(mm);
                        }
                    case "px": case "像素": Need(1, 1);
                        {
                            if (AutomationContext.MmPerPixel <= 0)
                                throw new ExpressionException("还没标定比例尺：请先用“标定”节点设定 1 像素 = 多少毫米");
                            return Value.Num(N(0) / AutomationContext.MmPerPixel);
                        }
                    case "scale": case "比例尺":
                        Need(0, 1);
                        if (AutomationContext.MmPerPixel <= 0) throw new ExpressionException("还没标定比例尺");
                        return Value.Num(AutomationContext.MmPerPixel);
                    // 惰性"如果"：只求选中的分支（分支里可以写赋值 {n}=0 / {n}={n}+1，不会两边都执行）
                    case "if": case "如果": Need(3, 3);
                        return A(0).AsBool()
                            ? new Parser(argTexts[1]).ParseOr()
                            : new Parser(argTexts[2]).ParseOr();
                    default:
                        throw new ExpressionException(string.Format(
                            "不认识的函数 {0}（可用：取整/绝对值/四舍五入/最小/最大/长度/子串/替换/包含/拼接/去空格/大写/小写/正则/现在/如果）", rawName));
                }
            }
        }
    }
}
