using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 涂抹算子：把指定颜色（灰度或彩色）涂到图上。
    /// 两种方式：0=鼠标涂抹（在「原图」上按住左键拖动，松手出结果）；1=坐标涂抹（参数指定圆心 X/Y，每次运行涂一个圆）。
    /// 轨迹存为整图像素坐标，Execute 时叠加到输入图输出；可清除重涂。颜色可选灰度或 RGB 彩色。
    /// </summary>
    public class PaintBrushTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "涂抹";

        /// <summary>最近一次 Execute 的结果摘要（笔数/点数/颜色）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>鼠标涂抹轨迹（真实像素坐标的折线段列表，每条为一条连续笔画），UI 层涂抹时追加</summary>
        public List<List<Point>> BrushStrokes { get; } = [];

        /// <summary>笔刷直径（真实像素），UI 涂抹与 Execute 共用，随参数面板"笔刷半径"同步</summary>
        public int BrushWidth { get; set; } = 12;

        /// <summary>是否已有鼠标涂抹内容</summary>
        public bool HasStroke => BrushStrokes.Count > 0;

        /// <summary>UI 层开始新笔画时调用</summary>
        public void BeginStroke(Point p) => BrushStrokes.Add([p]);

        /// <summary>UI 层每收到一个鼠标移动点就追加进当前笔画（跳过重复采样点）</summary>
        public void ContinueStroke(Point p)
        {
            if (BrushStrokes.Count == 0) return;
            List<Point> cur = BrushStrokes[^1];
            if (cur[^1] != p) cur.Add(p);
        }

        /// <summary>清除所有涂抹轨迹</summary>
        public void ClearStroke() => BrushStrokes.Clear();

        /// <summary>保存涂抹轨迹状态（存链文件用）：序列化为 JSON，含笔刷直径与全部笔画</summary>
        public string SaveState()
        {
            if (!HasStroke) return null;
            var strokes = BrushStrokes
                .Where(st => st.Count > 0)
                .Select(st => st.SelectMany(p => new[] { p.X, p.Y }).ToArray())
                .ToArray();
            return JsonConvert.SerializeObject(new object[] { "paint", BrushWidth, strokes });
        }

        /// <summary>从链文件恢复涂抹轨迹状态；格式不符或解析失败时安全忽略</summary>
        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                var arr = JsonConvert.DeserializeObject<object[]>(state);
                if (arr == null || arr.Length < 3 || (string)arr[0] != "paint") return;
                BrushWidth = Convert.ToInt32(arr[1]);
                BrushStrokes.Clear();
                // 轨迹存的是扁平 [x0,y0,x1,y1,...]，逐对还原成 Point
                var strokes = ((Newtonsoft.Json.Linq.JArray)arr[2])
                    .Select(ja => ((Newtonsoft.Json.Linq.JArray)ja).ToObject<int[]>())
                    .Select(pts =>
                    {
                        var list = new List<Point>();
                        for (int i = 0; i + 1 < pts.Length; i += 2)
                            list.Add(new Point(pts[i], pts[i + 1]));
                        return list;
                    });
                foreach (List<Point> st in strokes) BrushStrokes.Add(st);
            }
            catch { /* 旧格式/损坏状态：忽略，按无涂抹处理 */ }
        }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                ParamName = "颜色类型 0灰度1彩色",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "颜色:{0}",
                Group = "涂抹色",
                Tip = "0=涂成指定灰度；1=涂成指定的 RGB 彩色"
            },
            new TaskParamDesc { ParamName = "灰度值", Min = 0, Max = 255, DefaultValue = 128, DisplayFormat = "灰度:{0}", Group = "涂抹色", Tip = "颜色类型=0 时使用" },
            new TaskParamDesc { ParamName = "红色", Min = 0, Max = 255, DefaultValue = 255, DisplayFormat = "R:{0}", Group = "涂抹色", Tip = "颜色类型=1 时使用" },
            new TaskParamDesc { ParamName = "绿色", Min = 0, Max = 255, DefaultValue = 0, DisplayFormat = "G:{0}", Group = "涂抹色", Tip = "颜色类型=1 时使用" },
            new TaskParamDesc { ParamName = "蓝色", Min = 0, Max = 255, DefaultValue = 0, DisplayFormat = "B:{0}", Group = "涂抹色", Tip = "颜色类型=1 时使用" },
            new TaskParamDesc { ParamName = "笔刷半径", Min = 2, Max = 200, DefaultValue = 12, DisplayFormat = "半径:{0}px", Tip = "涂抹笔刷半径（真实像素）" },
            new TaskParamDesc
            {
                ParamName = "涂抹方式 0鼠标1坐标",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "方式:{0}",
                Tip = "0=鼠标涂抹：在「原图」上按住左键拖动；1=坐标涂抹：按下方 X/Y 坐标一次涂一个圆"
            },
            new TaskParamDesc { ParamName = "坐标X", Min = 0, Max = 100000, DefaultValue = 0, DisplayFormat = "X:{0}", Group = "坐标涂抹", Tip = "涂抹方式=1 时：圆心 X（像素）" },
            new TaskParamDesc { ParamName = "坐标Y", Min = 0, Max = 100000, DefaultValue = 0, DisplayFormat = "Y:{0}", Group = "坐标涂抹", Tip = "涂抹方式=1 时：圆心 Y（像素）" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat == null || srcMat.Empty()) return new Mat();
            int colorType = paramValues[0];                    // 0灰度 1彩色
            int grayV = paramValues[1];
            int rr = paramValues[2], gg = paramValues[3], bb = paramValues[4];
            int radius = System.Math.Max(1, paramValues[5]);   // 笔刷半径
            int mode = paramValues.Length > 6 ? paramValues[6] : 0;   // 0鼠标 1坐标
            int cx = paramValues.Length > 7 ? paramValues[7] : 0;
            int cy = paramValues.Length > 8 ? paramValues[8] : 0;
            BrushWidth = System.Math.Max(2, radius * 2);

            Scalar color = colorType == 1 ? new Scalar(bb, gg, rr) : new Scalar(grayV, grayV, grayV);
            Mat dst = VisionHelper.ToBgrCopy(srcMat);   // 输出恒为彩色，通道一致
            if (mode == 1)
            {
                // 坐标涂抹：一次性涂一个圆；坐标超出图片则提示
                bool inside = cx >= 0 && cy >= 0 && cx < dst.Cols && cy < dst.Rows;
                if (inside)
                    Cv2.Circle(dst, new Point(cx, cy), radius, color, -1, LineTypes.AntiAlias);
                LastSummary = inside
                    ? $"涂抹(坐标): 在 ({cx},{cy}) 涂了半径 {radius}px 的圆"
                    : "涂抹(坐标): 坐标超出图片范围，未涂抹";
            }
            else
            {
                // 鼠标涂抹：把轨迹折线画成圆头粗线（端点/拐角为圆弧，笔感自然）
                int points = 0;
                foreach (List<Point> stroke in BrushStrokes)
                {
                    for (int i = 1; i < stroke.Count; i++)
                        Cv2.Line(dst, stroke[i - 1], stroke[i], color, BrushWidth, LineTypes.AntiAlias);
                    if (stroke.Count == 1)
                        Cv2.Circle(dst, stroke[0], BrushWidth / 2, color, -1, LineTypes.AntiAlias);
                    points += stroke.Count;
                }
                string colorName = colorType == 1 ? $"RGB({rr},{gg},{bb})" : $"灰度{grayV}";
                LastSummary = $"涂抹(鼠标): {BrushStrokes.Count} 笔、{points} 个采样点，颜色 {colorName}，半径 {radius}px";
            }
            return dst;
        }
    }
}
