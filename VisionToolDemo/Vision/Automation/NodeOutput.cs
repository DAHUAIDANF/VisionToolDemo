using System;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 一个节点跑完之后留下的"可被引用的输出"。
    ///
    /// 为什么需要它：节点图里"全局变量"节点要能把**任意一个已经跑过的节点**的结果记下来
    /// （比如第 3 个节点识别到的文字、第 5 个节点点击的坐标），而不是只认"上一个文字结果"。
    /// 运行器在每个动作节点执行后填一份，运行期按节点 id 存在 AutomationContext.NodeOutputs 里。
    /// </summary>
    public sealed class NodeOutput
    {
        /// <summary>节点摘要（IResultReporter.LastSummary）</summary>
        public string Summary = "";

        /// <summary>识别到的文字（OCR/条码）</summary>
        public string Text = "";

        /// <summary>是否命中过目标（模板匹配/形状匹配）</summary>
        public bool HasTarget;
        public float CenterX, CenterY;
        public double Score;
        public int Scale;

        /// <summary>截图保存出来的文件路径（开了"保存图片"时）</summary>
        public string SavedPath = "";

        /// <summary>是否真的做过点击/移动（干跑下也有坐标，只是没真的动鼠标）</summary>
        public bool HasClick;
        public int ClickX, ClickY;

        /// <summary>键盘输入实际发送的内容</summary>
        public string SentContent = "";

        /// <summary>命令行节点：退出码（-1 = 没等到/没执行）</summary>
        public int ExitCode = -1;

        /// <summary>命令行节点：标准输出 / 错误输出（已按 UTF-8 解码，过长会截断）</summary>
        public string Stdout = "";
        public string Stderr = "";

        /// <summary>窗口等"有没有"类结果："1" / "0"</summary>
        public string BoolResult = "";

        /// <summary>
        /// 取某一项的值；没有这一项就返回空串（调用方据此给出"该节点没有这个结果"的提示）。
        /// 序号与 SetVarTask.ParamDescriptions 里"取哪一项"的说明一一对应。
        /// </summary>
        public string Get(int item) => item switch
        {
            0 => Summary,
            1 => Text,
            2 => HasTarget ? F(CenterX) : "",
            3 => HasTarget ? F(CenterY) : "",
            4 => HasTarget ? F(CenterX) + "," + F(CenterY) : "",
            5 => HasTarget ? Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "",
            6 => Scale > 0 ? Scale.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            7 => SavedPath,
            8 => HasClick ? ClickX.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            9 => HasClick ? ClickY.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            10 => HasClick ? ClickX + "," + ClickY : "",
            11 => SentContent,
            12 => ExitCode >= 0 ? ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            13 => Stdout,
            14 => Stderr,
            15 => BoolResult,
            _ => "",
        };

        private static string F(float v)
            => Math.Round(v).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>每一项的名字（"全局变量"节点的下拉框与提示共用，顺序与 Get 一致）</summary>
        public static readonly string[] ItemNames =
        [
            "结果摘要", "文字结果", "目标中心X", "目标中心Y", "目标中心X,Y", "匹配得分", "命中倍率",
            "存图路径", "点击坐标X", "点击坐标Y", "点击屏幕坐标", "发送的按键内容",
            "命令退出码", "命令标准输出", "命令错误输出", "是否存在(1/0)",
        ];

        public static string ItemName(int item)
            => item >= 0 && item < ItemNames.Length ? ItemNames[item] : "第 " + item + " 项";
    }
}
