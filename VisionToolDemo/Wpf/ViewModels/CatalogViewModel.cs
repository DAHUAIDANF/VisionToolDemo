using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 算子与节点总览页 ViewModel：搜索、分类筛选、选中详情的全部数据与命令。
    ///
    /// 视图行为留在 CatalogPage（View 层）：
    ///   · 详情区（参数清单/字符串槽卡片）依据 CurrentDetail 动态构建；
    ///   · 主题色点点击保留在 View。
    /// 分类 chips 已数据化（Chips + SelectCategoryCommand），由 ItemsControl 模板渲染。
    /// </summary>
    public sealed class CatalogViewModel : ViewModelBase
    {
        private readonly List<CatalogItem> _all = new();
        private readonly DispatcherTimer _searchTimer;
        private string _countText = "";
        private string _searchText = "";
        private string _category = "全部";
        private CatalogItem _selected;
        private CatalogDetail _currentDetail;

        /// <summary>总览计数（构造时生成）</summary>
        public string CountText { get => _countText; private set => Set(ref _countText, value); }

        /// <summary>搜索框文字（双向绑定；防抖 150ms 后刷新）</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                {
                    _searchTimer.Stop();
                    _searchTimer.Start();
                }
            }
        }

        /// <summary>过滤后的列表（ListBox.ItemsSource）</summary>
        public ObservableCollection<CatalogItem> Items { get; } = new();

        /// <summary>分类 chips（ItemsControl 数据化渲染）</summary>
        public ObservableCollection<CatalogChip> Chips { get; } = new();

        /// <summary>当前选中项（ListBox.SelectedItem 双向绑定）</summary>
        public CatalogItem SelectedItem
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value)) BuildDetail(value);
            }
        }

        /// <summary>当前详情数据（View 依据它构建详情区）</summary>
        public CatalogDetail CurrentDetail
        {
            get => _currentDetail;
            private set
            {
                if (Set(ref _currentDetail, value)) DetailChanged?.Invoke(value);
            }
        }

        /// <summary>点击分类 chip（CommandParameter = 分类名，空/全部 = 全部）</summary>
        public ICommand SelectCategoryCommand { get; }

        /// <summary>详情数据已就绪（View 重建详情区）</summary>
        public event Action<CatalogDetail> DetailChanged;

        public CatalogViewModel()
        {
            _all.AddRange(NodeCatalog.All);
            CountText = string.Format("共 {0} 项（{1} 个视觉算子 + {2} 种自动化节点）",
                _all.Count,
                _all.Count(i => !string.IsNullOrEmpty(i.OpName)),
                _all.Count(i => i.Kind.HasValue));

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Refresh(); };

            SelectCategoryCommand = new RelayCommand(p =>
            {
                string c = p as string;
                _category = c ?? "全部";
                Refresh();
                // 同步所有 chip 的选中态（属性通知让 ToggleButton 刷新）
                for (int i = 0; i < Chips.Count; i++)
                    Chips[i].IsSelected = _category == Chips[i].Category;
            });

            Refresh();
        }

        /// <summary>按搜索词 + 分类过滤并重建列表与分类 chips</summary>
        private void Refresh()
        {
            string q = (SearchText ?? "").Trim().ToLowerInvariant();
            var filtered = _all.Where(i => q.Length == 0 || i.SearchKey.Contains(q)).ToList();

            Chips.Clear();
            var cats = new List<string> { "全部" };
            cats.AddRange(NodeCatalog.CategoryOrder.Where(c => filtered.Any(i => i.Category == c)));
            foreach (string c in cats)
            {
                int count = c == "全部" ? filtered.Count : filtered.Count(i => i.Category == c);
                if (count == 0 && c != "全部") continue;
                Chips.Add(new CatalogChip
                {
                    Category = c,
                    Text = c + " " + count,
                    IsSelected = _category == c,
                });
            }

            Items.Clear();
            foreach (var item in filtered
                .Where(i => _category == "全部" || i.Category == _category)
                .OrderBy(i => Array.IndexOf(NodeCatalog.CategoryOrder, i.Category))
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
                Items.Add(item);
        }

        /// <summary>选中项 → 组装详情数据（字符串槽 + 参数清单 / 视觉算子参数）</summary>
        private void BuildDetail(CatalogItem item)
        {
            if (item == null)
            {
                CurrentDetail = null;
                return;
            }

            var d = new CatalogDetail
            {
                Title = item.Title,
                Sub = item.Category + (string.IsNullOrEmpty(item.OpName) ? " · 自动化节点" : " · 视觉算子"),
            };

            if (item.Kind.HasValue)
            {
                var slots = AutoNodeInfo.StringSlots(item.Kind.Value);
                if (slots.Length > 0)
                {
                    var sec = new CatalogSection("输入内容（字符串槽）");
                    for (int i = 0; i < slots.Length; i++)
                        sec.Rows.Add((string.Format("{0}. {1}{2}", i, slots[i].Name, slots[i].MultiLine ? "（多行）" : ""), slots[i].Tip));
                    d.Sections.Add(sec);
                }
                var defs = AutoNodeInfo.Params(item.Kind.Value);
                if (defs != null && defs.Length > 0)
                {
                    var sec = new CatalogSection("参数");
                    foreach (var p in defs) sec.Rows.Add((ParamDisplay.LabelText(p), ParamDisplay.HelpText(p)));
                    d.Sections.Add(sec);
                }
                if (slots.Length == 0 && (defs == null || defs.Length == 0))
                    d.Sections.Add(new CatalogSection("该节点没有可调参数（行为由固定规则决定）"));
            }
            else
            {
                var task = VisionTaskRegistry.GetTask(item.OpName);
                if (task == null)
                {
                    d.Sections.Add(new CatalogSection("取不到该算子（可能已被移除）"));
                }
                else
                {
                    var sec = new CatalogSection("参数（共 " + task.ParamDescriptions.Length + " 个）");
                    foreach (var p in task.ParamDescriptions)
                        sec.Rows.Add((ParamDisplay.LabelText(p), ParamDisplay.HelpText(p)));
                    d.Sections.Add(sec);
                    if (AutomationSupport.CanLocate(task))
                        d.Sections.Add(new CatalogSection("结果里能给出目标中心坐标（可用于点击/变量）"));
                    if (AutomationSupport.TryGetText(task, out _))
                        d.Sections.Add(new CatalogSection("结果里能给出文字（可用于变量/输入）"));
                }
            }
            CurrentDetail = d;
        }
    }

    /// <summary>分类 chip 数据项</summary>
    public sealed class CatalogChip
    {
        public string Category { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsSelected { get; set; }
    }

    /// <summary>详情区的数据模型（View 依据它构建 UI）</summary>
    public sealed class CatalogDetail
    {
        public string Title { get; set; } = "";
        public string Sub { get; set; } = "";
        public List<CatalogSection> Sections { get; } = new();
    }

    /// <summary>详情区的一个小节（标题 + 若干行 名称/说明）</summary>
    public sealed class CatalogSection
    {
        public string Header { get; set; }
        public List<(string name, string tip)> Rows { get; } = new();
        public CatalogSection(string header) { Header = header; }
    }
}
