using System.Linq;
using VisionToolDemo.Wpf;
using Xunit;

namespace VisionToolDemo.Tests
{
    /// <summary>
    /// 节点库目录回归测试：
    /// 1) 视觉算子循环会跳过与「专用节点」同名的算子（相机取图/鼠标点击/键盘输入/
    ///    浏览器元素/命令行/表达式/HTTP请求），避免节点库出现两个同名条目；
    /// 2) 所有条目的标题在节点库内唯一。
    /// </summary>
    public class NodeCatalogTests
    {
        [Fact]
        public void Catalog_Titles_AreUnique()
        {
            var all = NodeCatalog.All;
            Assert.NotNull(all);
            Assert.NotEmpty(all);

            var dup = all.GroupBy(i => i.Title).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, "节点库出现重复条目：" + string.Join("、", dup));
        }

        [Fact]
        public void Catalog_Contains_AutomationDedicatedNodes()
        {
            var all = NodeCatalog.All;
            // 专用节点（流程/采集/动作/变量）应在目录中
            foreach (var expect in new[] { "开始", "结束", "鼠标点击", "键盘输入", "相机取图", "表达式" })
                Assert.Contains(all, i => i.Title == expect);
        }

        [Fact]
        public void Catalog_VisionOps_StillCoverCoreOperators()
        {
            var all = NodeCatalog.All;
            foreach (var expect in new[] { "二值化", "深度学习推理", "条码/二维码识别", "直线卡尺" })
                Assert.Contains(all, i => i.Title == expect || i.Title.Contains(expect));
        }
    }
}
