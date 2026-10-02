using System;

namespace VisionToolDemo.Vision.Automation
{
    /// <summary>
    /// 内置流程样本：自动化页「示例」菜单直接载入，免手搭节点图。
    /// 放在 Vision 层（而非 WPF 页面）是为了让无界面测试能反射加载验证。
    /// </summary>
    public static class AutomationSamples
    {
        /// <summary>一个内置样本：标题 / 说明 / 节点图 JSON</summary>
        public sealed class Sample
        {
            public string Title;
            public string Note;
            public string Json;
        }

        public static readonly Sample[] Items =
        {
            new Sample { Title = "变量与表达式", Note = "变量初值 → 表达式自增 → 记录 → 弹窗展示结果", Json = """
{"version": 5, "nodes": [{"Id": 1, "Kind": 0, "X": 120, "Y": 160, "NextId": 2, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 2, "Kind": 9, "X": 120, "Y": 220, "NextId": 3, "AltNextId": -1, "Params": [9, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 3, "Kind": 14, "X": 120, "Y": 280, "NextId": 4, "AltNextId": -1, "Params": [0], "Enabled": true, "OpName": ""}, {"Id": 4, "Kind": 9, "X": 120, "Y": 340, "NextId": 5, "AltNextId": -1, "Params": [6, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 5, "Kind": 8, "X": 120, "Y": 400, "NextId": 6, "AltNextId": -1, "Params": [0, 3], "Enabled": true, "OpName": ""}, {"Id": 6, "Kind": 12, "X": 120, "Y": 460, "NextId": -1, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}], "templates": {}, "texts": {"2": "count", "4": "结果", "5": "示例1：count 从 1 自增为 {count}"}, "keys": {"2": "1", "4": "{count}"}, "ops": {}, "extra": {"3:0": "{count}={count}+1", "3:1": "count"}}
""" },
            new Sample { Title = "条件分支", Note = "全局变量 count=10，条件比较后走「是/否」分支并记录", Json = """
{"version": 5, "nodes": [{"Id": 1, "Kind": 0, "X": 120, "Y": 160, "NextId": 2, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 2, "Kind": 9, "X": 120, "Y": 220, "NextId": 3, "AltNextId": -1, "Params": [9, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 3, "Kind": 3, "X": 120, "Y": 280, "NextId": 4, "AltNextId": 5, "Params": [1, 4], "Enabled": true, "OpName": ""}, {"Id": 4, "Kind": 9, "X": 120, "Y": 340, "NextId": 6, "AltNextId": -1, "Params": [6, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 5, "Kind": 9, "X": 120, "Y": 400, "NextId": 6, "AltNextId": -1, "Params": [6, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 6, "Kind": 8, "X": 120, "Y": 460, "NextId": 7, "AltNextId": -1, "Params": [0, 3], "Enabled": true, "OpName": ""}, {"Id": 7, "Kind": 12, "X": 120, "Y": 520, "NextId": -1, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}], "templates": {}, "texts": {"2": "count", "3": "count", "4": "分支", "5": "分支", "6": "示例2：count=10>5 → 走「是」分支"}, "keys": {"2": "10", "3": "5", "4": "是（count>5）", "5": "否"}, "ops": {}, "extra": {}}
""" },
            new Sample { Title = "循环提前跳出", Note = "循环体内递减 n，条件 n=0 时由「结束循环」提前跳出", Json = """
{"version": 5, "nodes": [{"Id": 1, "Kind": 0, "X": 120, "Y": 160, "NextId": 2, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 2, "Kind": 9, "X": 120, "Y": 220, "NextId": 3, "AltNextId": -1, "Params": [9, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 3, "Kind": 7, "X": 120, "Y": 280, "NextId": 7, "AltNextId": 4, "Params": [100, 0], "Enabled": true, "OpName": ""}, {"Id": 4, "Kind": 14, "X": 120, "Y": 340, "NextId": 5, "AltNextId": -1, "Params": [0], "Enabled": true, "OpName": ""}, {"Id": 5, "Kind": 3, "X": 120, "Y": 400, "NextId": 6, "AltNextId": 3, "Params": [1, 0], "Enabled": true, "OpName": ""}, {"Id": 6, "Kind": 22, "X": 120, "Y": 460, "NextId": 7, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 7, "Kind": 8, "X": 120, "Y": 520, "NextId": 8, "AltNextId": -1, "Params": [0, 3], "Enabled": true, "OpName": ""}, {"Id": 8, "Kind": 12, "X": 120, "Y": 580, "NextId": -1, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}], "templates": {}, "texts": {"2": "n", "5": "n", "7": "示例3：循环递减到 n=0 提前跳出"}, "keys": {"2": "5", "5": "0"}, "ops": {}, "extra": {"4:0": "{n}={n}-1", "4:1": "n"}}
""" },
            new Sample { Title = "规则与总判定", Note = "两条规则（95 过、60 不过）+ 结果聚合判 NG", Json = """
{"version": 5, "nodes": [{"Id": 1, "Kind": 0, "X": 120, "Y": 160, "NextId": 2, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 2, "Kind": 9, "X": 120, "Y": 220, "NextId": 3, "AltNextId": -1, "Params": [9, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 3, "Kind": 17, "X": 120, "Y": 280, "NextId": 4, "AltNextId": -1, "Params": [0, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 4, "Kind": 9, "X": 120, "Y": 340, "NextId": 5, "AltNextId": -1, "Params": [6, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 5, "Kind": 17, "X": 120, "Y": 400, "NextId": 6, "AltNextId": -1, "Params": [0, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 6, "Kind": 18, "X": 120, "Y": 460, "NextId": 7, "AltNextId": -1, "Params": [1, 1], "Enabled": true, "OpName": ""}, {"Id": 7, "Kind": 8, "X": 120, "Y": 520, "NextId": 8, "AltNextId": -1, "Params": [0, 3], "Enabled": true, "OpName": ""}, {"Id": 8, "Kind": 12, "X": 120, "Y": 580, "NextId": -1, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}], "templates": {}, "texts": {"2": "score", "4": "score", "7": "示例4：规则 95 通过、60 不通过 → 总判定 NG"}, "keys": {"2": "95", "4": "60"}, "ops": {}, "extra": {"3:0": "score", "3:1": "90", "3:2": "100", "5:0": "score", "5:1": "90", "5:2": "100"}}
""" },
            new Sample { Title = "网页自动化", Note = "打开网址 → 等「登录」出现并点击 → 填用户名/密码 → 提交 → 读整页文本 → 弹窗展示（需 Windows 实机跑浏览器）", Json = """
{"version": 5, "nodes": [{"Id": 1, "Kind": 0, "X": 120, "Y": 160, "NextId": 2, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}, {"Id": 2, "Kind": 23, "X": 120, "Y": 220, "NextId": 3, "AltNextId": -1, "Params": [0, 6, 0], "Enabled": true, "OpName": ""}, {"Id": 3, "Kind": 24, "X": 120, "Y": 280, "NextId": 4, "AltNextId": -1, "Params": [0, 0, 0, 15], "Enabled": true, "OpName": ""}, {"Id": 4, "Kind": 24, "X": 120, "Y": 340, "NextId": 5, "AltNextId": -1, "Params": [0, 1, 0], "Enabled": true, "OpName": ""}, {"Id": 5, "Kind": 24, "X": 120, "Y": 400, "NextId": 6, "AltNextId": -1, "Params": [0, 1, 0], "Enabled": true, "OpName": ""}, {"Id": 6, "Kind": 24, "X": 120, "Y": 460, "NextId": 7, "AltNextId": -1, "Params": [0, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 7, "Kind": 24, "X": 120, "Y": 520, "NextId": 8, "AltNextId": -1, "Params": [0, 4, 0], "Enabled": true, "OpName": ""}, {"Id": 8, "Kind": 9, "X": 120, "Y": 580, "NextId": 9, "AltNextId": -1, "Params": [6, 0, 0], "Enabled": true, "OpName": ""}, {"Id": 9, "Kind": 8, "X": 120, "Y": 640, "NextId": 10, "AltNextId": -1, "Params": [0, 3], "Enabled": true, "OpName": ""}, {"Id": 10, "Kind": 12, "X": 120, "Y": 700, "NextId": -1, "AltNextId": -1, "Params": [], "Enabled": true, "OpName": ""}], "templates": {}, "texts": {"8": "结果", "9": "网页自动化完成：{结果}"}, "keys": {"8": "{页面文本}"}, "ops": {}, "extra": {"2:1": "https://example.com", "3:0": "登录", "4:0": "用户名", "4:1": "admin", "5:0": "密码", "5:1": "123456", "6:0": "提交", "7:2": "页面文本"}}
""" },
        };
    }
}