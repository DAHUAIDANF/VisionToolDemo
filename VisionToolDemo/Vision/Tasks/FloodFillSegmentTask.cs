using System;
using Newtonsoft.Json;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 漫水填充分割：从若干**种子点**出发，把颜色/灰度相近且连通的区域整片填出来。
    ///
    /// 与其他分割方式的区别：阈值/色差分割是"全图按值判定"，会把图像各处颜色相近但
    /// **不相连**的区域一起选中（例如背景和前景恰好同色）；漫水填充只看**连通性**，
    /// 只取与种子相连的那一片 —— 这是按"物体"而不是按"颜色"分割。
    ///
    /// 种子由 UI 层在图上点选注入；也可先用"自动种子"模式，由算子按网格撒点自动找。
    /// </summary>
    public class FloodFillSegmentTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "漫水填充分割";

        /// <summary>UI 层注入的种子点（图像坐标）</summary>
        public System.Collections.Generic.List<Point> Seeds { get; } = new();

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次填充区域占比</summary>
        public double FilledPercent { get; private set; }

        public void AddSeed(Point p)
        {
            if (!Seeds.Contains(p)) Seeds.Add(p);
        }

        public void ClearSeeds() => Seeds.Clear();

        public string SaveState()
        {
            var flat = new System.Collections.Generic.List<int>();
            foreach (var s in Seeds) { flat.Add(s.X); flat.Add(s.Y); }
            return flat.Count > 0 ? JsonConvert.SerializeObject(flat) : null;
        }

        public void LoadState(string state)
        {
            Seeds.Clear();
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                int[] p = JsonConvert.DeserializeObject<int[]>(state);
                if (p == null) return;
                for (int i = 0; i + 1 < p.Length; i += 2)
                    AddSeed(new Point(p[i], p[i + 1]));
            }
            catch { /* 损坏状态忽略 */ }
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "颜色容差",
                Min = 0, Max = 120, DefaultValue = 20,
                DisplayFormat = "loDiff:{0}",
                Group = "分割",
                Tip = "种子像素与相邻像素的允许色差。调大能跨过渐变/噪点填得更满；调小只填几乎同色的连通区。"
            },
            new TaskParamDesc
            {
                ParamName = "填充方式 0四连通1八连通",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "conn:{0}",
                Group = "分割",
                Tip = "4 连通不跨对角线（更严格），8 连通允许斜向蔓延（更容易漏到相邻区域）。"
            },
            new TaskParamDesc
            {
                ParamName = "固定范围 0关1开",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "fixed:{0}",
                Group = "分割",
                Tip = "开：每个像素只与**种子像素**比较（适合内部颜色均匀的背景）；" +
                      "关：与**相邻像素**逐级比较（适合带渐变的区域）。"
            },
            new TaskParamDesc
            {
                ParamName = "自动种子步长",
                Min = 0, Max = 200, DefaultValue = 0,
                DisplayFormat = "grid:{0}px",
                Group = "分割",
                Tip = "0 = 只用鼠标点选的种子。>0 = 按该步长在图上撒网格种子，自动填出多块区域，" +
                      "适合快速分块；此时鼠标种子仍会一起参与。"
            },
            new TaskParamDesc
            {
                ParamName = "最小区域面积",
                Min = 0, Max = 100000, DefaultValue = 100,
                DisplayFormat = "area≥{0}",
                Group = "后处理",
                Tip = "填出来的区域小于该面积就丢弃（滤掉种子落在噪点上产生的小斑块）。"
            },
            new TaskParamDesc
            {
                ParamName = "输出 0掩膜1叠加",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "out:{0}",
                Group = "输出",
                Tip = "0 = 黑白掩膜；1 = 原图上叠加高亮填充区域并标出种子点。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            FilledPercent = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "漫水填充: 输入为空";
                return srcMat?.Clone();
            }

            int loDiff = paramValues[0];
            int conn = paramValues[1] == 1 ? 8 : 4;
            bool fixedRange = paramValues[2] == 1;
            int grid = paramValues[3];
            int minArea = paramValues[4];
            int outMode = paramValues[5];

            using Mat bgr = VisionHelper.ToBgrCopy(srcMat);

            // 种子集合 = 鼠标点选 + （可选）网格自动撒点
            var seeds = new System.Collections.Generic.List<Point>(Seeds);
            if (grid > 0)
            {
                for (int y = grid / 2; y < bgr.Rows; y += grid)
                    for (int x = grid / 2; x < bgr.Cols; x += grid)
                        seeds.Add(new Point(x, y));
            }

            using Mat mask = new Mat(bgr.Rows + 2, bgr.Cols + 2, MatType.CV_8UC1, Scalar.Black);
            using Mat filled = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.Black);

            FloodFillFlags flags = conn == 8 ? FloodFillFlags.Link8 : FloodFillFlags.Link4;
            if (fixedRange) flags |= FloodFillFlags.FixedRange;

            using Mat work = bgr.Clone();
            var lo = new Scalar(loDiff, loDiff, loDiff);
            var hi = new Scalar(loDiff, loDiff, loDiff);

            int applied = 0;
            foreach (var s in seeds)
            {
                if (s.X < 0 || s.Y < 0 || s.X >= bgr.Cols || s.Y >= bgr.Rows) continue;
                try
                {
                    // MaskOnly：只更新 mask，不改动图像（省一次拷贝，也便于多轮标记）
                    Cv2.FloodFill(work, mask, s, Scalar.White, out Rect _, lo, hi,
                        flags | FloodFillFlags.MaskOnly);
                    applied++;
                }
                catch { /* 个别种子越界/异常不影响其余 */ }
            }

            // mask 比图像大 1px 边框，取内部区域作为填充结果
            using (Mat inner = new Mat(mask, new Rect(1, 1, bgr.Cols, bgr.Rows)))
                inner.CopyTo(filled);

            if (minArea > 0)
                RemoveSmallComponents(filled, minArea);

            int total = filled.Rows * filled.Cols;
            FilledPercent = 100.0 * Cv2.CountNonZero(filled) / total;

            Mat dst;
            if (outMode == 0)
            {
                dst = filled.Clone();
            }
            else
            {
                dst = VisionHelper.ToBgrCopy(srcMat);
                using Mat overlay = new Mat(dst.Size(), dst.Type(), new Scalar(0, 200, 0));
                using Mat blended = new Mat();
                Cv2.AddWeighted(dst, 0.65, overlay, 0.35, 0, blended);
                blended.CopyTo(dst, filled);
                foreach (var s in seeds)
                    Cv2.Circle(dst, s, 4, Scalar.Red, -1);
            }

            LastSummary = string.Format("漫水填充: 种子 {0} 个(生效 {1}) 填充 {2:F1}%  容差{3} {4}连通{5}",
                seeds.Count, applied, FilledPercent, loDiff, conn, fixedRange ? " 固定范围" : "");
            return dst;
        }

        private static void RemoveSmallComponents(Mat bin, int minArea)
        {
            using Mat labels = new Mat();
            using Mat stats = new Mat();
            using Mat centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);
            for (int i = 1; i < n; i++)
            {
                if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < minArea)
                {
                    using Mat comp = new Mat();
                    Cv2.Compare(labels, i, comp, CmpTypes.EQ);
                    bin.SetTo(Scalar.Black, comp);
                }
            }
        }
    }
}
