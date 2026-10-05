using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using VisionToolDemo.Vision;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 裸 .raw 数据的参数框（WPF 版，等价旧界面的 RawOpenDialog）。
    ///
    /// 为什么必须有它：裸数据没有文件头，宽高/位深/偏移只能由人给。
    /// 给错不会报错 —— 只会"看起来是图，其实是噪声"（旧代码里记过这个坑：
    /// 同一份 16 位数据按 8 位读，std 从 44 变成 82）。
    /// 所以这里带上「按文件大小自动推断位深」和"期望字节数 vs 实际文件大小"的对照。
    /// </summary>
    public sealed class RawParamsWindow : Window, Ui.IModalResult
    {
        public RawImageParams Result { get; private set; }
        public bool ModalResult { get; private set; }

        private readonly string _path;
        private readonly TextBox _w, _h, _offset, _fullScale;
        private readonly ComboBox _depth, _format;
        private readonly CheckBox _bigEndian, _is24Bgr;
        private readonly TextBlock _info;

        public RawParamsWindow(string filePath, RawImageParams defaults = null)
        {
            _path = filePath;
            var d = defaults ?? new RawImageParams();

            Ui.ApplyTheme(this);
            Title = "裸 RAW 参数 — " + Path.GetFileName(filePath);
            Width = 520;
            SizeToContent = SizeToContent.Height;
            CanResize = false;

            _w = Ui.Input(d.Width.ToString(CultureInfo.InvariantCulture));
            _h = Ui.Input(d.Height.ToString(CultureInfo.InvariantCulture));
            _depth = Ui.Combo(new[] { "8 位", "16 位", "24 位" },
                d.BitDepth == 16 ? 1 : d.BitDepth == 24 ? 2 : 0);
            _format = Ui.Combo(new[] { "灰度", "BayerRG", "BayerGR", "BayerGB", "BayerBG", "RGB", "BGR" },
                Array.IndexOf(new[] { "灰度", "BayerRG", "BayerGR", "BayerGB", "BayerBG", "RGB", "BGR" }, d.Format) is int i && i >= 0 ? i : 0);
            _offset = Ui.Input(d.Offset.ToString(CultureInfo.InvariantCulture));
            _fullScale = Ui.Input(d.FullScale.ToString(CultureInfo.InvariantCulture));
            _bigEndian = Ui.Check("16 位数据是大端（默认小端）", d.BigEndian);
            _is24Bgr = Ui.Check("24 位数据是 BGR 排列（默认，取消则按 RGB）", d.Is24BitBgr);
            _info = Ui.Dim("");

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Ui.Dim("裸 RAW = 没有文件头的像素数据。填错了不会报错，只会得到噪声，请对照相机规格填写。"));
            panel.Children.Add(new Border { Height = 8 });
            panel.Children.Add(Ui.Row("宽度(像素)", _w));
            panel.Children.Add(Ui.Row("高度(像素)", _h));
            panel.Children.Add(Ui.Row("位深", _depth));
            panel.Children.Add(Ui.Row("像素格式", _format));
            panel.Children.Add(Ui.Row("数据偏移(字节)", _offset));
            panel.Children.Add(Ui.Row("16位满量程", _fullScale));
            panel.Children.Add(_bigEndian);
            panel.Children.Add(_is24Bgr);

            var auto = Ui.Btn("按文件大小自动推断位深", AutoDetect);
            var ok = Ui.Btn("确定", Confirm, true);
            var cancel = Ui.Btn("取消", () => { ModalResult = false; Close(); });

            var bar = Ui.Bar(auto, ok, cancel);
            bar.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            panel.Children.Add(bar);
            panel.Children.Add(Ui.Card(_info));
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

            // 每次改参数都刷新"期望字节数 vs 文件大小"，让用户当场发现自己填错了
            foreach (var tb in new[] { _w, _h, _offset, _fullScale }) tb.TextChanged += (_, _) => RefreshInfo();
            _depth.SelectionChanged += (_, _) => RefreshInfo();
            _format.SelectionChanged += (_, _) => RefreshInfo();
            RefreshInfo();
        }

        private int Depth => _depth.SelectedIndex switch { 1 => 16, 2 => 24, _ => 8 };
        private string Format => _format.SelectedItem as string ?? "灰度";

        private RawImageParams Build()
        {
            long.TryParse(_offset.Text.Trim(), out long off);
            int.TryParse(_fullScale.Text.Trim(), out int fs);
            return new RawImageParams
            {
                Width = int.TryParse(_w.Text.Trim(), out int w) ? w : 0,
                Height = int.TryParse(_h.Text.Trim(), out int h) ? h : 0,
                BitDepth = Depth,
                Format = Format,
                Offset = off,
                FullScale = fs > 0 ? fs : 65535,
                BigEndian = _bigEndian.IsChecked == true,
                Is24BitBgr = _is24Bgr.IsChecked == true,
            };
        }

        private void RefreshInfo()
        {
            var p = Build();
            long fileLen = 0;
            try { fileLen = new FileInfo(_path).Length; } catch { }
            long need = p.ExpectedBytes;
            string verdict = need == 0 ? "参数还没填全"
                : fileLen == need - p.Offset ? "✓ 与文件大小完全吻合（最可信）"
                : fileLen - p.Offset >= need ? string.Format(CultureInfo.InvariantCulture,
                    "文件比需要的多 {0} 字节（可能有附加段/对齐，通常没问题）", fileLen - p.Offset - need)
                : string.Format(CultureInfo.InvariantCulture, "⚠ 文件比需要的少 {0} 字节（会读出越界/截断的图像）", need - (fileLen - p.Offset));
            _info.Text = string.Format(CultureInfo.InvariantCulture,
                "文件大小：{0:N0} 字节\n单像素字节：{1}    需要（不含偏移）：{2:N0} 字节\n{3}",
                fileLen, p.BytesPerPixel, need, verdict);
        }

        private void AutoDetect()
        {
            try
            {
                var p = Build();
                if (p.Width <= 0 || p.Height <= 0) { Ui.Warn("先填宽度和高度"); return; }
                bool color = Format is "RGB" or "BGR";
                long len = new FileInfo(_path).Length;
                int depth = RawImageLoader.DetectBitDepth(len, p.Width, p.Height, p.Offset, color);
                _depth.SelectedIndex = depth == 16 ? 1 : depth == 24 ? 2 : 0;
                // 16 位时顺手按实际数据推断满量程（10/12 位对齐成 16 位很常见）
                if (depth == 16)
                {
                    var probe = Build();
                    int fs = RawImageLoader.DetectFullScale16(_path, probe);
                    if (fs > 0) _fullScale.Text = fs.ToString(CultureInfo.InvariantCulture);
                }
                RefreshInfo();
            }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }

        private void Confirm()
        {
            var p = Build();
            if (p.Width <= 0 || p.Height <= 0) { Ui.Warn("宽度和高度必须大于 0"); return; }
            if (p.Offset < 0) { Ui.Warn("偏移不能是负数"); return; }
            Result = p;
            ModalResult = true; Close();
        }
    }
}
