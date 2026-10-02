using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionToolDemo.Vision.Tasks;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>自动化节点类型</summary>
    public enum AutoNodeKind
    {
        Start,      // 入口
        Capture,    // 屏幕截图
        Match,      // 模板匹配（用已导入的模板）
        Condition,  // 条件：上次匹配是否命中
        Click,      // 鼠标点击
        Key,        // 键盘输入
        Wait,       // 等待
        Loop,       // 循环（回到指定节点）
        Popup,      // 弹窗提示（可确认/取消）
        SetVar,     // 全局变量（记录信息）
        Ocr,        // 字符识别（识别到的文字进全局变量）
        VisionOp,   // 通用视觉算子（节点自己挑一个，覆盖其余 90+ 个算子）
        End,        // 结束
        // ↓ 新增种类一律追加在**末尾**：Kind 在文件里按整数保存，插到中间会让老图的节点全部错位
        Command,        // 命令行（跑外部程序，拿退出码与输出）
        Expression,     // 表达式（算一个值写进变量）
        WaitCondition,  // 等待条件（轮询到满足/超时）
        Window,         // 窗口操作（激活/关闭/是否存在）
        Rule,           // 规则判定（一条规则 → 通过/不通过 + 原因）
        Aggregate,      // 结果聚合（本轮总判定 OK/NG + 明细）
        Calibrate,      // 标定（像素 → 毫米的比例尺）
        Http,           // HTTP 请求（上报/取参数）
        Roi,            // 区域裁剪ROI（把当前图裁到指定区域并替换，后续节点只在该区域检测）
        BreakLoop,      // 结束循环（循环体内提前跳出：由条件节点的分支连进来即可终止当前循环）
        Browser,        // 浏览器操作（识别/激活/刷新/后退/前进/滚动/打开网址）
        BrowserElement, // 浏览器元素（识别网页按钮/输入框：点击/输入/读文本）
    }

    /// <summary>一个自动化节点</summary>
    public sealed class AutoNode
    {
        public int Id;
        public AutoNodeKind Kind;
        public float X, Y;

        /// <summary>主出口：下一个节点 id；-1 = 结束</summary>
        public int NextId = -1;

        /// <summary>副出口：条件节点的“否”分支 / 循环的“回到”目标；-1 = 未连</summary>
        public int AltNextId = -1;

        /// <summary>与算子 ParamDescriptions 对齐的参数值；合成节点（开始/条件/结束）不使用</summary>
        public int[] Params = [];

        public bool Enabled = true;

        /// <summary>仅"视觉算子"节点使用：该节点挑的算子名（如 "二值化"）</summary>
        public string OpName = "";

        public AutoNode Clone() => new()
        {
            Id = Id, Kind = Kind, X = X, Y = Y,
            NextId = NextId, AltNextId = AltNextId,
            Params = (int[])Params.Clone(), Enabled = Enabled, OpName = OpName,
        };
    }

    /// <summary>节点类型的元信息（显示名、对应算子、参数定义）</summary>
    public static class AutoNodeInfo
    {
        public static string Title(AutoNodeKind k) => k switch
        {
            AutoNodeKind.Start => "开始",
            AutoNodeKind.Capture => "屏幕截图",
            AutoNodeKind.Match => "模板匹配",
            AutoNodeKind.Condition => "条件：是否命中",
            AutoNodeKind.Click => "鼠标点击",
            AutoNodeKind.Key => "键盘输入",
            AutoNodeKind.Wait => "等待延时",
            AutoNodeKind.Loop => "循环",
            AutoNodeKind.Popup => "弹窗提示",
            AutoNodeKind.SetVar => "全局变量",
            AutoNodeKind.Ocr => "字符识别OCR",
            AutoNodeKind.VisionOp => "视觉算子",
            AutoNodeKind.Command => "命令行",
            AutoNodeKind.Expression => "表达式",
            AutoNodeKind.WaitCondition => "等待条件",
            AutoNodeKind.Window => "窗口操作",
            AutoNodeKind.Rule => "规则判定",
            AutoNodeKind.Aggregate => "结果聚合",
            AutoNodeKind.Calibrate => "标定",
            AutoNodeKind.Http => "HTTP请求",
            AutoNodeKind.Roi => "区域裁剪ROI",
            AutoNodeKind.BreakLoop => "结束循环",
            AutoNodeKind.Browser => "浏览器操作",
            AutoNodeKind.BrowserElement => "浏览器元素",
            _ => "结束",
        };

        /// <summary>对应的视觉算子名；合成节点返回 null</summary>
        public static string TaskName(AutoNodeKind k) => k switch
        {
            AutoNodeKind.Capture => "屏幕截图",
            AutoNodeKind.Match => "模板匹配",
            AutoNodeKind.Click => "鼠标点击",
            AutoNodeKind.Key => "键盘输入",
            AutoNodeKind.Wait => "等待延时",
            AutoNodeKind.Popup => "弹窗提示",
            AutoNodeKind.SetVar => "全局变量",
            AutoNodeKind.Ocr => "字符识别OCR",
            AutoNodeKind.Command => "命令行",
            AutoNodeKind.Expression => "表达式",
            AutoNodeKind.WaitCondition => "等待条件",
            AutoNodeKind.Window => "窗口操作",
            AutoNodeKind.Rule => "规则判定",
            AutoNodeKind.Aggregate => "结果聚合",
            AutoNodeKind.Calibrate => "标定",
            AutoNodeKind.Http => "HTTP请求",
            AutoNodeKind.Roi => "区域裁剪ROI",
            AutoNodeKind.Browser => "浏览器操作",
            AutoNodeKind.BrowserElement => "浏览器元素",
            _ => null,      // VisionOp 的算子由节点自带的 OpName 决定
        };

        /// <summary>
        /// 该种类节点用到的字符串槽（顺序即槽号）：名字 + 提示 + 是否多行。
        /// 节点属性面板按它渲染输入框；运行时运行器按同样的槽号注入，两边不会错位。
        /// 返回空数组表示该种类用自己专门的一栏（键盘输入/弹窗/全局变量…）。
        /// </summary>
        public static (string Name, string Tip, bool MultiLine)[] StringSlots(AutoNodeKind k) => k switch
        {
            AutoNodeKind.Command =>
            [
                ("命令 / 程序", "如 python、notepad、C:\\tools\\x.exe。支持 {变量}", false),
                ("参数", "命令行参数，如 -c \"print(1)\"。支持 {变量}", false),
                ("工作目录", "留空 = 程序当前目录。支持 {变量}", false),
            ],
            AutoNodeKind.Expression =>
            [
                ("表达式", "如 {中心X} + 20、拼接(\"订单 \", {订单号})。支持四则/比较/逻辑与常用函数", true),
                ("结果变量名", "算出来的值写进这个变量（留空只记日志）", false),
            ],
            AutoNodeKind.WaitCondition =>
            [
                ("变量名", "条件=变量比较时用；可填 {变量} 间接指定", false),
                ("比较值", "条件=变量比较时用；支持 {变量}", false),
                ("窗口标题", "条件=窗口出现时用；普通文本=包含，/正则/=正则", false),
            ],
            AutoNodeKind.Window =>
            [
                ("窗口标题", "普通文本=标题包含；/正则/=正则，如 /^记事本/", false),
                ("结果变量名", "动作=是否存在时，把 1/0 写进这个变量", false),
            ],
            AutoNodeKind.Rule =>
            [
                ("变量名", "要判定的值（通常是前面节点存进变量的测量/识别结果）", false),
                ("期望值 / 下限", "数值范围的下限，或等于/包含的期望值", false),
                ("上限", "只用于数值范围；留空表示不设上限", false),
                ("结果变量名（可选）", "通过写 OK，不通过写 NG；留空则只记进本轮明细", false),
            ],
            AutoNodeKind.Aggregate =>
            [
                ("总判定变量名", "留空默认「总判定」，值为 OK / NG", false),
                ("明细变量名", "留空默认「判定明细」，每条规则一行", false),
            ],
            AutoNodeKind.Calibrate =>
            [
                ("像素长度 / mm每像素", "方式=由长度换算：填量到的像素数（也可填存了该值的变量名）；方式=直接填：填 mm/像素", false),
                ("写入变量名", "留空默认「比例尺」", false),
            ],
            AutoNodeKind.Http =>
            [
                ("URL", "http:// 或 https:// 开头，支持 {变量}", false),
                ("正文", "JSON 或文本，支持 {变量} 插值（GET 不用填）", true),
                ("结果变量名", "响应正文写进这个变量", false),
            ],
            AutoNodeKind.Roi =>
            [
                ("区域变量名", "区域来源=变量 时用：变量值形如 10,20,300,200（X,Y,宽,高，逗号或空格分隔）；支持 {变量} 间接引用", false),
            ],
            _ => [],
        };

        /// <summary>
        /// 某个节点实际会用哪个算子：通用视觉算子节点用它自己挑的那个，其余按种类固定。
        /// 运行器与参数面板都走这里，保证"面板里看到的参数"和"运行时用的算子"永远一致。
        /// </summary>
        public static string TaskName(AutoNode n)
        {
            if (n == null) return null;
            if (n.Kind != AutoNodeKind.VisionOp) return TaskName(n.Kind);
            return string.IsNullOrWhiteSpace(n.OpName) ? null : n.OpName;
        }

        /// <summary>节点参数定义（通用视觉算子按节点自己选的算子取）</summary>
        public static TaskParamDesc[] Params(AutoNode n)
        {
            if (n == null) return [];
            string task = TaskName(n);
            if (task != null)
            {
                var t = VisionTaskRegistry.GetTask(task);
                if (t != null) return t.ParamDescriptions;
            }
            return Params(n.Kind);
        }

        public static int[] DefaultParams(AutoNode n)
            => Params(n).Select(d => d.DefaultValue).ToArray();

        /// <summary>节点参数定义（合成节点自带极简定义）</summary>
        public static TaskParamDesc[] Params(AutoNodeKind k)
        {
            string task = TaskName(k);
            if (task != null)
            {
                var t = VisionTaskRegistry.GetTask(task);
                if (t != null) return t.ParamDescriptions;
            }
            return k switch
            {
                AutoNodeKind.Loop =>
                [
                    new TaskParamDesc { ParamName = "循环次数", Min = 1, Max = 100000, DefaultValue = 1,
                        DisplayFormat = "次数:{0}", Group = "循环",
                        Tip = "回到“循环目标”节点反复执行的次数。达到次数后走主出口继续。\n" +
                        "循环体内的节点可以直接用 **{循环序号}**（当前第几轮）和 **{循环总次数}**，\n" +
                        "例如截图/弹窗/条件判断里写 第{循环序号}/{循环总次数} 轮。\n" +
                        "下面选了“次数来源=变量”时，以变量里的数字为准。" },
                    new TaskParamDesc { ParamName = "次数来源 0固定1全局变量", Min = 0, Max = 1, DefaultValue = 0,
                        DisplayFormat = "次数来源:{0}", Group = "循环",
                        Tip = "1 = 循环次数取自**全局变量**：变量名填在【节点属性 → 输入内容】的「文本」框。\n" +
                        "变量值必须是正整数，否则这一轮会明确报错中止（不会瞎循环）。\n" +
                        "**每轮都会重新读这个变量**：循环体里把次数递减（如表达式 n=n-1）就能实现\n" +
                        "“递减到 0 退出”的直到式循环。" },
                ],
                AutoNodeKind.Condition =>
                [
                    new TaskParamDesc { ParamName = "判断依据 0上次匹配1全局变量", Min = 0, Max = 1, DefaultValue = 0,
                        DisplayFormat = "依据:{0}", Group = "条件",
                        Tip = "0 = 沿用原来的判断（上一次模板匹配是否命中）；\n" +
                        "1 = 判断**全局变量**：变量名填在【节点属性 → 输入内容】的「文本」框，\n" +
                        "    比较值填在「按键 / 组合键」框（支持 {变量} 插值）。" },
                    new TaskParamDesc { ParamName = "比较 0等于1不等于2包含3不包含4大于5小于6非空", Min = 0, Max = 6, DefaultValue = 0,
                        DisplayFormat = "比较:{0}", Group = "条件",
                        Tip = "两边都能当数字时按数值比（4 大于 / 5 小于）；否则按文本比。\n" +
                        "6 非空：只看变量有没有值，不用填比较值。" },
                ],
                _ => [],
            };
        }

        public static int[] DefaultParams(AutoNodeKind k)
            => Params(k).Select(d => d.DefaultValue).ToArray();
    }

    /// <summary>自动化节点图</summary>
    public sealed class AutomationGraph
    {
        public List<AutoNode> Nodes { get; } = new();
        private int _nextId = 1;

        /// <summary>
        /// 按节点 id 保存的模板图 —— "模板匹配"节点各自一份，互不干扰。
        ///
        /// 为什么不放进 AutoNode：Mat 是二进制大对象，混进节点 JSON 既难读也难比对
        /// （节点参数是 int[]，模板走 VisionHelper 的 base64 PNG，单独存在同一个文件里）。
        /// 没有单独导入的匹配节点会退回页面顶部的"默认模板"（AutomationContext.Template）。
        /// </summary>
        public Dictionary<int, Mat> NodeTemplates { get; } = new();

        public AutoNode Get(int id) => Nodes.FirstOrDefault(n => n.Id == id);

        /// <summary>
        /// 按节点 id 保存的输入文本 / 按键（"键盘输入"节点各自一份）。
        ///
        /// 与模板图同理：算子参数体系只收 int[]，字符串塞不进去；用全局槽的话
        /// 同一张图里多个"键盘输入"节点只能共用 4 个槽，既不够用也看不出谁是谁。
        /// 节点自带内容优先，为空时才退回全局槽（兼容旧图与自动化面板）。
        /// </summary>
        public Dictionary<int, string> NodeTexts { get; } = new();
        public Dictionary<int, string> NodeKeys { get; } = new();

        /// <summary>取该节点自己的输入文本（没有则空串）</summary>
        public string GetNodeText(int id) => NodeTexts.TryGetValue(id, out var s) ? s : "";

        /// <summary>取该节点自己的按键/组合键（没有则空串）</summary>
        public string GetNodeKey(int id) => NodeKeys.TryGetValue(id, out var s) ? s : "";

        public void SetNodeText(int id, string text)
        {
            if (string.IsNullOrEmpty(text)) NodeTexts.Remove(id);
            else NodeTexts[id] = text;
        }

        public void SetNodeKey(int id, string key)
        {
            if (string.IsNullOrEmpty(key)) NodeKeys.Remove(id);
            else NodeKeys[id] = key;
        }

        /// <summary>
        /// 第 2 个以上的字符串槽（键 = "节点id:槽号"）。
        /// 0 号与 1 号仍然用 NodeTexts / NodeKeys 存（老文件、老节点都按那两个键读写），
        /// 新增节点（命令行/表达式等）需要更多字符串时用这里，互不干扰。
        /// </summary>
        public Dictionary<string, string> NodeExtraStrings { get; } = new();

        /// <summary>取节点第 slot 个字符串槽（0=「文本」框，1=「按键」框）</summary>
        public string GetNodeString(int id, int slot)
        {
            if (slot == 0) return GetNodeText(id);
            if (slot == 1) return GetNodeKey(id);
            return NodeExtraStrings.TryGetValue(id + ":" + slot, out var s) ? s : "";
        }

        public void SetNodeString(int id, int slot, string value)
        {
            if (slot == 0) { SetNodeText(id, value); return; }
            if (slot == 1) { SetNodeKey(id, value); return; }
            string key = id + ":" + slot;
            if (string.IsNullOrEmpty(value)) NodeExtraStrings.Remove(key);
            else NodeExtraStrings[key] = value;
        }

        public void ClearNodeInputs()
        {
            NodeTexts.Clear();
            NodeKeys.Clear();
            NodeExtraStrings.Clear();
            NodeOps.Clear();
        }

        /// <summary>按节点 id 保存"通用视觉算子"节点挑的算子名</summary>
        public Dictionary<int, string> NodeOps { get; } = new();

        public string GetNodeOp(int id) => NodeOps.TryGetValue(id, out var s) ? s : "";

        public void SetNodeOp(int id, string opName)
        {
            if (string.IsNullOrEmpty(opName)) NodeOps.Remove(id);
            else NodeOps[id] = opName;
        }

        /// <summary>
        /// 用另一张图**替换**本图内容（节点、模板、输入内容、算子、槽位）。
        /// 用途：旧的 WinForms 编辑器是序列化/反序列化的权威实现，WPF 页面借它读写文件，
        /// 于是需要把图在两者之间整体搬运一次。
        /// </summary>
        public void ReplaceWith(AutomationGraph other)
        {
            if (other == null || ReferenceEquals(other, this)) return;
            Clear();
            foreach (var n in other.Nodes) Nodes.Add(n.Clone());
            foreach (var kv in other.NodeTemplates)
                if (kv.Value != null && !kv.Value.Empty()) NodeTemplates[kv.Key] = kv.Value.Clone();
            foreach (var kv in other.NodeTexts) NodeTexts[kv.Key] = kv.Value;
            foreach (var kv in other.NodeKeys) NodeKeys[kv.Key] = kv.Value;
            foreach (var kv in other.NodeOps) NodeOps[kv.Key] = kv.Value;
            foreach (var kv in other.NodeExtraStrings) NodeExtraStrings[kv.Key] = kv.Value;
            SetNextId(other.Nodes.Count == 0 ? 1 : other.Nodes.Max(n => n.Id) + 1);
        }

        /// <summary>取该节点自己的模板图（没有则 null）</summary>
        public Mat GetNodeTemplate(int id)
            => NodeTemplates.TryGetValue(id, out var m) ? m : null;

        /// <summary>设置模板图（接管所有权；传空/空图等于清除）</summary>
        public void SetNodeTemplate(int id, Mat tpl)
        {
            if (NodeTemplates.TryGetValue(id, out var old) && !ReferenceEquals(old, tpl))
                old?.Dispose();
            if (tpl == null || tpl.Empty())
            {
                NodeTemplates.Remove(id);
                tpl?.Dispose();
                return;
            }
            NodeTemplates[id] = tpl;
        }

        public void RemoveNodeTemplate(int id)
        {
            if (NodeTemplates.TryGetValue(id, out var old))
            {
                old?.Dispose();
                NodeTemplates.Remove(id);
            }
        }

        public void ClearNodeTemplates()
        {
            foreach (var m in NodeTemplates.Values) m?.Dispose();
            NodeTemplates.Clear();
        }

        /// <summary>该匹配节点最终会用哪张模板：节点自己的优先，否则用全局默认模板</summary>
        public Mat ResolveTemplate(int nodeId)
            => GetNodeTemplate(nodeId) ?? AutomationContext.Template;

        public AutoNode Add(AutoNodeKind kind, float x, float y)
        {
            var n = new AutoNode
            {
                Id = _nextId++,
                Kind = kind,
                X = x,
                Y = y,
                Params = AutoNodeInfo.DefaultParams(kind),   // VisionOp 先给空数组，选完算子再对齐
            };
            Nodes.Add(n);
            return n;
        }

        /// <summary>删除节点，并把所有指向它的连接清掉（不留悬空引用）</summary>
        public void Remove(int id)
        {
            RemoveNodeTemplate(id);      // 节点没了，它自己的模板图也要释放
            NodeTexts.Remove(id);
            NodeKeys.Remove(id);
            NodeOps.Remove(id);
            Nodes.RemoveAll(n => n.Id == id);
            foreach (var n in Nodes)
            {
                if (n.NextId == id) n.NextId = -1;
                if (n.AltNextId == id) n.AltNextId = -1;
            }
        }

        public void Clear()
        {
            Nodes.Clear();
            ClearNodeTemplates();
            ClearNodeInputs();
            _nextId = 1;
        }

        /// <summary>从文件装载后修正 id 分配器，避免新建节点与已有 id 冲突</summary>
        public void SetNextId(int next)
        {
            _nextId = Math.Max(1, next);
        }

        /// <summary>确保参数数组与当前参数定义等长（参数定义变过时补齐/截断）</summary>
        public void NormalizeParams()
        {
            foreach (var n in Nodes)
            {
                var defs = AutoNodeInfo.Params(n);      // 通用视觉算子按它自己选的算子取定义
                if (n.Params == null || n.Params.Length != defs.Length)
                {
                    var np = new int[defs.Length];
                    for (int i = 0; i < defs.Length; i++)
                        np[i] = (n.Params != null && i < n.Params.Length) ? n.Params[i] : defs[i].DefaultValue;
                    n.Params = np;
                }
            }
        }

        /// <summary>
        /// 校验图是否可运行。返回 null 表示可以运行，否则返回原因。
        /// 早失败比运行到一半才报错好 —— 用户能立刻知道是哪个节点没连。
        /// </summary>
        public string Validate()
        {
            // 只用**错误**拦运行：警告（悬空节点、画布上多余副出口之类）只是提醒，
            // 要是它们也拦，用户会莫名其妙"什么都跑不了"
            foreach (var issue in ValidateAll())
                if (issue.IsError) return issue.Message;
            return null;
        }

        /// <summary>
        /// 全部校验问题（错误 + 警告），界面用它在"校验结果"里逐条列出并定位到节点。
        /// 参考项目把校验结果做成结构化 issue 列表（level/code/message/node_id），
        /// 这里保持一致：只有 IsError 的问题会拦住运行，警告只是提醒。
        /// </summary>
        public List<GraphIssue> ValidateAll()
        {
            var issues = new List<GraphIssue>();

            var starts = Nodes.Where(n => n.Kind == AutoNodeKind.Start).ToList();
            if (starts.Count == 0)
                issues.Add(GraphIssue.Error(0, "图里没有“开始”节点：节点面板点右键或从节点库添加一个“开始”"));
            if (starts.Count > 1)
                issues.Add(GraphIssue.Error(starts[1].Id, "图里有多个“开始”节点，只允许一个"));
            if (Nodes.Count(n => n.Kind == AutoNodeKind.End) == 0)
                issues.Add(GraphIssue.Error(0, "图里没有“结束”节点"));

            foreach (var n in Nodes)
            {
                if (n.NextId >= 0 && Get(n.NextId) == null)
                    issues.Add(GraphIssue.Error(n.Id, string.Format("节点 #{0} 的主出口指向了不存在的节点", n.Id)));
                if (n.AltNextId >= 0 && Get(n.AltNextId) == null)
                    issues.Add(GraphIssue.Error(n.Id, string.Format("节点 #{0} 的副出口指向了不存在的节点", n.Id)));
            }

            if (starts.Count == 1)
            {
                if (starts[0].NextId < 0)
                    issues.Add(GraphIssue.Error(starts[0].Id, "“开始”节点还没有连接下一个节点"));
                else if (!ReachesEnd(out int visited))
                    issues.Add(GraphIssue.Warn(starts[0].Id,
                        string.Format("从“开始”出发走不到“结束”（能走到的节点 {0} 个），运行时会在中间停住", visited)));
                else if (visited < Nodes.Count)
                    issues.Add(GraphIssue.Warn(starts[0].Id,
                        string.Format("有 {0} 个节点从“开始”出发走不到（悬空节点），确认是有意保留", Nodes.Count - visited)));
            }

            // 只有条件/循环节点用得上副出口，别的地方填了容易误导
            foreach (var n in Nodes.Where(n => n.AltNextId >= 0))
            {
                if (n.Kind != AutoNodeKind.Condition && n.Kind != AutoNodeKind.Loop)
                    issues.Add(GraphIssue.Warn(n.Id,
                        string.Format("节点 #{0}（{1}）设了副出口，但只有“条件”和“循环”会走副出口", n.Id, AutoNodeInfo.Title(n.Kind))));
            }

            // "全局变量"节点引用了不存在的节点 → 跑到那里才发现太晚，提前说清
            foreach (var n in Nodes.Where(n => n.Kind == AutoNodeKind.SetVar))
            {
                if (n.Params == null || n.Params.Length < 2) continue;
                int refId = n.Params[1];
                if (refId != 0 && Get(refId) == null)
                    issues.Add(GraphIssue.Error(n.Id,
                        string.Format("“全局变量”节点 #{0} 要取节点 #{1} 的输出，但图里没有这个节点", n.Id, refId)));
            }

            // 模板匹配没有模板就等于白跑，提前拦住并指名道姓说是哪个节点
            foreach (var n in Nodes.Where(n => n.Kind == AutoNodeKind.Match))
            {
                if (ResolveTemplate(n.Id)?.Empty() != false)
                    issues.Add(GraphIssue.Error(n.Id,
                        "还没有模板图：选中该节点后在“实例属性”里点“导入模板图”，或先设一张默认模板"));
            }

            // 等待条件在"模板命中"模式下同样需要模板
            foreach (var n in Nodes.Where(n => n.Kind == AutoNodeKind.WaitCondition))
            {
                int cond = n.Params != null && n.Params.Length > 0 ? n.Params[0] : 1;
                if (cond == 1 && ResolveTemplate(n.Id)?.Empty() != false
                    && Nodes.All(x => x.Kind != AutoNodeKind.Match))
                    issues.Add(GraphIssue.Warn(n.Id,
                        "等待条件用“模板命中”但还没有模板图：选中后在“实例属性”里导入"));
            }

            // "区域裁剪ROI"选了变量来源却没填变量名 → 跑到那里才报错太晚，提前提醒
            foreach (var n in Nodes.Where(n => n.Kind == AutoNodeKind.Roi))
            {
                if (n.Params != null && n.Params.Length > 0 && n.Params[0] == 1
                    && string.IsNullOrWhiteSpace(GetNodeText(n.Id)))
                    issues.Add(GraphIssue.Warn(n.Id,
                        "“区域裁剪ROI”节点选了“区域来源=变量”，但还没填变量名（节点属性 → 输入内容）"));
            }

            // 引用了"取哪一项"但该节点看起来给不出这项 → 运行时会跳过，提前提醒
            foreach (var n in Nodes.Where(n => n.Kind == AutoNodeKind.SetVar))
            {
                if (n.Params == null || n.Params.Length < 3 || n.Params[1] == 0) continue;
                var src = Get(n.Params[1]);
                if (src == null) continue;
                int item = n.Params[2];
                bool plausible = src.Kind switch
                {
                    AutoNodeKind.Capture => item is 0 or 7,
                    AutoNodeKind.Ocr => item is 0 or 1,
                    AutoNodeKind.Click => item is 0 or 8 or 9 or 10,
                    _ => true,
                };
                if (!plausible)
                    issues.Add(GraphIssue.Warn(n.Id, string.Format(
                        "节点 #{0} 想取 \"{1}\" 的第 {2} 项（{3}），但 #{1} 是{4}节点，运行时多半取不到",
                        n.Id, n.Params[1], item, NodeOutput.ItemName(item), AutoNodeInfo.Title(src.Kind))));
            }

            return issues;
        }

        /// <summary>连通性检查：从开始出发能否到达结束（防呆，不阻断运行）</summary>
        public bool ReachesEnd(out int visitedCount)
        {
            var start = Nodes.FirstOrDefault(n => n.Kind == AutoNodeKind.Start);
            visitedCount = 0;
            if (start == null) return false;
            var seen = new HashSet<int>();
            var q = new Queue<int>();
            q.Enqueue(start.Id);
            while (q.Count > 0)
            {
                int id = q.Dequeue();
                if (!seen.Add(id)) continue;
                var n = Get(id);
                if (n == null) continue;
                visitedCount++;
                if (n.Kind == AutoNodeKind.End) return true;
                if (n.NextId >= 0) q.Enqueue(n.NextId);
                if (n.AltNextId >= 0) q.Enqueue(n.AltNextId);
            }
            return false;
        }
    }

    /// <summary>一次自动化运行的结果</summary>
    public sealed class AutomationRunResult
    {
        public bool Ok;
        public string Error = "";
        public int Steps;
        public int Actions;
        public bool ActuallyExecuted;

        /// <summary>本轮有几个动作算子"本该做却被跳过"（没目标/越界/内容空…）</summary>
        public int SkippedActions;

        /// <summary>本轮总耗时（毫秒）—— 界面统计卡片与运行记录都用它</summary>
        public long TotalMs;

        /// <summary>每个节点的耗时（键 = 节点 id，毫秒）；画布上直接标在节点卡片上</summary>
        public Dictionary<int, long> NodeTimes { get; } = new();

        /// <summary>本轮的第一个输入图（界面"原图"页签用；所有权归本对象，DisposeImages 会释放）</summary>
        public Mat InputImage;

        /// <summary>最后一个动作节点的输出（非拥有者，释放请用 DisposeImages）</summary>
        public Mat FinalImage;

        /// <summary>
        /// 每个动作节点产生的图像（键 = 节点 id），供界面"选中节点看当前处理结果"用。
        /// 一次运行内所有节点共用同一张输入图，输出都是新对象，所以这里能全部留下。
        /// 所有权归调用方：换下一轮或关闭界面时必须 DisposeImages()，否则每轮泄漏一份（主屏一张约 6MB）。
        /// </summary>
        public Dictionary<int, Mat> NodeImages { get; } = new();

        /// <summary>
        /// 每个节点的执行摘要（键 = 节点 id）。界面在"节点属性"里显示它，
        /// 这样选中匹配节点就能直接看到"目标中心 图像(x,y) 屏幕(x,y)"这类坐标信息。
        /// </summary>
        public Dictionary<int, string> NodeSummaries { get; } = new();

        /// <summary>
        /// 匹配节点算出的目标坐标（键 = 节点 id）：
        /// 图像坐标 + 屏幕坐标（本轮确实抓过屏时才有）+ 命中倍率。
        /// 界面用它显示一行"目标中心 图像(x,y) → 屏幕(x,y)" ——
        /// 这就是"鼠标会点哪里"，干跑时核对的就是它。
        /// </summary>
        public Dictionary<int, (float CX, float CY, int SX, int SY, bool HasScreen, int Scale)> NodeTargets { get; } = new();

        public List<string> Lines { get; } = new();

        /// <summary>
        /// 释放本次运行留下的全部图像（含 FinalImage 指向的那张）。
        /// 按引用去重：某个算子如果把输入原样返回（不完全不可能），
        /// 同一个 Mat 会出现在多个节点下，重复 Dispose 会抛。
        /// </summary>
        public void DisposeImages()
        {
            var seen = new HashSet<Mat>();
            foreach (var m in NodeImages.Values)
                if (m != null && seen.Add(m)) m.Dispose();
            NodeImages.Clear();
            NodeSummaries.Clear();
            NodeTargets.Clear();
            NodeTimes.Clear();
            if (InputImage != null && !seen.Contains(InputImage)) InputImage.Dispose();
            InputImage = null;
            FinalImage = null;
        }
    }

    /// <summary>
    /// 节点图执行器。
    ///
    /// 执行模型很朴素：从“开始”出发沿连线走，每个节点执行一次自己的动作。
    /// 唯一的分支是“条件”节点（按上次匹配是否命中走两条出口之一）；
    /// 循环用“循环”节点实现（回到指定节点，达到次数后走主出口）。
    ///
    /// 安全：最大步数是硬上限。节点图很容易连成环（这是循环的本意），
    /// 也容易因为连错而变成死循环 —— 没有上限的话程序会一直点鼠标停不下来。
    /// </summary>
    public static class AutomationRunner
    {
        /// <summary>
        /// 条件节点的比较：0等于 1不等于 2包含 3不包含 4大于 5小于 6非空。
        /// 两边都能解析成数字时按数值比（"9" &lt; "10" 该成立），否则按文本比。
        /// 变量不存在时只有"不等于/不包含/非空"这类否定判断才可能成立 —— 否则会误导用户。
        /// </summary>
        /// <summary>比较方式的名字（日志与界面提示共用）</summary>
        public static string CompareName(int mode) => mode switch
        {
            1 => "不等于", 2 => "包含", 3 => "不包含", 4 => "大于", 5 => "小于", 6 => "非空", _ => "等于",
        };

        public static bool Compare(bool have, string actual, string expect, int mode)
        {
            expect ??= "";
            if (mode == 6) return have && !string.IsNullOrEmpty(actual);   // 非空
            if (!have) return mode is 1 or 3;                             // 没有这个变量
            actual ??= "";
            if (mode is 4 or 5)
            {
                bool okA = double.TryParse(actual.Trim(), out double a);
                bool okB = double.TryParse(expect.Trim(), out double b);
                if (!okA || !okB) return false;                           // 不是数字就不比大小
                return mode == 4 ? a > b : a < b;
            }
            bool contains = actual.Contains(expect, StringComparison.Ordinal);
            return mode switch
            {
                0 => string.Equals(actual, expect, StringComparison.Ordinal),
                1 => !string.Equals(actual, expect, StringComparison.Ordinal),
                2 => contains,
                3 => !contains,
                _ => false,
            };
        }

        public static AutomationRunResult Run(AutomationGraph graph, int maxSteps, Action<string> log)
        {
            var result = new AutomationRunResult();
            if (graph == null) { result.Error = "节点图为空"; return result; }

            // 一轮运行开始：清空上一轮的全局变量 / 规则结果 / 动作计数 / 节点输出。
            // 变量是"本轮自动化过程中的信息"，跨轮残留会把上一轮的值误当成这一轮的结果；
            // UI 与 --run 入口各自调用过 BeginRound，这里再兜底一次（幂等）——
            // 任何宿主（反射/嵌入/测试）直接调 Run 也能得到干净的运行环境。
            AutomationContext.BeginRound();

            graph.NormalizeParams();
            string invalid = graph.Validate();
            if (invalid != null) { result.Error = invalid; return result; }

            var start = graph.Nodes.First(n => n.Kind == AutoNodeKind.Start);
            var loopCounters = new Dictionary<int, int>();
            // 循环栈：记录当前正在执行哪一层循环（嵌套时先进后出）。
            // "结束循环"节点从这里取"当前循环"，提前跳出时直接走它的主出口。
            var loopStack = new Stack<int>();
            var logs = result.Lines;

            void Emit(string s)
            {
                logs.Add(s);
                log?.Invoke(s);
            }
            Emit(string.Format("=== 开始执行节点图（{0} 个节点）{1} ===",
                graph.Nodes.Count,
                AutomationContext.DryRun ? "  ★干跑模式，不会真实操作" : "  ★真实执行★"));

            int cur = start.NextId;
            int steps = 0;
            bool lastMatchFound = false;
            Mat image = null;
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            // 截图节点的"隐藏本软件窗口"要覆盖整轮运行，而不是只覆盖那一次抓屏：
            // 只藏那一下的话，后面"鼠标点击"的屏幕坐标是照着"没有本窗口"的桌面算出来的，
            // 窗口一恢复就把目标挡住了 —— 点下去等于点自己。这里进入外层作用域，
            // 节点自己的抓屏作用域变成嵌套（ScreenCapture 按深度计数，嵌套时不会提前恢复）。
            IDisposable hideScope = null;

            try
            {
                while (cur >= 0)
                {
                    if (++steps > maxSteps)
                    {
                        result.Error = string.Format(
                            "执行步数超过上限 {0}，已中止（节点图里可能有连错形成的死循环）", maxSteps);
                        Emit("!! " + result.Error);
                        break;
                    }

                    var node = graph.Get(cur);
                    if (node == null)
                    {
                        result.Error = string.Format("节点 {0} 不存在（连接已失效）", cur);
                        Emit("!! " + result.Error);
                        break;
                    }
                    if (!node.Enabled)
                    {
                        Emit(string.Format("  [跳过] {0} (id {1})", AutoNodeInfo.Title(node.Kind), node.Id));
                        cur = node.NextId;
                        continue;
                    }

                    // 用"按节点"的版本：通用视觉算子节点的算子名存在节点上，
                    // 按种类取会得到 null（接着 GetTask(null) 会抛 ArgumentNullException）
                    string task = AutoNodeInfo.TaskName(node);
                    try
                    {
                        if (node.Kind == AutoNodeKind.Start || node.Kind == AutoNodeKind.End)
                        {
                            // 合成节点没有动作
                        }
                        else if (node.Kind == AutoNodeKind.Condition)
                        {
                            bool taken;
                            string why;
                            if (node.Params.Length > 0 && node.Params[0] == 1)
                            {
                                // 判断全局变量
                                int cmp = node.Params.Length > 1 ? Math.Clamp(node.Params[1], 0, 6) : 0;
                                string varName = graph.GetNodeText(node.Id).Trim();
                                string expect = AutomationContext.ExpandVariables(graph.GetNodeKey(node.Id) ?? "");
                                bool have = AutomationContext.TryResolveVariable(varName, out string actual);
                                taken = Compare(have, actual, expect, cmp);
                                why = string.Format("变量 {0} = \"{1}\" {2} \"{3}\"",
                                    varName.Length == 0 ? "(未填名字)" : varName,
                                    have ? actual : "(没有这个变量)",
                                    CompareName(cmp), expect);
                            }
                            else
                            {
                                taken = lastMatchFound;
                                why = string.Format("上次匹配{0}", lastMatchFound ? "命中" : "未命中");
                            }
                            Emit(string.Format("  [条件] {0} -> 走“{1}”分支", why, taken ? "是" : "否"));
                            cur = taken ? node.NextId : node.AltNextId;
                            continue;
                        }
                        else if (node.Kind == AutoNodeKind.BreakLoop)
                        {
                            // 提前结束当前循环：条件是"继续循环"走回循环目标、不满足就走到本节点，
                            // 从栈顶取出当前循环节点，直接跳它的主出口（等于 while 条件循环的 break）
                            if (loopStack.Count == 0)
                            {
                                result.Error = string.Format(
                                    "节点 #{0}（结束循环）当前不在任何循环里——它必须放在循环目标之后的循环体内", node.Id);
                                Emit("!! " + result.Error);
                                break;
                            }
                            int loopId = loopStack.Pop();
                            loopCounters.Remove(loopId);   // 计数清掉：下次再进该循环会重新计数
                            Emit(string.Format("  [结束循环] 提前结束循环（循环节点 #{0}），走主出口继续", loopId));
                            cur = graph.Get(loopId)?.NextId ?? -1;
                            if (cur < 0)
                            {
                                result.Error = string.Format(
                                    "结束循环后找不到循环节点 #{0} 的主出口（请把主出口连到后续节点）", loopId);
                                Emit("!! " + result.Error);
                                break;
                            }
                            continue;
                        }
                        else if (node.Kind == AutoNodeKind.Loop)
                        {
                            loopStack.Push(node.Id);   // 进入本轮循环守卫：结束循环节点从这里找"当前循环"
                            int limit = node.Params.Length > 0 ? Math.Max(1, node.Params[0]) : 1;
                            // 次数来源 = 全局变量：**每轮重新读**，循环体可以中途改次数
                            // （递减到 0 就退出，配合"表达式 n=n-1"做"直到条件满足"式循环）
                            bool firstLoop = !loopCounters.ContainsKey(node.Id);
                            string countVarName = node.Params.Length > 1 && node.Params[1] == 1
                                ? graph.GetNodeText(node.Id).Trim()
                                : "";
                            if (node.Params.Length > 1 && node.Params[1] == 1 && countVarName.Length == 0)
                            {
                                result.Error = string.Format(
                                    "循环节点 #{0} 选了“次数来源=全局变量”，但没有填变量名（填在【节点属性 → 输入内容】的「文本」框）", node.Id);
                                Emit("!! " + result.Error);
                                break;
                            }
                            if (countVarName.Length > 0)
                            {
                                bool haveCount = AutomationContext.TryResolveVariable(countVarName, out string countText);
                                int fromVar = 0;
                                bool parseOk = haveCount && int.TryParse((countText ?? "").Trim(), out fromVar);
                                if (!haveCount || !parseOk)
                                {
                                    // 变量缺失或不是数字：无法确定本轮次数，必须报错（不能静默当成一次）
                                    result.Error = string.Format(
                                        "循环节点 #{0} 的次数取自变量 {1}，但它{2}（值必须是正整数）",
                                        node.Id, countVarName,
                                        string.IsNullOrWhiteSpace(countText) ? "这一轮还没有值" : "的值是 \"" + countText + "\"");
                                    Emit("!! " + result.Error);
                                    break;
                                }
                                if (fromVar < 1)
                                {
                                    // 次数降到 0 或以下：自然结束本轮循环走主出口，而不是报错。
                                    // 配合循环体里 "n=n-1" 做"直到条件满足"式循环：减到 0 即结束，
                                    // 或由"结束循环"节点提前跳出 —— 两种终止方式都不该变成运行错误。
                                    Emit(string.Format(
                                        "  [循环] 次数变量 {0} = {1} ≤ 0，循环结束走主出口", countVarName, fromVar));
                                    loopStack.Pop();
                                    loopCounters.Remove(node.Id);
                                    cur = node.NextId;
                                    continue;
                                }
                                limit = fromVar;
                                if (firstLoop)
                                    Emit(string.Format("  [循环] 次数取自变量 {0} = {1}（每轮重读，循环体可中途修改次数）",
                                        countVarName, limit));
                            }
                            // done = 已完成迭代数；"次数 3" 应让循环体恰好执行 3 次。
                            // 修正：进入先判 done < limit，达标前的每轮才回跳并递增（此前先 ++ 再判，
                            // done=limit 时已回跳 limit-1 次，循环体少跑一次——"次数 3 只跑 2 次"的 bug）
                            int done = loopCounters.TryGetValue(node.Id, out int d) ? d : 0;
                            if (done < limit)
                            {
                                done++;
                                loopCounters[node.Id] = done;
                                // 自动注入循环序号：循环体内的节点可以直接用 {循环序号} / {循环总次数}
                                // （截图命名、弹窗显示"第几轮"、条件节点判"最后一轮"等都不用再手写计数器）
                                AutomationContext.SetVariable("循环序号", done.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                AutomationContext.SetVariable("循环总次数", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                Emit(string.Format("  [循环] 第 {0}/{1} 次 -> 回到节点 {2}",
                                    done, limit, node.AltNextId));
                                if (node.AltNextId < 0)
                                {
                                    result.Error = string.Format(
                                        "循环节点 {0} 没有连接“循环目标”，且次数未到，无法继续", node.Id);
                                    Emit("!! " + result.Error);
                                    break;
                                }
                                cur = node.AltNextId;
                                continue;
                            }
                            Emit(string.Format("  [循环] 已完成 {0} 次，走主出口", done));
                            loopStack.Pop();
                            loopCounters.Remove(node.Id);   // 完成即清计数：主出口若再次连回本循环会重新计数
                        }
                        else
                        {
                            // 通用视觉算子：节点没挑算子就别执行（否则 CreateTask(null) 会静默变成空操作）
                            if (node.Kind == AutoNodeKind.VisionOp && string.IsNullOrWhiteSpace(node.OpName))
                            {
                                result.Error = string.Format("节点 #{0} 是“视觉算子”，还没有选择具体算子", node.Id);
                                Emit("!! " + result.Error);
                                break;
                            }

                            if (node.Kind == AutoNodeKind.Capture
                                && node.Params.Length > 5 && node.Params[5] == 1
                                && hideScope == null)
                            {
                                hideScope = ScreenCapture.SuppressOwnWindows();
                                int hid = AutomationContext.MoveWindowsAwayInsteadOfHiding
                                    ? ScreenCapture.OwnMovedWindowCount
                                    : ScreenCapture.OwnHiddenWindowCount;
                                Emit(hid > 0
                                    ? string.Format("  [截图] 已把本软件窗口{0} {1} 个（跑完整张图后恢复）",
                                        ScreenCapture.SuppressStrategyName, hid)
                                    : "  [截图] 没有需要处理的本程序窗口（已经最小化或已隐藏?）");
                            }

                            var taskObj = VisionTaskRegistry.CreateTask(task);
                            if (taskObj == null)
                            {
                                result.Error = "找不到算子: " + task;
                                Emit("!! " + result.Error);
                                break;
                            }

                            // 匹配节点需要模板
                            // 节点图注入的字符串参数：文本/按键两个框（键盘输入、弹窗、全局变量、截图目录都用它）
                            if (taskObj is IStringParamTask sp)
                            {
                                sp.NodeText = graph.GetNodeText(node.Id);
                                sp.NodeKey = graph.GetNodeKey(node.Id);
                            }
                            // 需要更多槽位的节点（命令行/表达式/等待条件/窗口）走这个：按需取任意槽
                            if (taskObj is INodeStringSource nss)
                            {
                                int nodeId = node.Id;
                                nss.NodeStringProvider = slot => graph.GetNodeString(nodeId, slot);
                            }

                            if (node.Kind == AutoNodeKind.Match)
                            {
                                // 节点自己的模板优先，没单独导入才用页面顶部的默认模板
                                Mat tpl = graph.ResolveTemplate(node.Id);
                                if (tpl == null || tpl.Empty())
                                {
                                    result.Error = string.Format(
                                        "“模板匹配”节点 #{0} 缺少模板图，请选中它后在“节点属性”里导入", node.Id);
                                    Emit("!! " + result.Error);
                                    break;
                                }
                                AutomationSupport.ApplyTemplate(taskObj, tpl);
                            }
                            else if (AutomationSupport.NeedsTemplate(taskObj))
                            {
                                // 通用视觉算子选了"模板匹配/形状匹配/模板差分…"这类时，
                                // 同样把模板喂进去（节点自己的优先，否则用默认模板）。
                                // 不做这一步的话这些算子会报"请先导入模板"，而界面上看起来模板是有的。
                                Mat tpl = graph.ResolveTemplate(node.Id);
                                if (tpl?.Empty() == false) AutomationSupport.ApplyTemplate(taskObj, tpl);
                            }

                            // 还没有截图时给一个空图兜底（源算子会忽略它），用完必须释放
                            bool tempInput = image == null;
                            var nodeWatch = System.Diagnostics.Stopwatch.StartNew();
                            Mat input = image ?? new Mat();
                            Mat output = null;
                            const int maxRetry = 1;   // 瞬时失败自动重试 1 次（截图偶发失败、命令竞争等）
                            int attempt = 0;
                            try
                            {
                                while (true)
                                {
                                    try
                                    {
                                        output = taskObj.Execute(input, node.Params);
                                        break;
                                    }
                                    catch (Exception ex) when (attempt < maxRetry)
                                    {
                                        attempt++;
                                        Emit(string.Format("  [重试] 节点“{0}”执行失败（{1}），等待后自动重试第 {2} 次…",
                                            AutoNodeInfo.Title(node.Kind), ex.Message, attempt));
                                        UiWait.Sleep(300);
                                    }
                                }
                            }
                            finally
                            {
                                if (tempInput) input.Dispose();
                            }

                            // 每个动作节点的输出都留一份给界面看（见 NodeImages 的说明）
                            if (output != null) result.NodeImages[node.Id] = output;

                            // 截图节点替换当前图像；区域裁剪ROI同样替换（让后续节点只在该区域检测）；
                            // 其它节点保留原图（它们只是顺带画个标记）
                            if (node.Kind == AutoNodeKind.Capture || node.Kind == AutoNodeKind.Roi) image = output;
                            else result.FinalImage = output;

                            if (taskObj is IActionStateTask ast && ast.Skipped)
                            {
                                result.SkippedActions++;
                                AutomationContext.SkippedActionCount++;
                            }
                            nodeWatch.Stop();
                            result.NodeTimes[node.Id] = nodeWatch.ElapsedMilliseconds;
                            string cost = string.Format("  {0}ms", nodeWatch.ElapsedMilliseconds);

                            if (node.Kind == AutoNodeKind.Match && taskObj is TemplateMatchTask m)
                            {
                                lastMatchFound = m.Found;
                                Emit(string.Format("  [匹配] {0}{1}", m.LastSummary, cost));
                                result.NodeSummaries[node.Id] = m.LastSummary;
                                if (m.Found)
                                {
                                    bool hasScreen = AutomationContext.HasCapture;
                                    result.NodeTargets[node.Id] = (
                                        m.BestCenter.X, m.BestCenter.Y,
                                        (int)Math.Round(AutomationContext.CaptureOriginX + m.BestCenter.X),
                                        (int)Math.Round(AutomationContext.CaptureOriginY + m.BestCenter.Y),
                                        hasScreen, m.BestScale);
                                }
                            }
                            else if (taskObj is IResultReporter rep)
                            {
                                string tag = node.Kind == AutoNodeKind.VisionOp ? node.OpName : AutoNodeInfo.Title(node.Kind);
                                Emit(string.Format("  [{0}] {1}{2}", tag, rep.LastSummary, cost));
                                result.NodeSummaries[node.Id] = rep.LastSummary;
                            }
                            else
                            {
                                // 没有摘要的算子（如"灰度转换"）也要留一行：否则日志里一片空白，
                                // 既看不出它跑过，也看不出是哪一步慢
                                string tag = node.Kind == AutoNodeKind.VisionOp ? node.OpName : AutoNodeInfo.Title(node.Kind);
                                Emit(string.Format("  [{0}] 完成{1}", tag, cost));
                            }

                            // 文字类算子的结果（OCR/条码）发布给"全局变量"节点与 {变量} 插值
                            if (AutomationSupport.TryGetText(taskObj, out string recognized)
                                && !string.IsNullOrEmpty(recognized))
                            {
                                AutomationContext.LastText = recognized;
                                Emit("  [文字] " + recognized);
                            }

                            // 记下这个节点的输出："全局变量"节点可以按 id 引用**指定节点**的结果
                            // （第 3 步识别到的文字、第 5 步点击的坐标、截图存到哪…）
                            var nodeOut = new NodeOutput
                            {
                                Summary = (taskObj as IResultReporter)?.LastSummary ?? "",
                                Text = AutomationSupport.TryGetText(taskObj, out string t2) ? (t2 ?? "") : "",
                            };
                            // 找目标的算子不止"模板匹配"：形状匹配/几何定位/镜头/圆卡尺/码也要能取到坐标
                            if (AutomationSupport.TryGetTarget(taskObj, out bool tgFound,
                                    out float tgX, out float tgY, out double tgScore, out int tgScale))
                            {
                                nodeOut.HasTarget = tgFound;
                                nodeOut.CenterX = tgX;
                                nodeOut.CenterY = tgY;
                                nodeOut.Score = tgScore;
                                nodeOut.Scale = tgScale;

                                // 发布给"鼠标点击：目标来源=上次检测"。
                                // 原先只有模板匹配/形状匹配自己发布，其它定位算子发布不了，
                                // 用户在那几个算子后面接点击会发现"没有可用的检测目标"。
                                if (tgFound)
                                    DetectionStore.Publish(new Point2f(tgX, tgY), tgScore, task, new Rect(), tgScale);
                            }
                            if (taskObj is ScreenGrabTask sgt) nodeOut.SavedPath = sgt.LastSavedPath ?? "";
                            if (taskObj is MouseClickTask mct)
                            {
                                nodeOut.HasClick = mct.HasAction;
                                nodeOut.ClickX = mct.ClickX;
                                nodeOut.ClickY = mct.ClickY;
                            }
                            if (taskObj is KeyInputTask kit) nodeOut.SentContent = kit.Payload ?? "";
                            if (taskObj is CommandTask cmdTask)
                            {
                                nodeOut.ExitCode = cmdTask.ExitCode;
                                nodeOut.Stdout = cmdTask.Stdout ?? "";
                                nodeOut.Stderr = cmdTask.Stderr ?? "";
                            }
                            if (taskObj is WindowTask winTask) nodeOut.BoolResult = winTask.Exists ? "1" : "0";
                            if (taskObj is RuleTask ruleTask)
                            {
                                nodeOut.BoolResult = ruleTask.Passed ? "1" : "0";
                                nodeOut.Text = ruleTask.Reason ?? "";
                                if (ruleTask.LastRule != null) ruleTask.LastRule.NodeId = node.Id;
                            }
                            if (taskObj is AggregateTask aggTask)
                            {
                                nodeOut.BoolResult = aggTask.Verdict == "OK" ? "1" : "0";
                                nodeOut.Text = aggTask.Detail ?? "";
                            }
                            if (taskObj is HttpTask httpTask)
                            {
                                nodeOut.BoolResult = httpTask.Success ? "1" : "0";
                                nodeOut.Text = httpTask.ResponseBody ?? "";
                            }
                            AutomationContext.SetNodeOutput(node.Id, nodeOut);
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        result.Error = ex.Message;
                        Emit("!! " + ex.Message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        result.Error = string.Format("节点“{0}”执行出错: {1}",
                            AutoNodeInfo.Title(node.Kind), ex.Message);
                        Emit("!! " + result.Error);
                        break;
                    }

                    cur = node.NextId;
                }

                result.Ok = string.IsNullOrEmpty(result.Error);
                result.Steps = steps;
                totalWatch.Stop();
                result.TotalMs = totalWatch.ElapsedMilliseconds;
                result.Actions = AutomationContext.Input.ActionCount;
                result.SkippedActions = Math.Max(result.SkippedActions, AutomationContext.SkippedActionCount);
                result.ActuallyExecuted = !AutomationContext.DryRun && result.Actions > 0;
                Emit(string.Format("=== 执行结束：{0} 步，{1} 个动作{2} ===",
                    steps, result.Actions,
                    result.Ok ? "" : "，中止原因：" + result.Error));
            }
            finally
            {
                // 图像全部挂在 NodeImages 上由调用方统一释放，这里只补上"最终结果"的指向：
                // 只有截图节点时，最后那张截图就是结果
                result.FinalImage ??= image;
                // 输入图留一份克隆给界面"原图"页签：克隆是为了让调用方随时 DisposeImages 都不影响它
                if (result.InputImage == null && image != null && !image.Empty())
                    result.InputImage = image.Clone();
                if (result.TotalMs == 0) { totalWatch.Stop(); result.TotalMs = totalWatch.ElapsedMilliseconds; }
                hideScope?.Dispose();
            }

            return result;
        }
    }
}
