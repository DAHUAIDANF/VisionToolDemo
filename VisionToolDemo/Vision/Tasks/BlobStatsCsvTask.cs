using System;
using System.Globalization;
using System.IO;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 连通域统计 / CSV 导出：把二值图里每个连通域的量测值逐行导出，供 SPC / 追溯 / Excel 分析。
    ///
    /// 与 Blob分析、轮廓检测的区别：那两个只把结果画在图上、摘要写在标签里，数据出了程序就没了。
    /// 做产线统计（每件的位置/面积/长宽/圆度分布、抽检趋势）必须有结构化落盘，这就是本算子的用途。
    ///
    /// 输出：图上按面积排序给前 N 个连通域编号并画外接框；同时把全部（或前 N 个）连通域的
    /// 量测写成 CSV。CSV 路径为空时写到程序目录 blob_stats.csv，并在摘要里回显完整路径。
    /// 列：id,cx,cy,area,bbox_x,bbox_y,bbox_w,bbox_h,perimeter,circularity,aspect,fill_ratio
    /// </summary>
    public class BlobStatsCsvTask : IVisionTask, IResultReporter
    {
        public string TaskName => "连通域统计CSV";

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次导出到的 CSV 完整路径（UI 可用来提示/打开）</summary>
        public string LastCsvPath { get; private set; } = "";

        /// <summary>最近一次统计到的连通域数量</summary>
        public int LastCount { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "二值阈值",
                Min = 0,
                Max = 255,
                DefaultValue = 127,
                DisplayFormat = "thr:{0}",
                Group = "二值化"
            },
            new TaskParamDesc
            {
                ParamName = "自动阈值 0手动1Otsu",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "otsu:{0}",
                Group = "二值化"
            },
            new TaskParamDesc
            {
                ParamName = "最小面积",
                Min = 0,
                Max = 100000,
                DefaultValue = 20,
                DisplayFormat = "area≥{0}",
                Group = "轮廓"
            },
            new TaskParamDesc
            {
                ParamName = "最大面积",
                Min = 0,
                Max = 1000000,
                DefaultValue = 1000000,
                DisplayFormat = "area≤{0}",
                Group = "轮廓"
            },
            new TaskParamDesc
            {
                // 图上标注的个数上限：连通域可能上千个，全画会糊成一片
                ParamName = "标注个数上限",
                Min = 0,
                Max = 5000,
                DefaultValue = 30,
                DisplayFormat = "label≤{0}",
                Group = "标注"
            },
            new TaskParamDesc
            {
                // 0 = 全部导出（不受标注上限影响），1 = 只导出被标注的前 N 个
                ParamName = "导出范围 0全部1仅标注",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "csv:{0}",
                Group = "标注"
            },
            new TaskParamDesc
            {
                ParamName = "极性 0亮前景1暗前景",
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                DisplayFormat = "pol:{0}",
                Group = "二值化"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            LastCsvPath = "";
            LastCount = 0;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "连通域统计: 输入为空";
                return srcMat?.Clone();
            }

            int thr = paramValues[0];
            bool autoThr = paramValues[1] == 1;
            int minArea = paramValues[2];
            int maxArea = paramValues[3];
            int labelLimit = paramValues[4];
            bool onlyLabeled = paramValues[5] == 1;
            int polarity = paramValues[6];
            if (maxArea < minArea) maxArea = minArea;

            using Mat gray = VisionHelper.ToGray(srcMat);
            using Mat bin = new();
            double used = autoThr
                ? Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu)
                : SetThreshold(gray, bin, thr);
            if (polarity == 0)
                Cv2.BitwiseNot(bin, bin);

            using Mat labels = new();
            using Mat stats = new();
            using Mat centroids = new();
            int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids, PixelConnectivity.Connectivity8);

            // 收集通过面积筛选的连通域
            var rows = new System.Collections.Generic.List<BlobRow>();
            for (int i = 1; i < n; i++)   // 0 = 背景
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < minArea || area > maxArea) continue;
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                double cx = centroids.At<double>(i, 0);
                double cy = centroids.At<double>(i, 1);

                // 该连通域单独取出来算周长/圆度
                double perim = 0, circ = 0;
                using (Mat one = new())
                {
                    Cv2.Compare(labels, i, one, CmpTypes.EQ);
                    Cv2.FindContours(one, out Point[][] cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    if (cs.Length > 0 && cs[0].Length >= 3)
                    {
                        perim = Cv2.ArcLength(cs[0], true);
                        double a = Cv2.ContourArea(cs[0]);
                        circ = perim > 1e-9 ? 4 * Math.PI * a / (perim * perim) : 0;
                    }
                }

                rows.Add(new BlobRow
                {
                    Id = i,
                    Cx = cx,
                    Cy = cy,
                    Area = area,
                    Bx = x,
                    By = y,
                    Bw = w,
                    Bh = h,
                    Perimeter = perim,
                    Circularity = circ
                });
            }

            LastCount = rows.Count;
            rows.Sort((a, b) => b.Area.CompareTo(a.Area));

            Mat dst = VisionHelper.ToBgrCopy(srcMat);
            int drawn = Math.Min(labelLimit, rows.Count);
            for (int k = 0; k < drawn; k++)
            {
                BlobRow b = rows[k];
                var rect = new Rect(b.Bx, b.By, b.Bw, b.Bh);
                Cv2.Rectangle(dst, rect, Scalar.Lime, 2);
                Cv2.PutText(dst, (k + 1).ToString(), new Point(b.Bx, Math.Max(12, b.By - 4)),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Lime, 1);
            }

            int exportCount = onlyLabeled ? drawn : rows.Count;
            string path = ExportCsv(rows, exportCount, used);

            LastSummary = string.Format("连通域统计: 命中 {0} 个 (阈值 {1:F0})  导出 {2} 行 -> {3}",
                rows.Count, used, exportCount, string.IsNullOrEmpty(path) ? "失败" : path);
            return dst;
        }

        private static double SetThreshold(Mat gray, Mat bin, int thr)
        {
            Cv2.Threshold(gray, bin, thr, 255, ThresholdTypes.Binary);
            return thr;
        }

        /// <summary>把量测写成 CSV（UTF-8 带 BOM，Excel 打开中文列名不乱码）</summary>
        private string ExportCsv(System.Collections.Generic.List<BlobRow> rows, int count, double thr)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                string path = Path.Combine(dir, "blob_stats.csv");
                var sb = new StringBuilder();
                sb.AppendLine("# 连通域统计  阈值=" + thr.ToString("F1", CultureInfo.InvariantCulture)
                    + "  导出=" + count + "  总计=" + rows.Count + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("id,cx,cy,area,bbox_x,bbox_y,bbox_w,bbox_h,perimeter,circularity,aspect,fill_ratio");
                for (int k = 0; k < count && k < rows.Count; k++)
                {
                    BlobRow b = rows[k];
                    double aspect = b.Bh > 0 ? (double)b.Bw / b.Bh : 0;
                    double fill = (b.Bw > 0 && b.Bh > 0) ? (double)b.Area / (b.Bw * b.Bh) : 0;
                    sb.Append(k + 1).Append(',')
                      .Append(b.Cx.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Cy.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Area).Append(',')
                      .Append(b.Bx).Append(',').Append(b.By).Append(',')
                      .Append(b.Bw).Append(',').Append(b.Bh).Append(',')
                      .Append(b.Perimeter.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                      .Append(b.Circularity.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                      .Append(aspect.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                      .Append(fill.ToString("F4", CultureInfo.InvariantCulture))
                      .AppendLine();
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                LastCsvPath = path;
                return path;
            }
            catch (Exception ex)
            {
                LastSummary = "连通域统计: CSV 写入失败 " + ex.Message;
                return null;
            }
        }

        private struct BlobRow
        {
            public int Id;
            public double Cx, Cy;
            public int Area;
            public int Bx, By, Bw, Bh;
            public double Perimeter, Circularity;
        }
    }
}
