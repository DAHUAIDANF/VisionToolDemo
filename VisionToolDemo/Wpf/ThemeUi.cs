using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 顶栏主题色点公共逻辑：5 个主题色点（深蓝灰/深墨绿/深紫罗兰/暖橙红/亮色浅色），
    /// 与设置页/训练页同源，点击即时应用主题并刷新本页色点高亮。
    /// 各页面顶栏统一调用，保证软件整体风格一致。
    /// </summary>
    public static class ThemeUi
    {
        /// <summary>按当前主题刷新色点外观（颜色=各主题强调色，当前主题粗边框高亮，圆形圆角）</summary>
        public static void RefreshDots(params Border[] dots)
        {
            for (int i = 0; i < dots.Length && i < ThemeManager.Themes.Length; i++)
            {
                var t = ThemeManager.Themes[i];
                var dot = dots[i];
                dot.Background = new SolidColorBrush(t.Colors.TryGetValue("Accent", out var c) ? c : Colors.Gray);
                dot.BorderBrush = new SolidColorBrush(Colors.Gray);
                dot.BorderThickness = new Thickness(i == ThemeManager.Current ? 3 : 1);
                dot.CornerRadius = new CornerRadius(12);
                dot.ToolTip = (i == ThemeManager.Current ? "当前主题：" : "点击应用：") + t.Name;
            }
        }

        /// <summary>色点点击：应用主题（持久化+全局重绘）并刷新本页全部色点</summary>
        public static void ApplyFromClick(Border clicked, params Border[] all)
        {
            if (clicked?.Tag is not string tag || !int.TryParse(tag, out int idx)) return;
            ThemeManager.Apply(idx);
            RefreshDots(all);
        }
    }
}
