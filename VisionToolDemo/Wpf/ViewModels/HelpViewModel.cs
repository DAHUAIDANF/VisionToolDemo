using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VisionToolDemo.Vision.Automation;
using VisionToolDemo.Wpf.Mvvm;

namespace VisionToolDemo.Wpf.ViewModels
{
    /// <summary>
    /// 使用说明页 ViewModel：正文、⑦⑧ 动态算子/节点清单、⑪ 图文手册、复制命令。
    ///
    /// 图文卡片（①~⑥⑨⑩）是静态 XAML，由 View 直接渲染；
    /// ⑦⑧ 清单与 ⑪ 图文手册数据化（HelpGroup / HelpRow / HelpManualSection），
    /// 新加算子/节点/截图后自动同步。
    /// </summary>
    public sealed class HelpViewModel : ViewModelBase
    {
        private string _bodyText = "";
        private string _opCountText = "";
        private string _nodeCountText = "";

        /// <summary>⑩ 完整说明正文</summary>
        public string BodyText { get => _bodyText; private set => Set(ref _bodyText, value); }

        /// <summary>⑦ 视觉算子计数行</summary>
        public string OpCountText { get => _opCountText; private set => Set(ref _opCountText, value); }

        /// <summary>⑧ 自动化节点计数行</summary>
        public string NodeCountText { get => _nodeCountText; private set => Set(ref _nodeCountText, value); }

        /// <summary>⑦ 全部视觉算子（按分类分组）</summary>
        public ObservableCollection<HelpGroup> OperatorGroups { get; } = new();

        /// <summary>⑧ 全部自动化节点（按分类分组）</summary>
        public ObservableCollection<HelpGroup> NodeGroups { get; } = new();

        /// <summary>⑪ 软件使用图文手册（标题 + 说明 + 界面截图）</summary>
        public ObservableCollection<HelpManualSection> ManualSections { get; } = new();

        /// <summary>复制全部说明文字</summary>
        public ICommand CopyCommand { get; }

        /// <summary>状态栏提示回调（View 订阅转发给主窗口）</summary>
        public event Action<string> StatusRequested;

        public HelpViewModel()
        {
            try { BodyText = HelpText.HelpTextPublic ?? ""; }
            catch (Exception ex) { BodyText = "说明读取失败：" + ex.Message; }

            // 全部视觉算子（有 OpName 的条目）
            var ops = NodeCatalog.All.Where(i => !string.IsNullOrEmpty(i.OpName)).ToList();
            OpCountText = string.Format("共 {0} 个视觉算子，按功能分类：", ops.Count);
            FillGroup(OperatorGroups, ops);

            // 全部自动化节点（有 Kind 的条目）
            var nodes = NodeCatalog.All.Where(i => i.Kind.HasValue).ToList();
            NodeCountText = string.Format("共 {0} 种自动化节点，按分类：", nodes.Count);
            FillGroup(NodeGroups, nodes);

            BuildManualSections();

            CopyCommand = new RelayCommand(() =>
            {
                try
                {
                    Wpf.Ui.CopyToClipboard(HelpText.HelpTextPublic ?? "");
                    StatusRequested?.Invoke("使用说明已复制到剪贴板");
                }
                catch { /* 剪贴板被占用等异常忽略 */ }
            });
        }

        /// <summary>按 NodeCatalog.CategoryOrder 分类顺序分组填充</summary>
        private static void FillGroup(ObservableCollection<HelpGroup> host, System.Collections.Generic.List<CatalogItem> items)
        {
            foreach (string cat in NodeCatalog.CategoryOrder)
            {
                var group = items.Where(i => i.Category == cat).ToList();
                if (group.Count == 0) continue;
                var g = new HelpGroup(cat);
                foreach (var i in group) g.Items.Add(new HelpRow(i.Title + "：" + i.Sub));
                host.Add(g);
            }
        }

        /// <summary>
        /// ⑪ 软件使用图文手册：与《软件使用.md》同源的 16 个图文章节。
        /// 图片按相对路径 docs/img/xx.png 解析：优先程序输出目录，其次仓库根。
        /// </summary>
        private void BuildManualSections()
        {
            AddSection("1. 主界面与页面导航",
                "打开软件进入视觉流水线主界面：顶部工具栏（打开图片/视频/相机/实时跟踪/批量处理/运行整条链/导入导出），左侧算子库，中间图像区，右侧算子链与参数；左侧导航依次为 工作流、视觉、算子、记录、说明、训练、设置。",
                "01_主界面_视觉流水线.png");
            AddSection("2. 打开图片",
                "点「打开图片...」选择文件（也可拖拽到图像区，支持 JPG/PNG/BMP/RAW）；底部状态栏显示尺寸与缩放信息，鼠标悬停显示像素坐标与 BGR 值。",
                "02_打开图片.png");
            AddSection("3. 算子参数与 ROI",
                "双击左侧算子加入算子链，右侧显示该算子全部参数，修改即时生效；在图上拖框勾选「用选区做 ROI」即只在框内作业，也可存成模板供模板匹配类算子使用。",
                "03_算子参数面板.png");
            AddSection("4. 运行流水线与结果",
                "点「运行整条链」顺序执行全部步骤，链中与状态栏显示每步耗时与关键指标（如 Canny 边缘 4034 点）；「结果/原图」页签切换查看，结果图上右键可保存图片。",
                "04_流水线运行结果.png");
            AddSection("5. 实时目标跟踪",
                "顶部「实时跟踪...」打开窗口：打开视频/摄像头 → 暂停后在画面上拖框框住目标 → 恢复播放逐帧跟踪；支持 KCF 追踪器、重置跟踪、镜像画面，目标丢失画红框提示。",
                "05_实时跟踪窗口.png");
            AddSection("6. 自动化工作流画布",
                "工作流页把「截图→识别→判定→点击/按键→弹窗」搭成自动流程：左侧节点库 135 项（111 算子 + 24 自动化节点），双击或拖拽上画布，圆点连线，中键平移、滚轮缩放、Delete 删除。",
                "06_自动化工作流.png");
            AddSection("7. 内置流程示例",
                "点顶部「示例」弹出内置示例窗口，含 5 个样本：变量与表达式、条件分支、循环提前跳出、规则与总判定、网页自动化；选一个点「载入」即可自动搭好节点图。",
                "07_内置示例弹窗.png");
            AddSection("8. 载入示例后的节点图",
                "载入后画布自动生成完整节点图（含变量、表达式、条件、循环、弹窗等节点），节点上显示执行状态与耗时，右侧统计区显示总判定/耗时/步数/变量。",
                "08_示例已载入.png");
            AddSection("9. 干跑模式",
                "「干跑」只写日志、不真实操作（鼠标/键盘/弹窗/浏览器都不会真的执行），适合先验证流程逻辑；日志逐节点记录变量变化与分支走向。",
                "09_干跑完成.png");
            AddSection("10. 真实执行与弹窗提示",
                "「运行」真实执行：会真的截图、点鼠标、按键盘、弹窗；弹窗提示节点支持 {变量} 插值、确认/取消、自动关闭倒计时；执行中把鼠标甩到屏幕角落可立即中止。",
                "10_真实执行弹窗提示.png");
            AddSection("11. 运行日志",
                "底部「运行日志/校验结果/运行记录」三个页签：执行日志逐节点记录（含条件分支走向如 count=10>5 → 走「是」分支）、校验列出连接错误与警告、运行记录可回看历史。",
                "11_运行日志.png");
            AddSection("12. 算子与节点总览",
                "左侧导航「算子」进入总览页：135 项（111 视觉算子 + 24 自动化节点）按分类统一列出，可搜索、可看每个条目的功能说明，是了解全部能力的速查页。",
                "12_算子与节点总览.png");
            AddSection("13. 运行记录页",
                "左侧导航「记录」：每次运行流水线/节点图自动生成一条记录（含 trace、事件、结果图），按时间倒序列出，选中后可打开该轮目录与报告，回看追溯。",
                "13_运行记录页.png");
            AddSection("14. 使用说明页",
                "左侧导航「说明」即本页：基本流程、算子链、ROI、手势算子、自动化节点、深度学习与跟踪的图文说明，⑦⑧ 动态枚举全部算子与节点，⑩ 完整文字可一键「复制全部」。",
                "14_使用说明页.png");
            AddSection("15. 深度学习训练",
                "左侧导航「训练」：1 选训练集目录（每个子文件夹=一个类别）→ 2 框选标注（可选，支持人脸辅助预填）→ 3 设参数 → 4 训练监控（损失/准确率曲线、混淆矩阵）→ 5 导出 ONNX 回流水线推理。",
                "15_深度学习训练页.png");
            AddSection("16. 设置页与主题",
                "左侧导航「设置」：5 套界面主题（深蓝灰/深墨绿/深紫罗兰/暖橙红/亮色浅色）点击立即生效并自动保存；坐标系自检与鼠标定位自检可验证 ROI 框选一致性；可打开截图/运行记录目录。",
                "16_设置页_主题.png");
        }

        /// <summary>添加一个图文手册章节</summary>
        private void AddSection(string title, string description, string imageFile)
            => ManualSections.Add(new HelpManualSection(title, description, imageFile));
    }

    /// <summary>⑦⑧ 清单的一个分类分组</summary>
    public sealed class HelpGroup
    {
        public string Category { get; set; }
        public ObservableCollection<HelpRow> Items { get; } = new();
        public HelpGroup(string category) { Category = category; }
    }

    /// <summary>⑦⑧ 清单的一行（标题：说明）</summary>
    public sealed class HelpRow
    {
        public string Text { get; set; }
        public HelpRow(string text) { Text = text; }
    }

    /// <summary>
    /// ⑪ 图文手册的一个章节：标题 + 说明 + 界面截图。
    /// 图片解析顺序：程序输出目录 docs\img\xx.png → 仓库根 docs\img\xx.png；
    /// 都找不到时 ImagePath 为 null，界面留空但不报错。
    /// 【Avalonia】Image.Source 只认 IImage，不认字符串路径——这里预加载成 Bitmap。
    /// </summary>
    public sealed class HelpManualSection
    {
        public string Title { get; }
        public string Description { get; }
        public string ImagePath { get; }

        /// <summary>Avalonia 可直接显示的位图（解析失败为 null）</summary>
        public IImage? ImageBitmap { get; }

        public HelpManualSection(string title, string description, string imageFile)
        {
            Title = title;
            Description = description;
            ImagePath = ResolveImage(imageFile);
            ImageBitmap = LoadBitmap(ImagePath);
        }

        /// <summary>从磁盘路径加载 Avalonia 位图（文件不存在/损坏时返回 null）</summary>
        private static IImage? LoadBitmap(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                return new Bitmap(fs);
            }
            catch { return null; }
        }

        /// <summary>按相对文件名解析图片的绝对路径</summary>
        private static string ResolveImage(string file)
        {
            try
            {
                // 1) 程序输出目录：bin\Debug\net8.0\docs\img\xx.png
                string p1 = Path.Combine(AppContext.BaseDirectory, "docs", "img", file);
                if (File.Exists(p1)) return p1;
                // 2) 仓库根：向上找含 VisionToolDemo.csproj（或 软件使用.md）的目录
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    if (dir == null) break;
                    if (File.Exists(Path.Combine(dir.FullName, "VisionToolDemo.csproj")) ||
                        File.Exists(Path.Combine(dir.FullName, "软件使用.md")))
                    {
                        string p2 = Path.Combine(dir.FullName, "docs", "img", file);
                        return File.Exists(p2) ? p2 : null;
                    }
                }
            }
            catch { /* 路径异常忽略，返回 null */ }
            return null;
        }
    }
}
