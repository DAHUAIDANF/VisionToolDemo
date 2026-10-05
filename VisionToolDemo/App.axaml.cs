using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;

namespace VisionToolDemo;

/// <summary>
/// Avalonia 应用主体。
/// 启动流程：Program.Main → AppBuilder → OnFrameworkInitializationCompleted → MainWindow。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        // 全局异常捕获：写 %APPDATA%\VisionToolDemo\crash.log，供界面空白/闪退类问题排查
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogCrash("AppDomain", (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject.ToString());
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                LogCrash("Dispatcher", e.Exception.ToString());
                e.Handled = true; // 界面层异常不崩溃，便于继续观察
            };
        }
        catch { }
        AvaloniaXamlLoader.Load(this);
    }

    private static void LogCrash(string kind, string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisionToolDemo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] {kind}: {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 恢复上次选择的界面主题（存在 %APPDATA%\VisionToolDemo\theme.json）
            Wpf.ThemeManager.Restore();

            // 自动化真实执行（鼠标模拟/打字间隔/失败重试等待）期间让界面保持响应
            VisionToolDemo.Vision.Automation.UiWait.Sleep = ms => Wpf.Ui.SleepResponsive(ms);

            desktop.MainWindow = new Wpf.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
