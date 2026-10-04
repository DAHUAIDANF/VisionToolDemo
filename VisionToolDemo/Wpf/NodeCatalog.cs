using System;
using System.Collections.Generic;
using System.Linq;
using VisionToolDemo.Vision;
using VisionToolDemo.Vision.Automation;

namespace VisionToolDemo.Wpf
{
    /// <summary>
    /// 节点库里的一个条目（节点种类 或 视觉算子）。
    ///
    /// ⚠ 必须是**属性**而不是字段：WPF 的 {Binding Title} 只认属性，
    /// 绑到字段会静默失败（卡片渲染成空白，日志里只有一条 binding error）——
    /// 之前节点库/算子库"看不清里面的内容"就是这个原因。
    /// </summary>
    public sealed class CatalogItem
    {
        public string Title { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Category { get; set; } = "";
        public AutoNodeKind? Kind { get; set; }
        public string OpName { get; set; } = "";

        /// <summary>搜索用（标题 + 说明 + 分类 + 算子名）</summary>
        public string SearchKey { get; set; } = "";

        /// <summary>列表中兜底显示用（万一某处没给模板，至少能看到名字）</summary>
        public override string ToString() => Title.Length > 0 ? Title : OpName;
    }

    /// <summary>
    /// 节点库数据源：把"自动化节点种类"和"全部视觉算子"编成一份可搜索、可分类的清单。
    ///
    /// 参考 OpenVisionForge 的节点库：分类 + 搜索 + 卡片，而不是把所有东西平铺成一个下拉框
    /// （算子已经有 100 个，平铺完全没法用）。
    /// </summary>
    public static class NodeCatalog
    {
        private static List<CatalogItem> _items;

        public static IReadOnlyList<CatalogItem> All => _items ??= Build();

        private static List<CatalogItem> Build()
        {
            var list = new List<CatalogItem>();

            void AddKind(AutoNodeKind kind, string category, string sub)
            {
                var item = new CatalogItem
                {
                    Kind = kind,
                    Category = category,
                    Title = AutoNodeInfo.Title(kind),
                    Sub = sub,
                };
                item.SearchKey = (item.Title + " " + category + " " + sub + " " + kind).ToLowerInvariant();
                list.Add(item);
            }

            AddKind(AutoNodeKind.Start, "流程", "工作流的入口，只有一个");
            AddKind(AutoNodeKind.End, "流程", "工作流的出口");
            AddKind(AutoNodeKind.Condition, "流程", "按上次匹配是否命中，或按全局变量分支");
            AddKind(AutoNodeKind.Loop, "流程", "循环（次数可取自全局变量）");
            AddKind(AutoNodeKind.BreakLoop, "流程", "结束循环：循环体内提前跳出（条件节点分支连进来即可）");
            AddKind(AutoNodeKind.Wait, "流程", "等待固定毫秒数");
            AddKind(AutoNodeKind.WaitCondition, "流程", "轮询到条件满足（模板/变量/窗口），可超时中止");
            AddKind(AutoNodeKind.Popup, "流程", "弹窗提示（可确认/取消）");

            AddKind(AutoNodeKind.Capture, "采集", "屏幕截图（可存图、可隐藏本窗口）");
            AddKind(AutoNodeKind.Roi, "采集", "区域裁剪ROI：把当前图裁到指定区域并替换，后续节点只在该区域检测");
            AddKind(AutoNodeKind.Match, "定位匹配", "用模板匹配找目标，输出中心坐标");
            AddKind(AutoNodeKind.Ocr, "识别", "字符识别（OCR），文字进变量");
            AddKind(AutoNodeKind.Rule, "判定", "一条规则：数值范围/等于/包含/非空/下限");
            AddKind(AutoNodeKind.Aggregate, "判定", "汇总所有规则 → 总判定 OK/NG + 明细");
            AddKind(AutoNodeKind.Calibrate, "判定", "标定比例尺：1 像素 = 多少毫米");

            AddKind(AutoNodeKind.Click, "动作", "鼠标点击/双击/移动（坐标可来自变量）");
            AddKind(AutoNodeKind.Key, "动作", "键盘输入（内容可来自变量）");
            AddKind(AutoNodeKind.Window, "动作", "窗口激活/关闭/是否存在/最小化/最大化");
            AddKind(AutoNodeKind.Browser, "动作", "浏览器：按进程识别/激活，刷新/后退/前进/滚动，打开网址");
            AddKind(AutoNodeKind.BrowserElement, "动作", "浏览器元素：识别网页按钮/输入框/文字，点击/输入/读文本");

            AddKind(AutoNodeKind.SetVar, "变量与脚本", "记录信息到全局变量（可引用其它节点的输出）");
            AddKind(AutoNodeKind.Expression, "变量与脚本", "表达式计算（零依赖小语言）写进变量");
            AddKind(AutoNodeKind.Command, "变量与脚本", "命令行：跑外部程序，拿退出码与输出");
            AddKind(AutoNodeKind.Http, "变量与脚本", "HTTP 请求：上报结果或取参数");

            // 全部视觉算子（自动化专用的不列）
            foreach (string name in VisionTaskRegistry.GetVisionToolNames())
            {
                var task = VisionTaskRegistry.GetTask(name);
                int pc = task?.ParamDescriptions?.Length ?? 0;
                string cat = Classify(name);
                var item = new CatalogItem
                {
                    OpName = name,
                    Category = cat,
                    Title = name,
                    Sub = string.Format("{0} · {1} 个参数", cat, pc),
                };
                item.SearchKey = (name + " " + cat + " " + pc).ToLowerInvariant();
                list.Add(item);
            }

            return list;
        }

        /// <summary>算子按名字归类（用于节点库分组与筛选）</summary>
        public static string Classify(string opName)
        {
            string n = (opName ?? "").ToLowerInvariant();
            bool Has(params string[] keys) => keys.Any(k => n.Contains(k));

            if (Has("读取", "打开", "导入", "拍摄", "采集", "视频", "raw")) return "输入源";
            if (Has("标定", "比例", "棋盘", "畸变")) return "标定计量";
            if (Has("缩放", "裁剪", "旋转", "翻转", "仿射", "透视", "对齐", "拼")) return "几何变换";
            if (Has("滤波", "高斯", "中值", "均值", "锐化", "平滑", "去噪", "双边", "浓淡", "灰度校正", "gamma")) return "滤波增强";
            if (Has("二值", "阈值", "分割", "ostu", "otsu", "自适应")) return "阈值分割";
            if (Has("腐蚀", "膨胀", "开运算", "闭运算", "形态", "顶帽", "黑帽")) return "形态学";
            if (Has("边缘", "canny", "梯度", "sobel", "laplacian")) return "边缘";
            if (Has("轮廓", "连通", "blob", "区域", "凸包", "外接", "最小包围")) return "轮廓与Blob";
            if (Has("距离", "角度", "圆", "线", "面积", "周长", "拟合", "偏心", "同心", "平行", "垂直", "尺寸", "测量", "椭圆")) return "几何测量";
            if (Has("匹配", "定位", "特征", "形状", "模板")) return "定位匹配";
            if (Has("字符", "ocr", "条码", "二维码", "识别", "dm", "dpm", "文字", "深度学习", "onnx")) return "识别";
            if (Has("判定", "合格", "ok", "ng", "计数", "统计")) return "判定逻辑";
            if (Has("保存", "写", "导出", "记录")) return "记录输出";
            return "其它";
        }

        /// <summary>分类顺序（界面上 chips 与分组的显示顺序）</summary>
        public static readonly string[] CategoryOrder =
        [
            "流程", "采集", "定位匹配", "识别", "判定", "动作", "变量与脚本",
            "输入源", "标定计量", "几何变换", "滤波增强", "阈值分割", "形态学", "边缘",
            "轮廓与Blob", "几何测量", "判定逻辑", "记录输出", "其它",
        ];
    }
}
