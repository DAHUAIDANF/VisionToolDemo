using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VisionToolDemo.Vision;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 视频抽帧参数框（WPF 版，等价旧界面的 VideoOpenDialog）：
    /// 选起始时间、抽帧间隔、最多帧数；间隔填 0 = 只取一帧。
    /// </summary>
    public sealed class VideoImportWindow : Window, Ui.IModalResult
    {
        public double StartSeconds { get; private set; }
        public bool ModalResult { get; private set; }
        public double IntervalSeconds { get; private set; }
        public int MaxFrames { get; private set; } = 50;

        private readonly TextBox _start, _interval, _max;
        private readonly TextBlock _info;

        public VideoImportWindow(string path)
        {
            Ui.ApplyTheme(this);
            Title = "视频导入 — " + Path.GetFileName(path);
            Width = 560;
            SizeToContent = SizeToContent.Height;
            CanResize = false;

            var info = VideoImporter.Probe(path);
            _start = Ui.Input("0");
            _interval = Ui.Input("0");
            _max = Ui.Input("50");
            _info = Ui.Dim(info.Ok
                ? string.Format(CultureInfo.InvariantCulture,
                    "时长 {0:0.##}s   帧率 {1:0.###}   共 {2} 帧   {3}x{4}",
                    info.Duration, info.Fps, info.FrameCount, info.Width, info.Height)
                : "读不到视频信息：" + info.Error);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Ui.Card(_info, 10));
            panel.Children.Add(Ui.Dim("抽出的帧会存到视频同级的「<视频名>_视频抽帧」目录，" +
                                      "之后可直接对那个目录跑「批量处理」。"));
            panel.Children.Add(new Border { Height = 8 });
            panel.Children.Add(Ui.Row("起始时间(秒)", _start));
            panel.Children.Add(Ui.Row("抽帧间隔(秒)", _interval));
            panel.Children.Add(Ui.Row("最多帧数", _max));

            var one = Ui.Btn("只取一帧", () => { _interval.Text = "0"; _max.Text = "1"; });
            var ok = Ui.Btn("开始抽帧", Confirm, true);
            var cancel = Ui.Btn("取消", () => { ModalResult = false; Close(); });
            var bar = Ui.Bar(one, ok, cancel);

            panel.Children.Add(bar);
            panel.Children.Add(Ui.Dim("间隔填 0（默认）= 只取起始时间那一帧；批量做时域分析（跳动/闪烁/漂移）才需要多帧。"));
            Content = panel;
        }

        private void Confirm()
        {
            if (!double.TryParse(_start.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || s < 0)
            { Ui.Warn("起始时间要填不小于 0 的数字"); return; }
            if (!double.TryParse(_interval.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double itv) || itv < 0)
            { Ui.Warn("抽帧间隔要填不小于 0 的数字（0 = 只取一帧）"); return; }
            if (!int.TryParse(_max.Text.Trim(), out int max) || max < 1)
            { Ui.Warn("最多帧数要是大于 0 的整数"); return; }

            StartSeconds = s;
            IntervalSeconds = itv;
            MaxFrames = Math.Min(100000, max);
            ModalResult = true; Close();
        }
    }
}
