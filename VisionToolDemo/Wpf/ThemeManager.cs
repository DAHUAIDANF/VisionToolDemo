using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 主题管理器：多套界面配色 + 即时切换 + 持久化。
    /// 原理：Styles.xaml 里所有颜色都是 Application.Resources 中的 SolidColorBrush 实例，
    /// 各控件模板用 StaticResource 引用的是同一个实例。因此切换主题时**就地改这些实例的
    /// Color**，全界面所有引用（包括已解析的 StaticResource）会立即跟随变色，无需重建模板。
    /// 持久化：主题名写入 %APPDATA%\VisionToolDemo\theme.json，启动时自动恢复。
    /// </summary>
    public sealed class ThemePalette
    {
        /// <summary>主题显示名</summary>
        public string Name { get; set; } = "";

        /// <summary>颜色表：资源 key → 颜色</summary>
        public Dictionary<string, Color> Colors { get; } = new();

        public ThemePalette(string name) { Name = name; }

        /// <summary>链式设置单个颜色</summary>
        public ThemePalette Set(string key, Color c) { Colors[key] = c; return this; }
    }

    public static class ThemeManager
    {
        /// <summary>所有内置主题（索引 0 = 默认深蓝灰，与老界面一致）</summary>
        public static readonly ThemePalette[] Themes =
        [
            MakeDefault(),
            MakeGreen(),
            MakePurple(),
            MakeWarm(),
            MakeLight(),
        ];

        /// <summary>当前主题索引（默认 0）</summary>
        public static int Current { get; private set; }

        /// <summary>
        /// 页面级合并了 Styles.xaml 的根元素（每页一套独立 brush 实例，与 Application 级不同步）。
        /// 注册后主题切换会同步就地改这些页面字典里的实例，保证页面颜色即时跟随。
        /// </summary>
        private static readonly List<FrameworkElement> _pageRoots = new();

        /// <summary>
        /// 页面注册：把页面根元素加入主题同步列表，并立即把当前主题应用到其合并字典
        /// （懒加载页面创建时调用，避免新页面显示默认配色）。
        /// </summary>
        public static void RegisterPage(FrameworkElement root)
        {
            if (root == null || _pageRoots.Contains(root)) return;
            _pageRoots.Add(root);
            if (Application.Current == null) return;
            try
            {
                foreach (ResourceDictionary m in root.Resources.MergedDictionaries)
                    Repaint(m, Themes[Current]);
            }
            catch (Exception ex) { Log("页面注册同步异常: " + ex.Message); }
        }

        /// <summary>切换到指定主题并持久化；无 WPF 环境（无头/冒烟）时静默跳过</summary>
        public static void Apply(int index)
        {
            if (index < 0 || index >= Themes.Length) return;
            Current = index;
            var t = Themes[index];
            try
            {
                if (Application.Current == null) return;
                Log("开始应用");
                Repaint(Application.Current.Resources, t);
                // 页面级合并的 Styles.xaml 实例是独立 brush，必须一并就地改色，主题才能即时生效
                foreach (var root in _pageRoots)
                {
                    try
                    {
                        foreach (ResourceDictionary m in root.Resources.MergedDictionaries)
                            Repaint(m, t);
                    }
                    catch (Exception ex) { Log("页面同步异常: " + ex.Message); }
                }
                Log("应用完成");
            }
            catch (Exception ex)
            {
                // 主题应用失败不影响程序运行（保持旧配色），但记录原因供排查
                Log("应用异常: " + ex.GetType().Name + " " + ex.Message);
            }
            Save();
        }

        /// <summary>
        /// 递归遍历资源字典（含全部 MergedDictionaries），对每个颜色 key **就地改 SolidColorBrush 实例**。
        /// 关键：ResourceDictionary 的索引器【不查 MergedDictionaries】，直接 Application.Current.Resources[key]
        /// 永远取不到 Styles.xaml 里的实例；而控件 StaticResource 早已一次性绑定到 merged 里的实例，
        /// 只有就地改这个实例，已加载的所有控件才会即时变色（替换键对已绑定控件无效——那是主题不生效的根因）。
        /// </summary>
        private static void Repaint(ResourceDictionary rd, ThemePalette t)
        {
            if (rd == null) return;
            foreach (ResourceDictionary m in rd.MergedDictionaries)
                Repaint(m, t);
            foreach (var kv in t.Colors)
            {
                if (rd[kv.Key] is not SolidColorBrush b) continue;   // 只查本字典直接定义的键
                try
                {
                    // 就地改：已绑定控件（含 StaticResource 与 DynamicResource）全部跟随
                    b.Color = kv.Value;
                    Log($"  {kv.Key}: #{(kv.Value.R << 16) | (kv.Value.G << 8) | kv.Value.B:X6} (就地改)");
                }
                catch (InvalidOperationException)
                {
                    // 个别被冻结（Style Setter 共享时 WPF 会冻结 Freezable）的 brush 无法就地改：
                    // 替换键 + 控件改为 DynamicResource 引用（Styles.xaml 的 Setter 已是动态引用）即跟随
                    rd[kv.Key] = new SolidColorBrush(kv.Value);
                    Log($"  {kv.Key}: #{(kv.Value.R << 16) | (kv.Value.G << 8) | kv.Value.B:X6} (冻结→替换键)");
                }
            }
        }

        /// <summary>诊断日志：主题切换详情写 %APPDATA%\VisionToolDemo\theme.log，实机排查"哪个键没变色"用</summary>
        private static void Log(string line)
        {
            try
            {
                string path = ConfigPath().Replace("theme.json", "theme.log");
                // 首次启动时 %APPDATA%\VisionToolDemo 目录可能尚不存在，先创建避免日志静默丢失
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " 主题[" + Themes[Current].Name + "] " + line + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>启动时恢复上次主题</summary>
        public static void Restore()
        {
            try
            {
                string path = ConfigPath();
                if (File.Exists(path))
                {
                    string name = File.ReadAllText(path).Trim();
                    for (int i = 0; i < Themes.Length; i++)
                        if (Themes[i].Name == name) { Apply(i); return; }
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                string path = ConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Themes[Current].Name);
            }
            catch { }
        }

        /// <summary>配置文件路径：%APPDATA%\VisionToolDemo\theme.json</summary>
        private static string ConfigPath()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(dir, "VisionToolDemo", "theme.json");
        }

        // ==================== 五套主题配色 ====================

        /// <summary>① 默认：深蓝灰（原界面配色）</summary>
        private static ThemePalette MakeDefault() => new ThemePalette("深蓝灰")
            .Set("Bg", Rgb(0x1A, 0x1E, 0x24))
            .Set("PanelBg", Rgb(0x1F, 0x24, 0x2C))
            .Set("CardBg", Rgb(0x33, 0x3C, 0x4B))
            .Set("CardBgHover", Rgb(0x3E, 0x49, 0x59))
            .Set("NavBg", Rgb(0x12, 0x16, 0x1C))
            .Set("Line", Rgb(0x4A, 0x56, 0x66))
            .Set("Fg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("FgDim", Rgb(0xE6, 0xE6, 0xE6))
            .Set("FgFaint", Rgb(0xC0, 0xC0, 0xC0))
            .Set("Accent", Rgb(0x4C, 0x8D, 0xF6))
            .Set("AccentDim", Rgb(0x20, 0x30, 0x4A))
            .Set("Ok", Rgb(0x45, 0xDE, 0xA8))
            .Set("Warn", Rgb(0xFF, 0xC9, 0x4A))
            .Set("Ng", Rgb(0xFF, 0x8A, 0x8A))
            .Set("InputBg", Rgb(0x10, 0x15, 0x1B))
            .Set("SelBg", Rgb(0x40, 0x50, 0x6B))
            .Set("CardSubFg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("OnAccent", Rgb(0xFF, 0xFF, 0xFF));

        /// <summary>② 深墨绿：工业仪表风格</summary>
        private static ThemePalette MakeGreen() => new ThemePalette("深墨绿")
            .Set("Bg", Rgb(0x16, 0x20, 0x1B))
            .Set("PanelBg", Rgb(0x1B, 0x27, 0x20))
            .Set("CardBg", Rgb(0x2C, 0x3A, 0x31))
            .Set("CardBgHover", Rgb(0x36, 0x46, 0x3B))
            .Set("NavBg", Rgb(0x10, 0x19, 0x13))
            .Set("Line", Rgb(0x3F, 0x52, 0x46))
            .Set("Fg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("FgDim", Rgb(0xE6, 0xE6, 0xE6))
            .Set("FgFaint", Rgb(0xC0, 0xC0, 0xC0))
            .Set("Accent", Rgb(0x3F, 0xBF, 0x7F))
            .Set("AccentDim", Rgb(0x1B, 0x33, 0x27))
            .Set("Ok", Rgb(0x45, 0xDE, 0xA8))
            .Set("Warn", Rgb(0xFF, 0xC9, 0x4A))
            .Set("Ng", Rgb(0xFF, 0x8A, 0x8A))
            .Set("InputBg", Rgb(0x0E, 0x17, 0x12))
            .Set("SelBg", Rgb(0x33, 0x50, 0x3F))
            .Set("CardSubFg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("OnAccent", Rgb(0xFF, 0xFF, 0xFF));

        /// <summary>③ 深紫罗兰：夜间护眼</summary>
        private static ThemePalette MakePurple() => new ThemePalette("深紫罗兰")
            .Set("Bg", Rgb(0x1C, 0x19, 0x26))
            .Set("PanelBg", Rgb(0x22, 0x1F, 0x30))
            .Set("CardBg", Rgb(0x38, 0x32, 0x4D))
            .Set("CardBgHover", Rgb(0x42, 0x3B, 0x5A))
            .Set("NavBg", Rgb(0x14, 0x11, 0x19))
            .Set("Line", Rgb(0x4F, 0x46, 0x63))
            .Set("Fg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("FgDim", Rgb(0xE6, 0xE6, 0xE6))
            .Set("FgFaint", Rgb(0xC0, 0xC0, 0xC0))
            .Set("Accent", Rgb(0x9B, 0x7B, 0xF5))
            .Set("AccentDim", Rgb(0x2C, 0x24, 0x45))
            .Set("Ok", Rgb(0x45, 0xDE, 0xA8))
            .Set("Warn", Rgb(0xFF, 0xC9, 0x4A))
            .Set("Ng", Rgb(0xFF, 0x8A, 0x8A))
            .Set("InputBg", Rgb(0x11, 0x0F, 0x1A))
            .Set("SelBg", Rgb(0x4A, 0x3F, 0x6B))
            .Set("CardSubFg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("OnAccent", Rgb(0xFF, 0xFF, 0xFF));

        /// <summary>④ 暖橙红：暖色工作台</summary>
        private static ThemePalette MakeWarm() => new ThemePalette("暖橙红")
            .Set("Bg", Rgb(0x22, 0x1A, 0x16))
            .Set("PanelBg", Rgb(0x28, 0x20, 0x19))
            .Set("CardBg", Rgb(0x44, 0x35, 0x2A))
            .Set("CardBgHover", Rgb(0x50, 0x3F, 0x32))
            .Set("NavBg", Rgb(0x17, 0x12, 0x0E))
            .Set("Line", Rgb(0x5A, 0x4A, 0x3B))
            .Set("Fg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("FgDim", Rgb(0xE6, 0xE6, 0xE6))
            .Set("FgFaint", Rgb(0xC0, 0xC0, 0xC0))
            .Set("Accent", Rgb(0xF0, 0x8A, 0x3C))
            .Set("AccentDim", Rgb(0x3A, 0x2A, 0x1D))
            .Set("Ok", Rgb(0x45, 0xDE, 0xA8))
            .Set("Warn", Rgb(0xFF, 0xC9, 0x4A))
            .Set("Ng", Rgb(0xFF, 0x8A, 0x8A))
            .Set("InputBg", Rgb(0x15, 0x0F, 0x0B))
            .Set("SelBg", Rgb(0x5C, 0x45, 0x30))
            .Set("CardSubFg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("OnAccent", Rgb(0xFF, 0xFF, 0xFF));

        /// <summary>⑤ 亮色浅色：白天办公</summary>
        private static ThemePalette MakeLight() => new ThemePalette("亮色浅色")
            .Set("Bg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("PanelBg", Rgb(0xF5, 0xF5, 0xF5))
            .Set("CardBg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("CardBgHover", Rgb(0xEB, 0xEB, 0xEB))
            .Set("NavBg", Rgb(0xF0, 0xF0, 0xF0))
            .Set("Line", Rgb(0xD0, 0xD0, 0xD0))
            .Set("Fg", Rgb(0x00, 0x00, 0x00))
            .Set("FgDim", Rgb(0x1F, 0x1F, 0x1F))
            .Set("FgFaint", Rgb(0x3A, 0x3A, 0x3A))
            .Set("Accent", Rgb(0x2F, 0x6F, 0xDB))
            .Set("AccentDim", Rgb(0xD5, 0xE2, 0xF8))
            .Set("Ok", Rgb(0x12, 0xA0, 0x6E))
            .Set("Warn", Rgb(0xC9, 0x8A, 0x00))
            .Set("Ng", Rgb(0xD6, 0x45, 0x45))
            .Set("InputBg", Rgb(0xFF, 0xFF, 0xFF))
            .Set("SelBg", Rgb(0xC9, 0xDC, 0xFA))
            .Set("CardSubFg", Rgb(0x00, 0x00, 0x00))
            .Set("OnAccent", Rgb(0xFF, 0xFF, 0xFF));

        /// <summary>按 RGB 构造颜色</summary>
        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    }
}
