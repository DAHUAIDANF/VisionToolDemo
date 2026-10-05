using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PathShape = Avalonia.Controls.Shapes.Path;
using Avalonia.Controls.Shapes;

// 注意：不能直接 using OpenCvSharp; —— 它有 Point/Size/Rect，会和 System.Windows 的同名类型冲突（CS0104）。
// 只把真正用到的类型别名进来；Rect 一律写全名 OpenCvSharp.Rect。
using Mat = OpenCvSharp.Mat;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 自动化工作流页面（WPF 版节点工作台）。
    ///
    /// 布局与交互借鉴 OpenVisionForge Studio：
    ///   左「节点库」（搜索 + 分类 chips + 卡片，双击/拖拽添加）
    ///   中「画布」（节点卡片带参数摘要与**上次耗时**、贝塞尔连线、滚轮缩放/中键平移）
    ///   右「实例属性」（参数按 schema 生成、字符串槽、连线出口）
    ///   下「抽屉」（图像与结果 / 运行日志 / 校验结果 / 运行记录）
    ///
    /// 与旧 WinForms 版的关系：业务流程与算子完全复用（AutomationGraph / AutomationRunner /
    /// VisionTaskRegistry），这里只是把界面换成矢量渲染 + 自动布局，因此
    /// 缩放、任意分辨率、125%/150% DPI 下都不会发虚或错位。
    /// </summary>
    public partial class AutomationPage : UserControl, IShellPage
    {
        private readonly MainWindow _shell;
        private readonly AutomationGraph _graph = new();
        private readonly Dictionary<int, Border> _nodeVisuals = new();

        private int _selectedId = -1;

        /// <summary>
        /// 画布上已画出的连线与接线点。
        /// 为什么要留住引用：拖动节点时只改这几条线的几何、切换选中只改边框颜色，
        /// 而不是"清空画布重建所有控件"—— 后者在每次点击/拖动时都会重建几十个控件与位图，
        /// 是界面卡顿的典型来源。
        /// </summary>
        private readonly System.Collections.Generic.List<EdgeVisual> _edges = new();
        /// <summary>画布缩放/平移变换（x:Name 在 RenderTransform 内不生成字段，改为代码持有）</summary>
        private readonly ScaleTransform ZoomT = new() { ScaleX = 1, ScaleY = 1 };
        private readonly TranslateTransform PanT = new() { X = 40, Y = 40 };
        private readonly System.Collections.Generic.Dictionary<int, Ellipse> _ports = new();
        private int _inspectedId = int.MinValue;

        private sealed class EdgeVisual
        {
            public int From, To;
            public bool Alt;
            public PathShape Path;
            public PathFigure Fig;
            // 3 段折线：每段独立 LineSegment（Point 是 Avalonia 属性，拖动改点必触发重绘，
            // 比 PolyLineSegment.Points 索引赋值更可靠，避免拖动时连线"卡住/断开"）
            public LineSegment[] Seg = new LineSegment[3];
        }
        private int _linkFrom = -1;                 // 正在连线的源节点（-1 = 未在连线）
        private bool _dragging;
        private int _dragId = -1;
        private Point _dragStart;
        private float _dragOrigX, _dragOrigY;
        private bool _panning;
        private Point _panStart;
        private double _panOrigX, _panOrigY;
        private double _zoom = 1.0;

        /// <summary>节点卡片摘要 TextBlock 的引用（轻量刷新用：运行后只改文本，不重建画布）</summary>
        private readonly Dictionary<int, TextBlock> _summaryTexts = new();

        /// <summary>日志滚动合并节流：高频日志只排一次滚动，避免每行触发布局卡顿</summary>
        private bool _scrollPending;
        private bool _syncingParams;
        private AutomationRunResult _result;
        private int _stepLimit;                     // 0 = 跑到底

        private const double NodeW = 214;
        private const double NodeH = 70;

        /// <summary>ViewModel：命令与状态（节点画布/参数检查器等视图交互留在本类）</summary>
        internal readonly ViewModels.AutomationViewModel Vm = new();

        public AutomationPage(MainWindow shell)
        {
            InitializeComponent();
            // Avalonia 不在 RenderTransform 属性值里生成 x:Name 字段，这里显式挂变换组
            Surface.RenderTransform = new TransformGroup { Children = { ZoomT, PanT } };
            DataContext = Vm;
            Vm.OpenRecDirRequested += () => BtnOpenRecDir_Click(null, null);
            Vm.RefreshRecRequested += RefreshRecords;
            ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
            ThemeManager.RegisterPage(this);
            _shell = shell;

            BuildToolbar();
            BuildLibrary();
            SeedGraph();
            RebuildCanvas();
            RefreshRecords();
            SetStatus("节点工作台就绪：双击左侧节点库条目添加节点");
        }

        public AutomationGraph Graph => _graph;

        /// <summary>切到本页时安装顶部工具条</summary>
        public void OnShown(MainWindow shell) => BuildToolbar();

        private void SetStatus(string s) { Vm.StatusText = s ?? ""; _shell?.SetStatus(s); }

        // ================================================================ 工具栏

        private void BuildToolbar()
        {
            if (_shell == null) return;
            var items = new List<Control>();

            Button Btn(string text, Action click, bool primary = false)
            {
                var b = new Button
                {
                    Content = text,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                Ui.Class(b, primary ? "primary" : "flat");
                b.Click += (_, _) => { try { click(); } catch (Exception ex) { Error(ex.Message); } };
                items.Add(b);
                return b;
            }

            Btn("运行", () => RunGraph(false), true);
            Btn("干跑", () => RunGraph(true));
            Btn("停止", () => SetStatus("停止：本版运行是同步的，真实执行中把鼠标甩到屏幕角落可立即中止"));
            var sep = new Border { Width = 1, Margin = new Thickness(0, 4, 8, 4) };
            sep.Background = Ui.Brush("Line");
            items.Add(sep);
            Btn("校验", ValidateNow);
            Btn("自动布局", AutoLayout);
            Btn("单步", StepOnce);
            Btn("保存", SaveGraphFile);
            Btn("加载", LoadGraphFile);
            Btn("示例", SampleMenu);
            Btn("JSON", ShowJson);
            Btn("配方…", RecipeMenu);

            var dry = new CheckBox
            {
                Content = "干跑(只记日志)",
                IsChecked = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            dry.Checked += (_, _) => AutomationContext.DryRun = true;
            dry.Unchecked += (_, _) => AutomationContext.DryRun = false;
            AutomationContext.DryRun = true;
            items.Add(dry);

            _shell.SetToolbar(items);
        }

        // ================================================================ 节点库

        private readonly List<CatalogItem> _allItems = new();
        private string _category = "全部";

        private void BuildLibrary()
        {
            _allItems.Clear();
            _allItems.AddRange(NodeCatalog.All);

            // 搜索防抖：每敲一个字就重建分类 chips 与整张列表会明显卡顿，等 150ms 再刷新
            var searchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshLibrary(); };
            LibSearch.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
            ToolTip.SetTip(LibSearch, "输入算子名 / 节点名 / 分类任意片段");
            RefreshLibrary();
        }

        private void RefreshLibrary()
        {
            string q = (LibSearch.Text ?? "").Trim().ToLowerInvariant();
            var filtered = _allItems.Where(i => q.Length == 0 || i.SearchKey.Contains(q)).ToList();

            // 分类 chips（带数量）：2 列 Grid 严格对齐（WrapPanel 流式列参差；3 列会溢出左栏宽度）
            LibChips.Children.Clear();
            LibChips.RowDefinitions.Clear();
            var cats = new List<string> { "全部" };
            cats.AddRange(NodeCatalog.CategoryOrder.Where(c => filtered.Any(i => i.Category == c)));
            int chipIndex = 0;
            foreach (string c in cats)
            {
                int count = c == "全部" ? filtered.Count : filtered.Count(i => i.Category == c);
                if (count == 0 && c != "全部") continue;
                var chip = new ToggleButton
                {
                    Content = c + " " + count,
                    IsChecked = _category == c,
                };
                Ui.Class(chip, "chip");
                string cap = c;
                chip.Click += (_, _) => { _category = cap; RefreshLibrary(); };
                LibChips.Children.Add(chip);
                Grid.SetColumn(chip, chipIndex % 2);
                Grid.SetRow(chip, chipIndex / 2);
                chipIndex++;
            }
            for (int r = 0; r < (chipIndex + 1) / 2; r++)
                LibChips.RowDefinitions.Add(new RowDefinition());

            var shown = filtered.Where(i => _category == "全部" || i.Category == _category)
                .OrderBy(i => Array.IndexOf(NodeCatalog.CategoryOrder, i.Category))
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .Take(400).ToList();
            LibList.ItemsSource = shown;
        }

        private void LibList_MouseDoubleClick(object sender, TappedEventArgs e) => AddSelectedLibraryItem(null);

        private void LibList_PreviewMouseMove(object sender, PointerEventArgs e)
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            if (LibList.SelectedItem is not CatalogItem item) return;
            var dataObj = new DataObject();
            dataObj.Set("vtd.node", item);
            DragDrop.DoDragDrop(e, dataObj, DragDropEffects.Copy);
        }

        private void AddSelectedLibraryItem(Point? dropAt)
        {
            if (LibList.SelectedItem is not CatalogItem item) return;
            Point at = dropAt ?? NextFreeSpot();
            AddNode(item, (float)at.X, (float)at.Y);
        }

        private Point NextFreeSpot()
        {
            double maxX = 40;
            foreach (var n in _graph.Nodes) maxX = Math.Max(maxX, n.X + 240);
            return new Point(maxX, 60);
        }

        private void AddNode(CatalogItem item, float x, float y)
        {
            AutoNode node;
            if (item.Kind.HasValue) node = _graph.Add(item.Kind.Value, x, y);
            else
            {
                node = _graph.Add(AutoNodeKind.VisionOp, x, y);
                _graph.SetNodeOp(node.Id, item.OpName);
                node.OpName = item.OpName;
            }
            _selectedId = node.Id;
            RebuildCanvas();
            BuildInspector(true);
            SetStatus(string.Format("已添加节点 #{0} {1}", node.Id, AutoNodeInfo.Title(node.Kind)));
        }

        // ================================================================ 画布

        private void RebuildCanvas()
        {
            Surface.Children.Clear();
            _nodeVisuals.Clear();
            _edges.Clear();
            _ports.Clear();
            _summaryTexts.Clear();

            // 先画连线（节点覆盖在线上，视觉更清楚）
            foreach (var n in _graph.Nodes)
            {
                if (n.NextId >= 0 && _graph.Get(n.NextId) != null) AddEdge(n, _graph.Get(n.NextId), false);
                if (n.AltNextId >= 0 && _graph.Get(n.AltNextId) != null) AddEdge(n, _graph.Get(n.AltNextId), true);
            }
            foreach (var n in _graph.Nodes) AddNodeVisual(n);
            ZoomText.Text = string.Format("{0:P0}", _zoom);
            ScheduleAutoBackup();
        }

        /// <summary>
        /// 轻量刷新节点卡片：运行结束后节点结构与连线都不变，只把每个卡片的摘要文本
        /// （参数 + 上次耗时）更新成最新值，避免整画布全量重建（几十个节点时重建有明显开销）。
        /// </summary>
        private void RefreshSummaries()
        {
            foreach (var kv in _summaryTexts)
            {
                var n = _graph.Get(kv.Key);
                if (n != null) kv.Value.Text = CanvasSummary(n);
            }
            ZoomText.Text = string.Format("{0:P0}", _zoom);
        }

        /// <summary>图编辑防抖自动备份：改完图 900ms 不动就存一份到「图自动备份」，防止误操作丢图。</summary>
        private DispatcherTimer _backupTimer;

        private void ScheduleAutoBackup()
        {
            if (_backupTimer == null)
            {
                _backupTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(900),
                };
                _backupTimer.Tick += (_, _) =>
                {
                    _backupTimer.Stop();
                    AutoBackupGraph();
                };
            }
            _backupTimer.Stop();
            _backupTimer.Start();
        }

        private void AutoBackupGraph()
        {
            try
            {
                if (_graph.Nodes.Count == 0) return;      // 空图不备份
                string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "图自动备份");
                Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir,
                    "自动备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                File.WriteAllText(file, AutomationGraphIO.Export(_graph), new System.Text.UTF8Encoding(false));
                // 只保留最近 20 份，避免无限累积
                var old = Directory.GetFiles(dir, "自动备份_*.json")
                    .OrderByDescending(f => f).Skip(20).ToArray();
                foreach (var f in old)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }   // 备份失败不影响编辑
        }

        /// <summary>按 from/to 相对位置计算连线几何（端口 + 正交折线 4 点）。
        /// 目标在右侧：右出口→左入口，先水平→垂直→水平；
        /// 左侧：左出口→右入口，同样正交；
        /// 正下方：底出口→顶入口，先垂直→水平→垂直；
        /// 正上方：顶出口→底入口，同样正交。
        /// 正交折线始终是清晰单线，不会像贝塞尔那样在斜向连接时弯折成"两根线"。</summary>
        private void ComputeEdgeGeometry(AutoNode from, AutoNode to,
            out double x1, out double y1, out double x2, out double y2,
            out double mx1, out double my1, out double mx2, out double my2)
        {
            bool toRight = to.X >= from.X + NodeW;
            bool toLeft = to.X + NodeW <= from.X;
            if (toRight || toLeft)
            {
                // 水平优先：从源左/右边中点出发，先进水平段到中点，再垂直对齐，最后水平进目标
                x1 = toRight ? from.X + NodeW : from.X;
                y1 = from.Y + NodeH / 2;
                x2 = toRight ? to.X : to.X + NodeW;
                y2 = to.Y + NodeH / 2;
                double midX = (x1 + x2) / 2;
                mx1 = midX; my1 = y1;
                mx2 = midX; my2 = y2;
            }
            else if (to.Y >= from.Y + NodeH)   // 正下方：底出口 → 顶入口
            {
                x1 = from.X + NodeW / 2; y1 = from.Y + NodeH;
                x2 = to.X + NodeW / 2; y2 = to.Y;
                double midY = (y1 + y2) / 2;
                mx1 = x1; my1 = midY;
                mx2 = x2; my2 = midY;
            }
            else                               // 正上方：顶出口 → 底入口
            {
                x1 = from.X + NodeW / 2; y1 = from.Y;
                x2 = to.X + NodeW / 2; y2 = to.Y + NodeH;
                double midY = (y1 + y2) / 2;
                mx1 = x1; my1 = midY;
                mx2 = x2; my2 = midY;
            }
        }

        private void AddEdge(AutoNode from, AutoNode to, bool alt)
        {
            ComputeEdgeGeometry(from, to,
                out double x1, out double y1, out double x2, out double y2,
                out double mx1, out double my1, out double mx2, out double my2);

            var fig = new PathFigure { StartPoint = new Point(x1, y1) };
            var segs = new LineSegment[3];
            segs[0] = new LineSegment { Point = new Point(mx1, my1) };
            segs[1] = new LineSegment { Point = new Point(mx2, my2) };
            segs[2] = new LineSegment { Point = new Point(x2, y2) };
            fig.Segments.Add(segs[0]);
            fig.Segments.Add(segs[1]);
            fig.Segments.Add(segs[2]);
            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            var path = new PathShape
            {
                Data = geo,
                // 主出口灰色实线；副出口（条件/循环节点的"否"分支）黄色实线（用户要求不用虚线）
                // 用 SetResourceReference（DynamicResource 语义）绑定，主题切换（含"冻结→替换键"路径）必跟随
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            path.Stroke = Ui.Brush(alt ? "Warn" : "FgDim");
            Surface.Children.Add(path);
            _edges.Add(new EdgeVisual { From = from.Id, To = to.Id, Alt = alt, Path = path, Fig = fig, Seg = segs });
        }

        /// <summary>只重画连线（节点视觉不动）—— 连线变了但节点没变时用，避免整画布重建</summary>
        private void RebuildEdges()
        {
            foreach (var e in _edges) Surface.Children.Remove(e.Path);
            _edges.Clear();
            foreach (var n in _graph.Nodes)
            {
                if (n.NextId >= 0 && _graph.Get(n.NextId) != null) AddEdge(n, _graph.Get(n.NextId), false);
                if (n.AltNextId >= 0 && _graph.Get(n.AltNextId) != null) AddEdge(n, _graph.Get(n.AltNextId), true);
            }
        }

        /// <summary>拖动节点时只挪这几条线的几何（直接改每段 LineSegment 的 Point，不新建对象）。
        /// LineSegment.Point 是 Avalonia 属性：赋值自带变更通知；最后 InvalidateVisual 兜底强制重绘，
        /// 保证拖动过程中连线实时跟随、不会出现"节点移走了线还停在原地"的断开。</summary>
        private void UpdateEdgesFor(int nodeId)
        {
            foreach (var e in _edges)
            {
                if (e.From != nodeId && e.To != nodeId) continue;
                var from = _graph.Get(e.From);
                var to = _graph.Get(e.To);
                if (from == null || to == null) continue;
                ComputeEdgeGeometry(from, to,
                    out double x1, out double y1, out double x2, out double y2,
                    out double mx1, out double my1, out double mx2, out double my2);
                e.Fig.StartPoint = new Point(x1, y1);
                e.Seg[0].Point = new Point(mx1, my1);
                e.Seg[1].Point = new Point(mx2, my2);
                e.Seg[2].Point = new Point(x2, y2);
                e.Path.InvalidateVisual();
            }
        }

        /// <summary>只更新"选中/连线中"的视觉状态（边框颜色、接线点颜色）</summary>
        private void UpdateSelectionVisuals()
        {
            foreach (var kv in _nodeVisuals)
            {
                bool sel = kv.Key == _selectedId;
                kv.Value.Background = Ui.Brush("Line");
                kv.Value.BorderThickness = new Thickness(sel ? 2 : 1);
            }
            foreach (var kv in _ports)
                kv.Value.Fill = Ui.Brush(kv.Key == _linkFrom ? "Warn" : "Accent");
        }

        private void AddNodeVisual(AutoNode n)
        {
            var card = new Border
            {
                Width = NodeW,
                Height = NodeH,
                CornerRadius = new CornerRadius(8),
                // SetResourceReference = DynamicResource 语义：主题切换（含冻结→替换键路径）必跟随
                BorderThickness = new Thickness(n.Id == _selectedId ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Tag = n.Id,
            };
            ToolTip.SetTip(card, NodeTooltip(n));
            card.Background = Ui.Brush("CardBg");
            card.BorderBrush = Ui.Brush(n.Id == _selectedId ? "Accent" : "Line");
            Canvas.SetLeft(card, n.X);
            Canvas.SetTop(card, n.Y);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var idTxt = new TextBlock
            {
                Text = "#" + n.Id,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 6, 0),
            };
            idTxt.Foreground = Ui.Brush("FgDim");
            head.Children.Add(idTxt);
            var titleTxt = new TextBlock
            {
                Text = AutoNodeInfo.Title(n.Kind) + (n.Kind == AutoNodeKind.VisionOp ? " · " + (n.OpName ?? "") : ""),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            titleTxt.Foreground = Ui.Brush("Fg");   // 显式跟随主题（原为继承，主题切换后可能残留旧色）
            head.Children.Add(titleTxt);
            grid.Children.Add(head);

            var body = new TextBlock
            {
                Text = CanvasSummary(n),
                Classes = { "fainttext" },
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(body, 1);
            grid.Children.Add(body);
            _summaryTexts[n.Id] = body;

            // 用到模板的节点：卡片右下角挂一张模板缩略图（几个匹配节点时能一眼分清）
            Control cardContent = grid;
            var tpl = NodeUsesTemplate(n) ? _graph.GetNodeTemplate(n.Id) : null;
            if (tpl != null && !tpl.Empty())
            {
                var thumb = new Image
                {
                    Source = MatImage.ToThumbnail(tpl, 96),
                    Stretch = Stretch.Uniform,
                    Width = 46,
                    Height = 46,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                var thumbFrame = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(1),
                    Child = thumb,
                };
                ToolTip.SetTip(thumbFrame, Ui.Tip("本节点使用的模板图（缩略）"));
                thumbFrame.Background = Ui.Brush("InputBg");
                thumbFrame.BorderBrush = Ui.Brush("Line");
                var dock = new DockPanel();
                DockPanel.SetDock(thumbFrame, Dock.Right);
                dock.Children.Add(thumbFrame);
                dock.Children.Add(grid);
                cardContent = dock;
            }

            card.Child = cardContent;
            card.PointerPressed += Node_MouseDown;
            card.PointerMoved += Node_MouseMove;
            card.PointerReleased += Node_MouseUp;
            card.PointerPressed += (_, e) => { if (PointerButton(e) != MouseButton.Right) return; _selectedId = n.Id; UpdateSelectionVisuals(); BuildInspector(); };

            // 右侧接线点：点一下开始连线，再点目标节点完成
            var port = new Ellipse
            {
                Width = 14, Height = 14,
                StrokeThickness = 1,
                Cursor = new Cursor(StandardCursorType.Cross),
            };
            ToolTip.SetTip(port, "点这里开始连线，再点目标节点；按 Esc 取消");
            port.Fill = Ui.Brush(n.Id == _linkFrom ? "Warn" : "Accent");
            port.Stroke = Ui.Brush("Bg");
            port.PointerPressed += (_, e) =>
            {
                _linkFrom = _linkFrom == n.Id ? -1 : n.Id;
                SetStatus(_linkFrom >= 0
                    ? string.Format("从 #{0} 连线中：点目标节点完成；Esc 取消", _linkFrom)
                    : "已取消连线");
                UpdateSelectionVisuals();      // 只改颜色，不重建整块画布
                e.Handled = true;
            };
            Canvas.SetLeft(port, n.X + NodeW - 7);
            Canvas.SetTop(port, n.Y + NodeH / 2 - 7);
            Surface.Children.Add(port);
            _ports[n.Id] = port;

            Surface.Children.Add(card);
            _nodeVisuals[n.Id] = card;
        }

        /// <summary>这个节点是否用到模板图（用于卡片缩略图与"模板图"栏）</summary>
        private static bool NodeUsesTemplate(AutoNode n)
        {
            if (n == null) return false;
            if (n.Kind == AutoNodeKind.Match) return true;
            if (n.Kind == AutoNodeKind.WaitCondition)
                return n.Params == null || n.Params.Length == 0 || n.Params[0] == 1;   // 条件=模板命中
            if (n.Kind == AutoNodeKind.VisionOp && !string.IsNullOrWhiteSpace(n.OpName))
                return AutomationSupport.NeedsTemplate(VisionTaskRegistry.GetTask(n.OpName));
            return false;
        }

        /// <summary>画布上的一行摘要：关键内容 + 上有记录的耗时（参考项目节点卡片就是这么做的）</summary>
        private string CanvasSummary(AutoNode n)
        {
            string text;
            switch (n.Kind)
            {
                case AutoNodeKind.Capture:
                    text = _graph.GetNodeText(n.Id) is { Length: > 0 } d ? "存图 → " + d : "全屏截图";
                    break;
                case AutoNodeKind.Roi:
                    {
                        if (n.Params != null && n.Params.Length > 0 && n.Params[0] == 1)
                            text = "区域 ← 变量 " + (_graph.GetNodeText(n.Id) is { Length: > 0 } roiVar ? roiVar : "（未填）");
                        else if (n.Params != null && n.Params.Length >= 5)
                            text = string.Format("区域 {0},{1} {2}x{3}", n.Params[1], n.Params[2], n.Params[3], n.Params[4]);
                        else text = "区域（未设置）";
                        break;
                    }
                case AutoNodeKind.Match:
                    {
                        var t = _graph.GetNodeTemplate(n.Id);
                        string thr = n.Params != null && n.Params.Length > 0 ? n.Params[0] + "%" : "?";
                        text = string.Format("模板 {0}  阈值 {1}",
                            t == null || t.Empty() ? "（未导入）" : t.Cols + "x" + t.Rows, thr);
                        break;
                    }
                case AutoNodeKind.Ocr:
                    text = _graph.GetNodeText(n.Id) is { Length: > 0 } v ? "→ " + v : "识别文字进变量";
                    break;
                case AutoNodeKind.Click:
                    {
                        int src = n.Params != null && n.Params.Length > 0 ? n.Params[0] : 0;
                        text = src == 0 ? "来源：上次检测" : string.Format("固定 ({0},{1})", n.Params[1], n.Params[2]);
                        break;
                    }
                case AutoNodeKind.Key:
                    text = _graph.GetNodeText(n.Id) is { Length: > 0 } k ? "\"" + Cut(k, 20) + "\"" : "（用全局文本槽）";
                    break;
                case AutoNodeKind.SetVar:
                    text = _graph.GetNodeText(n.Id) is { Length: > 0 } vn ? vn + " ← ..." : "（未填变量名）";
                    break;
                default:
                    {
                        var slots = AutoNodeInfo.StringSlots(n.Kind);
                        if (slots.Length > 0)
                        {
                            string s0 = _graph.GetNodeString(n.Id, 0);
                            text = s0.Length > 0 ? Cut(s0, 26) : slots[0].Name + "（未填）";
                        }
                        else text = n.Params == null || n.Params.Length == 0 ? "" :
                            string.Join("  ", n.Params.Take(2).Select(p => p.ToString()));
                        break;
                    }
            }
            if (_result != null && _result.NodeTimes.TryGetValue(n.Id, out long ms))
                text = string.Format("{0}   [{1}ms]", text, ms);
            return text;
        }

        private string NodeTooltip(AutoNode n)
        {
            string summary = _result != null && _result.NodeSummaries.TryGetValue(n.Id, out string s) ? s : "（还没运行过）";
            return string.Format("#{0} {1}\n\n上次结果：{2}", n.Id, AutoNodeInfo.Title(n.Kind), summary);
        }

        private static string Cut(string s, int len)
        {
            s = (s ?? "").Replace("\n", " ").Trim();
            return s.Length <= len ? s : s.Substring(0, len) + "…";
        }

        // ---------------- 画布交互：选择 / 拖动 / 连线 / 平移 / 缩放 ----------------


        /// <summary>从指针事件取鼠标键（Avalonia 无 ChangedButton，需从 PointerUpdateKind 换算）</summary>
        private static MouseButton PointerButton(PointerEventArgs e)
        {
            return e.GetCurrentPoint(null).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButton.Left,
                PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButton.Right,
                PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButton.Middle,
                _ => MouseButton.None,
            };
        }

        private int HitTestNode(Point canvasPoint)
        {
            for (int i = _graph.Nodes.Count - 1; i >= 0; i--)
            {
                var n = _graph.Nodes[i];
                if (canvasPoint.X >= n.X && canvasPoint.X <= n.X + NodeW
                    && canvasPoint.Y >= n.Y && canvasPoint.Y <= n.Y + NodeH) return n.Id;
            }
            return -1;
        }

        private Point ToGraph(Point device)
            => new Point((device.X - PanT.X) / _zoom, (device.Y - PanT.Y) / _zoom);

        private void Node_MouseDown(object sender, PointerPressedEventArgs e)
        {
            if (sender is not Border card || card.Tag is not int id) return;
            e.Handled = true;

            // 正在连线：这一下就是"选目标节点"
            if (_linkFrom >= 0 && _linkFrom != id)
            {
                var from = _graph.Get(_linkFrom);
                if (from != null)
                {
                    from.NextId = id;
                    SetStatus(string.Format("已连线 #{0} → #{1}（需要走“否”分支时，把副出口改到条件/循环节点上）", _linkFrom, id));
                }
                _linkFrom = -1;
                _selectedId = id;
                RebuildEdges();                // 连线变了：只重画线
                UpdateSelectionVisuals();
                BuildInspector(true);          // 出口下拉要跟着变
                return;
            }

            _selectedId = id;
            _dragging = true;
            _dragId = id;
            _dragStart = e.GetPosition(Surface);
            var node = _graph.Get(id);
            if (node != null) { _dragOrigX = node.X; _dragOrigY = node.Y; }
            // 捕获鼠标：拖动时鼠标移出卡片也继续收到 Move/Up，不会"拖太快就丢/卡住"
            e.Pointer.Capture(card);
            UpdateSelectionVisuals();
            BuildInspector();
        }

        private void Node_MouseMove(object sender, PointerEventArgs e)
        {
            if (!_dragging || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            var node = _graph.Get(_dragId);
            if (node == null) return;
            var p = e.GetPosition(Surface);
            node.X = _dragOrigX + (float)(p.X - _dragStart.X);
            node.Y = _dragOrigY + (float)(p.Y - _dragStart.Y);
            if (_nodeVisuals.TryGetValue(node.Id, out var card))
            {
                Canvas.SetLeft(card, node.X);
                Canvas.SetTop(card, node.Y);
            }
            if (_ports.TryGetValue(node.Id, out var port))
            {
                Canvas.SetLeft(port, node.X + NodeW - 7);
                Canvas.SetTop(port, node.Y + NodeH / 2 - 7);
            }
            UpdateEdgesFor(node.Id);           // 连线跟着节点走（改控制点，不重建）
        }

        private void Node_MouseUp(object sender, PointerReleasedEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                if (sender is Border card) e.Pointer.Capture(null);
                UpdateSelectionVisuals();
            }
        }

        private void CanvasHost_MouseDown(object sender, PointerPressedEventArgs e)
        {
            Focus();
            if (PointerButton(e) is MouseButton.Middle or MouseButton.Right)
            {
                _panning = true;
                _panStart = e.GetPosition(CanvasHost);
                _panOrigX = PanT.X;
                _panOrigY = PanT.Y;
                e.Pointer.Capture(CanvasHost);
                return;
            }
            // 点空白处：如果在连线，取消；否则取消选择
            var gp = ToGraph(e.GetPosition(CanvasHost));
            if (HitTestNode(gp) < 0)
            {
                if (_linkFrom >= 0) { _linkFrom = -1; SetStatus("已取消连线"); UpdateSelectionVisuals(); }
                else { _selectedId = -1; UpdateSelectionVisuals(); BuildInspector(); }
            }
        }

        private void CanvasHost_MouseMove(object sender, PointerEventArgs e)
        {
            if (!_panning) return;
            var p = e.GetPosition(CanvasHost);
            PanT.X = _panOrigX + (p.X - _panStart.X);
            PanT.Y = _panOrigY + (p.Y - _panStart.Y);
        }

        private void CanvasHost_MouseUp(object sender, PointerReleasedEventArgs e)
        {
            if (_panning)
            {
                _panning = false;
                e.Pointer.Capture(null);
            }
        }

        private void CanvasHost_MouseWheel(object sender, PointerWheelEventArgs e)
        {
            double old = _zoom;
            double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
            double next = Math.Max(0.3, Math.Min(2.5, old * factor));
            if (Math.Abs(next - old) < 1e-6) return;

            var p = e.GetPosition(CanvasHost);
            PanT.X = p.X - (p.X - PanT.X) * (next / old);
            PanT.Y = p.Y - (p.Y - PanT.Y) * (next / old);
            _zoom = next;
            ZoomT.ScaleX = ZoomT.ScaleY = next;
            ZoomText.Text = string.Format("{0:P0}", next);
            e.Handled = true;
        }

        private void CanvasHost_DragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains("vtd.node") ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void CanvasHost_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.Contains("vtd.node") || e.Data.Get("vtd.node") is not CatalogItem item) return;
            var gp = ToGraph(e.GetPosition(CanvasHost));
            AddNode(item, (float)Math.Max(0, gp.X), (float)Math.Max(0, gp.Y));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _linkFrom >= 0)
            {
                _linkFrom = -1;
                SetStatus("已取消连线");
                UpdateSelectionVisuals();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete && _selectedId >= 0)
            {
                _graph.Remove(_selectedId);
                _selectedId = -1;
                RebuildCanvas();
                BuildInspector();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        // ================================================================ 实例属性

        private void BuildInspector(bool force = false)
        {
            // 同一个节点重复点选时不用重建：面板内容没变，重建纯属浪费（每次要新建二十几个控件）
            if (!force && _inspectedId == _selectedId && InspHost.Children.Count > 0) return;
            _inspectedId = _selectedId;

            InspHost.Children.Clear();
            var n = _graph.Get(_selectedId);
            if (n == null)
            {
                InspTitle.Text = "未选中节点";
                InspHost.Children.Add(new TextBlock
                {
                    Text = "在画布上点一个节点，这里会列出它的参数与输入内容。\n\n" +
                           "提示：双击左侧节点库条目添加节点；点节点右侧圆点再点目标节点即可连线；" +
                           "滚轮缩放、中键平移、Esc 取消连线、Delete 删除选中节点。",
                    Classes = { "fainttext" },
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            InspTitle.Text = string.Format("#{0}  {1}{2}", n.Id, AutoNodeInfo.Title(n.Kind),
                n.Kind == AutoNodeKind.VisionOp ? "  ·  " + (n.OpName ?? "(未选算子)") : "");

            AddSection("流程连线");
            AddExitCombo(n, "主出口 →", false);
            AddExitCombo(n, "副出口 →", true);

            AddSection("输入内容");
            var slots = AutoNodeInfo.StringSlots(n.Kind);
            if (slots.Length > 0)
            {
                for (int i = 0; i < slots.Length; i++) AddStringSlot(n, i, slots[i].Name, slots[i].Tip, slots[i].MultiLine);
            }
            else
            {
                AddStringSlot(n, 0, LegacySlotLabel(n.Kind, 0), "支持 {变量} 插值", false);
                AddStringSlot(n, 1, LegacySlotLabel(n.Kind, 1), "支持 {变量} 插值", false);
            }

            if (n.Kind == AutoNodeKind.SetVar) AddSetVarRefPickers(n);

            var defs = AutoNodeInfo.Params(n);
            if (defs != null && defs.Length > 0)
            {
                AddSection("参数");
                for (int i = 0; i < defs.Length; i++) AddParamRow(n, i, defs[i]);
            }

            if (NodeUsesTemplate(n))
            {
                AddSection("模板图");
                AddTemplateRow(n);
            }

            AddSection("操作");
            var del = new Button { Content = "删除该节点", Classes = { "flat" } };
            del.Click += (_, _) =>
            {
                _graph.Remove(n.Id);
                _selectedId = -1;
                RebuildCanvas();
                BuildInspector(true);
            };
            InspHost.Children.Add(del);
        }

        private static string LegacySlotLabel(AutoNodeKind kind, int slot) => (kind, slot) switch
        {
            (AutoNodeKind.Key, 0) => "文本 / 变量名",
            (AutoNodeKind.Key, 1) => "按键 / 组合键",
            (AutoNodeKind.Popup, 0) => "弹窗正文",
            (AutoNodeKind.SetVar, 0) => "变量名",
            (AutoNodeKind.SetVar, 1) => "固定文本（值来源=固定文本 时用）",
            (AutoNodeKind.Capture, 0) => "保存目录（留空=程序目录\\截图）",
            (AutoNodeKind.Condition, 0) => "变量名（判断依据=全局变量 时用）",
            (AutoNodeKind.Condition, 1) => "比较值",
            (AutoNodeKind.Loop, 0) => "变量名（次数来源=全局变量 时用）",
            (AutoNodeKind.Click, 0) => "变量名（坐标变量模式≠0；也可写两个变量 x,y）",
            (AutoNodeKind.Ocr, 0) => "识别结果写入的变量名",
            (_, 0) => "文本",
            (_, _) => "按键",
        };

        private void AddSection(string title)
        {
            var sec = new TextBlock
            {
                Text = title,
                Margin = new Thickness(0, InspHost.Children.Count == 0 ? 0 : 10, 0, 6),
            };
            Ui.Class(sec, "sectionheader");
            InspHost.Children.Add(sec);
        }

        private void AddExitCombo(AutoNode n, string label, bool alt)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lb = new TextBlock { Text = label, Classes = { "dimtext" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(lb, 0);
            row.Children.Add(lb);

            var cbo = new ComboBox();
            cbo.Items.Add("（无）");
            foreach (var other in _graph.Nodes.Where(x => x.Id != n.Id))
                cbo.Items.Add(string.Format("#{0} {1}", other.Id, AutoNodeInfo.Title(other.Kind)));
            int cur = alt ? n.AltNextId : n.NextId;
            var curNode = _graph.Get(cur);
            cbo.SelectedIndex = curNode == null ? 0 : cbo.Items.IndexOf(string.Format("#{0} {1}", curNode.Id, AutoNodeInfo.Title(curNode.Kind)));
            if (cbo.SelectedIndex < 0) cbo.SelectedIndex = 0;
            cbo.SelectionChanged += (_, _) =>
            {
                int id = ParseNodeId(cbo.SelectedItem as string);
                if (alt) n.AltNextId = id; else n.NextId = id;
                RebuildCanvas();
            };
            Grid.SetColumn(cbo, 1);
            row.Children.Add(cbo);
            InspHost.Children.Add(row);
        }

        private static int ParseNodeId(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith("#")) return -1;
            int sp = text.IndexOf(' ');
            string num = sp > 0 ? text.Substring(1, sp - 1) : text.Substring(1);
            return int.TryParse(num, out int id) ? id : -1;
        }

        private void AddStringSlot(AutoNode n, int slot, string label, string tip, bool multiLine)
        {
            InspHost.Children.Add(new TextBlock
            {
                Text = label,
                Classes = { "dimtext" },
                Margin = new Thickness(0, 2, 0, 3),
            });
            ToolTip.SetTip(InspHost.Children[InspHost.Children.Count - 1] as Control, tip);

            if (multiLine)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var preview = new TextBox
                {
                    Text = _graph.GetNodeString(n.Id, slot),
                    IsReadOnly = true,
                    MaxHeight = 60,
                    TextWrapping = TextWrapping.Wrap,
                };
                ToolTip.SetTip(preview, "点右侧「编辑…」用大窗口写（多行内容）");
                var btn = new Button { Content = "编辑…", Classes = { "flat" }, Margin = new Thickness(6, 0, 0, 0) };
                btn.Click += (_, _) =>
                {
                    // 表达式节点：编辑弹窗右上角带"表达式帮助"（其余槽位不带）
                    string help = (n.Kind == AutoNodeKind.Expression && slot == 0) ? ExpressionHelpText : null;
                    string text = EditLongText(label, _graph.GetNodeString(n.Id, slot), help);
                    if (text == null) return;
                    _graph.SetNodeString(n.Id, slot, text);
                    RebuildCanvas();
                    BuildInspector();
                };
                Grid.SetColumn(preview, 0);
                Grid.SetColumn(btn, 1);
                row.Children.Add(preview);
                row.Children.Add(btn);
                InspHost.Children.Add(row);
                return;
            }

                        var box = new TextBox { Text = _graph.GetNodeString(n.Id, slot) };
            ToolTip.SetTip(box, tip);
            box.TextChanged += (_, _) =>
            {
                _graph.SetNodeString(n.Id, slot, box.Text);
                UpdateNodeVisual(n.Id);
            };
            InspHost.Children.Add(box);
        }

        /// <summary>VisionMaster 风格参数行：左列参数名（固定宽/可换行/悬停说明）+ 右列控件，
        /// 行间细分割线；数值行当前值人话放在名称下方小字，随手柄/输入框实时更新。</summary>
        private void AddParamRow(AutoNode n, int index, TaskParamDesc def)
        {
            _graph.NormalizeParams();
            int cur = n.Params != null && index < n.Params.Length ? n.Params[index] : def.DefaultValue;

            var rowBorder = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Ui.Brush("Line"),
                Padding = new Thickness(0, 6, 0, 6),
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左列：参数名 + 取值图例 + 当前值人话，悬停给完整说明（图例 + 范围/默认 + 算子说明，永不空）
            var namePanel = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            string label = ParamDisplay.LabelText(def);
            namePanel.Children.Add(new TextBlock
            {
                Text = def.ParamName,
                Classes = { "dimtext" },
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(label) && label != def.ParamName)
                namePanel.Children.Add(new TextBlock
                {
                    Text = label,
                    Classes = { "fainttext" },
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            var valueLine = new TextBlock
            {
                Text = ParamDisplay.NameOf(def) + " = " + ParamDisplay.ValueText(def, cur),
                Classes = { "monotext" },
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            namePanel.Children.Add(valueLine);
            ToolTip.SetTip(namePanel, ParamDisplay.HelpText(def));
            Grid.SetColumn(namePanel, 0);

            // 右列：数值输入框（范围宽）或 滑杆+数值框（范围窄）
            var ctrlPanel = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(ctrlPanel, 1);
            if (def.Max - def.Min > 400)
            {
                var only = new TextBox { Text = cur.ToString(CultureInfo.InvariantCulture) };
                only.TextChanged += (_, _) =>
                {
                    if (_syncingParams) return;
                    if (!int.TryParse(only.Text, out int v)) return;
                    v = Math.Max(def.Min, Math.Min(def.Max, v));
                    SetParam(n, index, v, def, valueLine);
                };
                ctrlPanel.Children.Add(only);
            }
            else
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                var slider = new Slider
                {
                    Minimum = def.Min,
                    Maximum = def.Max,
                    Value = Math.Max(def.Min, Math.Min(def.Max, cur)),
                    IsSnapToTickEnabled = true,
                    TickFrequency = 1,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                var num = new TextBox { Text = cur.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(6, 0, 0, 0) };
                slider.ValueChanged += (_, _) =>
                {
                    if (_syncingParams) return;
                    int v = (int)Math.Round(slider.Value);
                    _syncingParams = true;
                    num.Text = v.ToString(CultureInfo.InvariantCulture);
                    _syncingParams = false;
                    SetParam(n, index, v, def, valueLine);
                };
                num.TextChanged += (_, _) =>
                {
                    if (_syncingParams) return;
                    if (!int.TryParse(num.Text, out int v)) return;
                    v = Math.Max(def.Min, Math.Min(def.Max, v));
                    _syncingParams = true;
                    slider.Value = v;
                    _syncingParams = false;
                    SetParam(n, index, v, def, valueLine);
                    num.Text = v.ToString(CultureInfo.InvariantCulture);
                };
                Grid.SetColumn(slider, 0);
                Grid.SetColumn(num, 1);
                row.Children.Add(slider);
                row.Children.Add(num);
                ctrlPanel.Children.Add(row);
            }

            rowGrid.Children.Add(namePanel);
            rowGrid.Children.Add(ctrlPanel);
            rowBorder.Child = rowGrid;
            InspHost.Children.Add(rowBorder);
        }

        private void SetParam(AutoNode n, int index, int value, TaskParamDesc def, TextBlock valueLine)
        {
            _graph.NormalizeParams();
            if (n.Params == null || index >= n.Params.Length) return;
            n.Params[index] = value;
            // 名称下方小字"当前值"随拖动手柄/输入框实时更新
            valueLine.Text = ParamDisplay.NameOf(def) + " = " + ParamDisplay.ValueText(def, value);
            UpdateNodeVisual(n.Id);
        }

        private void AddSetVarRefPickers(AutoNode n)
        {
            InspHost.Children.Add(new TextBlock
            {
                Text = "取值来源（比手打节点 id 友好）",
                Classes = { "dimtext" },
                Margin = new Thickness(0, 8, 0, 3),
            });

            var nodeCbo = new ComboBox();
            nodeCbo.Items.Add("（不用：按上面的“值来源”取）");
            foreach (var other in _graph.Nodes.Where(x => x.Id != n.Id && x.Id < n.Id))
                nodeCbo.Items.Add(string.Format("#{0} {1}", other.Id, AutoNodeInfo.Title(other.Kind)));
            int refId = n.Params != null && n.Params.Length > 1 ? n.Params[1] : 0;
            nodeCbo.SelectedIndex = 0;
            for (int i = 1; i < nodeCbo.Items.Count; i++)
                if (ParseNodeId(nodeCbo.Items[i] as string) == refId) nodeCbo.SelectedIndex = i;

            var itemCbo = new ComboBox();
            foreach (string name in NodeOutput.ItemNames) itemCbo.Items.Add(name);
            int refItem = n.Params != null && n.Params.Length > 2 ? Math.Clamp(n.Params[2], 0, NodeOutput.ItemNames.Length - 1) : 0;
            itemCbo.SelectedIndex = refItem;

            nodeCbo.SelectionChanged += (_, _) =>
            {
                _graph.NormalizeParams();
                n.Params[1] = ParseNodeId(nodeCbo.SelectedItem as string);
            };
            itemCbo.SelectionChanged += (_, _) =>
            {
                _graph.NormalizeParams();
                n.Params[2] = Math.Max(0, itemCbo.SelectedIndex);
            };
            InspHost.Children.Add(nodeCbo);
            InspHost.Children.Add(itemCbo);
        }

        private void AddTemplateRow(AutoNode n)
        {
            var t = _graph.GetNodeTemplate(n.Id);
            var tplInfo = new TextBlock
            {
                Text = t == null || t.Empty() ? "还没导入模板图" : string.Format("已导入 {0}x{1}", t.Cols, t.Rows),
                Margin = new Thickness(0, 0, 0, 4),
            };
            Ui.Class(tplInfo, t == null || t.Empty() ? "fainttext" : "dimtext");
            InspHost.Children.Add(tplInfo);

            if (t != null && !t.Empty())
            {
                // 模板缩略图：手上有好几个匹配节点时，"这个节点的模板是哪张"必须一眼可见
                var thumb = new Image
                {
                    Source = MatImage.ToThumbnail(t, 180),
                    Stretch = Stretch.Uniform,
                    MaxWidth = 180,
                    MaxHeight = 120,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };
                var thumbFrame2 = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(3),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Child = thumb,
                };
                ToolTip.SetTip(thumbFrame2, Ui.Tip("当前节点的模板图（按比例缩略）。"));
                thumbFrame2.Background = Ui.Brush("InputBg");
                thumbFrame2.BorderBrush = Ui.Brush("Line");
                InspHost.Children.Add(thumbFrame2);
            }

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var imp = new Button { Content = "导入模板图…", Classes = { "flat" } };
            imp.Click += async (_, _) =>
            {
                var picked = await PickImageAsync("导入模板图（小图，从屏幕或图片上裁下来的目标）");
                if (picked == null) return;
                var mat = Cv2.ImRead(picked, ImreadModes.Color);
                if (mat == null || mat.Empty())
                {
                    Error("这张图读不出来：" + picked);
                    return;
                }
                _graph.SetNodeTemplate(n.Id, mat);
                SetStatus(string.Format("节点 #{0} 已导入模板 {1}x{2}", n.Id, mat.Cols, mat.Rows));
                RebuildCanvas();
                BuildInspector();
            };
            row.Children.Add(imp);

            if (t != null && !t.Empty())
            {
                var clr = new Button { Content = "清除模板", Classes = { "flat" }, Margin = new Thickness(6, 0, 0, 0) };
                clr.Click += (_, _) =>
                {
                    _graph.SetNodeTemplate(n.Id, null);
                    RebuildCanvas();
                    BuildInspector();
                };
                row.Children.Add(clr);
            }
            InspHost.Children.Add(row);
        }

        private string EditLongText(string title, string current, string helpText = null)
        {
            var win = new EditLongTextWindow
            {
                Title = "编辑 —— " + title,
                Width = 700,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.Background = Ui.Brush("Bg");
            win.Foreground = Ui.Brush("Fg");
            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 右上角"帮助"（表达式节点等需要说明编写语法的槽位传 helpText）
            if (helpText != null)
            {
                var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
                var helpBtn = new Button
                {
                    Content = "表达式帮助",
                    Classes = { "flat" },
                    Height = 28,
                    Padding = new Thickness(14, 4, 14, 4),
                };
                ToolTip.SetTip(helpBtn, Ui.Tip("点开看表达式怎么编写：变量、赋值、运算符、函数清单与示例"));
                DockPanel.SetDock(helpBtn, Dock.Right);
                head.Children.Add(helpBtn);
                helpBtn.Click += (_, _) => ShowHelpWindow(title, helpText);
                Grid.SetRow(head, 0);
                grid.Children.Add(head);
            }

            var box = new TextBox
            {
                Text = current ?? "",
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Ui.Font("MonoFont"),
            };
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            string result = null;
            var ok = new Button { Content = "确定", Classes = { "primary" }, Width = 90 };
            var cancel = new Button { Content = "取消", Classes = { "flat" }, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (_, _) => { result = box.Text; win.ModalResult = true; win.Close(); };
            cancel.Click += (_, _) => { win.ModalResult = false; win.Close(); };
            bar.Children.Add(ok);
            bar.Children.Add(cancel);
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);

            win.Content = grid;
            return Ui.ShowModalResult(win) ? result : null;
        }

        /// <summary>长文本编辑弹窗（VisionMaster 风格：确定/取消 + 可选"表达式帮助"）</summary>
        private sealed class EditLongTextWindow : Window, Ui.IModalResult
        {
            public bool ModalResult { get; set; }
        }

        /// <summary>
        /// 表达式帮助内容（"T|" = 小节标题，"C|" = 等宽代码块，"N|" = 普通说明，空行 = 间距）。
        /// 与 ExpressionEvaluator 的语法保持同步。
        /// </summary>
        private static readonly string ExpressionHelpText =
            "T|表达式怎么写\n" +
            "N|表达式节点算一个值，可以写进变量，供后面的节点用 {变量名} 引用。\n" +
            "\n" +
            "T|1. 直接算一个值\n" +
            "C|   {中心X} + 20\n" +
            "C|   {得分} * 100\n" +
            "C|   拼接(\"订单 \", {订单号}, \" 已登记\")\n" +
            "\n" +
            "T|2. 变量赋值（用 ; 分隔多句，类型声明可写可不写）\n" +
            "C|   int n=1;  n=n+1;\n" +
            "C|   n = 1;\n" +
            "C|   n = n + 1;\n" +
            "N|赋值会写进全局变量表，后面的节点（弹窗/输入/截图目录名…）用 {n} 就能引用。\n" +
            "\n" +
            "T|3. 运算符\n" +
            "N|+ - * / % 四则与取模。+ 两边只要有一边是字符串就按拼接。\n" +
            "N|== != < <= > >= 比较（两边都能当数字就按数值比）。\n" +
            "N|&& || ! 逻辑；也能写 并且 / 或者 / 是 / 否。\n" +
            "\n" +
            "T|4. 常用函数（不区分大小写，中文名也行）\n" +
            "C|   取整/INT   四舍五入/ROUND   绝对值/ABS   最小/MIN   最大/MAX\n" +
            "C|   长度/LEN   子串/SUB   替换/REPLACE   包含/CONTAINS   拼接/CONCAT\n" +
            "C|   去空格/TRIM   大写/UPPER   小写/LOWER   正则/REGEX   现在/NOW\n" +
            "C|   如果/IF   毫米/MM   像素/PX   比例尺/SCALE\n" +
            "N|例：如果({得分} > 0.9, \"OK\", \"NG\")；正则({识别结果}, \"\\d{4,6}\")。\n" +
            "\n" +
            "T|5. 变量引用\n" +
            "N|花括号 {变量名} 和直接写变量名 n 都行；还没写过的变量会报错（提示先赋值）。\n" +
            "N|也可以把结果存进变量：在下面的“结果变量名”框里填名字即可。\n";

        /// <summary>弹一个可滚动、带代码块的帮助窗口（深色主题，与界面一致）</summary>
        private void ShowHelpWindow(string title, string helpText)
        {
            var win = new Window
            {
                Title = "帮助 —— " + title,
                Width = 640,
                Height = 580,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            win.Background = Ui.Brush("Bg");
            win.Foreground = Ui.Brush("Fg");

            var panel = new StackPanel { Margin = new Thickness(18) };
            foreach (string line in helpText.Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0)
                {
                    panel.Children.Add(new Border { Height = 8 });
                    continue;
                }
                if (line.StartsWith("T|"))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = line.Substring(2),
                        Classes = { "sectionheader" },
                        Margin = new Thickness(0, 6, 0, 6),
                    });
                }
                else if (line.StartsWith("C|"))
                {
                    var codeBd = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 3, 0, 3),
                        Child = new TextBlock
                        {
                            Text = line.Substring(2),
                            FontFamily = Ui.Font("MonoFont"),
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    };
                    codeBd.Background = Ui.Brush("CardBg");
                    (codeBd.Child as TextBlock).Foreground = Ui.Brush("Fg");
                    panel.Children.Add(codeBd);
                }
                else if (line.StartsWith("N|"))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = line.Substring(2),
                        Classes = { "dimtext" },
                        Margin = new Thickness(0, 2, 0, 2),
                    });
                }
            }
            panel.Children.Add(new Border { Height = 6 });

            var ok = new Button { Content = "关闭", Classes = { "primary" }, Width = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            ok.Click += (_, _) => win.Close();
            panel.Children.Add(ok);

            win.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            Ui.ShowModalResult(win);
        }

        private void UpdateNodeVisual(int id)
        {
            var n = _graph.Get(id);
            if (n == null) return;
            if (_nodeVisuals.TryGetValue(id, out var card) && card.Child is Grid g && g.Children.Count > 1
                && g.Children[1] is TextBlock body)
                body.Text = CanvasSummary(n);
        }

        // ================================================================ 运行

        private bool ValidateBeforeRun()
        {
            var issues = _graph.ValidateAll();
            IssueList.ItemsSource = issues.Select(i => new
            {
                TagText = i.NodeId > 0 ? string.Format("[{0}] #{1}", i.LevelText, i.NodeId) : "[" + i.LevelText + "]",
                i.Message,
                i.NodeId,
            }).ToList();

            var errors = issues.Where(i => i.IsError).ToList();
            if (errors.Count > 0)
            {
                Drawer.SelectedIndex = 2;      // 切到"校验结果"
                SetStatus(string.Format("校验不通过：{0} 条错误 —— 见下方校验结果", errors.Count));
                return false;
            }
            if (issues.Count > 0)
                SetStatus(string.Format("校验通过（另有 {0} 条提醒）", issues.Count));
            return true;
        }

        private void ValidateNow()
        {
            if (ValidateBeforeRun()) SetStatus("校验通过：可以直接运行");
        }

        private void StepOnce()
        {
            _stepLimit = _stepLimit <= 0 ? 1 : _stepLimit + 1;
            SetStatus(string.Format("单步：本轮限制执行 {0} 步（再点“运行”恢复跑到底）", _stepLimit));
            RunGraph(AutomationContext.DryRun, _stepLimit);
        }

        private void RunGraph(bool dry, int stepLimit = 0)
        {
            if (!ValidateBeforeRun()) return;
            if (stepLimit <= 0) _stepLimit = 0;

            LogBox.Clear();
            AutomationContext.DryRun = dry;
            AutomationContext.BeginRound();
            RunRecorder.Begin();

            SetStatus(dry ? "干跑中…（不会真的动鼠标键盘）" : "★真实执行中★ 把鼠标甩到屏幕角落可中止");
            // 让状态栏先画出来，再开始同步执行（运行器在 UI 线程跑）
            Dispatcher.UIThread.Invoke(() => { }, DispatcherPriority.Render);

            var result = AutomationRunner.Run(_graph, stepLimit > 0 ? stepLimit : 2000, AppendLog);
            _result?.DisposeImages();
            MatImage.ResetCache();          // 上一轮的图已释放，位图缓存不能继续引用它
            _result = result;

            UpdateStats(result);
            UpdatePreview(result);
            RefreshSummaries();          // 运行不改变节点结构：轻量刷新摘要（替代全量重建画布）

            string dir = RunRecorder.Save(_graph, result, result.Lines, AutomationContext.MmPerPixel);
            RefreshRecords();

            SetStatus(string.Format("{0}：{1} 步 / {2} 个动作 / {3}ms{4}   记录：{5}",
                dry ? "干跑完成" : "真实执行完成", result.Steps, result.Actions, result.TotalMs,
                string.IsNullOrEmpty(result.Error) ? "" : "  中止：" + result.Error, dir));
        }

        private void AppendLog(string line)
        {
            Vm.AppendLog(line);
            // 高频日志（节点图每步一行）时每行 ScrollToEnd 都会强制测量+布局；
            // 合并成"每帧最多滚一次"，内容照常全部追加，界面不卡。
            if (_scrollPending) return;
            _scrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollPending = false;
            });
        }

        private void UpdateStats(AutomationRunResult r)
        {
            string verdict = AutomationContext.FinalVerdict;
            Vm.StatVerdict = verdict.Length == 0 ? (r.Ok ? "未判定" : "中止") : verdict;
            StatVerdict.Foreground = Ui.Brush(verdict == "NG" || (!r.Ok) ? "Ng" : verdict == "OK" ? "Ok" : "FgDim");
            Vm.StatMs = r.TotalMs + " ms";
            Vm.StatSteps = string.Format("{0} / {1}", r.Steps, r.Actions);
            Vm.StatSkip = string.Format("{0} / {1}", r.SkippedActions, r.Ok ? 0 : 1);
            Vm.StatNote = string.IsNullOrEmpty(r.Error)
                ? string.Format("规则 {0} 条，不通过 {1} 条；变量 {2} 个",
                    AutomationContext.RuleResults.Count,
                    AutomationContext.RuleResults.Count(x => !x.Ok),
                    AutomationContext.Variables.Count)
                : "中止原因：" + r.Error;
        }

        private void UpdatePreview(AutomationRunResult r)
        {
            if (r == null)
            {
                PreviewImage.Source = null;
                Vm.PreviewCaption = "还没运行";
                return;
            }
            Mat src = null;
            string what;
            if (_selectedId > 0 && r.NodeImages.TryGetValue(_selectedId, out var nodeImg))
            {
                src = nodeImg;
                what = string.Format("节点 #{0} 的处理结果", _selectedId);
            }
            else
            {
                src = r.FinalImage ?? r.InputImage;
                what = r.FinalImage != null ? "最终结果图" : "输入图像";
            }
            PreviewImage.Source = MatImage.ToBitmapSource(src);
            Vm.PreviewCaption = src == null
                ? "这张图取不到"
                : string.Format("{0}  {1}x{2}", what, src.Cols, src.Rows);
        }

        private void IssueList_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (IssueList.SelectedItem == null) return;
            var prop = IssueList.SelectedItem.GetType().GetProperty("NodeId");
            if (prop == null) return;
            int id = (int)(prop.GetValue(IssueList.SelectedItem) ?? 0);
            if (id <= 0) return;
            _selectedId = id;
            RebuildCanvas();
            BuildInspector();
            SetStatus("已定位到节点 #" + id);
        }

        // ================================================================ 运行记录

        private void RefreshRecords()
        {
            // 快速列表：只列目录（解析 event.json 要读几十个文件，放在每轮运行后做会卡）
            var list = RunRecorder.ListFast(80).Select(r => new
            {
                Title = string.Format("{0}  {1}  {2}ms  {3} 步",
                    r.Time.ToString("MM-dd HH:mm:ss"), (r.Verdict.Length > 0 ? r.Verdict : (r.Ok ? "完成" : "失败")),
                    r.TotalMs, r.Steps),
                Sub = r.Dir,
                r.Dir,
            }).ToList();
            RecordList.ItemsSource = list;
        }

        private void BtnRefreshRec_Click(object sender, RoutedEventArgs e) => RefreshRecords();

        private void BtnOpenRecDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(RunRecorder.RootDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = RunRecorder.RootDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        private void RecordList_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (RecordList.SelectedItem == null) return;
            var prop = RecordList.SelectedItem.GetType().GetProperty("Dir");
            string dir = prop?.GetValue(RecordList.SelectedItem) as string;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        // ================================================================ 图 / 布局 / JSON / 配方

        private void SeedGraph()
        {
            if (_graph.Nodes.Count > 0) return;
            var st = _graph.Add(AutoNodeKind.Start, 60, 60);
            var en = _graph.Add(AutoNodeKind.End, 60, 200);
            st.NextId = en.Id;
        }

        private void AutoLayout()
        {
            var start = _graph.Nodes.FirstOrDefault(n => n.Kind == AutoNodeKind.Start);
            if (start == null) { Error("没有“开始”节点，无法自动布局"); return; }

            var level = new Dictionary<int, int> { [start.Id] = 0 };
            var order = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(start.Id);
            while (q.Count > 0)
            {
                int id = q.Dequeue();
                order.Add(id);
                var n = _graph.Get(id);
                if (n == null) continue;
                foreach (int nx in new[] { n.NextId, n.AltNextId })
                {
                    if (nx < 0 || _graph.Get(nx) == null) continue;
                    int lv = level[id] + 1;
                    if (!level.TryGetValue(nx, out int old) || lv > old) level[nx] = lv;
                    if (!order.Contains(nx)) q.Enqueue(nx);
                }
            }
            // 悬空节点放到最后
            foreach (var n in _graph.Nodes.Where(x => !level.ContainsKey(x.Id)))
            {
                level[n.Id] = level.Values.DefaultIfEmpty(0).Max() + 1;
                order.Add(n.Id);
            }

            var perLevel = new Dictionary<int, int>();
            foreach (int id in order)
            {
                var n = _graph.Get(id);
                if (n == null) continue;
                int lv = level.TryGetValue(id, out int l) ? l : 0;
                perLevel.TryGetValue(lv, out int row);
                perLevel[lv] = row + 1;
                n.X = 60 + lv * 270;
                n.Y = 60 + row * 96;
            }
            RebuildCanvas();
            SetStatus("已按拓扑分层自动布局");
        }

        private void ShowJson()
        {
            string json = ExportJson();
            var win = new Window
            {
                Title = "节点图 JSON（可复制/保存，就是保存到文件的同一份）",
                Width = 760,
                Height = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.Background = Ui.Brush("Bg");
            win.Foreground = Ui.Brush("Fg");
            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new TextBox
            {
                Text = json,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Ui.Font("MonoFont"),
            };
            grid.Children.Add(box);
            var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var copy = new Button { Content = "复制", Classes = { "flat" }, Width = 90 };
            copy.Click += (_, _) => { try { Ui.CopyToClipboard(json); SetStatus("JSON 已复制到剪贴板"); } catch { } };
            var close = new Button { Content = "关闭", Classes = { "primary" }, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (_, _) => win.Close();
            bar.Children.Add(copy);
            bar.Children.Add(close);
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);
            win.Content = grid;
            Ui.ShowModalResult(win);
        }

        /// <summary>导出节点图 JSON（与旧版编辑器格式一致，保证与已有 .autograph.json 互通）</summary>
        private string ExportJson()
            => AutomationGraphIO.Export(_graph);

        private async void SaveGraphFile()
        {
            var picked = await PickSaveGraphAsync();
            if (picked == null) return;
            System.IO.File.WriteAllText(picked, ExportJson());
            SetStatus("已保存：" + picked);
        }

        private async void LoadGraphFile()
        {
            var picked = await PickOpenGraphAsync();
            if (picked == null) return;
            LoadGraphFrom(picked);
        }

        private void LoadGraphFrom(string path)
        {
            try
            {
                AutomationGraphIO.Import(_graph, System.IO.File.ReadAllText(path));
                _selectedId = -1;
                RebuildCanvas();
                BuildInspector();
                ValidateBeforeRun();
                SetStatus("已加载：" + path);
            }
            catch (Exception ex) { Error("加载失败：" + ex.Message); }
        }


        // ================================================================ StorageProvider 文件对话框助手

        /// <summary>选择一张图片（返回本地路径；取消返回 null）</summary>
        private async Task<string> PickImageAsync(string title)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
                },
            });
            var f = files.FirstOrDefault();
            return f?.TryGetLocalPath();
        }

        /// <summary>打开节点图文件（.autograph.json）</summary>
        private async Task<string> PickOpenGraphAsync()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "加载节点图",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("节点图") { Patterns = new[] { "*.autograph.json", "*.json" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
                },
            });
            var f = files.FirstOrDefault();
            return f?.TryGetLocalPath();
        }

        /// <summary>保存节点图文件（.autograph.json）</summary>
        private async Task<string> PickSaveGraphAsync()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存节点图",
                SuggestedFileName = "工作流.autograph.json",
                DefaultExtension = "autograph.json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("节点图") { Patterns = new[] { "*.autograph.json" } },
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                },
            });
            return file?.TryGetLocalPath();
        }

        /// <summary>「示例」菜单：内置几个可直接载入的流程样本，免手搭节点图。</summary>
        private void SampleMenu()
        {
            var win = new Window
            {
                Title = "内置流程示例",
                Width = 480,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.Background = Ui.Brush("Bg");
            win.Foreground = Ui.Brush("Fg");
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "选择一个内置示例载入画布（当前画布会被替换）。样本用「全局变量 / 表达式 / 条件 / 循环 / 规则」演示节点图的常见写法，可直接运行或改造。",
                TextWrapping = TextWrapping.Wrap,
                Classes = { "dimtext" },
                Margin = new Thickness(0, 0, 0, 12),
            });
            // 示例卡片列表包进滚动条：样本多时窗口高度放不下，可上下滚动查看（DPI 缩放也不裁）
            var listHost = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listPanel = new StackPanel();
            foreach (var sample in AutomationSamples.Items)
            {
                var card = new Border
                {
                    Classes = { "card" },
                    Margin = new Thickness(0, 0, 0, 8),
                };
                var sp = new StackPanel { Margin = new Thickness(10) };
                sp.Children.Add(new TextBlock { Text = sample.Title, Classes = { "cardtitle" } });
                sp.Children.Add(new TextBlock
                {
                    Text = sample.Note,
                    Classes = { "cardsub" },
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                var load = new Button
                {
                    Content = "载入此示例",
                    Classes = { "flat" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                string json = sample.Json;
                load.Click += (_, _) => { LoadSampleJson(json, sample.Title); win.Close(); };
                sp.Children.Add(load);
                card.Child = sp;
                listPanel.Children.Add(card);
            }
            listHost.Content = listPanel;
            panel.Children.Add(listHost);
            win.Content = panel;
            Ui.ShowModalResult(win);
        }

        /// <summary>把一段节点图 JSON 载入画布（内置示例 / 粘贴的 JSON 共用）</summary>
        private void LoadSampleJson(string json, string title)
        {
            try
            {
                AutomationGraphIO.Import(_graph, json);
                _selectedId = -1;
                RebuildCanvas();
                BuildInspector();
                ValidateBeforeRun();
                SetStatus("已载入内置示例：" + title + "（干跑可安全试运行，真实执行会操作鼠标键盘）");
            }
            catch (Exception ex) { Error("载入示例失败：" + ex.Message); }
        }

        private void RecipeMenu()
        {
            var win = new Window
            {
                Title = "配方（参数预设）",
                Width = 420,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.Background = Ui.Brush("Bg");
            win.Foreground = Ui.Brush("Fg");
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "配方 = 把所有节点的参数与输入内容存成一份预设，换产品时一键套用。",
                Classes = { "dimtext" },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
            var save = new Button { Content = "保存当前为配方…", Classes = { "primary" }, Margin = new Thickness(0, 0, 0, 8) };
            save.Click += (_, _) => { SaveRecipe(); win.Close(); };
            var load = new Button { Content = "套用已有配方…", Classes = { "flat" } };
            load.Click += (_, _) => { ApplyRecipe(); win.Close(); };
            panel.Children.Add(save);
            panel.Children.Add(load);
            win.Content = panel;
            Ui.ShowModalResult(win);
        }

        private async void SaveRecipe()
        {
            var picked = await PickSaveRecipeAsync();
            if (picked == null) return;
            var data = new Dictionary<string, object>();
            foreach (var n in _graph.Nodes)
            {
                data[n.Id.ToString(CultureInfo.InvariantCulture)] = new
                {
                    kind = n.Kind.ToString(),
                    op = n.OpName,
                    @params = n.Params,
                    text = _graph.GetNodeText(n.Id),
                    key = _graph.GetNodeKey(n.Id),
                    s2 = _graph.GetNodeString(n.Id, 2),
                    s3 = _graph.GetNodeString(n.Id, 3),
                };
            }
            System.IO.File.WriteAllText(picked,
                Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
            SetStatus("配方已保存：" + picked);
        }


        /// <summary>保存配方文件（.recipe.json）</summary>
        private async Task<string> PickSaveRecipeAsync()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存配方",
                SuggestedFileName = "配方.recipe.json",
                DefaultExtension = "recipe.json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("配方") { Patterns = new[] { "*.recipe.json" } },
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                },
            });
            return file?.TryGetLocalPath();
        }

        /// <summary>打开配方文件（.recipe.json）</summary>
        private async Task<string> PickOpenRecipeAsync()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "套用配方",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("配方") { Patterns = new[] { "*.recipe.json", "*.json" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
                },
            });
            var f = files.FirstOrDefault();
            return f?.TryGetLocalPath();
        }

        
        private async void ApplyRecipe()
        {
            var picked = await PickOpenRecipeAsync();
            if (picked == null) return;
            try
            {
                var data = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JObject>>(
                        System.IO.File.ReadAllText(picked));
                int applied = 0;
                foreach (var kv in data)
                {
                    if (!int.TryParse(kv.Key, out int id)) continue;
                    var n = _graph.Get(id);
                    if (n == null) continue;
                    var p = kv.Value["params"]?.ToObject<int[]>();
                    if (p != null) n.Params = p;
                    string text = kv.Value.Value<string>("text");
                    string key = kv.Value.Value<string>("key");
                    if (text != null) _graph.SetNodeText(id, text);
                    if (key != null) _graph.SetNodeKey(id, key);
                    string s2 = kv.Value.Value<string>("s2");
                    string s3 = kv.Value.Value<string>("s3");
                    if (s2 != null) _graph.SetNodeString(id, 2, s2);
                    if (s3 != null) _graph.SetNodeString(id, 3, s3);
                    applied++;
                }
                RebuildCanvas();
                BuildInspector();
                SetStatus(string.Format("配方已套用：命中 {0} 个节点（按节点 id 匹配，结构不同时只套用同 id 的节点）", applied));
            }
            catch (Exception ex) { Error("配方套用失败：" + ex.Message); }
        }

        private void Error(string message)
        {
            SetStatus("出错：" + message);
            Ui.Notice(message, "出错了", true);
        }
        /// <summary>顶栏主题色点点击：应用主题并刷新本页色点（全软件风格统一）</summary>
        private void ThemeDot_Click(object sender, PointerPressedEventArgs e)
        {
            ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
        }

    }
}
