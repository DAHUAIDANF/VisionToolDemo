using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 鼠标点击：按“上一个算子检测到的目标”或固定坐标操作鼠标。
    ///
    /// 定位换算：检测是在**截图**上做的，截图坐标 (u,v) 对应屏幕坐标
    /// (截图原点.X + u, 截图原点.Y + v)。多显示器时虚拟桌面原点可能不是 (0,0)
    /// （副屏在主屏左侧时为负），漏掉这个偏移就会整体点偏。
    ///
    /// 安全：是否真的操作鼠标由**全局干跑开关**决定（默认干跑，只记日志）。
    /// 这一项刻意不做成算子参数 —— 开关分散在多处时，用户很容易只改一处，
    /// 剩下的仍在真实点击，那是最危险的配置。
    /// </summary>
    public class MouseClickTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.IActionStateTask, Automation.IStringParamTask
    {
        /// <summary>节点属性里的「文本」框：坐标变量模式下填变量名（支持 {变量}）</summary>
        public string NodeText { get; set; } = "";

        /// <summary>未使用（接口要求）</summary>
        public string NodeKey { get; set; } = "";
        public string TaskName => "鼠标点击";

        public string LastSummary { get; private set; } = "";

        /// <summary>本次实际点击的屏幕坐标（干跑时是“本来会点”的位置）</summary>
        public int ClickX { get; private set; }
        public int ClickY { get; private set; }

        /// <summary>本轮已发出动作（干跑时表示"本应发出"，实际未操作系统）</summary>
        public bool HasAction { get; private set; }

        /// <summary>是否真的产生了系统输入。**干跑恒为 false** —— 界面若要提示
        /// "刚才真的点了"，必须看这个而不是 HasAction。</summary>
        public bool ActuallyExecuted => HasAction && !Automation.AutomationContext.DryRun;

        /// <summary>是否因为找不到目标/坐标越界而跳过</summary>
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "目标来源 0上次检测1固定坐标", Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "来源:{0}", Group = "定位",
                Tip = "0上次检测：点“模板匹配 / 形状匹配 / 几何定位”等算子最近一次命中的目标中心。\n" +
                "   在一轮自动化里这个目标会在每轮开始时清空，避免用到上一轮的陈旧位置。\n" +
                "1固定坐标：直接点下面的 X/Y（屏幕坐标）。" },
            new TaskParamDesc { ParamName = "X", Min = -10000, Max = 10000, DefaultValue = 0,
                DisplayFormat = "X:{0}", Group = "定位" },
            new TaskParamDesc { ParamName = "Y", Min = -10000, Max = 10000, DefaultValue = 0,
                DisplayFormat = "Y:{0}", Group = "定位" },
            new TaskParamDesc { ParamName = "偏移X", Min = -2000, Max = 2000, DefaultValue = 0,
                DisplayFormat = "dX:{0}", Group = "定位",
                Tip = "在目标中心基础上再偏移若干像素。用于点目标的某个局部" +
                "（例如按钮的上半部分，或避开图标正中心的文字）。" },
            new TaskParamDesc { ParamName = "偏移Y", Min = -2000, Max = 2000, DefaultValue = 0,
                DisplayFormat = "dY:{0}", Group = "定位" },
            new TaskParamDesc { ParamName = "按键 0左1右2中", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "键:{0}", Group = "动作" },
            new TaskParamDesc { ParamName = "方式 0单击1双击2只移动", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "方式:{0}", Group = "动作",
                Tip = "2只移动：只把鼠标移过去不按下。用于悬停出菜单，或先确认位置对不对。" },
            new TaskParamDesc { ParamName = "动作后等待ms", Min = 0, Max = 10000, DefaultValue = 300,
                DisplayFormat = "等待:{0}ms", Group = "动作",
                Tip = "动作完成后等待多久再继续。界面响应通常需要 200~800ms；" +
                "填 0 容易在目标程序还没反应过来时就开始下一步，导致连锁失败。" },
            // 新增参数一律追加在末尾（参数按索引保存）
            new TaskParamDesc
            {
                ParamName = "坐标变量模式 0不用1变量图像坐标2变量屏幕坐标",
                Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "坐标变量:{0}", Group = "坐标",
                Tip = "0 = 不用变量（按上面的“目标来源”）。\n" +
                "1/2 = 坐标从**全局变量**里取，【节点属性 → 输入内容】的「文本」框可写：\n" +
                "      · 一个变量名（它的值是 420,285 这种）；\n" +
                "      · 两个变量名，如 x,y —— x 当横坐标、y 当纵坐标；\n" +
                "      · 或者直接写坐标 420,285（临时试坐标用）。\n" +
                "      1 = 按**图像坐标**处理（自动加截图原点，与“目标来源=上次检测”一致）；\n" +
                "      2 = 按**屏幕坐标**处理（直接用，适合取“点击屏幕坐标”这类变量）。\n" +
                "      配合“全局变量”节点：把 目标中心X,Y 或 点击屏幕坐标 存下来即可。" },
            new TaskParamDesc
            {
                ParamName = "随机偏移 0关1开",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "随机:{0}", Group = "动作",
                Tip = "0 = 关闭（默认）。\n" +
                "1 = 在最终点击位置基础上叠加随机偏移，模拟真人操作、降低被识别为“机器操作”的概率。\n" +
                "每次执行独立随机（均匀分布 ±半径）。干跑模式同样计算并显示“本来会点的位置”。" },
            new TaskParamDesc
            {
                ParamName = "随机半径px",
                Min = 0, Max = 500, DefaultValue = 5,
                DisplayFormat = "±{0}px", Group = "动作",
                Tip = "随机偏移的半径（像素）。最终位置 = 目标位置 + 均匀分布在 [-半径, +半径] 的随机值。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            HasAction = false;
            Skipped = false;
            ClickX = ClickY = 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int source = Math.Clamp(paramValues[0], 0, 1);
            int x = paramValues[1], y = paramValues[2];
            int dx = paramValues[3], dy = paramValues[4];
            int btn = Math.Clamp(paramValues[5], 0, 2);
            int mode = Math.Clamp(paramValues[6], 0, 2);
            int waitMs = Math.Max(0, paramValues[7]);
            // 新增参数：旧图（保存时只有 8 个参数）数组更短，越界读会抛
            int varMode = paramValues.Length > 8 ? Math.Clamp(paramValues[8], 0, 2) : 0;
            // 随机偏移：老图（8 参数）默认关闭
            int randMode = paramValues.Length > 9 ? Math.Clamp(paramValues[9], 0, 1) : 0;
            int randR = paramValues.Length > 10 ? Math.Max(0, paramValues[10]) : 5;

            int sx, sy;
            string origin;
            if (varMode != 0)
            {
                // 坐标从全局变量来。变量名写在该节点「文本」框里；值形如 "420,285"。
                // 这一栏可以写四种东西，按"最具体"到"最通用"的顺序试：
                //   ① 坐标字面量 420,285      —— 临时试坐标不用先建变量
                //   ② 一个变量名 目标坐标      —— 它的值是 "420,285"
                //   ③ 两个变量名 x,y          —— x 是横坐标、y 是纵坐标（很多人习惯分开记）
                //   ④ 都不是 → 跳过并把"这一轮有哪些变量"列出来
                string box = (NodeText ?? "").Trim();
                int vx, vy;
                string raw, how;
                if (TryParsePoint(box, out vx, out vy))
                {
                    raw = box;
                    how = "框里直接写的坐标";
                }
                else if (Automation.AutomationContext.TryResolveVariable(box, out string single))
                {
                    if (!TryParsePoint(single, out vx, out vy))
                    {
                        Skipped = true;
                        LastSummary = string.Format("鼠标点击: 跳过 —— 变量 {0} 的值 \"{1}\" 不是坐标（要形如 420,285）",
                            box, single);
                        return dst;
                    }
                    raw = single;
                    how = "变量 " + box;
                }
                else if (TryResolveTwoVariables(box, out vx, out vy, out string pairDesc))
                {
                    raw = pairDesc;
                    how = "两个变量";
                }
                else
                {
                    Skipped = true;
                    LastSummary = box.Length == 0
                        ? "鼠标点击: 跳过 —— 开了坐标变量模式，但没填变量名（节点属性 → 输入内容 → 文本）"
                        : string.Format("鼠标点击: 跳过 —— 找不到变量 \"{0}\"（也可以写两个变量名，如 x,y）。{1}",
                            box, KnownVariablesHint());
                    return dst;
                }
                if (varMode == 1)
                {
                    var vp = Automation.CoordinateSpace.ImageToScreen(
                        vx, vy, Automation.AutomationContext.CaptureImageToScreenScale);
                    sx = vp.X + dx;
                    sy = vp.Y + dy;
                }
                else { sx = vx + dx; sy = vy + dy; }
                origin = string.Format("{0}=\"{1}\"（{2}）", how, raw,
                    varMode == 1 ? "图像坐标" : "屏幕坐标");
            }
            else if (source == 0)
            {
                if (!Automation.DetectionStore.TryGet(out Point2f c, out double sc, out string src))
                {
                    Skipped = true;
                    string why = Automation.DetectionStore.MissReason;
                    LastSummary = string.IsNullOrEmpty(why)
                        ? "鼠标点击: 跳过 —— 没有可用的检测目标（上游没有匹配算子命中）"
                        : "鼠标点击: 跳过 —— 没有可用的检测目标：" + why;
                    return dst;
                }
                // 图像坐标 → 屏幕坐标：截图原点 + DPI 换算（屏幕上量到的像素可能不是 SetCursorPos 的坐标）
                var sp = Automation.CoordinateSpace.ImageToScreen(
                    c.X, c.Y, Automation.AutomationContext.CaptureImageToScreenScale);
                sx = sp.X + dx;
                sy = sp.Y + dy;
                origin = string.Format("{0} 得分{1:F2}", src, sc);
            }
            else
            {
                sx = x + dx;
                sy = y + dy;
                origin = "固定坐标";
            }

            // 随机偏移：模拟真人操作，每次独立随机（均匀分布 ±半径）。干跑同样计算，显示"本来会点的位置"
            string randNote = "";
            if (randMode == 1 && randR > 0)
            {
                int rx = Random.Shared.Next(-randR, randR + 1);
                int ry = Random.Shared.Next(-randR, randR + 1);
                sx += rx; sy += ry;
                randNote = string.Format(" 随机±{0}px→实际({1},{2})", randR, rx, ry);
            }

            ClickX = sx; ClickY = sy;

            // 屏幕范围校验：坐标打字打错时点下去很危险，先拦住
            var vb = Automation.ScreenCapture.VirtualBounds;
            if (!vb.Contains(sx, sy))
            {
                Skipped = true;
                LastSummary = string.Format(
                    "鼠标点击: 跳过 —— 坐标 ({0},{1}) 不在屏幕范围 {2} 内。检查坐标/截图原点是否正确",
                    sx, sy, vb);
                return dst;
            }

            try
            {
                var sim = Automation.AutomationContext.Input;
                var button = (Automation.InputSimulator.MouseButton)btn;
                if (mode == 1) sim.DoubleClick(sx, sy, button);
                else if (mode == 2) sim.MoveTo(sx, sy);
                else sim.Click(sx, sy, button);
                HasAction = true;
            }
            catch (OperationCanceledException ex)
            {
                Skipped = true;
                LastSummary = "鼠标点击: 已中止 —— " + ex.Message;
                return dst;
            }
            catch (Exception ex)
            {
                Skipped = true;
                LastSummary = "鼠标点击: 失败 —— " + ex.Message;
                return dst;
            }

            DrawMarker(dst, sx, sy);
            if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);

            string[] modeName = { "单击", "双击", "移动" };
            LastSummary = string.Format("鼠标点击: {0} 屏幕({1},{2})  [{3}]{4}{5}",
                modeName[mode], sx, sy, origin, randNote,
                Automation.AutomationContext.DryRun ? "   ★干跑，未真实操作" : "");
            return dst;
        }

        /// <summary>
        /// 解析变量里的坐标值。宽容一点：逗号/空格/分号/竖线分隔、"x=420 y=285" 带键名的写法都认
        /// —— 用户从日志里抄下来的形式五花八门。
        /// </summary>
        public static bool TryParsePoint(string text, out int x, out int y)
        {
            x = y = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Replace("x=", " ", StringComparison.OrdinalIgnoreCase)
                           .Replace("y=", " ", StringComparison.OrdinalIgnoreCase)
                           .Replace("(", " ").Replace(")", " ")
                           .Replace("，", ",").Replace("；", ";");
            string[] parts = s.Split([' ', ',', ';', '|', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
        }

        /// <summary>
        /// 把 "x,y" 这种**两个变量名**解析成坐标：x 是横坐标、y 是纵坐标。
        /// 有些用户习惯把 X 和 Y 分别记到两个变量里（本项目实测就有人这么用），
        /// 这里顺手支持一下，省得非要合并成一个 "x,y" 值。
        /// </summary>
        private static bool TryResolveTwoVariables(string box, out int x, out int y, out string desc)
        {
            x = y = 0;
            desc = "";
            if (string.IsNullOrWhiteSpace(box)) return false;
            string[] names = box.Split([',', ' ', ';', '|', '/', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (names.Length < 2) return false;
            if (!Automation.AutomationContext.TryResolveVariable(names[0], out string sx)) return false;
            if (!Automation.AutomationContext.TryResolveVariable(names[1], out string sy)) return false;
            if (!int.TryParse(sx.Trim(), out x)) return false;
            if (!int.TryParse(sy.Trim(), out y)) return false;
            desc = string.Format("{0}={1}, {2}={3}", names[0], x, names[1], y);
            return true;
        }

        /// <summary>
        /// 变量找不到时把"这一轮到底有哪些变量"报出来。
        /// 只写"找不到变量 x,y"用户没法判断是名字打错、还是上游节点没写成功
        /// （实测有人把变量名框当成"填坐标的地方"，填了字面量 x,y）。
        /// </summary>
        private static string KnownVariablesHint()
        {
            var names = new System.Collections.Generic.List<string>(Automation.AutomationContext.Variables.Keys);
            if (names.Count == 0) return "这一轮还没有任何变量被写过（先加“全局变量”节点记录）。";
            names.Sort(StringComparer.Ordinal);
            string list = names.Count <= 8 ? string.Join("、", names)
                                           : string.Join("、", names.GetRange(0, 8)) + " 等 " + names.Count + " 个";
            return "这一轮已有的变量：" + list + "。";
        }

        /// <summary>在结果图上标出实际点击位置，便于肉眼确认“它到底要往哪点”</summary>
        private static void DrawMarker(Mat dst, int sx, int sy)
        {
            if (dst == null || dst.Empty()) return;
            // 屏幕坐标 → 图内像素：反着用同一套换算（DPI 不感知时图上像素与屏幕坐标差一个比例）
            double scale = Automation.AutomationContext.CaptureImageToScreenScale;
            if (scale <= 0) scale = 1.0;
            int ux = (int)Math.Round((sx - Automation.AutomationContext.CaptureOriginX) / scale);
            int uy = (int)Math.Round((sy - Automation.AutomationContext.CaptureOriginY) / scale);
            if (ux < 0 || uy < 0 || ux >= dst.Cols || uy >= dst.Rows) return;

            var c = new Point(ux, uy);
            Cv2.Circle(dst, c, 14, Scalar.Red, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, ux - 20, uy, ux - 6, uy, Scalar.Red, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, ux + 6, uy, ux + 20, uy, Scalar.Red, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, ux, uy - 20, ux, uy - 6, Scalar.Red, 2, LineTypes.AntiAlias);
            Cv2.Line(dst, ux, uy + 6, ux, uy + 20, Scalar.Red, 2, LineTypes.AntiAlias);
        }
    }
}
