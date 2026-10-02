using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 掩膜算子：UI 层框选掩膜区域（矩形/内切圆）与笔刷自由涂抹，
    /// 命中区域（框内/笔迹）像素涂黑、其余保持原图不变，可对边缘做高斯羽化。
    /// "作用模式"参数决定生效方式：1=矩形框选（默认） 2=内切圆框选 3=笔刷自由涂抹。
    /// 掩膜坐标是整图坐标，处理始终针对整图（不做 ROI 裁剪）。
    /// 显示分工：掩膜区域轮廓/笔迹（红）由 UI 层叠加在原图上；结果图为掩膜后的图像。
    /// </summary>
    public class MaskTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "掩膜";

        /// <summary>最近一次 Execute 的结果摘要（掩膜区域尺寸与形状；未设掩膜时为空）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>掩膜区域（真实像素坐标），UI 层框选后赋值；框内涂黑、框外保持原图</summary>
        public Rect MaskRect { get; private set; }
        public bool HasMask { get; private set; }

        /// <summary>笔刷轨迹（真实像素坐标的折线段列表，每条为一条连续笔画）</summary>
        public List<List<Point>> BrushStrokes { get; } = [];

        /// <summary>笔刷线宽（真实像素）——UI 涂画与 Execute 涂黑共用，随参数面板调整</summary>
        public int BrushWidth { get; set; } = 30;

        /// <summary>存在有效掩膜（框选或至少一条笔迹）</summary>
        public bool HasAny => HasMask || BrushStrokes.Count > 0;

        /// <summary>UI层框选后调用</summary>
        public void SetMask(Rect rect)
        {
            MaskRect = rect;
            HasMask = true;
        }

        /// <summary>UI 层每收到一个鼠标移动点就追加进当前笔画；开始新笔画时先调用 BeginStroke</summary>
        public void BeginStroke(Point p)
        {
            BrushStrokes.Add([p]);
        }

        public void ContinueStroke(Point p)
        {
            if (BrushStrokes.Count == 0) return;
            List<Point> cur = BrushStrokes[^1];
            if (cur[^1] != p) cur.Add(p);
        }

        public void ClearMask()
        {
            MaskRect = new Rect();
            HasMask = false;
            BrushStrokes.Clear();
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                // 作用模式：1=仅框选（矩形） 2=仅框选（圆形） 3=仅笔刷。
                // 框选与笔刷轨迹始终同时记录，模式只决定哪个参与涂黑——
                // 矩形/圆形框大头，笔刷补涂细节，随时切换
                ParamName = "作用模式",
                Min = 1,
                Max = 3,
                DefaultValue = 1,
                DisplayFormat = "模式:{0}",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                ParamName = "羽化",
                Min = 0,
                Max = 50,
                DefaultValue = 0,
                DisplayFormat = "羽化:{0}px",
                ForceOdd = false
            },
            new TaskParamDesc
            {
                // 笔刷线宽（真实像素）：模式3（笔刷）下笔迹生效
                ParamName = "笔刷线宽",
                Min = 2,
                Max = 200,
                DefaultValue = 30,
                DisplayFormat = "线宽:{0}px",
                ForceOdd = false
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            if (srcMat == null || srcMat.Empty())
                return new Mat();
            LastSummary = "";
            if (!HasAny)
                return VisionHelper.ToBgrCopy(srcMat);

            int mode = paramValues.Length > 0 ? paramValues[0] : 1;    // 1=仅框选(矩形) 2=仅框选(圆形) 3=仅笔刷
            int feather = paramValues.Length > 1 ? paramValues[1] : 0; // 羽化半径（高斯核 2n+1）
            int brushW = paramValues.Length > 2 ? paramValues[2] : 30; // 笔刷线宽
            BrushWidth = brushW;

            bool useBrush = mode == 3; // 笔刷仅在模式 3 生效

            using (Mat srcBgr = VisionHelper.ToBgrCopy(srcMat))
            using (Mat mask = Mat.Zeros(srcMat.Rows, srcMat.Cols, MatType.CV_8UC1).ToMat())
            {
                var parts = new List<string>();

                if (useBrush && BrushStrokes.Count > 0)
                {
                    foreach (List<Point> stroke in BrushStrokes)
                    {
                        // 折线连接相邻采样点，圆头线帽让端点/拐角处为圆弧，笔感自然
                        for (int i = 1; i < stroke.Count; i++)
                            Cv2.Line(mask, stroke[i - 1], stroke[i], Scalar.White, brushW, LineTypes.AntiAlias);
                        if (stroke.Count == 1)
                            Cv2.Circle(mask, stroke[0], brushW / 2, Scalar.White, -1);
                    }
                    parts.Add($"笔刷{BrushStrokes.Count}笔");
                }

                if (!useBrush && HasMask)
                {
                    // 掩膜区域与图像范围求交（标准求交，负坐标也正确）
                    int x1 = System.Math.Max(0, MaskRect.X);
                    int y1 = System.Math.Max(0, MaskRect.Y);
                    int x2 = System.Math.Min(MaskRect.X + MaskRect.Width, srcMat.Cols);
                    int y2 = System.Math.Min(MaskRect.Y + MaskRect.Height, srcMat.Rows);
                    int w = x2 - x1;
                    int h = y2 - y1;
                    if (w > 0 && h > 0)
                    {
                        if (mode == 2)
                        {
                            // 圆形：框的内切圆（沿用旧参数语义）
                            int r = System.Math.Min(w, h) / 2;
                            if (r >= 1)
                            {
                                Cv2.Circle(mask, new Point(x1 + (w / 2), y1 + (h / 2)), r, Scalar.White, -1);
                                parts.Add($"圆 {w}×{h}");
                            }
                        }
                        else
                        {
                            Cv2.Rectangle(mask, new Rect(x1, y1, w, h), Scalar.White, -1);
                            parts.Add($"矩形 {w}×{h}");
                        }
                    }
                }

                if (parts.Count == 0)
                    return srcBgr.Clone(); // 该模式下没有任何掩膜内容：返回原图

                LastSummary = $"掩膜: {string.Join(" + ", parts)}{(feather > 0 ? $", 羽化 {feather}px" : "")} (线宽 {brushW}px)";

                // 羽化：高斯模糊掩膜，得到边缘渐变
                if (feather > 0)
                {
                    int k = (feather * 2) + 1;
                    Cv2.GaussianBlur(mask, mask, new OpenCvSharp.Size(k, k), 0);
                }

                // 命中区涂黑：输出 = src × (1 − mask/255)，其余系数为 1 保持原图
                Mat dst = new();
                using (Mat srcF = new())
                using (Mat inv = new())
                using (Mat maskF = new())
                using (Mat outF = new())
                using (Mat m1 = new())
                {
                    srcBgr.ConvertTo(srcF, MatType.CV_32FC3);
                    Cv2.BitwiseNot(mask, inv); // 反相：命中→0（涂黑），其余→255（保留）
                    inv.ConvertTo(m1, MatType.CV_32FC1, 1.0 / 255.0);
                    Cv2.Merge(new[] { m1, m1, m1 }, maskF);
                    Cv2.Multiply(srcF, maskF, outF);
                    outF.ConvertTo(dst, MatType.CV_8UC3);
                }
                return dst;
            }
        }

        /// <summary>掩膜状态序列化：[矩形x,y,w,h, 笔宽, [笔1x,笔1y..],[笔2..]]——矩形与笔刷可同时存在；
        /// 兼容旧格式（裸 int[] = 矩形、"rect"/"brush" 单一 tag）</summary>
        public string SaveState()
        {
            if (!HasAny) return null;
            var strokes = BrushStrokes
                .Where(s => s.Count > 0)
                .Select(s => s.SelectMany(p => new[] { p.X, p.Y }).ToArray())
                .ToArray();
            return JsonConvert.SerializeObject(new object[]
            {
                "both",
                HasMask ? MaskRect.X : -1,
                HasMask ? MaskRect.Y : -1,
                HasMask ? MaskRect.Width : -1,
                HasMask ? MaskRect.Height : -1,
                BrushWidth,
                strokes
            });
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                // 与 CaliperTask 一致先试旧格式（裸 int[] = 矩形）
                int[] r = JsonConvert.DeserializeObject<int[]>(state);
                if (r is { Length: 4 })
                {
                    SetMask(new Rect(r[0], r[1], r[2], r[3]));
                    return;
                }
            }
            catch (System.Exception)
            {
                // 不是矩形格式，继续试 tag 格式
            }
            try
            {
                Newtonsoft.Json.Linq.JArray a = Newtonsoft.Json.Linq.JArray.Parse(state);
                string tag = a.Count >= 1 ? a[0].ToString() : "";
                if (tag == "rect")
                {
                    SetMask(new Rect((int)a[1], (int)a[2], (int)a[3], (int)a[4]));
                    return;
                }
                if (tag == "brush")
                {
                    ClearMask();
                    BrushWidth = (int)a[1];
                    LoadStrokes((Newtonsoft.Json.Linq.JArray)a[2]);
                    return;
                }
                if (tag == "both" && a.Count >= 7)
                {
                    ClearMask();
                    int x = (int)a[1], y = (int)a[2], w = (int)a[3], h = (int)a[4];
                    if (x >= 0 && y >= 0 && w > 0 && h > 0)
                        SetMask(new Rect(x, y, w, h));
                    BrushWidth = (int)a[5];
                    LoadStrokes((Newtonsoft.Json.Linq.JArray)a[6]);
                }
            }
            catch (System.Exception)
            {
                // 损坏内容：忽略
            }
        }
        private void LoadStrokes(Newtonsoft.Json.Linq.JArray arr)
        {
            foreach (Newtonsoft.Json.Linq.JArray t in arr)
            {
                int[] flat = t.Select(v => (int)v).ToArray();
                if (flat.Length < 2) continue;
                BeginStroke(new Point(flat[0], flat[1]));
                for (int i = 2; i + 1 < flat.Length; i += 2)
                    ContinueStroke(new Point(flat[i], flat[i + 1]));
            }
        }
    }
}
