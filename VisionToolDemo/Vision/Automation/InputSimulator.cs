using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 鼠标/键盘模拟（对应 AutoHotkey 的核心能力）。
    ///
    /// ============================ 安全设计 ============================
    /// 这类功能能**真实操作用户的电脑**：一旦逻辑写错或匹配到错误目标，
    /// 它会疯狂点击、乱敲键盘，甚至覆盖用户正在编辑的内容。
    /// 因此本类强制以下几层保护，且默认就是最安全的一档：
    ///
    ///   1. **干跑（DryRun）默认开启** —— 只记录"本来要点哪里、按什么键"，
    ///      完全不产生任何输入。必须先干跑观察日志确认无误，再显式关闭。
    ///   2. **失效保护（Failsafe）** —— 每次动作前检查光标是否停在屏幕四角
    ///      的小方块内；是则立刻中止。用户把鼠标甩到角落就能叫停，
    ///      不需要去点界面上的停止按钮（而那一刻界面可能已经被自动化搞乱了）。
    ///   3. **动作次数上限** —— 累计动作数超过上限即中止，防止死循环。
    ///   4. **动作日志** —— 每个动作都记下来，事后可查"它到底做了什么"。
    /// ==================================================================
    /// </summary>
    public sealed class InputSimulator
    {
        // ---------------- 安全开关 ----------------

        /// <summary>干跑：只记录不执行。**默认 true**，需要用户显式关闭。</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>失效保护：光标停在屏幕角落时中止</summary>
        public bool FailsafeEnabled { get; set; } = true;

        /// <summary>角落判定的边长（像素）</summary>
        public int FailsafeCornerSize { get; set; } = 8;

        /// <summary>单次任务的动作次数上限</summary>
        public int MaxActions { get; set; } = 500;

        /// <summary>已执行（或干跑记录）的动作数</summary>
        public int ActionCount { get; private set; }

        /// <summary>动作日志（最多保留 500 条，避免长时间运行吃内存）</summary>
        public List<string> Log { get; } = new();

        /// <summary>每个动作的回调（UI 用来实时显示）</summary>
        public event Action<string> OnAction;

        /// <summary>被失效保护中止时置位，供上层区分"正常结束"与"被叫停"</summary>
        public bool FailsafeTripped { get; private set; }

        /// <summary>鼠标动作之间的最小间隔，避免快过目标程序的处理能力</summary>
        public int MoveSettleMs { get; set; } = 12;

        /// <summary>
        /// 逐字符输入时每个字符之间的额外间隔（毫秒）。0 = 尽快发（默认，与旧行为一致）。
        /// 有些程序会丢太快的合成输入（远程桌面尤其明显），这时调大它。
        /// </summary>
        public int TypeDelayMs { get; set; }

        /// <summary>一轮自动化开始前重置计数（动作上限按"每轮"算，而不是按会话累计）</summary>
        public void ResetCounters()
        {
            ActionCount = 0;
            FailsafeTripped = false;
        }

        private void CheckSafety(string desc)
        {
            if (FailsafeTripped) throw new OperationCanceledException("失效保护已触发，自动化已中止");

            if (ActionCount >= MaxActions)
                throw new OperationCanceledException(
                    string.Format("动作次数达到上限 {0} 次，已中止（防止死循环）", MaxActions));

            if (FailsafeEnabled && IsCursorInCorner())
            {
                FailsafeTripped = true;
                throw new OperationCanceledException(
                    "检测到鼠标停在屏幕角落 —— 失效保护已将自动化中止");
            }
        }

        private void Record(string desc)
        {
            ActionCount++;
            string line = string.Format("[{0:HH:mm:ss.fff}] {1}{2}",
                DateTime.Now, DryRun ? "(干跑) " : "", desc);
            if (Log.Count < 500) Log.Add(line);
            OnAction?.Invoke(line);
        }

        /// <summary>光标是否停在虚拟桌面四角之一</summary>
        public bool IsCursorInCorner()
        {
            var vb = ScreenCapture.VirtualBounds;
            if (!GetCursorPos(out POINT p)) return false;
            int d = Math.Max(1, FailsafeCornerSize);
            bool left = p.X <= vb.Left + d;
            bool right = p.X >= vb.Right - 1 - d;
            bool top = p.Y <= vb.Top + d;
            bool bottom = p.Y >= vb.Bottom - 1 - d;
            return (left || right) && (top || bottom);   // 任一角落命中即算
        }

        // ---------------- 鼠标 ----------------

        public enum MouseButton { Left, Right, Middle }

        /// <summary>最近一次成功移动用的是哪条路径（SetCursorPos / SendInput），日志与自检用</summary>
        public string LastMoveMethod { get; private set; } = "";

        /// <summary>
        /// 把光标移到屏幕坐标（当前进程坐标系）。
        ///
        /// 优先用 SetCursorPos：它直接吃虚拟桌面坐标，与截图坐标系天然一致
        /// （SendInput 的绝对模式要把坐标归一化到 0..65535，多显示器含负坐标时容易算错）。
        /// 但 SetCursorPos 在几种真实环境下会直接失败（返回 0）：
        ///   · 光标被别的程序用 ClipCursor 限制在某个矩形里（游戏/全屏程序）；
        ///   · 当前输入桌面不是我们所在的桌面（锁屏、UAC 安全桌面、别的会话）；
        ///   · 进程没有窗口站的写权限（以服务/另一会话启动）。
        /// 所以这里带一条 SendInput 绝对移动的退路，并且**读回核对**是否真的到位 ——
        /// "返回成功但其实没动"比报错更危险：后面的点击会落在光标实际所在的位置。
        /// </summary>
        public void MoveTo(int x, int y)
        {
            CheckSafety(string.Format("移动鼠标到 ({0},{1})", x, y));
            if (!DryRun) MoveCursorReal(x, y);
            Record(string.Format("移动鼠标到 ({0},{1})", x, y));
        }

        /// <summary>真实移动光标（不带干跑判断）：SetCursorPos → 核对 → SendInput 退路</summary>
        // ReSharper disable once UnusedMember.Global（测试与自检会调用）
        internal void MoveCursorReal(int x, int y)
        {
            ReleaseOwnMouseCapture();

            bool moved = SetCursorPos(x, y);
            int err = moved ? 0 : Marshal.GetLastWin32Error();
            if (moved && IsAt(x, y))
            {
                LastMoveMethod = "SetCursorPos";
                Settle();
                return;
            }

            // 退路：注入式绝对移动（带 VIRTUALDESK，按虚拟桌面归一化）
            if (TryMoveBySendInput(x, y))
            {
                LastMoveMethod = "SendInput";
                Settle();
                return;
            }

            throw new InvalidOperationException(DescribeMoveFailure(x, y, moved, err));
        }

        private void Settle()
        {
            if (MoveSettleMs > 0) UiWait.Sleep(MoveSettleMs);
        }

        /// <summary>
        /// 释放本程序自己可能持有的鼠标捕获。
        ///
        /// 在图上拖动画 ROI/卡尺时会 SetCapture；如果捕获没被释放（比如拖到一半窗口被隐藏、
        /// 或 MouseUp 落在别处），我们自己就"抓着"鼠标。这时注入的移动/点击可能被忽略或
        /// 落在我们自己身上。真实操作前统一松手，代价可以忽略。
        /// </summary>
        private static void ReleaseOwnMouseCapture()
        {
            try { ReleaseCapture(); } catch { /* 拿不到捕获就算了：这只是防御性动作 */ }
        }

        /// <summary>光标是否在目标点附近（容差 2px：圆整数与缩放会让它差一两像素）</summary>
        private static bool IsAt(int x, int y)
            => GetCursorPos(out POINT p) && Math.Abs(p.X - x) <= 2 && Math.Abs(p.Y - y) <= 2;

        /// <summary>
        /// SendInput 绝对移动并核对。坐标按虚拟桌面归一化到 0..65535
        /// （MOUSEEVENTF_VIRTUALDESK 让这个范围覆盖整个虚拟桌面，而不只是主屏）。
        /// </summary>
        private static bool TryMoveBySendInput(int x, int y)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 1 || vh <= 1) return false;

            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dx = NormalizeAbsolute(x - vx, vw - 1);
            inputs[0].u.mi.dy = NormalizeAbsolute(y - vy, vh - 1);
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1) return false;
            return IsAt(x, y);
        }

        /// <summary>像素坐标 → 0..65535 归一化坐标（虚拟桌面跨度按 span 个像素算）</summary>
        internal static int NormalizeAbsolute(int offset, int span)
        {
            if (span <= 0) return 0;
            int v = (int)Math.Round(offset * 65535.0 / span);
            return Math.Max(0, Math.Min(65535, v));
        }

        /// <summary>把失败原因写清楚：Win32 错误码 + 这个码在自动化里通常意味着什么</summary>
        internal static string DescribeMoveFailure(int x, int y, bool setCursorPosOk, int win32Error)
        {
            string why;
            switch (win32Error)
            {
                case 5:
                    why = "拒绝访问（错误 5）：目标程序以管理员权限运行，而本程序不是；或当前会话不允许写输入桌面。可试试让本程序也以管理员身份运行。";
                    break;
                case 87:
                    why = "参数无效（错误 87）：坐标或桌面度量异常。";
                    break;
                case 1400:
                    why = "桌面句柄无效（错误 1400）：输入桌面不是当前桌面（锁屏、UAC 安全桌面、或另一个会话）。";
                    break;
                case 0:
                    why = setCursorPosOk
                        ? "SetCursorPos 报告成功，但光标没有到位，注入式移动也被拒绝。"
                        : "系统没有给出错误码。";
                    break;
                default:
                    why = string.Format("Win32 错误 {0}。", win32Error);
                    break;
            }
            return string.Format(
                "无法把光标移到 ({0},{1})。{2} 屏幕范围 {3}。\n" +
                "其它常见原因：光标被别的程序限制在某个矩形里（ClipCursor，常见于游戏/全屏程序）、" +
                "鼠标正被按住/捕获、远程桌面会话已断开、或虚拟机没有鼠标设备。\n" +
                "可以先在“自动化（节点）”页面点「测试鼠标定位」确认本机是否允许程序控制光标" +
                "（它会分别测窗口可见/隐藏两种情况，能直接区分是不是 UIPI 拦截）。\n" +
                "本程序当前{4}以管理员身份运行。",
                x, y, why, ScreenCapture.VirtualBounds, IsElevated ? "已" : "未");
        }

        /// <summary>
        /// 本程序是否以管理员身份运行。UIPI 拦截注入时这是最关键的一条信息：
        /// 被操作的程序是管理员而本程序不是，注入必然被拦。
        /// </summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 注入被系统拦住时的排查清单（取自微软 Power Automate 的 UIPI 排错文档）。
        /// 这类失败**拿不到 Win32 错误码**，只能靠环境判断，所以要把可能性一次列全。
        /// </summary>
        public static string UipiChecklist => string.Join(Environment.NewLine, new[]
        {
            "系统拦截了程序注入的鼠标/键盘输入（UIPI：不同完整性级别的程序之间禁止互相操作）。按可能性排查：",
            "  1. 被操作的程序以管理员身份运行 —— 让它别用管理员运行，或让本程序也以管理员身份运行；",
            "  2. 执行期间弹出了 UAC 对话框（切到了安全桌面）—— 先处理掉再跑；",
            "  3. 桌面被锁定，或屏保启动了 —— 解锁并关掉屏保；",
            "  4. 远程桌面窗口被最小化 —— 恢复窗口，或按微软文档设置 RemoteDesktop_SuppressWhenMinimized=2；",
            "  5. 服务器管理器自动启动触发了 UAC —— 关掉它的自动启动。",
        });

        /// <summary>
        /// 真实执行前的自检：在"运行期间的真实状态"下试一次移动光标，并**自动挑一种可行方式**。
        ///
        /// 返回 null = 可以真实执行（AutomationContext.MoveWindowsAwayInsteadOfHiding 已设为该用的方式）；
        /// 否则返回原因（含 UIPI 排查清单），调用方应当放弃这次运行。
        ///
        /// 为什么必须在开始前做：环境不允许时，问题会在跑到中间才暴露，
        /// 那时前面几步的截图/匹配已经做完，甚至已经有动作真的做出去了。
        /// </summary>
        public string PreflightForRealRun()
        {
            if (!TryGetCursor(out int ox, out int oy))
                return "读不到光标位置，无法判断真实执行是否可行（会话可能已断开）。";

            bool savedDry = DryRun;
            string hideFailure = TryStrategy(false, out string hideDetail);
            string awayFailure = null;
            if (hideFailure != null)
                awayFailure = TryStrategy(true, out string awayDetail);

            try { SetCursorPos(ox, oy); } catch { /* 还原失败不影响结论 */ }
            DryRun = savedDry;

            if (hideFailure == null)
            {
                AutomationContext.MoveWindowsAwayInsteadOfHiding = false;
                return null;
            }
            if (awayFailure == null)
            {
                // 隐藏后无法注入（典型是前台窗口被提升权限的程序占住），改用"移出屏幕"：
                // 前台仍是我们自己，注入不会被拦，抓屏里也没有我们。
                AutomationContext.MoveWindowsAwayInsteadOfHiding = true;
                return null;
            }

            return "隐藏本程序窗口后：" + hideFailure + Environment.NewLine +
                   "改成把本程序窗口移出屏幕后：" + awayFailure + Environment.NewLine + Environment.NewLine +
                   "本程序当前" + (IsElevated ? "已" : "未") + "以管理员身份运行。" +
                   Environment.NewLine + UipiChecklist;
        }

        /// <summary>按指定方式让窗口不挡路，然后试移动一次光标。返回 null 表示这种方式可行。</summary>
        private string TryStrategy(bool moveAway, out string detail)
        {
            detail = "";
            bool saved = AutomationContext.MoveWindowsAwayInsteadOfHiding;
            AutomationContext.MoveWindowsAwayInsteadOfHiding = moveAway;
            string failure = null;
            try
            {
                DryRun = false;
                using (ScreenCapture.SuppressOwnWindows(60))
                {
                    var vb = ScreenCapture.VirtualBounds;
                    int tx = vb.Left + (vb.Width / 2), ty = vb.Top + (vb.Height / 2);
                    MoveCursorReal(tx, ty);
                    detail = string.Format("移到 ({0},{1}) 成功（{2}）", tx, ty, LastMoveMethod);
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message.Split('\n')[0];
                detail = failure;
            }
            finally
            {
                AutomationContext.MoveWindowsAwayInsteadOfHiding = saved;
                DryRun = true;      // 交给调用方恢复真正的值
            }
            return failure;
        }

        /// <summary>
        /// 鼠标定位自检：分别测"窗口可见"和"窗口隐藏（真实运行时的状态）"两种情况，
        /// 最后把光标还原。两者结果不同就基本能断定是 UIPI（前台窗口完整性级别）。
        /// </summary>
        public string SelfTestMouse(int probes = 2)
        {
            if (!TryGetCursor(out int ox, out int oy))
                return "读取光标位置失败：拿不到输入设备状态（会话可能已断开）。";

            var lines = new List<string>();
            lines.Add("本程序" + (IsElevated ? "已" : "未") + "以管理员身份运行"
                + (IsElevated ? "。" : "（若被操作的程序是管理员，注入会被系统拦下）。"));

            lines.Add("— 窗口可见时 —");
            bool visibleOk = ProbeMoves(probes, lines, out string visibleFail);

            lines.Add("— 窗口隐藏时 —");
            bool hiddenOk;
            using (ScreenCapture.HideOwnWindows(120))
                hiddenOk = ProbeMoves(probes, lines, out string hiddenFail);

            lines.Add("— 窗口移出屏幕时（隐藏不行时运行器会自动改用这种方式）—");
            bool awayOk;
            using (ScreenCapture.MoveOwnWindowsAway(120))
                awayOk = ProbeMoves(probes, lines, out string awayFail);

            try { SetCursorPos(ox, oy); } catch { /* 还原失败不影响结论 */ }
            lines.Add(string.Format("已把光标还原到 ({0},{1})", ox, oy));

            if (hiddenOk) lines.Add("结论：真实执行期间可以控制光标（用「隐藏窗口」）。");
            else if (awayOk)
                lines.Add("结论：隐藏窗口后无法控制光标，但**移出屏幕可以** —— 真实执行会自动改用后者，无需手动设置。" +
                          Environment.NewLine + UipiChecklist);
            else if (visibleOk)
                lines.Add("结论：窗口可见时能控制光标，隐藏和移出屏幕都不行 —— 典型的 UIPI 拦截。" +
                          Environment.NewLine + UipiChecklist);
            else
                lines.Add("结论：三种情况都不行。" + Environment.NewLine + UipiChecklist);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>在当前位置状态（可见/隐藏由调用方决定）下试几个点，返回是否全部到位</summary>
        private bool ProbeMoves(int probes, List<string> lines, out string firstFailure)
        {
            var vb = ScreenCapture.VirtualBounds;
            bool allOk = true;
            firstFailure = null;
            for (int i = 0; i < Math.Max(1, probes); i++)
            {
                int x = vb.Left + (vb.Width * (i + 1) / (probes + 1));
                int y = vb.Top + (vb.Height / 2);
                try
                {
                    MoveCursorReal(x, y);
                    bool ok = TryGetCursor(out int gx, out int gy);
                    bool exact = ok && Math.Abs(gx - x) <= 2 && Math.Abs(gy - y) <= 2;
                    lines.Add(string.Format("  要求 ({0},{1}) → 实际 ({2},{3})  {4}（{5}）",
                        x, y, gx, gy, exact ? "成功" : "**没到位**", LastMoveMethod));
                    if (!exact) { allOk = false; firstFailure ??= "光标没到位"; }
                }
                catch (Exception ex)
                {
                    lines.Add(string.Format("  要求 ({0},{1}) → 失败：{2}", x, y, ex.Message.Split('\n')[0]));
                    allOk = false;
                    firstFailure ??= ex.Message;
                }
            }
            return allOk;
        }

        /// <summary>单击</summary>
        public void Click(int x, int y, MouseButton button = MouseButton.Left)
        {
            string name = button switch
            {
                MouseButton.Right => "右键",
                MouseButton.Middle => "中键",
                _ => "左键",
            };
            CheckSafety(string.Format("{0}单击 ({1},{2})", name, x, y));

            if (!DryRun)
            {
                // 走与 MoveTo 同一条路径：带退路 + 读回核对。
                // "移动报成功但没到位"必须在这里挡住，否则点击会落在光标实际所在的地方。
                MoveCursorReal(x, y);

                uint down, up;
                switch (button)
                {
                    case MouseButton.Right: down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
                    case MouseButton.Middle: down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
                    default: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
                }
                SendMouse(down);
                SendMouse(up);
            }
            Record(string.Format("{0}单击 ({1},{2})", name, x, y));
        }

        /// <summary>双击</summary>
        public void DoubleClick(int x, int y, MouseButton button = MouseButton.Left)
        {
            CheckSafety(string.Format("双击 ({0},{1})", x, y));
            Click(x, y, button);
            if (!DryRun) UiWait.Sleep(40);
            Click(x, y, button);
            Record(string.Format("双击 ({0},{1})", x, y));
        }

        /// <summary>把当前光标位置读回来（日志/调试用）</summary>
        public static bool TryGetCursor(out int x, out int y)
        {
            bool ok = GetCursorPos(out POINT p);
            x = p.X; y = p.Y;
            return ok;
        }

        // ---------------- 键盘 ----------------

        public void KeyDown(ushort vk)
        {
            CheckSafety("按下键 " + VkName(vk));
            if (!DryRun) SendKeyInput(vk, false);
            Record("按下键 " + VkName(vk));
        }

        public void KeyUp(ushort vk)
        {
            CheckSafety("抬起键 " + VkName(vk));
            if (!DryRun) SendKeyInput(vk, true);
            Record("抬起键 " + VkName(vk));
        }

        /// <summary>按下并抬起一个专用键（enter/tab/esc/f1../方向键 等）</summary>
        public void PressKey(string keyName)
        {
            if (!TryParseKey(keyName, out ushort vk))
                throw new ArgumentException("无法识别的按键名: " + keyName);
            CheckSafety("按键 " + keyName);
            if (!DryRun) { SendKeyInput(vk, false); SendKeyInput(vk, true); }
            Record("按键 " + keyName);
        }

        /// <summary>
        /// 发送组合键，例如 "Ctrl+C"、"Ctrl+Shift+S"、"Alt+F4"、"Enter"。
        /// 修饰键按住到主键抬起之后，保证目标程序收到的是组合而不是两次单键。
        /// </summary>
        public void SendCombo(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            string[] parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries);
            var mods = new List<ushort>();
            ushort main = 0;
            bool hasMain = false;

            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (!hasMain && IsModifier(p, out ushort mvk)) { mods.Add(mvk); continue; }
                if (!TryParseKey(p, out ushort vk))
                    throw new ArgumentException("无法识别的按键: " + p + "（组合键: " + combo + "）");
                main = vk;
                hasMain = true;
            }
            if (!hasMain) throw new ArgumentException("组合键缺少主键: " + combo);

            CheckSafety("组合键 " + combo);
            if (!DryRun)
            {
                foreach (ushort m in mods) SendKeyInput(m, false);
                SendKeyInput(main, false);
                SendKeyInput(main, true);
                // 修饰键反序抬起，否则 Ctrl+Shift+字母 这类组合可能把 Shift 卡住
                for (int i = mods.Count - 1; i >= 0; i--) SendKeyInput(mods[i], true);
            }
            Record("组合键 " + combo);
        }

        /// <summary>
        /// 输入一段文本（中文/符号都可以）。
        ///
        /// 用 KEYEVENTF_UNICODE 直接投递字符，不走剪贴板、也不依赖当前输入法：
        ///   · 走模拟按键（VkKeyScan）只能处理当前键盘布局能打出的 ASCII，
        ///     中文和很多符号会失败；
        ///   · 走剪贴板会破坏用户剪贴板里的内容。
        /// </summary>
        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string label = text.Length <= 40 ? text : text.Substring(0, 40) + "…";
            CheckSafety(string.Format("输入文本 \"{0}\"", label));
            if (!DryRun)
            {
                foreach (char c in text)
                {
                    SendUnicode(c, false);
                    SendUnicode(c, true);
                    if (TypeDelayMs > 0) UiWait.Sleep(TypeDelayMs);
                }
            }
            Record(string.Format("输入文本 \"{0}\"", label));
        }

        // ---------------- 键名解析 ----------------

        private static bool IsModifier(string name, out ushort vk)
        {
            switch (name.ToLowerInvariant())
            {
                case "ctrl": case "control": vk = VK_CONTROL; return true;
                case "shift": vk = VK_SHIFT; return true;
                case "alt": vk = VK_MENU; return true;
                case "win": case "lwin": vk = VK_LWIN; return true;
                default: vk = 0; return false;
            }
        }

        /// <summary>按键名 -> 虚拟键码。支持常用功能键、方向键、编辑键与单字符</summary>
        public static bool TryParseKey(string name, out ushort vk)
        {
            vk = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim();
            string low = n.ToLowerInvariant();

            if (NamedKeys.TryGetValue(low, out ushort named)) { vk = named; return true; }

            if (low.Length >= 2 && low[0] == 'f' && int.TryParse(low.Substring(1), out int fn)
                && fn >= 1 && fn <= 24)
            { vk = (ushort)(VK_F1 + fn - 1); return true; }

            // 单字符：字母/数字的虚拟键码就是其大写 ASCII 码
            if (n.Length == 1)
            {
                char c = char.ToUpperInvariant(n[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = (ushort)c; return true; }
                if (PunctuationKeys.TryGetValue(n, out ushort pv)) { vk = pv; return true; }
            }
            return false;
        }

        private static string VkName(ushort vk)
        {
            foreach (var kv in NamedKeys) if (kv.Value == vk) return kv.Key;
            return "0x" + vk.ToString("X2");
        }

        private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = VK_RETURN, ["return"] = VK_RETURN, ["tab"] = VK_TAB,
            ["esc"] = VK_ESCAPE, ["escape"] = VK_ESCAPE, ["space"] = VK_SPACE,
            ["backspace"] = VK_BACK, ["delete"] = VK_DELETE, ["del"] = VK_DELETE,
            ["insert"] = VK_INSERT, ["home"] = VK_HOME, ["end"] = VK_END,
            ["pageup"] = VK_PRIOR, ["pagedown"] = VK_NEXT,
            ["up"] = VK_UP, ["down"] = VK_DOWN, ["left"] = VK_LEFT, ["right"] = VK_RIGHT,
            ["ctrl"] = VK_CONTROL, ["control"] = VK_CONTROL, ["shift"] = VK_SHIFT,
            ["alt"] = VK_MENU, ["win"] = VK_LWIN,
            ["capslock"] = VK_CAPITAL, ["printscreen"] = VK_SNAPSHOT,
        };

        private static readonly Dictionary<string, ushort> PunctuationKeys = new()
        {
            ["-"] = VK_OEM_MINUS, ["="] = VK_OEM_PLUS, ["["] = VK_OEM_4, ["]"] = VK_OEM_6,
            ["\\"] = VK_OEM_5, [";"] = VK_OEM_1, ["'"] = VK_OEM_7, [","] = VK_OEM_COMMA,
            ["."] = VK_OEM_PERIOD, ["/"] = VK_OEM_2,
            ["\u0060"] = VK_OEM_3,          // 反引号（用转义写，源码里直接写会把注释外的引号搞乱）
        };

        // ---------------- P/Invoke ----------------

        private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
            SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

        private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
            VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B,
            VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
            VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
            VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_SNAPSHOT = 0x2C,
            VK_F1 = 0x70, VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_COMMA = 0xBC,
            VK_OEM_MINUS = 0xBD, VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF, VK_OEM_3 = 0xC0,
            VK_OEM_4 = 0xDB, VK_OEM_5 = 0xDC, VK_OEM_6 = 0xDD, VK_OEM_7 = 0xDE;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private static void SendMouse(uint flag)
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = flag;
            if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
                throw new InvalidOperationException(string.Format(
                    "SendInput(鼠标) 被系统拒绝（Win32 错误 {0}）。常见原因：目标窗口以管理员权限运行" +
                    "而本程序不是，或当前输入桌面不是本程序所在的桌面（锁屏/UAC 安全桌面）。",
                    Marshal.GetLastWin32Error()));
        }

        private static void SendKeyInput(ushort vk, bool keyUp)
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vk;
            inputs[0].u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
            if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) != 1)
                throw new InvalidOperationException("SendInput(键盘) 被系统拒绝");
        }

        private static void SendUnicode(char c, bool keyUp)
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = 0;
            inputs[0].u.ki.wScan = c;
            inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
    }
}
