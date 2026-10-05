using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace VisionToolDemo.Wpf
{
    /// <summary>一套主题的调色板（显示名 + 颜色表）</summary>
    public sealed class ThemePalette
    {
        public string Name { get; set; } = "";
        public Dictionary<string, Color> Colors { get; } = new();

        public ThemePalette(string name) { Name = name; }

        public ThemePalette Set(string key, Color c) { Colors[key] = c; return this; }
    }

    /// <summary>
    /// 主题管理器（Avalonia 版）：多套界面配色 + 即时切换 + 持久化。
    ///
    /// 与 WPF 版的本质区别：Avalonia 的 SolidColorBrush 不可变，不能"就地改实例"，
    /// 因此改为【替换主题资源字典】：5 套配色各是一个 Wpf/Themes/*.axaml 资源字典，
    /// 切换时清空 Application.Resources.MergedDictionaries 并加载新主题字典；
    /// 控件样式与页面里所有颜色都通过 {DynamicResource Key} 引用，字典一换，
    /// 全界面（含已实例化控件）自动跟随，无需重建模板。
    /// 同时切换 Fluent 主题的 RequestedThemeVariant（深色主题=Dark，亮色浅色=Light），
    /// 让 Fluent 默认控件（输入框/下拉框/勾选框等）的底色也随主题走。
    /// 持久化：主题名写入 %APPDATA%\VisionToolDemo\theme.json，启动时自动恢复。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>主题资源字典文件名（Wpf/Themes/*.axaml）</summary>
        private static readonly string[] ThemeFiles =
        [
            "DeepBlueGray",   // 0 深蓝灰（默认）
            "DeepGreen",      // 1 深墨绿
            "DeepPurple",     // 2 深紫罗兰
            "WarmOrange",     // 3 暖橙红
            "Light",          // 4 亮色浅色
        ];

        /// <summary>所有内置主题（索引 0 = 默认深蓝灰）</summary>
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

        /// <summary>主题切换事件：页面/窗口可订阅做额外刷新（代码构建的控件配色重设等）</summary>
        public static event Action<int> ThemeChanged;

        /// <summary>已注册的页面根（兼容旧接口；Avalonia 下页面颜色靠 DynamicResource 自动跟随）</summary>
        private static readonly List<object> _pageRoots = new();

        /// <summary>
        /// 页面注册（兼容 WPF 版接口）：记录页面根元素。
        /// Avalonia 下页面颜色全部经 DynamicResource 引用主题字典，切换时自动跟随，
        /// 无需像 WPF 那样就地改页面级字典。
        /// </summary>
        public static void RegisterPage(object root)
        {
            if (root == null || _pageRoots.Contains(root)) return;
            _pageRoots.Add(root);
        }

        /// <summary>切换到指定主题并持久化；无 Avalonia 环境（无头/冒烟）时静默跳过</summary>
        public static void Apply(int index)
        {
            if (index < 0 || index >= Themes.Length) return;
            Current = index;
            try
            {
                var app = Application.Current;
                if (app == null) return;

                // 1) 切换 Fluent 主题明暗（深色主题=Dark，亮色浅色=Light），标准控件底色跟随
                app.RequestedThemeVariant = index == 4 ? ThemeVariant.Light : ThemeVariant.Dark;

                // 2) 替换 Application 级合并的主题字典：清空 → 加载新主题
                if (app.Resources is ResourceDictionary rd)
                {
                    rd.MergedDictionaries.Clear();
                    // Avalonia 11 的 ResourceDictionary 没有 Source 属性，
                    // 用 AvaloniaXamlLoader 直接加载主题 .axaml 资源字典
                    var dict = Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
                        new Uri("avares://VisionToolDemo/Wpf/Themes/" + ThemeFiles[index] + ".axaml")) as ResourceDictionary;
                    if (dict != null) rd.MergedDictionaries.Add(dict);
                }

                Log("应用完成 -> " + Themes[index].Name);
                ThemeChanged?.Invoke(index);
            }
            catch (Exception ex)
            {
                // 主题应用失败不影响程序运行（保持旧配色），但记录原因供排查
                Log("应用异常: " + ex.GetType().Name + " " + ex.Message);
            }
            Save();
        }

        /// <summary>诊断日志：主题切换详情写 %APPDATA%\VisionToolDemo\theme.log</summary>
        private static void Log(string line)
        {
            try
            {
                string path = ConfigPath().Replace("theme.json", "theme.log");
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

        /// <summary>配置文件路径：%APPDATA%\VisionToolDemo\theme.json（Linux 上为 ~/.config/VisionToolDemo/theme.json）</summary>
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
            .Set("Fg", Rgb(0xF6, 0xF9, 0xFD))
            .Set("FgDim", Rgb(0xCB, 0xD4, 0xE0))
            .Set("FgFaint", Rgb(0xB4, 0xBF, 0xCE))
            .Set("Accent", Rgb(0x4C, 0x8D, 0xF6))
            .Set("AccentDim", Rgb(0x20, 0x30, 0x4A))
            .Set("Ok", Rgb(0x45, 0xDE, 0xA8))
            .Set("Warn", Rgb(0xFF, 0xC9, 0x4A))
            .Set("Ng", Rgb(0xFF, 0x8A, 0x8A))
            .Set("InputBg", Rgb(0x10, 0x15, 0x1B))
            .Set("SelBg", Rgb(0x40, 0x50, 0x6B))
            .Set("CardSubFg", Rgb(0xD3, 0xDB, 0xE6))
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

        /// <summary>⑤ 亮色浅色：白天办公（文字黑色）</summary>
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
