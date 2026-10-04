using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
// 不能 using OpenCvSharp;（Point/Size/Rect 与 System.Windows 冲突，CS0104）
using Mat = OpenCvSharp.Mat;
using Cv2 = OpenCvSharp.Cv2;
using Vec3b = OpenCvSharp.Vec3b;
using ImreadModes = OpenCvSharp.ImreadModes;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;   // AutomationSupport（模板注入判定）在这里
using VisionToolDemo.Vision.Tasks;        // MaskTask 等算子在 Tasks 命名空间

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 视觉流水线页（算子链）：打开图片 → 挑算子加入链 → 调参数 → 跑整条链 → 逐步看结果。
    ///
    /// 与旧 WinForms 版对齐的能力：算子链、逐步耗时与摘要、选中某一步看该步的输出图、
    /// 需要模板的算子可导入模板**或在图上拉框直接裁**(旧界面也是这么干的)。
    /// 界面本身是 WPF：矢量渲染 + Grid 自动布局，任意分辨率与 DPI 下都不挤、不发虚。
    /// </summary>
    public partial class PipelinePage : UserControl, IShellPage
    {
        /// <summary>算子链里的一步</summary>
        private sealed class ChainStep
        {
            public string OpName = "";
            /// <summary>缓存的算子实例：掩膜/卡尺/圆卡尺等状态型算子把外部状态（框选区域、笔刷轨迹、
            /// 测量线、模板）存在实例上；每次执行都新建实例会让这些状态丢失（掩膜设置了等于没设）。
            /// 首次用到时惰性创建，之后一直复用。</summary>
            public IVisionTask Task;
            public int[] Values = Array.Empty<int>();
            public Mat Template;               // 需要模板的算子（可空）
            public Mat Input;                  // 本步输入（= 上一步的输出；第 1 步 = 原图）。只引用不拥有，不 Dispose
            public Mat Output;                 // 上次运行的输出（可空）
            public long Ms;
            public string Summary = "";

            public string Title => OpName;
            public string Sub
            {
                get
                {
                    string t = MatAlive(Template)
                        ? string.Format("模板 {0}x{1}", Template.Cols, Template.Rows) : "";
                    string res = MatAlive(Output)
                        ? string.Format("{0}x{1}", Output.Cols, Output.Rows) : "";
                    string ms = Ms > 0 ? Ms + "ms" : "";
                    string s = string.Join("  ", new[] { t, res, ms }.Where(x => x.Length > 0));
                    if (Summary.Length > 0) s = (s.Length > 0 ? s + "  " : "") + Cut(Summary, 34);
                    return s.Length > 0 ? s : "（还没运行）";
                }
            }
        }

        private readonly List<CatalogItem> _ops = new();
        private readonly List<ChainStep> _chain = new();
        private string _category = "全部";
        private IVisionTask _browserTask;       // 算子库里当前选中的算子（用于"加入链"）
        private int[] _browserValues;
        private ChainStep _editing;             // 正在编辑参数的链步（null = 在编辑浏览器里的算子）
        private Mat _srcMat;

        /// <summary>当前输入图文件名（深度学习相似度"设参考图"的标签用）</summary>
        private string _lastInputName = "";
        private RawImageParams _rawParams;      // 最近一次裸 RAW 参数（同一批 raw 共用，批量也用它）
        private double _viewToMat = 1.0;        // 显示位图 → 原图 的倍率（预览降采样时 > 1）
        private bool _showSource;
        private bool _syncing;
        private bool _draggingSel;
        private bool _maskBrushing;           // 掩膜笔刷（模式3）涂抹中：左键按住拖动，逐点追加进当前笔画
        private bool _paintBrushing;           // 涂抹算子（方式=鼠标）涂抹中：左键按住拖动，逐点追加进当前笔画
        private bool _draggingCaliper;        // 卡尺画线中：选中直线卡尺/边缘对卡尺时，左键拖动 = 画扫描线
        private Point _selStart;              // 本次拖动起点（控件坐标，只在拖动过程中用）
        private Point _caliperStart;          // 卡尺画线起点（Overlay 控件坐标）
        private OpenCvSharp.Point _caliperStartImage;  // 卡尺画线起点（原图像素坐标）
        // —— 几何工具（圆卡尺/线线距离/点线距离/种子类/GrabCut 的图上交互共用一套拖动状态）——
        private bool _geomDragging;           // 几何工具拖动中（画线/圆/框）
        private Point _geomStart;             // 几何拖动起点（Overlay 控件坐标）
        private OpenCvSharp.Point _geomStartImage;     // 几何拖动起点（原图像素坐标）
        private int _llStage;                 // 线线距离：0=正在画第1条线，1=正在画第2条线
        private bool _plWaitPoint;            // 点线距离：线已画好，等待单击测量点
        private bool _gcStage;                // GrabCut：false=画前景框，true=画排除框
        private readonly List<System.Windows.Shapes.Ellipse> _seedDots = new();   // 分水岭/泛洪 种子点显示（Overlay 上的红点集合）
        private int _tpStage;                 // 三点角度：0=等顶点，1=等臂A，2=等臂B（第 3 点后注入）
        private readonly GeometryFit.P2[] _tpPts = new GeometryFit.P2[3];   // 三点角度：进行中暂存的三个点
        private readonly GeometryFit.P2[] _ppPts = new GeometryFit.P2[2];   // 点到点距离：进行中暂存的两个点
        private readonly List<GeometryFit.P2> _fitPts = new();             // 点集拟合（圆/椭圆/直线/圆度/直线度）：进行中已点
        private int _ccStage;                 // 同心度：0=外圆心 1=外圆周 2=内圆心 3=内圆周
        private readonly GeometryFit.P2[] _ccPts = new GeometryFit.P2[4];  // 同心度：进行中暂存的四个点
        private GeometryFit.P2 _ppTemp1A, _ppTemp1B;       // 平行垂直度：进行中暂存的第 1 条线
        private int _ppStage;                 // 点到点距离：0=等第1点，1=等第2点（第 2 点后注入）
        private OpenCvSharp.Point _llTemp1P1, _llTemp1P2;   // 两线夹角：暂存第 1 条线（第 2 条画完一起注入）
        private readonly List<System.Windows.Shapes.Ellipse> _manualDots = new(); // 手动点/线测量 已点位置的临时显示

        // —— 视图缩放 / 平移（滚轮缩放、中键或右键拖动平移）——
        // zoom=1 即"适应窗口"，与历史行为完全一致；pan 是缩放后图像左上角相对 Overlay 原点的偏移。
        private double _viewZoom = 1.0;
        private double _viewPanX, _viewPanY;
        private bool _panning;                 // 中键/右键拖动平移中
        private Point _panStart;               // 平移起点（Overlay 坐标）
        private double _panStartPanX, _panStartPanY;
        private Point _rightDownPos;           // 右键按下位置（Overlay 坐标），区分"右键单击"和"右键拖动平移"
        private bool _rightWasDown;
        /// <summary>
        /// 选区用**归一化坐标**（0~1，相对当前显示的那张图）保存，而不是控件坐标。
        /// 原因：原图与结果图的尺寸/长宽比可能不同，控件坐标存下来的框在切图后会"停在原地"，
        /// 看起来就是"框的位置和鼠标点/图像内容对不上"。归一化之后，
        /// 无论切页签、换图还是窗口缩放，框都跟着图像内容走。
        /// </summary>
        private Rect _selNorm;
        /// <summary>选中算子需要的"图上几何交互"类别（None = 普通 ROI 框选语义）</summary>
        private enum GeoTool { None, Line, Circle, TwoLines, LinePlusPoint, Seeds, GrabCut, ThreePoints, TwoPoints, FitPoints, TwoCircles }

        private bool _hasSel;

        /// <summary>ViewModel：命令与状态（Canvas/鼠标/参数面板等视图交互留在本类）</summary>
        internal readonly ViewModels.PipelineViewModel Vm = new();

        public PipelinePage()
        {
            InitializeComponent();
            DataContext = Vm;
            Vm.AddStepRequested += () => AddStep_Click(null, null);
            Vm.OpenImageRequested += OpenImageInteractive;
            Vm.ClearChainRequested += () => ClearChain_Click(null, null);
            Vm.MoveUpRequested += () => MoveUp_Click(null, null);
            Vm.MoveDownRequested += () => MoveDown_Click(null, null);
            Vm.RemoveStepRequested += () => RemoveStep_Click(null, null);
            Vm.RunChainRequested += () => RunChain_Click(null, null);
            Vm.ResetViewRequested += ResetView;
            Vm.SelectTabRequested += tab => ViewTab(tab);
            foreach (var item in NodeCatalog.All.Where(i => !string.IsNullOrEmpty(i.OpName)))
                _ops.Add(item);
            ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
            ThemeManager.RegisterPage(this);

            // 搜索防抖：逐字重建分类 chips + 100 个算子的列表会卡
            var searchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshOps(); };
            OpSearch.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
            OpSearch.ToolTip = "输入算子名或分类";
            OpList.SelectionChanged += (_, _) => SelectBrowserOperator();
            RefreshOps();

            // 尺寸变化时按归一化选区重画（框跟着图像内容走），不必重转位图。
            // 注意：坐标换算与框的绘制都统一以 Overlay(Canvas) 为原点，所以监听 Overlay 的尺寸变化。
            Overlay.SizeChanged += (_, _) => DrawSelectionFromNorm();
            RefreshChain();

            // ROI 键盘微调：挂在窗口 PreviewKeyDown，只在"已有选区且焦点不在输入控件"时拦截方向键
            Loaded += (_, _) =>
            {
                var win = Window.GetWindow(this);
                if (win != null) win.PreviewKeyDown += Window_PreviewKeyDown;
            };
            Unloaded += (_, _) =>
            {
                var win = Window.GetWindow(this);
                if (win != null) win.PreviewKeyDown -= Window_PreviewKeyDown;
            };
        }

        /// <summary>方向键平移选区；Shift+方向键调大小；Ctrl 加速 ×10。文本框/下拉/按钮里不拦截。</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_hasSel || Selection.Visibility != Visibility.Visible) return;
            if (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Up && e.Key != Key.Down) return;
            if (Keyboard.FocusedElement is TextBox
                || Keyboard.FocusedElement is ComboBox
                || Keyboard.FocusedElement is Button
                || Keyboard.FocusedElement is ListBox) return;

            var mat = CurrentDisplayMat();
            if (mat == null || mat.Empty()) return;

            int step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 10 : 1;
            double dx = step / (double)mat.Cols, dy = step / (double)mat.Rows;
            bool resize = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            double x = _selNorm.X, y = _selNorm.Y, w = _selNorm.Width, h = _selNorm.Height;
            switch (e.Key)
            {
                case Key.Left:
                    if (resize) w = Math.Max(dx, w - dx);
                    else x = Math.Max(0, x - dx);
                    break;
                case Key.Right:
                    if (resize) w = Math.Min(1 - x, w + dx);
                    else x = Math.Min(1 - w, x + dx);
                    break;
                case Key.Up:
                    if (resize) h = Math.Max(dy, h - dy);
                    else y = Math.Max(0, y - dy);
                    break;
                case Key.Down:
                    if (resize) h = Math.Min(1 - y, h + dy);
                    else y = Math.Min(1 - h, y + dy);
                    break;
            }

            _selNorm = new Rect(x, y, w, h);
            DrawSelectionFromNorm();
            var r = SelectionRectOn(mat);
            SetStatus(string.Format("选区微调 → 图像 {0}x{1} @ ({2},{3})   方向键=移动  Shift+方向键=调大小  Ctrl=×10",
                r.Width, r.Height, r.X, r.Y));
            e.Handled = true;
        }

        /// <summary>
        /// 把图上框选区域写入当前算子的 ROI 参数（启用 + X/Y/宽/高，位于参数数组末尾 5 个）。
        /// 旧链参数数组短时先用 PadValues 按最新参数定义补足，避免写错位置。
        /// </summary>
        private void FillRoiFromSelection(ChainStep step)
        {
            if (step?.Task == null) return;
            if (!_hasSel || !MatAlive(_srcMat)) { Warn("先在图上用左键拖一个框"); return; }
            var r = SelectionRectOn(_srcMat);
            if (r.Width < 2 || r.Height < 2) { Warn("选区太小或超出图片"); return; }

            int[] vals = PadValues(step.Task, step.Values);      // 补足到最新参数长度（含 5 个 ROI 参数）
            int n = vals.Length;
            if (n < 5) { Warn("该算子不支持 ROI 参数"); return; }
            vals[n - 5] = 1;                                      // 启用
            vals[n - 4] = r.X;
            vals[n - 3] = r.Y;
            vals[n - 2] = r.Width;
            vals[n - 1] = r.Height;
            step.Values = vals;
            BuildParams(step.Task, step.Values, step);
            SetStatus(string.Format("已把框选区域 ({0},{1} {2}x{3}) 写入该算子的 ROI（框内处理，框外保持原样）",
                r.X, r.Y, r.Width, r.Height));
        }

        /// <summary>参数当前值的人话：<c>区域 = 0（全屏）</c></summary>
        private static string ParamValueLine(TaskParamDesc d, int value)
            => ParamDisplay.NameOf(d) + " = " + ParamDisplay.ValueText(d, value);

        private static string Cut(string s, int n)
        {
            s = (s ?? "").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private void SetStatus(string s) { Vm.StatusText = s ?? ""; (Window.GetWindow(this) as MainWindow)?.SetStatus(s); }

        /// <summary>切到本页时安装顶部工具条：打开图片放在最显眼的位置</summary>
        public void OnShown(MainWindow shell)
        {
            if (shell == null) return;
            var items = new System.Collections.Generic.List<UIElement>();

            Button Btn(string text, Action click, bool primary = false)
            {
                var b = new Button
                {
                    Content = text,
                    Style = (Style)FindResource(primary ? "PrimaryButton" : "FlatButton"),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                b.Click += (_, _) => { try { click(); } catch (Exception ex) { Warn(ex.Message); } };
                items.Add(b);
                return b;
            }

            Btn("打开图片…", () => OpenImageInteractive(), true);
            Btn("打开视频…", () => OpenVideoInteractive());
            Btn("相机取帧…", () => OpenCameraInteractive());
            Btn("实时跟踪…", () =>
            {
                var win = new TrackPreviewWindow { Owner = Window.GetWindow(this) };
                win.Show();
            });
            Btn("批量处理…", () => OpenBatchWindow());
            Btn("运行整条链", () => RunChain_Click(null, null));
            Btn("导出算子里程…", () => ExportChain());
            Btn("导入算子里程…", () => ImportChain());
            shell.SetToolbar(items);

            if (_srcMat == null || _srcMat.Empty())
                SetStatus("视觉页：点「打开图片…」，或把图片文件直接拖进中间区域");
        }

        /// <summary>选文件 → 读图 → 设为当前输入（RAW/视频请暂用旧界面 --经典界面）</summary>
        public void OpenImageInteractive()
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开图片 / 裸 RAW / 相机 RAW",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp;*.gif"
                       + "|裸 RAW 数据|*.raw"
                       + "|相机 RAW 照片|" + Vision.RawCameraLoader.FilterString
                       + "|视频|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.m4v"
                       + "|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            OpenImageFile(dlg.FileName);
        }

        /// <summary>真正加载：所有入口（按钮/拖放/后续加 RAW、视频）都走这里</summary>
        public void OpenImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                Warn("文件不存在：" + path);
                return;
            }

            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (VideoExtensions.Contains(ext)) { OpenVideo(path); return; }

            // 裸 .raw 要先问参数（宽高/位深/偏移）；相机 RAW 自动解码，不用问
            if (ext == ".raw")
            {
                var dlg = new RawParamsWindow(path, _rawParams) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true) return;
                _rawParams = dlg.Result;     // 记住，批量处理同一批 raw 共用
            }

            if (!BatchProcessor.TryLoadImage(path, _rawParams, out Mat mat, out string err)
                || mat == null || mat.Empty())
            {
                Warn("这张图读不出来：" + (err ?? "未知原因") + "\n" + path);
                return;
            }
            SetInputImage(mat, System.IO.Path.GetFileName(path));
        }

        /// <summary>把一张图设为当前输入（统一释放旧图、清空上轮结果）</summary>
        private void SetInputImage(Mat mat, string label)
        {
            _lastInputName = label ?? "";
            foreach (var s in _chain) { s.Output?.Dispose(); s.Output = null; s.Input = null; s.Ms = 0; s.Summary = ""; }
            _srcMat?.Dispose();
            _srcMat = mat;
            _showSource = true;
            TabSource.IsChecked = true;
            _hasSel = false;
            _selNorm = default;
            Selection.Visibility = Visibility.Collapsed;
            ResetGeomToolState();   // 换图收起临时几何/种子点，多步交互（线线/点线/GrabCut）从零开始
            ResetView();            // 换图回到"适应窗口"（切页签/换步不重置，保留当前缩放位置）
            RefreshChain();
            RenderView();
            SetStatus(string.Format("已打开 {0}（{1}x{2}）：双击左侧算子加入链，再点「运行整条链」",
                label, mat.Cols, mat.Rows));
        }

        private static readonly string[] VideoExtensions =
            [".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts"];

        private void OpenVideoInteractive()
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开视频",
                Filter = "视频|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            OpenVideo(dlg.FileName);
        }

        /// <summary>视频抽帧：帧落盘到"<视频名>_视频抽帧"，并载入第一帧作为当前输入</summary>
        private void OpenVideo(string path)
        {
            var opt = new VideoImportWindow(path) { Owner = Window.GetWindow(this) };
            if (opt.ShowDialog() != true) return;

            string dir = VideoImporter.DefaultFrameDir(path);
            var frames = VideoImporter.Grab(path, opt.StartSeconds, opt.IntervalSeconds, opt.MaxFrames,
                opt.IntervalSeconds > 0 ? dir : null, out string err);

            if (frames.Count == 0)
            {
                Warn("视频抽帧失败：" + (err ?? "没取到任何帧"));
                return;
            }

            // 除第一帧外全部释放（这一批只是"落盘供批量处理"，不需要都留在内存里）
            var first = frames[0];
            for (int i = 1; i < frames.Count; i++) frames[i].Dispose();

            SetInputImage(first, System.IO.Path.GetFileName(path));
            SetStatus(frames.Count > 1
                ? string.Format("已从视频抽 {0} 帧（落盘：{1}）；当前输入是第一帧，可对该目录跑「批量处理」做时域分析",
                    frames.Count, dir)
                : string.Format("已取视频 1 帧（{0:0.###}s）", opt.StartSeconds));
        }

        private void OpenCameraInteractive()
        {
            var win = new CameraWindow { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true || win.CapturedMat == null || win.CapturedMat.Empty()) return;
            SetInputImage(win.CapturedMat, "相机取帧");
            SetStatus("已采到相机一帧作为当前输入");
        }

        /// <summary>把当前选区裁成模板，套到"链里选中的那一步"上</summary>
        private void UseSelectionAsTemplate()
        {
            if (_editing == null) { Warn("先在链里选中一个需要模板的算子（例如「模板匹配」）"); return; }
            var task = _editing.Task ??= VisionTaskRegistry.GetTask(_editing.OpName);
            if (task == null || !AutomationSupport.NeedsTemplate(task))
            {
                Warn(_editing.OpName + " 不需要模板；需要模板的是 模板匹配/模板差分/特征匹配/几何定位/形状匹配 这类算子");
                return;
            }
            var source = CurrentDisplayMat();
            if (source == null || source.Empty()) { Warn("先打开一张图片"); return; }
            if (!_hasSel) { Warn("先在图上拉一个框（左键拖动）"); return; }
            var rect = SelectionRectOn(source);
            if (rect.Width < 4 || rect.Height < 4) { Warn("选区太小或超出图片"); return; }
            var patch = new Mat(source, rect).Clone();
            _editing.Template?.Dispose();
            _editing.Template = patch;
            RefreshChain();
            ChainList.SelectedItem = _editing;
            SetStatus(string.Format("已把选区 {0}x{1} 设为「{2}」的模板", patch.Cols, patch.Rows, _editing.OpName));
        }

        // ---------------- 拖放 ----------------

        private void View_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void View_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            if (files.Length > 1) SetStatus(string.Format("拖入了 {0} 个文件，只打开第一个", files.Length));
            OpenImageFile(files[0]);
            e.Handled = true;
        }

        // ================================================================ 算子库

        private void RefreshOps()
        {
            string q = (OpSearch.Text ?? "").Trim().ToLowerInvariant();
            var filtered = _ops.Where(i => q.Length == 0 || i.SearchKey.Contains(q)).ToList();

            OpChips.Items.Clear();
            var cats = new List<string> { "全部" };
            cats.AddRange(NodeCatalog.CategoryOrder.Where(c => filtered.Any(i => i.Category == c)));
            foreach (string c in cats)
            {
                int count = c == "全部" ? filtered.Count : filtered.Count(i => i.Category == c);
                if (count == 0 && c != "全部") continue;
                var chip = new ToggleButton
                {
                    Content = c + " " + count,
                    IsChecked = _category == c,
                    Style = (Style)FindResource("Chip"),
                };
                string cap = c;
                chip.Click += (_, _) => { _category = cap; RefreshOps(); };
                OpChips.Items.Add(chip);
            }

            OpList.ItemsSource = filtered
                .Where(i => _category == "全部" || i.Category == _category)
                .OrderBy(i => i.Category)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .Take(300).ToList();
        }

        private void OpList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddStep_Click(sender, null);

        private void SelectBrowserOperator()
        {
            if (OpList.SelectedItem is not CatalogItem item) return;
            _browserTask = VisionTaskRegistry.GetTask(item.OpName);
            _browserValues = _browserTask == null
                ? Array.Empty<int>()
                : _browserTask.ParamDescriptions.Select(d => d.DefaultValue).ToArray();
            _editing = null;
            OpTitle.Text = _browserTask == null ? item.OpName + "（取不到）" : item.OpName + "（未加入链）";
            BuildParams(_browserTask, _browserValues, null);
            BuildTemplateRow(null, _browserTask);
            BuildModelRow(null, _browserTask);
            BuildTrackRow(null, _browserTask);
            ChainList.SelectedIndex = -1;
        }

        // ================================================================ 算子链

        private void AddStep_Click(object sender, object e)
        {
            if (_browserTask == null || OpList.SelectedItem is not CatalogItem item)
            {
                SetStatus("先在左侧选一个算子");
                return;
            }
            var step = new ChainStep
            {
                OpName = item.OpName,
                Task = _browserTask,             // 复用浏览器里的实例：掩膜/卡尺这类状态型算子把框选/笔迹带进链
                Values = (int[])_browserValues.Clone(),
            };
            // 「裁剪矩形」是个特殊的好用点：把视觉页当前拉的选区直接填进它的 X/Y/宽/高，
            // 于是"在图上选一块 → 裁剪 → 继续处理"三步就齐了，不用手抄坐标，也不依赖 ROI。
            string prefilled = "";
            if (step.OpName == "裁剪矩形" && _hasSel && _srcMat != null && !_srcMat.Empty())
            {
                var sel = SelectionRectOn(_srcMat);
                if (step.Values.Length >= 5)
                {
                    step.Values[0] = 0;                 // 模式 = 固定坐标
                    step.Values[1] = sel.X;
                    step.Values[2] = sel.Y;
                    step.Values[3] = sel.Width;
                    step.Values[4] = sel.Height;
                    prefilled = string.Format("，并已按选区填好 X={0} Y={1} 宽={2} 高={3}（原图坐标）",
                        sel.X, sel.Y, sel.Width, sel.Height);
                }
            }

            _chain.Add(step);
            RefreshChain();
            ChainList.SelectedItem = _chain.Last();
            SetStatus(string.Format("已加入算子链：{0}（共 {1} 步）{2}", step.OpName, _chain.Count, prefilled));
        }

        private void ClearChain_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _chain) { s.Output?.Dispose(); s.Template?.Dispose(); s.Input = null; }
            _chain.Clear();
            _editing = null;
            ResetGeomToolState();
            RefreshChain();
            RenderView();
            SetStatus("算子链已清空");
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);

        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

        private void MoveStep(int delta)
        {
            if (_editing == null) { SetStatus("先在链里选中一步"); return; }
            int i = _chain.IndexOf(_editing);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _chain.Count) return;
            (_chain[i], _chain[j]) = (_chain[j], _chain[i]);
            RefreshChain();
            ChainList.SelectedItem = _editing;
        }

        private void RemoveStep_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) { SetStatus("先在链里选中一步"); return; }
            _editing.Output?.Dispose();
            _editing.Template?.Dispose();
            _chain.Remove(_editing);
            _editing = null;
            ResetGeomToolState();
            RefreshChain();
            RenderView();
        }

        private void RefreshChain()
        {
            int keep = ChainList.SelectedIndex;
            ChainList.ItemsSource = null;
            ChainList.ItemsSource = _chain;
            if (keep >= 0 && keep < _chain.Count) ChainList.SelectedIndex = keep;
        }

        private void ChainList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChainList.SelectedItem is not ChainStep step) return;
            _editing = step;
            var task = step.Task ??= VisionTaskRegistry.GetTask(step.OpName);   // 复用缓存实例（掩膜状态保留）
            OpTitle.Text = string.Format("{0}（链里第 {1} 步）", step.OpName, _chain.IndexOf(step) + 1);
            BuildParams(task, step.Values, step);
            BuildTemplateRow(step, task);
            BuildModelRow(step, task);
            BuildTrackRow(step, task);
            RenderView();
            SetStatus(string.Format("正在编辑链里第 {0} 步：{1}", _chain.IndexOf(step) + 1, step.OpName));
        }

        // ================================================================ 参数

        private void BuildParams(IVisionTask task, int[] values, ChainStep step)
        {
            ParamHost.Children.Clear();
            var defs = task?.ParamDescriptions;
            if (task == null || defs == null || defs.Length == 0)
            {
                ParamHost.Children.Add(new TextBlock
                {
                    Text = "左侧选一个算子（双击加入链），这里显示它的参数。",
                    Style = (Style)FindResource("FaintText"),
                });
                return;
            }

            // 支持算子级 ROI 的算子：先把参数数组补足到"原参数 + 5 个 ROI"，
            // 旧链/模板导入的 Values 可能缺这 5 位，面板才能显示/编辑 ROI 行（未启用=整图处理）。
            int roiTotal = RoiRegion.RoiTotal(defs, task.GetType().Name);
            int[] full = PadValues(task, values, defs.Length + roiTotal);
            if (values == null || full.Length != values.Length)
            {
                values = full;
                if (step != null) step.Values = full;
            }

            // 掩膜算子：参数区顶部先给"怎么设掩膜"的说明和清除按钮，
            // 让框选/笔刷交互可见，而不是只有三个参数数字。
            if (task is MaskTask mm)
                BuildMaskHint(mm, ParamHost);
            if (task is PaintBrushTask pb)
                BuildPaintHint(pb, ParamHost);
            // 几何类算子（卡尺/圆卡尺/线线距离/点线距离/种子/GrabCut）：参数区顶部给"怎么在图上操作"的说明和清除按钮
            if (EditingGeoTool() != GeoTool.None)
                BuildGeomHint(ParamHost);

            // 局部函数：构建一行参数（参数名 + 当前值 + 手柄 + 可能的填充按钮）。
            // 既给算子自己的参数用，也给下面虚拟追加的 ROI 参数行用。
            void AddParamRow(TaskParamDesc def, int index, StackPanel target)
            {
                var wrap = new StackPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    ToolTip = Ui.Tip(ParamDisplay.HelpText(def)),   // 永远有内容（图例 + 范围/默认 + 算子说明）
                };
                // 第一行：参数名 + 取值图例（例如「区域   0=全屏 1=主屏 2=自定」）——
                // 之前只显示 "区域:0"，用户根本不知道 0/1/2 是什么
                wrap.Children.Add(new TextBlock
                {
                    Text = ParamDisplay.LabelText(def),
                    Style = (Style)FindResource("DimText"),
                    TextWrapping = TextWrapping.Wrap,
                });
                // 第二行：当前值的人话（会随手柄/输入框实时更新）
                var valueLine = new TextBlock
                {
                    Text = ParamValueLine(def, values[index]),
                    Style = (Style)FindResource("MonoText"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                wrap.Children.Add(valueLine);

                // 算子级 ROI 参数（"启用ROI 0整图1框选区域"）：参数行下方给"用图上框选填充"按钮，
                // 把图上拉好的框直接写入该算子的 ROI（区域外保持原样）。
                if (def.ParamName.StartsWith("启用ROI") && step != null)
                {
                    var fill = new Button
                    {
                        Content = "← 用图上框选填充 ROI",
                        Style = (Style)FindResource("FlatButton"),
                        Margin = new Thickness(0, 4, 0, 0),
                        Padding = new Thickness(8, 2, 8, 2),
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        ToolTip = Ui.Tip("先在图上用左键拖一个框，再点这里：把框的像素坐标写入该算子 ROI（框内做二值化，框外保持原样）"),
                    };
                    fill.Click += (_, _) => FillRoiFromSelection(step);
                    wrap.Children.Add(fill);
                }

                if (def.Max - def.Min > 400)
                {
                    var box = new TextBox { Text = values[index].ToString(CultureInfo.InvariantCulture) };
                    box.TextChanged += (_, _) =>
                    {
                        if (!int.TryParse(box.Text, out int v)) return;
                        values[index] = Math.Max(def.Min, Math.Min(def.Max, v));
                        valueLine.Text = ParamValueLine(def, values[index]);
                    };
                    wrap.Children.Add(box);
                }
                else
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    var slider = new Slider
                    {
                        Minimum = def.Min, Maximum = def.Max, TickFrequency = 1, IsSnapToTickEnabled = true,
                        Value = Math.Max(def.Min, Math.Min(def.Max, values[index])),
                    };
                    var num = new TextBox { Text = values[index].ToString(CultureInfo.InvariantCulture), Margin = new Thickness(6, 0, 0, 0) };
                    slider.ValueChanged += (_, _) =>
                    {
                        if (_syncing) return;
                        values[index] = (int)Math.Round(slider.Value);
                        _syncing = true; num.Text = values[index].ToString(CultureInfo.InvariantCulture); _syncing = false;
                        valueLine.Text = ParamValueLine(def, values[index]);
                    };
                    num.TextChanged += (_, _) =>
                    {
                        if (_syncing || !int.TryParse(num.Text, out int v)) return;
                        values[index] = Math.Max(def.Min, Math.Min(def.Max, v));
                        _syncing = true; slider.Value = values[index]; _syncing = false;
                        valueLine.Text = ParamValueLine(def, values[index]);
                    };
                    Grid.SetColumn(slider, 0); Grid.SetColumn(num, 1);
                    row.Children.Add(slider); row.Children.Add(num);
                    wrap.Children.Add(row);
                }
                target.Children.Add(wrap);
            }

            // 主参数按分组渲染（ParamGroups.GroupOf：显式 Group 优先，否则按参数名前缀自动归组）：
            // 组标题 + 组内参数块。深度学习算子 12 个参数不再平铺，分成 常规/滑窗识别/ROI 区域 等组，
            // 一眼找到要调的参数。未命中的参数归「常规」。
            StackPanel groupPanel = null;
            string groupName = null;
            for (int i = 0; i < defs.Length; i++)
            {
                string g = ParamGroups.GroupOf(defs[i]);
                if (g != groupName)
                {
                    groupName = g;
                    groupPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                    ParamHost.Children.Add(new TextBlock
                    {
                        Text = g,
                        Style = (Style)FindResource("SectionHeader"),
                        Margin = new Thickness(0, 6, 0, 0),
                    });
                    ParamHost.Children.Add(groupPanel);
                }
                AddParamRow(defs[i], i, groupPanel);
            }

            // 包装型算子级 ROI（滤波/边缘/颜色等"逐像素变换"类）：ParamDescriptions 里没有 ROI 参数，
            // 这里虚拟追加 5 个参数行（启用/X/Y/宽/高），点"用图上框选填充"把框写入；归入「ROI 区域」组。
            if (roiTotal > 0 && !RoiRegion.HasBuiltInRoi(defs))
            {
                var roiGroup = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                ParamHost.Children.Add(new TextBlock
                {
                    Text = "ROI 区域",
                    Style = (Style)FindResource("SectionHeader"),
                    Margin = new Thickness(0, 6, 0, 0),
                });
                ParamHost.Children.Add(roiGroup);
                var roiDefs = RoiRegion.ParamDescs();
                for (int k = 0; k < roiDefs.Length; k++) AddParamRow(roiDefs[k], defs.Length + k, roiGroup);
            }

            // 链步的参数改完要刷新列表上的摘要
            if (step != null)
                foreach (var tb in ParamHost.Children.OfType<StackPanel>())
                    tb.LostFocus += (_, _) => RefreshChain();
        }

        /// <summary>涂抹算子的参数区顶部说明：怎么涂、怎么切坐标方式、怎么清除。</summary>
        private void BuildPaintHint(PaintBrushTask paint, StackPanel host)
        {
            var tip = new TextBlock
            {
                Text = paint.HasStroke
                    ? "已涂 {N} 笔：方式=0 时继续在「原图」上按住左键涂抹；方式=1 时填坐标 X/Y（每次运行涂一个圆）；下方按钮可清除。".Replace("{N}", paint.BrushStrokes.Count.ToString())
                    : "未涂抹：方式=0 时在「原图」上按住左键拖动涂抹（颜色/半径在下面参数调）；方式=1 时填坐标 X/Y 即可。",
                Style = (Style)FindResource("DimText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            };
            host.Children.Add(tip);

            var clear = new Button
            {
                Content = paint.HasStroke ? "清除涂抹（重新涂）" : "（还没有涂抹内容）",
                IsEnabled = paint.HasStroke,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 3, 10, 3),
            };
            clear.Click += (_, _) =>
            {
                paint.ClearStroke();
                BuildParams(paint, _editing != null ? _editing.Values : _browserValues, _editing);
                RunChainSilently();
                SetStatus("涂抹已清除，结果图恢复原图");
            };
            host.Children.Add(clear);
        }

        /// <summary>需要模板的算子：导入模板 / 用图上选区裁模板 / 清除</summary>
        /// <summary>
        /// 深度学习算子的模型行：
        ///   · 深度学习推理：选择 .onnx 模型 + 标签文件（.txt 每行一个类别）；
        ///   · 深度学习相似度：选择特征 .onnx 模型 + 「设当前主图为参考」/「清除参考」。
        /// 与模板行同构：未加入链先提示；状态显示模型路径；失败当场 Warn。
        /// </summary>
        private void BuildModelRow(ChainStep step, IVisionTask task)
        {
            ModelHost.Children.Clear();
            if (task == null || !AutomationSupport.NeedsModel(task)) return;
            bool isSim = task is Vision.Tasks.DeepSimTask;
            var dl = task as Vision.Tasks.DeepLearnTask;
            var sim = task as Vision.Tasks.DeepSimTask;

            var box = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var sp = new StackPanel();
            if (isSim)
            {
                // 深度学习相似度：特征模型 + 参考图
                bool hasModel = sim != null && !string.IsNullOrWhiteSpace(sim.FeatureModelPath);
                sp.Children.Add(new TextBlock
                {
                    Text = hasModel
                        ? string.Format("特征模型：{0}\n参考图：{1}",
                            Path.GetFileName(sim.FeatureModelPath),
                            sim.HasReference ? "已设置（" + (GetRefLabel(sim) ?? "当前图") + "）" : "未设置")
                        : "这个算子需要特征 .onnx 模型 + 一张参考图（还没配）",
                    Style = (Style)FindResource("DimText"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                bool loaded = dl != null && !string.IsNullOrWhiteSpace(dl.ModelPath);
                sp.Children.Add(new TextBlock
                {
                    Text = loaded
                        ? string.Format("模型：{0}\n标签：{1} 类（{2}）",
                            Path.GetFileName(dl.ModelPath), dl.Labels.Count,
                            string.Join("/", dl.Labels.Take(4)) + (dl.Labels.Count > 4 ? "…" : ""))
                        : "这个算子需要 .onnx 模型 + 标签文件（还没加载）",
                    Style = (Style)FindResource("DimText"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            void AddBtn(string text, Action act)
            {
                var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (_, _) =>
                {
                    // 未入链的浏览器预览算子：模型/标签要挂在链里的步骤上，必须提示先加入链
                    if (step == null)
                    {
                        Warn("这个算子还没有加入算子链，模型会没处可挂。\n先在左侧列表双击（或选中后点「加入链」）把它加进去，再加载模型。");
                        return;
                    }
                    act();
                    RefreshChain();
                    ChainList.SelectedItem = step;
                    BuildModelRow(step, task);   // 刷新状态行
                };
                row.Children.Add(b);
            }
            AddBtn(isSim ? "选择特征模型(.onnx)…" : "选择模型(.onnx)…", () =>
            {
                var dlg = new OpenFileDialog { Title = "选择 ONNX 模型", Filter = "ONNX 模型|*.onnx|所有文件|*.*" };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    // 当场验证能加载（构造 InferenceSession 即加载图），失败立刻提示，不拖到运行
                    using (var probe = new InferenceSession(dlg.FileName)) { }
                    if (isSim)
                    {
                        sim.FeatureModelPath = dlg.FileName;
                        sim.UnloadModel();
                    }
                    else
                    {
                        dl.ModelPath = dlg.FileName;
                        dl.UnloadModel();
                    }
                    SetStatus("已加载模型：" + dlg.FileName);
                }
                catch (Exception ex)
                {
                    Warn("这个 .onnx 模型打不开：\n" + ex.Message);
                }
            });
            if (!isSim)
                AddBtn("选择标签(.txt)…", () =>
                {
                    var dlg = new OpenFileDialog { Title = "选择标签文件（每行一个类别）", Filter = "文本|*.txt|所有文件|*.*" };
                    if (dlg.ShowDialog() != true) return;
                    try
                    {
                        // 标签清洗：兼容直接粘贴 coco.yaml / 带序号 / 带引号的常见格式
                        // （"names:" 行、注释、"0: person"、"person"、"'person'"、"- person" 都能正确解析）
                        var lines = File.ReadAllLines(dlg.FileName)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0 && !l.StartsWith("names:") && !l.StartsWith("---") && !l.StartsWith("#"))
                            .Select(l => l.StartsWith("- ") ? l.Substring(2).Trim() : l)
                            .Select(l => (l.Length >= 2 && l[0] == '"' && l[l.Length - 1] == '"')
                                      || (l.Length >= 2 && l[0] == '\'' && l[l.Length - 1] == '\'') ? l.Substring(1, l.Length - 2) : l)
                            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d+\s*[:：]?\s*", ""))
                            .Where(l => l.Length > 0)
                            .ToList();
                        if (lines.Count == 0) { Warn("标签文件里没有内容"); return; }
                        dl.Labels.Clear();
                        dl.Labels.AddRange(lines);
                        SetStatus(string.Format("已加载标签 {0} 类：{1}", lines.Count, string.Join("/", lines.Take(6))));
                    }
                    catch (Exception ex)
                    {
                        Warn("标签文件读不了：\n" + ex.Message);
                    }
                });
            if (isSim)
            {
                // 深度学习相似度专属：设当前主图为参考 / 清除参考
                AddBtn("设当前主图为参考", () =>
                {
                    if (_srcMat == null || _srcMat.Empty()) { Warn("先打开一张图片作为参考图"); return; }
                    if (!sim.SetReferenceFrom(_srcMat, System.IO.Path.GetFileName(_lastInputName)))
                    {
                        Warn("参考图特征提取失败：\n" + simLastErr(sim));
                        return;
                    }
                    SetStatus("已设参考图：相似度算子将对后续输入与它比较");
                });
                AddBtn("清除参考", () =>
                {
                    sim.ClearReference();
                    SetStatus("已清除参考图");
                });
            }
            else if (dl != null && !string.IsNullOrWhiteSpace(dl.ModelPath))
            {
                AddBtn("清除模型", () => { dl.ModelPath = ""; dl.Labels.Clear(); dl.UnloadModel(); SetStatus("已清除深度学习模型"); });
            }

            sp.Children.Add(row);
            box.Child = sp;
            ModelHost.Children.Add(box);
        }

        /// <summary>深度学习相似度：参考图说明（内部字段 _refLabel 取不到就显示"当前图"）</summary>
        private static string GetRefLabel(Vision.Tasks.DeepSimTask sim)
        {
            var f = typeof(Vision.Tasks.DeepSimTask).GetField("_refLabel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return f?.GetValue(sim) as string;
        }

        /// <summary>深度学习相似度：最近一次参考特征提取失败原因（内部字段 _loadError）</summary>
        private static string simLastErr(Vision.Tasks.DeepSimTask sim)
        {
            var f = typeof(Vision.Tasks.DeepSimTask).GetField("_loadError",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (f?.GetValue(sim) as string) ?? "未知原因";
        }

        /// <summary>
        /// 目标跟踪算子的专用行：框选目标（用主图上的选框初始化追踪器）/ 重置 / 状态显示。
        /// 与模板行同构：未加入链先提示；失败当场 Warn。
        /// </summary>
        private void BuildTrackRow(ChainStep step, IVisionTask task)
        {
            TrackHost.Children.Clear();
            if (task == null || task is not Vision.Tasks.TrackTask tr) return;

            var box = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = tr.LastBox != null
                    ? string.Format("目标跟踪：已框选（{0},{1} {2}x{3}），累计 {4} 帧",
                        tr.LastBox.Value.X, tr.LastBox.Value.Y,
                        tr.LastBox.Value.Width, tr.LastBox.Value.Height, tr.FrameCount)
                    : "先在主图上用鼠标拖一个框框住目标，再点「框选目标」开始跟踪",
                Style = (Style)FindResource("DimText"),
                TextWrapping = TextWrapping.Wrap,
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            void AddBtn(string text, Action act)
            {
                var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (_, _) =>
                {
                    if (step == null)
                    {
                        Warn("这个算子还没有加入算子链，目标框会没处可挂。\n先在左侧列表双击（或选中后点「加入链」）把它加进去，再框选目标。");
                        return;
                    }
                    act();
                    RefreshChain();
                    ChainList.SelectedItem = step;
                    BuildTrackRow(step, task);
                };
                row.Children.Add(b);
            }
            AddBtn("框选目标", () =>
            {
                if (!_hasSel || !MatAlive(_srcMat))
                {
                    Warn("先在主图上用鼠标拖一个框框住目标（左键拖动），再点这里");
                    return;
                }
                var rect = SelectionRectOn(_srcMat);
                var type = step.Values != null && step.Values.Length > 0 ? step.Values[0] : 0;
                if (!tr.SetTarget(_srcMat, rect, type))
                {
                    Warn("框选失败：" + trLastHint(tr));
                    return;
                }
                SetStatus("目标跟踪已初始化：后续帧将自动跟踪并画框");
            });
            AddBtn("重置", () =>
            {
                tr.ResetTracking();
                SetStatus("目标跟踪已重置，请重新框选");
            });
            sp.Children.Add(row);
            box.Child = sp;
            TrackHost.Children.Add(box);
        }

        /// <summary>目标跟踪：最近一次失败提示（内部字段 _hint）</summary>
        private static string trLastHint(Vision.Tasks.TrackTask tr)
        {
            var f = typeof(Vision.Tasks.TrackTask).GetField("_hint",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (f?.GetValue(tr) as string) ?? "未知原因";
        }

        private void BuildTemplateRow(ChainStep step, IVisionTask task)
        {
            TemplateHost.Children.Clear();
            if (task == null || !AutomationSupport.NeedsTemplate(task)) return;

            var box = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var sp = new StackPanel();
            var t = step?.Template;
            sp.Children.Add(new TextBlock
            {
                Text = t == null || t.Empty() ? "这个算子需要模板/参考图（还没有）"
                                              : string.Format("模板已就绪：{0}x{1}", t.Cols, t.Rows),
                Style = (Style)FindResource("DimText"),
            });

            // 模板缩略图：光看尺寸数字没法确认"导进来的到底是不是我要找的那个东西"
            if (t != null && !t.Empty())
            {
                var thumb = new Image
                {
                    Source = MatImage.ToThumbnail(t, 180),
                    Stretch = Stretch.Uniform,
                    MaxWidth = 180,
                    MaxHeight = 120,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                var frame = new Border
                {
                    Background = (Brush)FindResource("InputBg"),
                    BorderBrush = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = thumb,
                    ToolTip = Ui.Tip("这就是当前模板图（按比例缩略显示）。\n缩略图与实际模板是同一张图，可直接确认内容对不对。"),
                };
                sp.Children.Add(frame);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            void AddBtn(string text, Action act, Thickness margin)
            {
                var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), Margin = margin };
                b.Click += (_, _) =>
                {
                    // 未加入链的浏览器预览算子：模板要挂在链里的步骤上，这里必须提示先加入链
                    if (step == null)
                    {
                        Warn("这个算子还没有加入算子链，模板会没处可挂。\n先在左侧列表双击（或选中后点「加入链」）把它加进去，再设模板。");
                        return;
                    }
                    act();
                    RefreshChain();
                    ChainList.SelectedItem = step;
                };
                row.Children.Add(b);
            }
            AddBtn("导入模板图…", () =>
            {
                var dlg = new OpenFileDialog { Title = "选择模板图", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*" };
                if (dlg.ShowDialog() != true) return;
                var m = Cv2.ImRead(dlg.FileName, ImreadModes.Color);
                if (m == null || m.Empty()) { Warn("这张图读不出来：" + dlg.FileName); return; }
                step.Template?.Dispose();
                step.Template = m;
                SetStatus("已导入模板：" + dlg.FileName);
            }, new Thickness(0, 0, 6, 0));
            AddBtn("用图上选区当模板", () =>
            {
                var source = CurrentDisplayMat();      // 选区是在这张图上画的，就从它裁
                if (source == null || source.Empty()) { Warn("先打开一张图片"); return; }
                if (!_hasSel) { Warn("先在图上拉一个框（左键拖动）"); return; }
                var rect = SelectionRectOn(source);    // 归一化选区 → 该图像素
                if (rect.Width < 4 || rect.Height < 4) { Warn("选区太小或超出图片"); return; }
                var patch = new Mat(source, rect).Clone();
                step.Template?.Dispose();
                step.Template = patch;
                SetStatus(string.Format("已从图上裁出模板 {0}x{1}", patch.Cols, patch.Rows));
            }, new Thickness(0, 0, 6, 0));
            if (MatAlive(t))
                AddBtn("清除模板", () => { step.Template?.Dispose(); step.Template = null; }, new Thickness(0));

            sp.Children.Add(row);
            box.Child = sp;
            TemplateHost.Children.Add(box);
        }

        private void Warn(string message)
        {
            SetStatus("提示：" + message);
            Ui.Notice(message, "提示");
        }

        // ================================================================ 打开图片 / 运行

        private void BtnOpenImage_Click(object sender, RoutedEventArgs e) => OpenImageInteractive();

        private void RunChain_Click(object sender, RoutedEventArgs e)
        {
            if (!MatAlive(_srcMat)) { Warn("先打开一张图片"); return; }
            if (_chain.Count == 0) { Warn("算子链是空的：先在左侧双击算子加入链"); return; }

            Mat current = _srcMat;
            OpenCvSharp.Rect chainRoi = default;
            bool useChainRoi = false;
            if (ChkRoi.IsChecked == true)
            {
                if (!_hasSel)
                {
                    Warn("勾了“用选区做 ROI”，但还没在图上拉框");
                    return;
                }
                // ROI 作用在**原图**上：把归一化选区换算到当前原图的像素（切过页签也不会错位）。
                // 链级 ROI = “不裁剪图片，只在框内作业”：整条链仍在完整尺寸图上跑，
                // 支持框内处理的算子只对选区计算并把结果贴回原图（框外保持原样）。
                var r = SelectionRectOn(current);
                if (r.Width < 4 || r.Height < 4) { Warn("ROI 选区太小或超出图片"); return; }
                chainRoi = r;
                useChainRoi = true;
                SetStatus(string.Format("ROI（按原图）：只在选区 {0}x{1} @ ({2},{3}) 内作业，框外保持原样", r.Width, r.Height, r.X, r.Y));
            }
            foreach (var s in _chain) { s.Output?.Dispose(); s.Output = null; s.Input = null; s.Ms = 0; s.Summary = ""; }

            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _chain.Count; i++)
            {
                var step = _chain[i];
                var task = step.Task ??= VisionTaskRegistry.GetTask(step.OpName);   // 缓存实例：掩膜/卡尺状态跨次执行保留
                if (task == null)
                {
                    Warn(string.Format("第 {0} 步的算子取不到：{1}", i + 1, step.OpName));
                    break;
                }
                try
                {
                    // 记录本步输入：= 上一步的输出（第 1 步 = 原图）。
                    // 链级 ROI 不裁剪图片，current 始终是完整尺寸图，Input 即完整图（引用别名，不拥有、不 Dispose）。
                    step.Input = current;

                    if (AutomationSupport.NeedsTemplate(task))
                    {
                        if (!MatAlive(step.Template))
                            throw new InvalidOperationException("这个算子需要模板图，但还没有设（右侧可导入或用图上选区裁）");
                        AutomationSupport.ApplyTemplate(task, step.Template);
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // 参数防御：链文件/模板导入后 Values 可能缺失（null/短数组），
                    // 直接 Execute 会 paramValues[0] 越界。统一用默认值补足到 ParamDescriptions 长度
                    //（支持算子级 ROI 的算子再多补 5 个 ROI 参数位，未启用=整图处理，兼容旧链）。
                    int[] stepValues = PadValues(task, step.Values);
                    string roiNote = null;      // ROI 的摘要后缀（框选尺寸）
                    Mat output;
                    if (useChainRoi)
                    {
                        // 链级 ROI（勾选“用选区做ROI”）= 不裁剪图片，只在框内作业：
                        // 支持包装的逐像素/局部算子 → 框内计算后贴回完整图（框外原样）；
                        // 内置 ROI 参数的算子（二值化系列）→ 把链级选区写入其 ROI 参数，由算子自己框内处理；
                        // 其余算子（深度学习/几何测量等不适合局部切割）→ 保持整图处理并注明。
                        // 矩形先与当前输入图求交：链中某步改变尺寸时不会越界。
                        var roiOnThis = chainRoi & new OpenCvSharp.Rect(0, 0, current.Cols, current.Rows);
                        if (roiOnThis.Width < 1 || roiOnThis.Height < 1)
                        {
                            output = task.Execute(current, stepValues);
                            roiNote = "（该步输入尺寸变化，ROI 越界，按整图处理）";
                        }
                        else if (RoiRegion.NeedsWrap(task.ParamDescriptions, task.GetType().Name))
                        {
                            using Mat sub = new Mat(current, roiOnThis);     // ROI 子视图（不拷贝像素）
                            Mat outSub = task.Execute(sub, stepValues);
                            if (outSub == null || outSub.Empty())
                                throw new InvalidOperationException("在框选区域内处理失败（无输出图）");
                            output = current.Clone();                        // 完整尺寸结果：框外保持原样
                            RoiRegion.PasteAny(output, outSub, roiOnThis);   // 通道自适应贴回
                            if (!ReferenceEquals(outSub, sub)) outSub.Dispose();
                            roiNote = $"（框内作业 {roiOnThis.Width}x{roiOnThis.Height}）";
                        }
                        else if (RoiRegion.HasBuiltInRoi(task.ParamDescriptions) && stepValues.Length >= 5)
                        {
                            // 内置 ROI 的算子：把链级选区写入其 ROI 参数（尾 5 位：启用/X/Y/宽/高）
                            stepValues[stepValues.Length - 5] = 1;
                            stepValues[stepValues.Length - 4] = roiOnThis.X;
                            stepValues[stepValues.Length - 3] = roiOnThis.Y;
                            stepValues[stepValues.Length - 2] = roiOnThis.Width;
                            stepValues[stepValues.Length - 1] = roiOnThis.Height;
                            output = task.Execute(current, stepValues);
                            roiNote = $"（框内作业 {roiOnThis.Width}x{roiOnThis.Height}）";
                        }
                        else
                        {
                            output = task.Execute(current, stepValues);
                            roiNote = "（该算子不支持框内作业，按整图处理）";
                        }
                    }
                    else if (RoiRegion.NeedsWrap(task.ParamDescriptions, task.GetType().Name)
                             && RoiRegion.TryGet(current, stepValues, out OpenCvSharp.Rect wrapRoi))
                    {
                        // 算子级 ROI（通用包装）：只在框选区域内做处理，区域外保持原图不变。
                        // 二值化系列已把 ROI 内置进算子（ParamDescriptions 含"启用ROI"），不在这里重复包装。
                        using Mat sub = new Mat(current, wrapRoi);           // ROI 子视图（不拷贝像素）
                        Mat outSub = task.Execute(sub, stepValues);
                        if (outSub == null || outSub.Empty())
                            throw new InvalidOperationException("在框选区域内处理失败（无输出图）");
                        output = current.Clone();
                        RoiRegion.PasteAny(output, outSub, wrapRoi);         // 通道自适应贴回，框外原样
                        if (!ReferenceEquals(outSub, sub)) outSub.Dispose();
                        roiNote = $"（框选 {wrapRoi.Width}x{wrapRoi.Height}）";
                    }
                    else
                    {
                        output = task.Execute(current, stepValues);
                    }
                    sw.Stop();
                    step.Ms = sw.ElapsedMilliseconds;
                    // 防"共享引用"：若算子把输入原样返回（个别算子对无操作分支可能这样做），
                    // 会让两步的 Output 指向同一个 Mat——重跑 foreach 释放一次后另一处字段就悬垂。
                    // 强制成独立对象，链里每个步骤的输出都自持一份。
                    if (output != null && ReferenceEquals(output, current))
                        output = output.Clone();
                    step.Output = output;
                    step.Summary = (task as IResultReporter)?.LastSummary + (roiNote ?? "") ?? "";
                    // 注意：**不要**释放 current —— 它就是上一步的 Output，界面上还要显示它。
                    // 每一步的输出都留在 step.Output 里，下一轮运行或切换图片时统一释放。
                    current = output;
                }
                catch (Exception ex)
                {
                    step.Summary = "失败：" + ex.Message;
                    RefreshChain();
                    Warn(string.Format("第 {0} 步（{1}）执行失败：\n{2}", i + 1, step.OpName, ex.Message));
                    RenderView();
                    return;
                }
                RefreshChain();
                ChainList.SelectedItem = step;
            }
            totalWatch.Stop();

            _showSource = false;
            TabResult.IsChecked = true;
            RenderView();
            var last = _chain.LastOrDefault();
            ImageViewInfo.Text = string.Format("整条链 {0} 步完成，共 {1}ms", _chain.Count, totalWatch.ElapsedMilliseconds);
            SetStatus(string.Format("算子链执行完成：{0} 步 / {1}ms{2}", _chain.Count, totalWatch.ElapsedMilliseconds,
                last != null && last.Summary.Length > 0 ? "  最后一步：" + Cut(last.Summary, 60) : ""));
        }

        private void ViewTab_Click(object sender, RoutedEventArgs e)
            => ViewTab(TabSource.IsChecked == true ? "input" : "result");

        /// <summary>切换 原图/结果 页签（命令入口）</summary>
        private void ViewTab(string tab)
        {
            _showSource = tab == "input";
            RenderView();
        }

        // ================================================================ 显示

        /// <summary>把步骤参数补足到算子声明的长度：短了用默认值补，长了截断，null 重建。
        /// 链文件/模板导入后 Values 缺失是"运行即崩"的主要来源，统一在这里兜底。</summary>
        /// <summary>
        /// 参数防御：链文件/模板导入后 Values 可能缺失（null/短数组），统一用默认值补足。
        /// 支持算子级 ROI 的算子额外补 5 个 ROI 参数位（默认 0=整图处理，与旧链兼容）。
        /// </summary>
        private static int[] PadValues(IVisionTask task, int[] values, int? need = null)
        {
            var desc = task?.ParamDescriptions;
            if (desc == null || desc.Length == 0) return values ?? Array.Empty<int>();
            int len = need ?? (desc.Length + RoiRegion.RoiTotal(desc, task.GetType().Name));
            if (values != null && values.Length == len) return values;
            var r = new int[len];
            for (int i = 0; i < len; i++)
                r[i] = (values != null && i < values.Length)
                    ? values[i]
                    : (i < desc.Length ? desc[i].DefaultValue : RoiRegion.DefaultValues[i - desc.Length]);
            return r;
        }

        /// <summary>Mat 是否仍可用：非 null、未释放、非空。
        /// 步骤的 Input 是上一步 Output 的引用别名，Output 一旦被释放（重跑/切页/清链），
        /// 别的字段还可能指着它；直接 .Empty() 会抛 ObjectDisposedException，统一用这个判断。</summary>
        private static bool MatAlive(Mat m) => m != null && !m.IsDisposed && !m.Empty();

        private Mat CurrentDisplayMat()
        {
            // "原图"页签：选中链中某步时显示**该步的输入**（= 上一步的结果；第 1 步输入 = 原图），
            // 没选中步骤或该步还没有输入时退回真正的原图。
            if (_showSource)
            {
                if (_editing != null && MatAlive(_editing.Input)) return _editing.Input;
                return MatAlive(_srcMat) ? _srcMat : null;
            }
            if (_editing != null && MatAlive(_editing.Output)) return _editing.Output;
            for (int i = _chain.Count - 1; i >= 0; i--)
                if (MatAlive(_chain[i].Output)) return _chain[i].Output;
            return MatAlive(_srcMat) ? _srcMat : null;
        }

        private void RenderView()
        {
            var mat = CurrentDisplayMat();
            // 拿回"显示位图 → 原图"的倍率：预览降采样时它不是 1，坐标换算要用
            View.Source = MatImage.ToBitmapSource(mat, out _viewToMat);
            // 用 MatAlive 判空：mat 可能因悬垂引用已释放（见 CurrentDisplayMat 注释），直接 Empty() 会崩
            EmptyHint.Visibility = MatAlive(mat) ? Visibility.Collapsed : Visibility.Visible;
            if (!MatAlive(mat)) return;
            string what;
            if (_showSource)
                what = _editing != null && MatAlive(_editing.Input)
                    ? "第 " + (_chain.IndexOf(_editing) + 1) + " 步的输入（上一步结果）"
                    : "原图";
            else
                what = _editing != null && MatAlive(_editing.Output)
                    ? "链里第 " + (_chain.IndexOf(_editing) + 1) + " 步的结果"
                    : "结果图";
            // 降采样时把倍率写出来：坐标/框选是按原图算的，用户能一眼确认"看到的是缩过的图"
            string preview = _viewToMat > 1.01
                ? string.Format("   预览已缩放（显示 = 原图 1/{0:0.##}）", _viewToMat)
                : "";
            if (_viewZoom > 1.001)
                preview += string.Format("   视图放大 ×{0:0.##}（滚轮缩放 / 中键·右键拖动 / 重置视图）", _viewZoom);
            if (!IsShowingSource)
                preview += "    ←  这不是原始原图（是该步输入=上一步结果）；几何/ROI 作用于该步输入图";
            // 几何工具临时元素与种子点要跟随当前显示：缩放/平移/切页签后重新定位
            RefreshSeedDots();
            // 显示几何：图像像素、控件尺寸、缩放、上下/左右留白 —— "Y 对不上"时先看这一行
            string geometry = "";
            if (View.Source is System.Windows.Media.Imaging.BitmapSource bmp2 && bmp2.PixelWidth > 0
                && Overlay.ActualWidth > 10)
            {
                double sc = Math.Min(Overlay.ActualWidth / bmp2.PixelWidth, Overlay.ActualHeight / bmp2.PixelHeight);
                double dw = bmp2.PixelWidth * sc, dh = bmp2.PixelHeight * sc;
                geometry = string.Format("   显示 {0:0}x{1:0}（缩放 {2:0.###}，左右留白 {3:0.#}，上下留白 {4:0.#}）",
                    dw, dh, sc, (Overlay.ActualWidth - dw) / 2, (Overlay.ActualHeight - dh) / 2);
            }
            ImageViewInfo.Text = string.Format("{0}  {1}x{2}  {3} 通道{4}{5}",
                what, mat.Cols, mat.Rows, mat.Channels(), geometry, preview);
            DrawSelectionFromNorm();     // 切页签/换图后把框按归一化选区重新画到图上
        }

        /// <summary>
        /// 控件坐标（鼠标位置）→ 原图像素。
        ///
        /// 关键：预览是 <c>Stretch=Uniform</c> 且**大图会被降采样**后再显示，
        /// 所以必须按"实际显示位图"的尺寸算留白与缩放，再乘回"显示位图 → 原图"的倍率。
        /// 之前直接拿 mat.Cols/Rows 当显示尺寸算，降采样一开，
        /// 框选位置、像素值、裁模板就全都和鼠标点对不上（这正是用户看到的那个问题）。
        /// </summary>
        /// <summary>Overlay 坐标 → 归一化图像坐标（0~1）。这是唯一的几何换算入口，别处都调用它。
        /// 注意：换算与框的绘制统一以 Overlay(Canvas) 为原点，避免 View(Image) 与 Overlay 布局原点不一致导致框偏移。
        /// 缩放/平移后：先把 Overlay 坐标逆变换回"适应窗口"坐标，再做留白换算。</summary>
        private bool TryMapToNormalized(Point p, out double nx, out double ny)
        {
            nx = ny = 0;
            if (Overlay.ActualWidth < 10 || Overlay.ActualHeight < 10) return false;
            if (View.Source is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth <= 0) return false;

            // 逆视图变换：Overlay 点 → 适应窗口坐标（zoom=1 时即恒等）
            double qx = (p.X - _viewPanX) / _viewZoom;
            double qy = (p.Y - _viewPanY) / _viewZoom;

            double aw = Overlay.ActualWidth, ah = Overlay.ActualHeight;
            double scale = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);   // 显示倍率
            double dw = bmp.PixelWidth * scale, dh = bmp.PixelHeight * scale;
            double ox = (aw - dw) / 2, oy = (ah - dh) / 2;                        // Uniform 居中的留白

            nx = (qx - ox) / dw;
            ny = (qy - oy) / dh;
            return nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
        }

        /// <summary>归一化 → Overlay 坐标（画选区和反向核对用；与 Canvas.SetLeft/SetTop 同一坐标系，已含缩放/平移）</summary>
        private bool TryUnmapNormalized(double nx, double ny, out Point p)
        {
            p = new Point();
            if (View.Source is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth <= 0) return false;
            double aw = Overlay.ActualWidth, ah = Overlay.ActualHeight;
            double scale = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);
            double dw = bmp.PixelWidth * scale, dh = bmp.PixelHeight * scale;
            double ox = (aw - dw) / 2, oy = (ah - dh) / 2;
            p = new Point(ox + nx * dw, oy + ny * dh);
            // 视图变换：适应窗口坐标 → Overlay 坐标
            p = new Point(p.X * _viewZoom + _viewPanX, p.Y * _viewZoom + _viewPanY);
            return true;
        }

        /// <summary>
        /// 与 TryMapToNormalized 相同的换算，但**不要求点落在图像区内**：落到留白（Uniform 居中的
        /// 上下/左右空白）的点会被 clamp 到图像边界 [0,1]。用于拖框落定，保证非正常尺寸图
        /// （比例差大、留白大）时拖出画面也不会把整框丢掉，生效区域 = 图像内被框住的部分。
        /// </summary>
        private bool TryMapToNormalizedClamped(Point p, out double nx, out double ny)
        {
            nx = ny = 0;
            if (Overlay.ActualWidth < 10 || Overlay.ActualHeight < 10) return false;
            if (View.Source is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth <= 0) return false;

            double qx = (p.X - _viewPanX) / _viewZoom;
            double qy = (p.Y - _viewPanY) / _viewZoom;

            double aw = Overlay.ActualWidth, ah = Overlay.ActualHeight;
            double scale = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);
            double dw = bmp.PixelWidth * scale, dh = bmp.PixelHeight * scale;
            double ox = (aw - dw) / 2, oy = (ah - dh) / 2;

            nx = Math.Clamp((qx - ox) / dw, 0, 1);
            ny = Math.Clamp((qy - oy) / dh, 0, 1);
            return true;
        }

        /// <summary>控件坐标（鼠标位置）→ 指定图像的像素坐标（含降采样倍率换算）</summary>
        private bool TryMapToImage(Point p, Mat mat, out int x, out int y)
        {
            x = y = 0;
            if (!MatAlive(mat)) return false;
            if (!TryMapToNormalized(p, out double nx, out double ny)) return false;
            x = Math.Max(0, Math.Min(mat.Cols - 1, (int)Math.Round(nx * mat.Cols)));
            y = Math.Max(0, Math.Min(mat.Rows - 1, (int)Math.Round(ny * mat.Rows)));
            return true;
        }

        /// <summary>归一化选区 → 指定图像上的像素矩形（ROI 与裁模板都用它，保证与显示一致）</summary>
        private OpenCvSharp.Rect SelectionRectOn(Mat mat)
        {
            if (!MatAlive(mat)) return new OpenCvSharp.Rect();
            int x = Math.Max(0, Math.Min(mat.Cols - 1, (int)Math.Round(_selNorm.X * mat.Cols)));
            int y = Math.Max(0, Math.Min(mat.Rows - 1, (int)Math.Round(_selNorm.Y * mat.Rows)));
            int w = Math.Max(1, Math.Min(mat.Cols - x, (int)Math.Round(_selNorm.Width * mat.Cols)));
            int h = Math.Max(1, Math.Min(mat.Rows - y, (int)Math.Round(_selNorm.Height * mat.Rows)));
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        /// <summary>选区是否可用（已画 且 换算到该图后至少 4x4）</summary>
        private bool HasUsableSelection(Mat mat)
        {
            if (!_hasSel || !MatAlive(mat)) return false;
            var r = SelectionRectOn(mat);
            return r.Width >= 4 && r.Height >= 4;
        }

        /// <summary>按归一化选区把框画出来（切页签/换图/窗口缩放都调用它）</summary>
        private void DrawSelectionFromNorm()
        {
            // 只在"输入视图"上画：结果图尺寸可能只有几十像素，把"归一化坐标的框"画在上面会严重误导。
            // 判断用 _showSource（而不是 IsShowingSource）：中间步骤的输入视图显示上一步结果，同样允许画。
            if (!_hasSel || !_showSource)
            {
                Selection.Visibility = Visibility.Collapsed;
                CaliperLine.Visibility = Visibility.Collapsed;
                return;
            }
            if (!TryUnmapNormalized(_selNorm.X, _selNorm.Y, out Point p1)
                || !TryUnmapNormalized(_selNorm.X + _selNorm.Width, _selNorm.Y + _selNorm.Height, out Point p2))
            {
                Selection.Visibility = Visibility.Collapsed;
                return;
            }
            Canvas.SetLeft(Selection, p1.X);
            Canvas.SetTop(Selection, p1.Y);
            Selection.Width = Math.Max(0, p2.X - p1.X);
            Selection.Height = Math.Max(0, p2.Y - p1.Y);
            Selection.Visibility = Selection.Width > 1 && Selection.Height > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void View_MouseMove(object sender, MouseEventArgs e)
        {
            if (_panning)
            {
                Point p = e.GetPosition(Overlay);
                _viewPanX = _panStartPanX + (p.X - _panStart.X);
                _viewPanY = _panStartPanY + (p.Y - _panStart.Y);
                ApplyViewTransform();
                e.Handled = true;
                return;
            }
            var mat = CurrentDisplayMat();
            if (!TryMapToImage(e.GetPosition(Overlay), mat, out int x, out int y))
            {
                PixelInfo.Text = "";
                if (_draggingSel) UpdateSelection(e.GetPosition(Overlay));
                return;
            }

            string info = "";
            try
            {
                if (mat.Channels() == 3)
                {
                    Vec3b c = mat.At<Vec3b>(y, x);
                    info = string.Format("B{0} G{1} R{2}", c.Item0, c.Item1, c.Item2);
                }
                else if (mat.Channels() == 1) info = "gray " + mat.At<byte>(y, x);
            }
            catch { }
            PixelInfo.Text = string.Format("x: {0}  y: {1}   {2}", x, y, info);

            if (_maskBrushing)
            {
                // 笔刷涂抹：把当前鼠标点换算到原图像素后追加进当前笔画（跳过与上一点重复的采样）
                var mm = CurrentDisplayMat();
                if (TryMapToImage(e.GetPosition(Overlay), mm, out int px, out int py))
                    EditingMask()?.ContinueStroke(new OpenCvSharp.Point(px, py));   // 同：追加图像像素点
                e.Handled = true;
                return;
            }

            if (_paintBrushing)
            {
                // 涂抹算子（鼠标方式）：同样换算到原图像素后追加进当前笔画
                var pm = CurrentDisplayMat();
                if (TryMapToImage(e.GetPosition(Overlay), pm, out int ppx, out int ppy))
                    EditingPaint()?.ContinueStroke(new OpenCvSharp.Point(ppx, ppy));
                e.Handled = true;
                return;
            }

            if (_geomDragging)
            {
                var cur = e.GetPosition(Overlay);
                switch (EditingGeoTool())
                {
                    case GeoTool.Line:
                        CaliperLine.X2 = cur.X; CaliperLine.Y2 = cur.Y;
                        break;
                    case GeoTool.Circle:
                        ShowGeomCircle(_geomStart, cur);
                        break;
                    case GeoTool.TwoLines:
                        CaliperLine.X2 = cur.X; CaliperLine.Y2 = cur.Y;
                        break;
                    case GeoTool.LinePlusPoint:
                        if (!_plWaitPoint) { CaliperLine.X2 = cur.X; CaliperLine.Y2 = cur.Y; }
                        break;
                    case GeoTool.GrabCut:
                        var gcRect = _gcStage ? GcExclude : Selection;
                        double gx = Math.Min(_geomStart.X, cur.X), gy = Math.Min(_geomStart.Y, cur.Y);
                        Canvas.SetLeft(gcRect, gx); Canvas.SetTop(gcRect, gy);
                        gcRect.Width = Math.Abs(cur.X - _geomStart.X);
                        gcRect.Height = Math.Abs(cur.Y - _geomStart.Y);
                        break;
                }
                if (TryMapToImage(cur, mat, out int cx, out int cy))
                    SetStatus(string.Format("拖到 图像({0},{1})   起点 图像({2},{3})",
                        cx, cy, _geomStartImage.X, _geomStartImage.Y));
                e.Handled = true;
                return;
            }

            if (_draggingSel)
            {
                UpdateSelection(e.GetPosition(Overlay));
                e.Handled = true;
            }

            if (_draggingCaliper)
            {
                // 卡尺画线跟随：实时拉长临时线（黄），松手才注入
                var cur = e.GetPosition(Overlay);
                CaliperLine.X2 = cur.X; CaliperLine.Y2 = cur.Y;
                if (TryMapToImage(cur, mat, out int cx, out int cy))
                    SetStatus(string.Format("卡尺画线：拖到 图像({0},{1})   起点 图像({2},{3})",
                        cx, cy, _caliperStartImage.X, _caliperStartImage.Y));
                return;
            }

            if (_draggingSel)
            {
                UpdateSelection(e.GetPosition(Overlay));
                // 拖动时实时报"当前落脚点 → 图像像素"。与状态栏里选区左上角的坐标对照，
                // 就能立刻判断差的是留白（上下/左右）、显示倍率，还是别的环节
                if (TryMapToImage(e.GetPosition(Overlay), mat, out int dx2, out int dy2))
                    SetStatus(string.Format("拖到 图像({0},{1})   起点换算 图像({2},{3})",
                        dx2, dy2, _dragStartImageX, _dragStartImageY));
            }
        }

        private int _dragStartImageX;
        private int _dragStartImageY;

        private void View_MouseLeave(object sender, MouseEventArgs e)
        {
            PixelInfo.Text = "";
        }

        // ================================================================ 掩膜设置（框选矩形/圆形 / 笔刷自由涂抹）

        /// <summary>当前正在编辑的掩膜算子实例：链里选中一步 → 用该步缓存实例；否则用左侧浏览器里选中的算子。
        /// 返回 null 表示当前不是掩膜算子（其它算子的左键框选语义不变）。</summary>
        private MaskTask EditingMask()
        {
            if (_editing?.Task is MaskTask m) return m;
            return _browserTask as MaskTask;
        }

        /// <summary>当前正在编辑的涂抹算子：链里选中一步 → 用该步缓存实例；否则用左侧浏览器里选中的算子。
        /// 返回 null 表示当前不是涂抹算子。</summary>
        private PaintBrushTask EditingPaint()
        {
            if (_editing?.Task is PaintBrushTask pb) return pb;
            return _browserTask as PaintBrushTask;
        }

        /// <summary>当前涂抹算子的「涂抹方式」：0=鼠标 1=坐标。从正在编辑的那组参数值里读。</summary>
        private int EditingPaintMode(PaintBrushTask paint)
        {
            int[] values = _editing != null ? _editing.Values : _browserValues;
            if (paint == null || values == null || values.Length < 7) return 0;
            return Math.Clamp(values[6], 0, 1);
        }

        /// <summary>当前掩膜算子的「作用模式」：1=矩形框选 2=内切圆框选 3=笔刷自由涂抹。
        /// 从正在编辑的那组参数值里读（链里步骤用 step.Values，浏览器预览用 _browserValues）。</summary>
        private int EditingMaskMode(MaskTask mask)
        {
            int[] values = _editing != null ? _editing.Values : _browserValues;
            if (mask == null || values == null || values.Length == 0) return 1;
            return Math.Clamp(values[0], 1, 3);
        }

        /// <summary>当前正在编辑的卡尺类算子（直线卡尺/边缘对卡尺）：链里选中一步 → 用该步缓存实例；
        /// 否则用左侧浏览器里选中的算子。返回 null 表示当前不是卡尺算子（左键仍是框选语义）。</summary>
        private ILineCaliper EditingCaliper()
        {
            if (_editing?.Task is ILineCaliper c) return c;
            return _browserTask as ILineCaliper;
        }

        /// <summary>当前选中算子需要的图上几何交互类别（链里选中一步用该步，否则用浏览器预览）：
        /// 直线/边缘卡尺=画线；圆卡尺=画圆；线线距离=两条线；点线距离=线+点；分水岭/泛洪=点种子；GrabCut=两次框选。</summary>
        private GeoTool EditingGeoTool()
        {
            object t = (_editing?.Task as object) ?? _browserTask;
            if (t is ILineCaliper) return GeoTool.Line;
            if (t is CircleCaliperTask) return GeoTool.Circle;
            if (t is LineLineDistanceTask || t is Angle2LinesTask) return GeoTool.TwoLines;   // 线线距离/两线夹角都画两条线
            if (t is PointLineDistanceTask) return GeoTool.LinePlusPoint;
            if (t is WatershedTask || t is FloodFillSegmentTask) return GeoTool.Seeds;
            if (t is GrabCutTask) return GeoTool.GrabCut;
            if (t is Angle3PointTask) return GeoTool.ThreePoints;   // 三点角度：单击顶点+两臂端点
            if (t is PointToPointTask) return GeoTool.TwoPoints;    // 点到点距离：单击两个点
            if (t is ParallelPerpTask) return GeoTool.TwoLines;     // 平行垂直度：画两条线判定平行/垂直
            if (t is CircleFitTask || t is EllipseFitTask || t is LineFitTask
                || t is RoundnessTask || t is StraightnessTask) return GeoTool.FitPoints;  // 点集拟合：单击加点（圆≥3/椭圆≥5/线≥2）
            if (t is ConcentricityTask) return GeoTool.TwoCircles;  // 同心度：外圆(圆心+圆周)→内圆(圆心+圆周)
            return GeoTool.None;
        }

        /// <summary>点集拟合类算子凑齐多少点才算可拟合（圆≥3、椭圆≥5、直线≥2）</summary>
        private int FitMinPoints()
        {
            var t = (_editing?.Task as object) ?? _browserTask;
            if (t is EllipseFitTask) return 5;
            if (t is CircleFitTask || t is RoundnessTask) return 3;
            return 2;   // LineFitTask / StraightnessTask
        }

        /// <summary>点集拟合类算子：手动点注入</summary>
        private void SetFitManualPoints()
        {
            var t = (_editing?.Task as object) ?? _browserTask;
            if (t is CircleFitTask cf) cf.SetManualPoints(_fitPts);
            else if (t is EllipseFitTask ef) ef.SetManualPoints(_fitPts);
            else if (t is LineFitTask lf) lf.SetManualPoints(_fitPts);
            else if (t is RoundnessTask rt) rt.SetManualPoints(_fitPts);
            else if (t is StraightnessTask st) st.SetManualPoints(_fitPts);
        }

        /// <summary>显示圆卡尺的临时圆（起点=圆心、当前点=圆周）；起终点都是 Overlay 控件坐标</summary>
        private void ShowGeomCircle(Point center, Point edge)
        {
            double rr = Math.Sqrt((edge.X - center.X) * (edge.X - center.X)
                               + (edge.Y - center.Y) * (edge.Y - center.Y));
            Canvas.SetLeft(GeomCircle, center.X - rr);
            Canvas.SetTop(GeomCircle, center.Y - rr);
            GeomCircle.Width = Math.Max(2, rr * 2);
            GeomCircle.Height = Math.Max(2, rr * 2);
            GeomCircle.Visibility = Visibility.Visible;
        }

        /// <summary>当前选中算子是否是"种子点类"（分水岭/泛洪）：用于取 Seeds 列表显示</summary>
        private System.Collections.Generic.List<OpenCvSharp.Point> EditingSeeds()
        {
            if (_editing?.Task is WatershedTask ws) return ws.Seeds;
            if (_editing?.Task is FloodFillSegmentTask ff) return ff.Seeds;
            if (_browserTask is WatershedTask wb) return wb.Seeds;
            if (_browserTask is FloodFillSegmentTask fb) return fb.Seeds;
            return null;
        }

        /// <summary>掩膜算子参数面板顶部：说明怎么设掩膜 + 「清除掩膜」按钮。
        /// 用户先看到的必须是"怎么把掩膜画出来"，而不是一堆数字。</summary>
        private void BuildMaskHint(MaskTask mask, StackPanel host)
        {
            var tip = new TextBlock
            {
                Text = mask.HasAny
                    ? "掩膜已设置：模式 1/2 在「原图」上左键拖框（重新拖 = 换区域）；模式 3 按住左键涂抹；下方按钮可清除。"
                    : "未设置掩膜：在「原图」上左键拖框设置矩形/内切圆（模式 1/2），或按住左键自由涂抹（模式 3）。",
                Style = (Style)FindResource("DimText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            };
            host.Children.Add(tip);

            var clear = new Button
            {
                Content = mask.HasAny ? "清除掩膜（重新画）" : "（还没有掩膜内容）",
                IsEnabled = mask.HasAny,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 3, 10, 3),
            };
            clear.Click += (_, _) =>
            {
                mask.ClearMask();                    // 清掉框选与笔刷
                BuildParams(mask, _editing != null ? _editing.Values : _browserValues, _editing);
                RunChainSilently();                  // 清掉后立即重跑，结果图恢复原样
                SetStatus("掩膜已清除，结果图恢复原图");
            };
            host.Children.Add(clear);
        }

        /// <summary>把当前种子列表画到 Overlay 上（图像坐标 → 显示坐标，跟随缩放/平移）。
        /// 每次加种子、切步骤、换图、清空后调用，保证图上看到的点与算子里的状态一致。</summary>
        private void RefreshSeedDots()
        {
            ClearSeedDots();
            var seeds = EditingSeeds();
            var mat = CurrentDisplayMat();
            if (seeds == null || !MatAlive(mat)) return;
            foreach (var sd in seeds)
            {
                if (TryUnmapNormalized(sd.X / (double)Math.Max(1, mat.Cols), sd.Y / (double)Math.Max(1, mat.Rows), out Point ov))
                {
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 12, Height = 12,
                        Fill = System.Windows.Media.Brushes.Red,
                        Stroke = System.Windows.Media.Brushes.White,
                        StrokeThickness = 1.5,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(dot, ov.X - 6);
                    Canvas.SetTop(dot, ov.Y - 6);
                    Overlay.Children.Add(dot);
                    _seedDots.Add(dot);
                }
            }
        }

        private void ClearSeedDots()
        {
            foreach (var d in _seedDots) Overlay.Children.Remove(d);
            _seedDots.Clear();
        }

        /// <summary>几何工具状态复位：切步骤/换图/清链时调用，收起临时图形、重置多步交互（线线/点线/GrabCut/手动点）</summary>
        private void ResetGeomToolState()
        {
            _geomDragging = false;
            _draggingCaliper = false;
            _llStage = 0;
            _plWaitPoint = false;
            _gcStage = false;
            _tpStage = 0;
            _ppStage = 0;
            _ccStage = 0;
            _fitPts.Clear();
            CaliperLine.Visibility = Visibility.Collapsed;
            GeomCircle.Visibility = Visibility.Collapsed;
            GcExclude.Visibility = Visibility.Collapsed;
            ClearSeedDots();
            ClearManualDots();
        }

        /// <summary>手动点测量（三点角度/点到点距离）：把刚点的位置画成红点（图/输入图像素坐标 → 显示坐标）</summary>
        private void AddManualDot(int ix, int iy, Mat mat)
        {
            if (!MatAlive(mat) || mat.Cols < 1 || mat.Rows < 1) return;
            if (!TryUnmapNormalized(ix / (double)mat.Cols, iy / (double)mat.Rows, out Point ov)) return;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 12, Height = 12,
                Fill = System.Windows.Media.Brushes.Red,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, ov.X - 6);
            Canvas.SetTop(dot, ov.Y - 6);
            Overlay.Children.Add(dot);
            _manualDots.Add(dot);
        }

        private void ClearManualDots()
        {
            foreach (var d in _manualDots) Overlay.Children.Remove(d);
            _manualDots.Clear();
        }

        /// <summary>几何类算子参数面板顶部：说明"怎么在图上操作" + 「清除几何」按钮。
        /// 卡尺/圆卡尺/线线距离/点线距离/种子/GrabCut 的几何都只能靠图上画出来，光有数字用户没法设。</summary>
        private void BuildGeomHint(StackPanel host)
        {
            var t = (_editing?.Task as object) ?? _browserTask;
            string tipTxt, doneTxt; bool hasGeom;
            switch (EditingGeoTool())
            {
                case GeoTool.Circle:
                    var cc = (CircleCaliperTask)t;
                    tipTxt = "未设置：在「原图」上按住左键拖动，起点=圆心、松手处=圆周（半径=拖动距离）。";
                    doneTxt = string.Format("圆心 ({0},{1})  半径 {2}px", cc.Center.X, cc.Center.Y, cc.Radius);
                    hasGeom = cc.HasCircle;
                    break;
                case GeoTool.TwoLines:
                    // 线线距离 / 两线夹角 / 平行垂直度 都画两条线，但状态与清除方式不同，按类型分派
                    if (t is LineLineDistanceTask ld2)
                    {
                        tipTxt = ld2.HasLine1
                            ? "第 1 条线已画好：再按住左键拖动画第 2 条线，完成后自动计算两条线距离。"
                            : "未设置：按住左键拖动画第 1 条线 → 再拖动画第 2 条线。";
                        doneTxt = ld2.HasLine1 && ld2.HasLine2 ? "两条线已就位" : ld2.HasLine1 ? "已画第 1 条线" : "（还没有线）";
                        hasGeom = ld2.HasLine1 || ld2.HasLine2;
                    }
                    else if (t is Angle2LinesTask a2)
                    {
                        tipTxt = a2.HasManualLines
                            ? "两条线已画好：显示两线夹角。重新拖动 = 改线位置/角度。"
                            : "未设置：按住左键拖动画第 1 条线 → 再拖动画第 2 条线，完成后自动计算夹角。";
                        doneTxt = a2.HasManualLines ? "两条线已就位（夹角已算）" : "（还没有线）";
                        hasGeom = a2.HasManualLines;
                    }
                    else if (t is ParallelPerpTask pp2)
                    {
                        tipTxt = pp2.HasManualLines
                            ? "两条线已画好：判定平行/垂直。重新拖动 = 改线位置/角度。"
                            : "未设置：按住左键拖动画第 1 条线 → 再拖动画第 2 条线，完成后判定平行/垂直。";
                        doneTxt = pp2.HasManualLines ? "两条线已就位（判定已出）" : "（还没有线）";
                        hasGeom = pp2.HasManualLines;
                    }
                    else   // 兜底：保持旧行为，不会崩
                    {
                        var ld = (LineLineDistanceTask)t;
                        tipTxt = ld.HasLine1
                            ? "第 1 条线已画好：再按住左键拖动画第 2 条线，完成后自动计算两条线距离。"
                            : "未设置：按住左键拖动画第 1 条线 → 再拖动画第 2 条线。";
                        doneTxt = ld.HasLine1 && ld.HasLine2 ? "两条线已就位" : ld.HasLine1 ? "已画第 1 条线" : "（还没有线）";
                        hasGeom = ld.HasLine1 || ld.HasLine2;
                    }
                    break;
                case GeoTool.LinePlusPoint:
                    var pd = (PointLineDistanceTask)t;
                    tipTxt = pd.HasLine
                        ? "测量线已画好：单击图上一点作为测量点（距离=点到直线垂距）。"
                        : "未设置：按住左键拖动画测量线 → 再单击一点作为测量点。";
                    doneTxt = pd.HasLine && pd.HasPoint ? "线 + 测量点已就位" : pd.HasLine ? "已画线，等测量点" : "（还没有几何）";
                    hasGeom = pd.HasLine || pd.HasPoint;
                    break;
                case GeoTool.Seeds:
                    var seeds = EditingSeeds();
                    int n = seeds?.Count ?? 0;
                    tipTxt = "未设置：单击「原图」上每个目标内部加一个种子点（可连续加多个）。";
                    doneTxt = n > 0 ? "已加 " + n + " 个种子点（单击可继续加）" : "（还没有种子）";
                    hasGeom = n > 0;
                    break;
                case GeoTool.GrabCut:
                    var gc = (GrabCutTask)t;
                    tipTxt = gc.InitRect.Width > 0
                        ? "前景框已设：可再拖一次画排除框（可选），或直接看抠图结果。"
                        : "未设置：按住左键拖一个框框住前景目标（第一次拖动）→ 再拖一次画排除框（可选）。";
                    doneTxt = gc.InitRect.Width > 0
                        ? string.Format("前景框 {0}x{1} @ ({2},{3})", gc.InitRect.Width, gc.InitRect.Height, gc.InitRect.X, gc.InitRect.Y)
                        : "（还没有框）";
                    hasGeom = gc.InitRect.Width > 0;
                    break;
                case GeoTool.ThreePoints:
                    var a3 = (Angle3PointTask)t;
                    tipTxt = "未设置：依次单击「顶点 → 第1个臂端点 → 第2个臂端点」（自动取点对不上时用手动）。";
                    doneTxt = a3.HasManualPoints
                        ? string.Format("三点已指定：顶点({0:F0},{1:F0})", a3.ManualPoints[0].X, a3.ManualPoints[0].Y)
                        : "（还没有手动点；重点 3 次即可）";
                    hasGeom = a3.HasManualPoints;
                    break;
                case GeoTool.TwoPoints:
                    var pp = (PointToPointTask)t;
                    tipTxt = "未设置：依次单击两个测量点（自动取点对不上时用手动）。";
                    doneTxt = pp.HasManualPoints
                        ? string.Format("两点已指定：({0:F0},{1:F0})→({2:F0},{3:F0})", pp.ManualPoints[0].X, pp.ManualPoints[0].Y, pp.ManualPoints[1].X, pp.ManualPoints[1].Y)
                        : "（还没有手动点；重点 2 次即可）";
                    hasGeom = pp.HasManualPoints;
                    break;
                case GeoTool.FitPoints:
                    int needPts = FitMinPoints();
                    if (t is CircleFitTask cf) { tipTxt = "未设置：单击加测量点（≥3），拟合圆（自动选最大轮廓对不上时用手动）。"; doneTxt = cf.HasManualPoints ? $"已手动拟合圆（{cf.ManualPoints.Count} 点）" : "（还没有手动点）"; hasGeom = cf.HasManualPoints; }
                    else if (t is EllipseFitTask ef) { tipTxt = "未设置：单击加测量点（≥5），拟合椭圆。"; doneTxt = ef.HasManualPoints ? $"已手动拟合椭圆（{ef.ManualPoints.Count} 点）" : "（还没有手动点）"; hasGeom = ef.HasManualPoints; }
                    else if (t is LineFitTask lf) { tipTxt = "未设置：单击加测量点（≥2），拟合直线。"; doneTxt = lf.HasManualPoints ? $"已手动拟合直线（{lf.ManualPoints.Count} 点）" : "（还没有手动点）"; hasGeom = lf.HasManualPoints; }
                    else if (t is RoundnessTask rt) { tipTxt = "未设置：单击加测量点（≥3），评估圆度。"; doneTxt = rt.HasManualPoints ? $"已手动取点评估圆度（{rt.ManualPoints.Count} 点）" : "（还没有手动点）"; hasGeom = rt.HasManualPoints; }
                    else { var st = (StraightnessTask)t; tipTxt = "未设置：单击加测量点（≥2），评估直线度。"; doneTxt = st.HasManualPoints ? $"已手动取点评估直线度（{st.ManualPoints.Count} 点）" : "（还没有手动点）"; hasGeom = st.HasManualPoints; }
                    tipTxt = "单击加测量点（至少 " + needPts + " 点，可继续加更准；自动取样对不上时用手动）。";
                    break;
                case GeoTool.TwoCircles:
                    var ccx = (ConcentricityTask)t;
                    tipTxt = "未设置：依次单击「外圆圆心 → 外圆圆周 → 内圆圆心 → 内圆圆周」（自动找内外圆对不上时用手动）。";
                    doneTxt = ccx.HasManualCircles
                        ? string.Format("两圆已指定：外R {0:F0}px 内R {1:F0}px", ccx.ManualOuter.R, ccx.ManualInner.R)
                        : "（还没有手动圆；点 4 次即可）";
                    hasGeom = ccx.HasManualCircles;
                    break;
                default:
                    var cl = (ILineCaliper)t;
                    tipTxt = "未设置卡尺：在「原图」上按住左键拖一条扫描线（起点→终点），软件沿该线提取灰度剖面找边缘。";
                    doneTxt = cl.HasCaliper ? "扫描线已设置（重新拖 = 改位置/角度/长度）" : "（还没有卡尺）";
                    hasGeom = cl.HasCaliper;
                    break;
            }
            host.Children.Add(new TextBlock
            {
                Text = (hasGeom ? doneTxt + "\n" : "") + (hasGeom ? "重新在图上操作即可改；下方按钮可清除。" : tipTxt),
                Style = (Style)FindResource("DimText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });

            var clear = new Button
            {
                Content = hasGeom ? "清除几何（重新画）" : "（还没有几何）",
                IsEnabled = hasGeom,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 3, 10, 3),
            };
            clear.Click += (_, _) =>
            {
                switch (EditingGeoTool())
                {
                    case GeoTool.Circle: ((CircleCaliperTask)t).ClearCircle(); break;
                    case GeoTool.TwoLines:
                        if (t is LineLineDistanceTask lld) lld.ClearGeometry();
                        else if (t is Angle2LinesTask a2c) a2c.ClearManualLines();
                        else if (t is ParallelPerpTask ppc) ppc.ClearManualLines();
                        break;
                    case GeoTool.LinePlusPoint: ((PointLineDistanceTask)t).ClearGeometry(); break;
                    case GeoTool.Seeds:
                        if (t is WatershedTask w) w.ClearSeeds();
                        if (t is FloodFillSegmentTask f) f.ClearSeeds();
                        RefreshSeedDots();
                        break;
                    case GeoTool.GrabCut:
                        var g = (GrabCutTask)t;
                        g.InitRect = new OpenCvSharp.Rect(); g.ExcludeRect = new OpenCvSharp.Rect();
                        break;
                    case GeoTool.ThreePoints: ((Angle3PointTask)t).ClearManualPoints(); break;
                    case GeoTool.TwoPoints: ((PointToPointTask)t).ClearManualPoints(); break;
                    case GeoTool.FitPoints:
                        if (t is CircleFitTask c1) c1.ClearManualPoints();
                        else if (t is EllipseFitTask e1) e1.ClearManualPoints();
                        else if (t is LineFitTask l1) l1.ClearManualPoints();
                        else if (t is RoundnessTask r1) r1.ClearManualPoints();
                        else if (t is StraightnessTask s1) s1.ClearManualPoints();
                        break;
                    case GeoTool.TwoCircles: ((ConcentricityTask)t).ClearManualCircles(); break;
                    default: ((ILineCaliper)t).ClearCaliper(); break;
                }
                ResetGeomToolState();
                BuildParams(_editing?.Task ?? _browserTask, _editing != null ? _editing.Values : _browserValues, _editing);
                RunChainSilently();
                SetStatus("几何已清除，结果图恢复原图");
            };
            host.Children.Add(clear);
        }

        /// <summary>静默重跑整条链（不弹警告）：掩膜框选/笔刷/清除后用来刷新结果图。
        /// 链为空或没图时静默返回；失败原因写进状态栏而不是弹窗，避免涂抹时连环弹窗。</summary>
        private void RunChainSilently()
        {
            if (_srcMat == null || _srcMat.Empty() || _chain.Count == 0) return;
            try { RunChain_Click(null, null); }
            catch (Exception ex) { SetStatus("重跑失败: " + ex.Message); }
        }

        // ================================================================ 视图缩放 / 平移

        /// <summary>把当前 zoom/pan 应用到图像控件（先缩放再平移，缩放中心=左上角）</summary>
        private void ApplyViewTransform()
        {
            var g = new System.Windows.Media.TransformGroup();
            g.Children.Add(new System.Windows.Media.ScaleTransform(_viewZoom, _viewZoom));
            g.Children.Add(new System.Windows.Media.TranslateTransform(_viewPanX, _viewPanY));
            View.RenderTransform = g;
            DrawSelectionFromNorm();   // 框必须跟着图像一起动
        }

        private void ResetView()
        {
            _viewZoom = 1.0;
            _viewPanX = _viewPanY = 0;
            _panning = false;
            ApplyViewTransform();
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e) => ResetView();

        private void View_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 以鼠标位置为锚点缩放：缩放前后鼠标下的图像点保持不动
            Point p = e.GetPosition(Overlay);
            double k = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double nz = Math.Clamp(_viewZoom * k, 0.2, 20.0);
            if (Math.Abs(nz - _viewZoom) < 1e-9) return;
            double ratio = nz / _viewZoom;
            _viewPanX = p.X - (p.X - _viewPanX) * ratio;
            _viewPanY = p.Y - (p.Y - _viewPanY) * ratio;
            _viewZoom = nz;
            ApplyViewTransform();
            e.Handled = true;
        }

        private void View_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 中键 / 右键：开始平移（左键是框选，走 MouseLeftButtonDown，不在这里处理）
            if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right)
            {
                if (e.ChangedButton == MouseButton.Right)
                {
                    _rightWasDown = true;
                    _rightDownPos = e.GetPosition(Overlay);
                }
                BeginPan(e.GetPosition(Overlay));
                e.Handled = true;   // 消费事件，阻止继续冒泡到父级容器
            }
        }

        private void View_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                EndPan();
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                // 区分"右键单击"和"右键拖动平移"：按下→抬起基本没移动 = 单击 → 弹"是否保存图像"；
                // 移动超过阈值 = 平移，只结束平移不弹窗（用户要求：结果图上右击弹窗选择是否保存图片）
                bool wasClick = _rightWasDown
                    && (e.GetPosition(Overlay) - _rightDownPos).Length < 5;
                _rightWasDown = false;
                EndPan();
                if (wasClick) PromptSaveResultImage();
                e.Handled = true;
            }
        }

        private void BeginPan(Point p)
        {
            _panning = true;
            _panStart = p;
            _panStartPanX = _viewPanX;
            _panStartPanY = _viewPanY;
            Mouse.Capture(View);
        }

        private void EndPan()
        {
            if (!_panning) return;
            _panning = false;
            View.ReleaseMouseCapture();
        }

        /// <summary>右键单击图像：弹窗选择是否保存当前图像（原图或当前步骤结果图）</summary>
        private void PromptSaveResultImage()
        {
            var mat = CurrentDisplayMat();
            if (!MatAlive(mat))
            {
                Ui.Notice("当前没有可保存的图像。", "提示");
                return;
            }

            bool isSource = ReferenceEquals(mat, _srcMat);
            bool ok = Ui.Confirm(
                string.Format("是否保存当前图像？\n（当前显示：{0}）",
                    isSource ? "原图" : "结果图 / 当前步骤图像"),
                "保存图像");
            if (!ok) return;

            var dlg = new SaveFileDialog
            {
                Title = "保存图像",
                FileName = string.Format("图像_{0:yyyyMMdd_HHmmss}.png", DateTime.Now),
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
                DefaultExt = ".png",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                Cv2.ImWrite(dlg.FileName, mat);
                Ui.Notice("已保存：" + dlg.FileName, "保存成功");
            }
            catch (Exception ex)
            {
                Ui.Error("保存失败：" + ex.Message);
            }
        }

        /// <summary>当前显示的是不是"原图"（不是则说明在放某一步的结果/小图）</summary>
        private bool IsShowingSource => ReferenceEquals(CurrentDisplayMat(), _srcMat);

        private void View_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // ROI/模板/几何框只在"输入视图"（原图 或 选中步的输入=上一步结果）上画：
            // 像 OCR 这类节点的结果可能只有几十像素（示例里是 63x43），被 Uniform 放大铺满显示区，
            // 在它上面拉框等于用"小图的相对位置"去套真实尺寸的原图 —— 位置必然对不上。
            // 所以显示结果图时先切回输入视图并说清楚；输入视图下（含链中间步的"该步输入"）允许直接画，
            // 坐标按当前显示图换算后注入算子（卡尺/ROI 作用于该步输入，语义正确）。
            // 注意不能用 IsShowingSource 判断：选中链里第 2 步及以后时，"输入视图"显示的是上一步结果
            // （不是原图 _srcMat），IsShowingSource 恒为 false，会把每一次点击都吞掉 —— 这就是
            // "画线算子放第一个能画、下移到中间就画不了"的原因。
            if (!_showSource)
            {
                _showSource = true;
                TabSource.IsChecked = true;
                RenderView();
                SetStatus("已切回「输入」视图：ROI/模板/几何请在图上操作（本次点击先切回，再点一次即可）");
                e.Handled = true;
                return;
            }

            var maskTask = EditingMask();
            if (maskTask != null && EditingMaskMode(maskTask) == 3)
            {
                // 笔刷模式（作用模式=3）：按下即落第一笔，拖动期间由 MouseMove 持续追加。
                // 坐标必须换算到原图像素（当前已保证显示的是原图），否则涂的位置和看到的不一致。
                var mat0 = CurrentDisplayMat();
                if (TryMapToImage(e.GetPosition(Overlay), mat0, out int mx, out int my))
                {
                    _maskBrushing = true;
                    maskTask.BeginStroke(new OpenCvSharp.Point(mx, my));   // 掩膜轨迹是图像像素坐标，用 OpenCV 的 Point
                    View.CaptureMouse();
                    SetStatus(string.Format("掩膜笔刷：涂抹中（线宽 {0}px），松手结束笔画并刷新结果",
                        maskTask.BrushWidth));
                }
                e.Handled = true;
                return;
            }

            var paintTask = EditingPaint();
            if (paintTask != null && EditingPaintMode(paintTask) == 0)
            {
                // 涂抹算子（方式=鼠标）：按下落第一笔，拖动期间由 MouseMove 持续追加，
                // 松手后整链重跑把涂抹颜色画到结果图。坐标换算到原图像素。
                var pmat = CurrentDisplayMat();
                if (TryMapToImage(e.GetPosition(Overlay), pmat, out int px0, out int py0))
                {
                    _paintBrushing = true;
                    paintTask.BeginStroke(new OpenCvSharp.Point(px0, py0));
                    View.CaptureMouse();
                    SetStatus(string.Format("涂抹：正在涂（颜色按参数面板，半径 {0}px），松手结束并刷新结果",
                        paintTask.BrushWidth / 2));
                }
                e.Handled = true;
                return;
            }

            // —— 几何工具统一分流：直线/边缘卡尺=画线，圆卡尺=画圆，线线距离=两条线，
            //    点线距离=线+测量点，分水岭/泛洪=点种子，GrabCut=两次框选。
            //    位置/角度/长度/半径/种子都只能靠图上操作注入，光有数字用户没法设。
            var geoTool = EditingGeoTool();
            if (geoTool != GeoTool.None)
            {
                // 未入链拦截：几何点/线/圆只能作用在"已在算子链里的步骤"上。
                // _editing != null = 正在编辑链步（必在链里）；否则要确认浏览器算子实例
                // 已在链中（AddStep 复用 _browserTask 实例入链，用 ReferenceEquals 判定）。
                // 只在算子库选中、还没加入链就在图上画 → 几何无处安放，提示先入链。
                bool geomInChain = _editing != null
                    || (_browserTask != null && _chain.Any(s => ReferenceEquals(s.Task, _browserTask)));
                if (!geomInChain)
                {
                    Warn("「" + (_browserTask?.TaskName ?? "该几何算子") +
                        "」还没加入算子链：请先在左侧点「加入链」（或双击算子）后再画点/线/圆");
                    e.Handled = true;
                    return;
                }
                _geomDragging = true;
                _geomStart = e.GetPosition(Overlay);
                if (TryMapToImage(_geomStart, CurrentDisplayMat(), out int gix, out int giy))
                    _geomStartImage = new OpenCvSharp.Point(gix, giy);
                else _geomStartImage = new OpenCvSharp.Point(-1, -1);
                View.CaptureMouse();
                switch (geoTool)
                {
                    case GeoTool.Line:
                        _draggingCaliper = true;
                        _caliperStart = _geomStart;
                        _caliperStartImage = _geomStartImage;
                        CaliperLine.X1 = _geomStart.X; CaliperLine.Y1 = _geomStart.Y;
                        CaliperLine.X2 = _geomStart.X; CaliperLine.Y2 = _geomStart.Y;
                        CaliperLine.Visibility = Visibility.Visible;
                        SetStatus("卡尺画线：按住拖动到目标终点，松手即完成扫描线（起点已锁定）");
                        break;
                    case GeoTool.Circle:
                        ShowGeomCircle(_geomStart, _geomStart);
                        SetStatus("圆卡尺：按住拖动，起点=圆心、松手处=圆周（半径=拖动距离）");
                        break;
                    case GeoTool.TwoLines:
                        CaliperLine.X1 = _geomStart.X; CaliperLine.Y1 = _geomStart.Y;
                        CaliperLine.X2 = _geomStart.X; CaliperLine.Y2 = _geomStart.Y;
                        CaliperLine.Visibility = Visibility.Visible;
                        SetStatus(_llStage == 0
                            ? "线线距离：正在画第 1 条线（拖动到终点松开）"
                            : "线线距离：正在画第 2 条线（拖动到终点松开）");
                        break;
                    case GeoTool.LinePlusPoint:
                        if (_plWaitPoint)
                        {
                            // 线已画好：这一次按下是单击选测量点（MouseUp 判位移）
                            SetStatus("点线距离：单击图中一个点作为测量点（松手即确定）");
                        }
                        else
                        {
                            CaliperLine.X1 = _geomStart.X; CaliperLine.Y1 = _geomStart.Y;
                            CaliperLine.X2 = _geomStart.X; CaliperLine.Y2 = _geomStart.Y;
                            CaliperLine.Visibility = Visibility.Visible;
                            SetStatus("点线距离：正在画测量线（拖动到终点松开）");
                        }
                        break;
                    case GeoTool.Seeds:
                        // 单击加种子：MouseUp 判位移决定是"单击"还是"误拖"；这里只锁定起点
                        SetStatus("分水岭/泛洪：单击图上一点加一个种子点（可连续加多个，参数面板可清空）");
                        break;
                    case GeoTool.ThreePoints:
                        SetStatus(_tpStage == 0
                            ? "三点角度：单击角的顶点"
                            : _tpStage == 1 ? "三点角度：已点顶点，单击第 1 个臂的端点" : "三点角度：已点 2 点，单击第 2 个臂的端点（完成）");
                        break;
                    case GeoTool.TwoPoints:
                        SetStatus(_ppStage == 0
                            ? "点到点距离：单击第 1 个测量点"
                            : "点到点距离：已点第 1 点，单击第 2 个测量点（完成）");
                        break;
                    case GeoTool.FitPoints:
                        SetStatus(string.Format("点集拟合：单击加测量点（至少 {0} 点，已点 {1}；每加一点自动重拟合）",
                            FitMinPoints(), _fitPts.Count));
                        break;
                    case GeoTool.TwoCircles:
                        SetStatus(_ccStage == 0 ? "同心度：单击外圆的圆心"
                            : _ccStage == 1 ? "同心度：已点外圆圆心，单击外圆圆周上一点（半径=距离）"
                            : _ccStage == 2 ? "同心度：外圆已定，单击内圆的圆心"
                            : "同心度：已点内圆圆心，单击内圆圆周上一点（完成）");
                        break;
                    case GeoTool.GrabCut:
                        var gcRect = _gcStage ? GcExclude : Selection;
                        gcRect.Visibility = Visibility.Visible;
                        Canvas.SetLeft(gcRect, _geomStart.X);
                        Canvas.SetTop(gcRect, _geomStart.Y);
                        gcRect.Width = 0; gcRect.Height = 0;
                        SetStatus(_gcStage
                            ? "GrabCut：正在画排除框（第二次拖动，松手后执行）"
                            : "GrabCut：正在画前景框（第一次拖动，松手后再拖一次画排除框，可选）");
                        break;
                }
                e.Handled = true;
                return;
            }

            _draggingSel = true;
            _selStart = e.GetPosition(Overlay);
            var startMat = CurrentDisplayMat();
            if (TryMapToImage(_selStart, startMat, out int sx0, out int sy0))
            {
                _dragStartImageX = sx0;
                _dragStartImageY = sy0;
            }
            else { _dragStartImageX = _dragStartImageY = -1; }
            View.CaptureMouse();
            Selection.Visibility = Visibility.Visible;
            UpdateSelection(_selStart);
            e.Handled = true;
        }

        private void View_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_maskBrushing)
            {
                // 笔刷结束：松开即完成这条笔画，整链重跑一次把涂黑效果画到结果图
                _maskBrushing = false;
                View.ReleaseMouseCapture();
                var msk = EditingMask();
                if (msk != null)
                {
                    SetStatus(string.Format("掩膜笔刷：已画 {0} 条笔画，正在刷新结果…", msk.BrushStrokes.Count));
                    RunChainSilently();
                }
                e.Handled = true;
                return;
            }

            if (_paintBrushing)
            {
                // 涂抹结束：松开即完成这条笔画，整链重跑一次把涂抹颜色画到结果图
                _paintBrushing = false;
                View.ReleaseMouseCapture();
                var pt = EditingPaint();
                if (pt != null)
                {
                    SetStatus(string.Format("涂抹：已涂 {0} 笔，正在刷新结果…", pt.BrushStrokes.Count));
                    RunChainSilently();
                }
                e.Handled = true;
                return;
            }

            if (_geomDragging)
            {
                // 几何工具松手：按当前算子类型把"这次画出来的几何"注入并静默重跑
                _geomDragging = false;
                View.ReleaseMouseCapture();
                e.Handled = true;
                var cur = e.GetPosition(Overlay);
                var matG = CurrentDisplayMat();
                bool moved = (cur.X - _geomStart.X) * (cur.X - _geomStart.X)
                           + (cur.Y - _geomStart.Y) * (cur.Y - _geomStart.Y) > 12;   // >~3.5px 才算拖动/单击位移
                bool gotStart = TryMapToImage(_geomStart, matG, out int gsx, out int gsy);
                bool gotEnd = TryMapToImage(cur, matG, out int gex, out int gey);
                switch (EditingGeoTool())
                {
                    case GeoTool.Line:
                        CaliperLine.Visibility = Visibility.Collapsed;
                        var cal = EditingCaliper();
                        if (cal != null && gotStart && gotEnd && (gsx != gex || gsy != gey))
                        {
                            cal.SetCaliper(new OpenCvSharp.Point(gsx, gsy), new OpenCvSharp.Point(gex, gey));
                            double len = Math.Sqrt((gex - gsx) * (gex - gsx) + (gey - gsy) * (gey - gsy));
                            double deg = Math.Atan2(gey - gsy, gex - gsx) * 180 / Math.PI;
                            SetStatus(string.Format(
                                "卡尺扫描线：({0},{1})→({2},{3})  长 {4:0}px  方向 {5:0}°（已注入，正在刷新结果…）",
                                gsx, gsy, gex, gey, len, deg));
                            RunChainSilently();
                        }
                        else if (cal != null) SetStatus("卡尺画线太短，未生效（再拖长一点）");
                        break;

                    case GeoTool.Circle:
                        GeomCircle.Visibility = Visibility.Collapsed;
                        var cc = _editing?.Task as CircleCaliperTask ?? _browserTask as CircleCaliperTask;
                        if (cc != null && gotStart && gotEnd)
                        {
                            double rr = Math.Sqrt((gex - gsx) * (gex - gsx) + (gey - gsy) * (gey - gsy));
                            if (rr >= 2)
                            {
                                cc.SetCircle(new OpenCvSharp.Point(gsx, gsy), (int)Math.Round(rr));
                                SetStatus(string.Format(
                                    "圆卡尺：圆心 ({0},{1})  半径 {2:0}px（已注入，正在刷新结果…）", gsx, gsy, rr));
                                RunChainSilently();
                            }
                            else SetStatus("圆卡尺半径太短，未生效（拖大一点）");
                        }
                        break;

                    case GeoTool.TwoLines:
                        CaliperLine.Visibility = Visibility.Collapsed;
                        var ld = _editing?.Task as LineLineDistanceTask ?? _browserTask as LineLineDistanceTask;
                        var al = _editing?.Task as Angle2LinesTask ?? _browserTask as Angle2LinesTask;
                        var ppT = _editing?.Task as ParallelPerpTask ?? _browserTask as ParallelPerpTask;
                        if ((ld != null || al != null || ppT != null) && gotStart && gotEnd && (gsx != gex || gsy != gey))
                        {
                            if (_llStage == 0)
                            {
                                if (ld != null)
                                {
                                    ld.SetLine1(new OpenCvSharp.Point(gsx, gsy), new OpenCvSharp.Point(gex, gey));
                                    SetStatus("第 1 条线已画好，请再拖动画第 2 条线（完成后自动计算两条线距离）");
                                }
                                else if (ppT != null)
                                {
                                    _ppTemp1A = new GeometryFit.P2(gsx, gsy);
                                    _ppTemp1B = new GeometryFit.P2(gex, gey);
                                    SetStatus("平行垂直度：第 1 条线已画好，请再拖动画第 2 条线");
                                }
                                else
                                {
                                    _llTemp1P1 = new OpenCvSharp.Point(gsx, gsy);
                                    _llTemp1P2 = new OpenCvSharp.Point(gex, gey);
                                    SetStatus("两线夹角：第 1 条线已画好，请再拖动画第 2 条线");
                                }
                                _llStage = 1;
                            }
                            else
                            {
                                _llStage = 0;
                                if (ld != null)
                                {
                                    ld.SetLine2(new OpenCvSharp.Point(gsx, gsy), new OpenCvSharp.Point(gex, gey));
                                    SetStatus("两条线已就位，正在计算线线距离…");
                                }
                                else if (ppT != null)
                                {
                                    ppT.SetManualLines(_ppTemp1A, _ppTemp1B,
                                        new GeometryFit.P2(gsx, gsy), new GeometryFit.P2(gex, gey));
                                    SetStatus("平行垂直度：两条线已就位，正在判定平行/垂直…");
                                }
                                else
                                {
                                    al.SetManualLines(_llTemp1P1, _llTemp1P2,
                                        new OpenCvSharp.Point(gsx, gsy), new OpenCvSharp.Point(gex, gey));
                                    SetStatus("两线夹角：两条线已就位，正在计算夹角…");
                                }
                                RunChainSilently();
                            }
                        }
                        break;

                    case GeoTool.ThreePoints:
                        var a3 = _editing?.Task as Angle3PointTask ?? _browserTask as Angle3PointTask;
                        if (a3 != null && gotStart && !moved)
                        {
                            // 单击推进：顶点→臂A端点→臂B端点；每点一个立即在图上画红点反馈，
                            // 满 3 点才 SetManualPoints（避免中途状态被 Execute 当"已手动指定"）
                            _tpPts[_tpStage] = new GeometryFit.P2(gsx, gsy);
                            AddManualDot(gsx, gsy, matG);
                            _tpStage++;
                            if (_tpStage >= 3)
                            {
                                _tpStage = 0;
                                a3.SetManualPoints(_tpPts[0], _tpPts[1], _tpPts[2]);
                                ClearManualDots();
                                SetStatus(string.Format("三点角度：顶点({0:F0},{1:F0}) 两臂端点已就位，正在测角…",
                                    _tpPts[0].X, _tpPts[0].Y));
                                RunChainSilently();
                            }
                            else
                            {
                                SetStatus(_tpStage == 1
                                    ? "顶点已记下，单击第 1 个臂的端点"
                                    : "已记 2 点，单击第 2 个臂的端点（完成）");
                            }
                        }
                        break;

                    case GeoTool.TwoPoints:
                        var pp = _editing?.Task as PointToPointTask ?? _browserTask as PointToPointTask;
                        if (pp != null && gotStart && !moved)
                        {
                            _ppPts[_ppStage] = new GeometryFit.P2(gsx, gsy);
                            AddManualDot(gsx, gsy, matG);
                            _ppStage++;
                            if (_ppStage >= 2)
                            {
                                _ppStage = 0;
                                pp.SetManualPoints(_ppPts[0], _ppPts[1]);
                                ClearManualDots();
                                SetStatus(string.Format("点到点距离：两点已就位 ({0:F0},{1:F0})→({2:F0},{3:F0})，正在测距…",
                                    _ppPts[0].X, _ppPts[0].Y, _ppPts[1].X, _ppPts[1].Y));
                                RunChainSilently();
                            }
                            else
                            {
                                SetStatus("第 1 点已记下，单击第 2 个测量点（完成）");
                            }
                        }
                        break;

                    case GeoTool.FitPoints:
                        if (gotStart && !moved)
                        {
                            _fitPts.Add(new GeometryFit.P2(gsx, gsy));
                            AddManualDot(gsx, gsy, matG);
                            int need = FitMinPoints();
                            if (_fitPts.Count >= need)
                            {
                                SetFitManualPoints();
                                ClearManualDots();
                                SetStatus(string.Format("点集拟合：已注入 {0} 个点（≥{1}），正在重拟合…（可继续加点更准）",
                                    _fitPts.Count, need));
                                RunChainSilently();
                            }
                            else
                            {
                                SetStatus(string.Format("点集拟合：已点 {0} 个，还需 {1} 个", _fitPts.Count, need - _fitPts.Count));
                            }
                        }
                        break;

                    case GeoTool.TwoCircles:
                        var cc2 = _editing?.Task as ConcentricityTask ?? _browserTask as ConcentricityTask;
                        if (cc2 != null && gotStart && !moved)
                        {
                            _ccPts[_ccStage] = new GeometryFit.P2(gsx, gsy);
                            AddManualDot(gsx, gsy, matG);
                            _ccStage++;
                            if (_ccStage == 1 || _ccStage == 3)
                                SetStatus(_ccStage == 1
                                    ? "外圆圆心已记下，单击外圆圆周上一点（半径=到圆心的距离）"
                                    : "内圆圆心已记下，单击内圆圆周上一点（完成）");
                            else if (_ccStage >= 4)
                            {
                                _ccStage = 0;
                                double r1 = _ccPts[0].DistanceTo(_ccPts[1]);
                                double r2 = _ccPts[2].DistanceTo(_ccPts[3]);
                                cc2.SetManualCircles(new GeometryFit.Circle2(_ccPts[0].X, _ccPts[0].Y, r1),
                                    new GeometryFit.Circle2(_ccPts[2].X, _ccPts[2].Y, r2));
                                ClearManualDots();
                                SetStatus(string.Format("同心度：外圆 R{0:F0}px 内圆 R{1:F0}px 已注入，正在计算…",
                                    r1, r2));
                                RunChainSilently();
                            }
                            else
                            {
                                SetStatus(_ccStage == 2
                                    ? "外圆已定，单击内圆的圆心"
                                    : "内圆圆心已记下，单击内圆圆周上一点（完成）");
                            }
                        }
                        break;

                    case GeoTool.LinePlusPoint:
                        CaliperLine.Visibility = Visibility.Collapsed;
                        var pd = _editing?.Task as PointLineDistanceTask ?? _browserTask as PointLineDistanceTask;
                        if (pd != null && gotStart && gotEnd)
                        {
                            if (!pd.HasLine || !_plWaitPoint)
                            {
                                // 画测量线
                                if (gsx != gex || gsy != gey)
                                {
                                    pd.SetLine(new OpenCvSharp.Point(gsx, gsy), new OpenCvSharp.Point(gex, gey));
                                    _plWaitPoint = true;
                                    SetStatus("测量线已画好，请单击图中一点作为测量点（距离=点到直线的垂距）");
                                }
                                else SetStatus("测量线太短，未生效（拖长一点）");
                            }
                            else
                            {
                                // 单击测量点
                                pd.SetPoint(new OpenCvSharp.Point(gsx, gsy));
                                _plWaitPoint = false;
                                SetStatus(string.Format("测量点 ({0},{1}) 已注入，正在计算点线距离…", gsx, gsy));
                                RunChainSilently();
                            }
                        }
                        break;

                    case GeoTool.Seeds:
                        var seeds = EditingSeeds();
                        if (seeds != null && gotStart && !moved)
                        {
                            // 单击加种子：落到"还没种"的位置
                            if (EditingGeoTool() == GeoTool.Seeds)
                            {
                                if (_editing?.Task is WatershedTask w) w.AddSeed(new OpenCvSharp.Point(gsx, gsy));
                                else if (_editing?.Task is FloodFillSegmentTask f) f.AddSeed(new OpenCvSharp.Point(gsx, gsy));
                                else if (_browserTask is WatershedTask wb) wb.AddSeed(new OpenCvSharp.Point(gsx, gsy));
                                else if (_browserTask is FloodFillSegmentTask fb) fb.AddSeed(new OpenCvSharp.Point(gsx, gsy));
                            }
                            RefreshSeedDots();
                            SetStatus(string.Format("种子点 ({0},{1}) 已添加（共 {2} 个），正在刷新结果…", gsx, gsy, seeds.Count));
                            RunChainSilently();
                        }
                        else if (seeds != null) SetStatus("种子：单击图上一点加种子（拖动不会加点）");
                        break;

                    case GeoTool.GrabCut:
                        Selection.Visibility = Visibility.Collapsed;
                        GcExclude.Visibility = Visibility.Collapsed;
                        var gc = _editing?.Task as GrabCutTask ?? _browserTask as GrabCutTask;
                        if (gc != null && gotStart && gotEnd)
                        {
                            int rx = Math.Min(gsx, gex), ry = Math.Min(gsy, gey);
                            int rw = Math.Abs(gex - gsx), rh = Math.Abs(gey - gsy);
                            if (rw >= 2 && rh >= 2)
                            {
                                if (!_gcStage)
                                {
                                    gc.InitRect = new OpenCvSharp.Rect(rx, ry, rw, rh);
                                    _gcStage = true;
                                    SetStatus(string.Format("前景框 {0}x{1} @ ({2},{3}) 已设，可再拖一次画排除框（可选），或直接看结果", rw, rh, rx, ry));
                                    RunChainSilently();
                                }
                                else
                                {
                                    gc.ExcludeRect = new OpenCvSharp.Rect(rx, ry, rw, rh);
                                    _gcStage = false;
                                    SetStatus(string.Format("排除框 {0}x{1} @ ({2},{3}) 已设，正在执行 GrabCut…", rw, rh, rx, ry));
                                    RunChainSilently();
                                }
                            }
                        }
                        break;
                }
                return;
            }

            if (_draggingCaliper)
            {
                // 卡尺画线结束：把起终点（原图像素）注入算子并静默重跑，结果图上立即出边缘标注
                _draggingCaliper = false;
                View.ReleaseMouseCapture();
                CaliperLine.Visibility = Visibility.Collapsed;
                var cal = EditingCaliper();
                var curEnd = e.GetPosition(Overlay);
                var matCal = CurrentDisplayMat();
                if (cal != null && TryMapToImage(_caliperStart, matCal, out int sx, out int sy)
                    && TryMapToImage(curEnd, matCal, out int ex, out int ey)
                    && (sx != ex || sy != ey))
                {
                    cal.SetCaliper(new OpenCvSharp.Point(sx, sy), new OpenCvSharp.Point(ex, ey));
                    double len = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
                    double deg = Math.Atan2(ey - sy, ex - sx) * 180 / Math.PI;
                    SetStatus(string.Format(
                        "卡尺扫描线：({0},{1})→({2},{3})  长 {4:0}px  方向 {5:0}°（已注入，正在刷新结果…）",
                        sx, sy, ex, ey, len, deg));
                    RunChainSilently();
                }
                else if (cal != null) SetStatus("卡尺画线太短，未生效（再拖长一点）");
                return;
            }

            if (!_draggingSel) return;
            _draggingSel = false;
            View.ReleaseMouseCapture();
            UpdateSelection(e.GetPosition(Overlay));

            var mat = CurrentDisplayMat();
            var a = _selStart; var b = e.GetPosition(Overlay);

            // 拖动区域 → 归一化选区（起点与终点都换算一次，再取 min/max）。
            // 用 clamp 版换算：图片比例与显示区差大（"非正常尺寸"）时 Uniform 留白巨大，
            // 拖框很容易把起点/终点拖出画面——旧代码要求两点都落在图像区 [0,1]，
            // 落白即整框失败（框消失），看起来就是"ROI 框和鼠标框选位置不一致"。
            // clamp 后框 = 图像内实际生效区域，拖出画面不再丢框。
            if (TryMapToNormalizedClamped(a, out double nx1, out double ny1)
                && TryMapToNormalizedClamped(b, out double nx2, out double ny2)
                && Math.Abs(nx2 - nx1) > 0.001 && Math.Abs(ny2 - ny1) > 0.001)
            {
                double sx = Math.Min(nx1, nx2), sy = Math.Min(ny1, ny2);
                double sw = Math.Abs(nx2 - nx1), sh = Math.Abs(ny2 - ny1);
                _selNorm = new Rect(sx, sy, sw, sh);
                _hasSel = true;
                DrawSelectionFromNorm();          // 吸附到图像对齐后的框（保证"框 = 实际生效区域"）
                var r = SelectionRectOn(mat);
                SetStatus(string.Format("选区（归一化 {0:0.0}%,{1:0.0}%～{2:0.0}%,{3:0.0}%）≈ 该图上 {4}x{5} @ ({6},{7})",
                    _selNorm.X * 100, _selNorm.Y * 100,
                    (_selNorm.X + _selNorm.Width) * 100, (_selNorm.Y + _selNorm.Height) * 100,
                    r.Width, r.Height, r.X, r.Y));

                // 掩膜算子（模式 1/2）下，把这次框选直接变成掩膜区域：框内涂黑、框外保留。
                // 只处理整图坐标（当前已切回原图），与 MaskTask.Execute 的整图语义一致。
                var maskNow = EditingMask();
                if (maskNow != null && EditingMaskMode(maskNow) != 3 && r.Width >= 2 && r.Height >= 2)
                {
                    maskNow.SetMask(new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height));
                    BuildParams(maskNow, _editing != null ? _editing.Values : _browserValues, _editing);
                    SetStatus(string.Format("掩膜已设为矩形 {0}x{1} @ ({2},{3})（框内涂黑），正在刷新结果…",
                        r.Width, r.Height, r.X, r.Y));
                    RunChainSilently();
                }
            }
            else
            {
                _hasSel = false;
                _selNorm = default;
                Selection.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateSelection(Point current)
        {
            double x = Math.Min(_selStart.X, current.X), y = Math.Min(_selStart.Y, current.Y);
            double w = Math.Abs(current.X - _selStart.X), h = Math.Abs(current.Y - _selStart.Y);
            Canvas.SetLeft(Selection, x);
            Canvas.SetTop(Selection, y);
            Selection.Width = w;
            Selection.Height = h;
            Selection.Visibility = w > 1 && h > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================ 算子里程（导出/导入/批量）

        /// <summary>把界面上的链转成可序列化的算子里程（模板转 base64 PNG）</summary>
        private List<ChainStepData> BuildChainData()
        {
            var list = new List<ChainStepData>();
            foreach (var s in _chain)
                list.Add(new ChainStepData
                {
                    OpName = s.OpName,
                    Values = (int[])(s.Values ?? Array.Empty<int>()).Clone(),
                    TemplatePngBase64 = OperatorChain.EncodeTemplate(s.Template),
                });
            return list;
        }

        private void ApplyChainData(List<ChainStepData> data)
        {
            foreach (var s in _chain) { s.Output?.Dispose(); s.Template?.Dispose(); s.Input = null; }
            _chain.Clear();
            foreach (var d in data)
                _chain.Add(new ChainStep
                {
                    OpName = d.OpName,
                    Values = d.Values ?? Array.Empty<int>(),
                    Template = OperatorChain.DecodeTemplate(d.TemplatePngBase64),
                });
            _editing = null;
            RefreshChain();
            if (_chain.Count > 0) ChainList.SelectedItem = _chain[0];
            RenderView();
        }

        private void ExportChain()
        {
            if (_chain.Count == 0) { Warn("算子里程是空的：先加入几个算子"); return; }
            var dlg = new SaveFileDialog
            {
                Title = "导出算子里程",
                Filter = "算子里程 (*.chain.json)|*.chain.json|JSON|*.json",
                FileName = "算子里程.chain.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, OperatorChain.ToJson(BuildChainData()));
                SetStatus("算子里程已导出：" + dlg.FileName +
                          "（可在「批量处理…」里选它，也可交给无人值守跑同名图）");
            }
            catch (Exception ex) { Warn("导出失败：" + ex.Message); }
        }

        private void ImportChain()
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入算子里程",
                Filter = "算子里程 (*.chain.json)|*.chain.json|JSON|*.json|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var data = OperatorChain.FromJson(System.IO.File.ReadAllText(dlg.FileName));
                ApplyChainData(data);
                SetStatus(string.Format("已导入算子里程：{0} 步（模板随文件一起来）", data.Count));
            }
            catch (Exception ex) { Warn("导入失败：" + ex.Message); }
        }

        /// <summary>批量处理：用当前链（或选一个算子里程文件）跑整个文件夹</summary>
        private void OpenBatchWindow()
        {
            var win = new BatchWindow(BuildChainData(), _rawParams) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }

        /// <summary>切走页面时释放大图与链上缓存，避免长期占内存</summary>
        public void ReleaseImages()
        {
            MatImage.ResetCache();
            _srcMat?.Dispose();
            _srcMat = null;
            // Input 是上一步 Output 的引用别名：释放 Output 后必须一起清 Input，
            // 否则下次 RenderView 访问 _editing.Input.Empty() 会抛 ObjectDisposedException
            foreach (var s in _chain) { s.Output?.Dispose(); s.Output = null; s.Input = null; }
            CaliperLine.Visibility = Visibility.Collapsed;
            View.Source = null;
        }
        /// <summary>顶栏主题色点点击：应用主题并刷新本页色点（全软件风格统一）</summary>
        private void ThemeDot_Click(object sender, MouseButtonEventArgs e)
        {
            ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
        }

    }
}
