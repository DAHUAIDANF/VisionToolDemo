using System;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 仿射变换 / 配准：把当前图对齐到一张"基准(模板)图"的位姿上，供后续量测复用同一坐标系。
    ///
    /// 为什么需要它：现有算子只有"透视校正"（四角点校正），没有任何把工件摆正并锁到
    /// 基准位姿的手段。没有配准，同一个工件换个摆放角度，前一张图标定的 ROI、量测线、
    /// 阈值全都不能复用——这是检测流程里最常见的痛点。
    ///
    /// 两种模式：
    ///   · 特征点配准 —— ORB 特征 + 匹配 + 估计相似/仿射变换。适用于有纹理、有角点的工件，
    ///                    可同时纠正平移/旋转/缩放。
    ///   · 相位相关配准 —— 频域互功率谱求平移（可选旋转先用特征粗估）。适用于纹理弱但
    ///                    光照稳定的图，对纯平移最稳。
    ///
    /// 输出：把输入图按估计出的变换重采样到基准图尺寸，使"基准图上的坐标 == 输出图上的坐标"。
    /// 变换矩阵与是否配准成功在摘要里回显。
    /// </summary>
    public class AffineAlignTask : IVisionTask, IResultReporter, IStatefulTask
    {
        public string TaskName => "仿射配准";

        /// <summary>UI 层赋值：基准图（模板）</summary>
        public Mat TemplateMat { get; set; }

        public string LastSummary { get; private set; } = "";

        /// <summary>最近一次估计出的 2x3 仿射矩阵（供扩展读取）；未成功时为 null</summary>
        public Mat LastTransform { get; private set; }

        public bool Aligned { get; private set; }

        public string SaveState() => VisionHelper.SaveTemplateState(TemplateMat);

        public void LoadState(string state)
        {
            Mat m = VisionHelper.LoadTemplateState(state);
            if (m != null)
                TemplateMat = m;
        }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "模式 0特征点1相位相关",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "mode:{0}",
                Group = "配准"
            },
            new TaskParamDesc
            {
                // ORB 特征数上限：太少配不准，太多慢且引入外点
                ParamName = "特征点数",
                Min = 100,
                Max = 5000,
                DefaultValue = 1000,
                DisplayFormat = "feat:{0}",
                Group = "配准"
            },
            new TaskParamDesc
            {
                // RANSAC 重投影阈值（像素）：越大越宽容，越小越容易被外点带偏
                ParamName = "RANSAC阈值",
                Min = 1,
                Max = 20,
                DefaultValue = 3,
                DisplayFormat = "ransac:{0}px",
                Group = "配准"
            },
            new TaskParamDesc
            {
                // 允许估计旋转/缩放（相似变换）还是仅平移
                ParamName = "变换 0相似1仅平移",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "tf:{0}",
                Group = "配准"
            },
            new TaskParamDesc
            {
                // 输出尺寸：0 = 用基准图尺寸（推荐，保证坐标系一致），1 = 保持原图尺寸
                ParamName = "输出尺寸 0基准1原图",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "size:{0}",
                Group = "配准"
            },
            new TaskParamDesc
            {
                ParamName = "边缘填充 0黑1白2原色",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "border:{0}",
                Group = "配准"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            LastTransform?.Dispose();
            LastTransform = null;
            Aligned = false;

            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "仿射配准: 输入为空";
                return srcMat?.Clone();
            }

            if (TemplateMat == null || TemplateMat.Empty())
            {
                LastSummary = "仿射配准: 请先导入基准图（模板）";
                return srcMat.Clone();
            }

            int mode = paramValues[0];
            int featureCount = paramValues[1];
            double ransacThresh = paramValues[2];
            bool similarity = paramValues[3] == 0;
            bool useRefSize = paramValues[4] == 0;
            int borderMode = paramValues[5];

            using Mat srcGray = VisionHelper.ToGray(srcMat);
            using Mat refGray = VisionHelper.ToGray(TemplateMat);

            Mat warp = null;
            string how;
            if (mode == 1)
                warp = EstimateByPhase(srcGray, refGray, out how);
            else
                warp = EstimateByFeatures(srcGray, refGray, featureCount, ransacThresh, similarity, out how);

            Size outSize = useRefSize
                ? new Size(refGray.Cols, refGray.Rows)
                : new Size(srcMat.Cols, srcMat.Rows);

            BorderTypes bt = borderMode switch
            {
                1 => BorderTypes.Replicate,
                2 => BorderTypes.Reflect101,
                _ => BorderTypes.Constant,
            };
            Scalar borderValue = borderMode == 1
                ? new Scalar(255, 255, 255, 255)
                : new Scalar(0, 0, 0, 0);

            Mat dst = new();
            if (warp == null || warp.Empty())
            {
                // 配准失败：原样返回（绝不返回空图，避免后续算子收到空 Mat）
                LastSummary = "仿射配准: 未能估计变换（" + how + "），已按原图输出";
                if (srcMat.Cols == outSize.Width && srcMat.Rows == outSize.Height)
                    return srcMat.Clone();
                Cv2.Resize(srcMat, dst, outSize, 0, 0, InterpolationFlags.Linear);
                return dst;
            }

            Cv2.WarpAffine(srcMat, dst, warp, outSize,
                InterpolationFlags.Linear, bt, borderValue);

            LastTransform = warp.Clone();
            Aligned = true;

            double angle = Math.Atan2(warp.At<double>(1, 0), warp.At<double>(0, 0)) * 180.0 / Math.PI;
            double scale = Math.Sqrt(warp.At<double>(0, 0) * warp.At<double>(0, 0)
                                   + warp.At<double>(1, 0) * warp.At<double>(1, 0));
            LastSummary = string.Format("仿射配准: {0}  平移({1:F1},{2:F1}) 旋转{3:F2}° 缩放{4:F3}",
                how, warp.At<double>(0, 2), warp.At<double>(1, 2), angle, scale);
            return dst;
        }

        /// <summary>ORB 特征匹配 + 相似/仿射估计（RANSAC）</summary>
        private static Mat EstimateByFeatures(Mat srcGray, Mat refGray, int featureCount,
            double ransacThresh, bool similarity, out string how)
        {
            how = "特征点不足";
            using ORB orb = ORB.Create(featureCount);
            using Mat d1 = new(), d2 = new();
            orb.DetectAndCompute(srcGray, null, out KeyPoint[] k1, d1);
            orb.DetectAndCompute(refGray, null, out KeyPoint[] k2, d2);
            if (k1.Length < 8 || k2.Length < 8 || d1.Empty() || d2.Empty())
            {
                how = string.Format("特征点不足 ({0}/{1})", k1.Length, k2.Length);
                return null;
            }

            using BFMatcher matcher = new(NormTypes.Hamming);
            DMatch[] matches = matcher.Match(d2, d1);        // 基准 -> 当前
            Array.Sort(matches, (a, b) => a.Distance.CompareTo(b.Distance));
            int keep = Math.Min(matches.Length, Math.Max(8, matches.Length / 3));

            var srcPts = new System.Collections.Generic.List<Point2f>();
            var refPts = new System.Collections.Generic.List<Point2f>();
            for (int i = 0; i < keep; i++)
            {
                if (matches[i].Distance > 60) continue;
                Point2f rp = k2[matches[i].QueryIdx].Pt;   // 基准图上的点
                Point2f sp = k1[matches[i].TrainIdx].Pt;   // 当前图上的点
                refPts.Add(rp);
                srcPts.Add(sp);
            }

            if (srcPts.Count < 4)
            {
                how = "有效匹配点不足 (" + srcPts.Count + ")";
                return null;
            }

            // 求"当前图 -> 基准图"的变换
            using Mat srcArr = Mat.FromArray(srcPts.ToArray());
            using Mat refArr = Mat.FromArray(refPts.ToArray());
            using Mat inliers = new();
            // OpenCvSharp 4.13: EstimateAffine2D(from, to, out inliers, method, ransacReprojThreshold,
            // maxIters, confidence, refineIters)
            Mat m2x3 = similarity
                ? Cv2.EstimateAffinePartial2D(srcArr, refArr, inliers,
                    RobustEstimationAlgorithms.RANSAC, ransacThresh, 2000, 0.99, 10)
                : Cv2.EstimateAffine2D(srcArr, refArr, inliers,
                    RobustEstimationAlgorithms.RANSAC, ransacThresh, 2000, 0.99, 10);

            if (m2x3 == null || m2x3.Empty())
            {
                how = "RANSAC 未能求出变换";
                return null;
            }

            int inlierCount = inliers.Empty() ? srcPts.Count : Cv2.CountNonZero(inliers);
            how = string.Format("特征点 {0}对/内点{1}", srcPts.Count, inlierCount);
            return m2x3;
        }

        /// <summary>相位相关求纯平移（对弱纹理图比特征点稳）</summary>
        private static Mat EstimateByPhase(Mat srcGray, Mat refGray, out string how)
        {
            how = "相位相关";
            // 相位相关要求同尺寸；不同则把当前图缩放到基准图尺寸再估平移
            using Mat s = new();
            if (srcGray.Size() != refGray.Size())
                Cv2.Resize(srcGray, s, refGray.Size(), 0, 0, InterpolationFlags.Linear);
            else
                srcGray.CopyTo(s);

            using Mat a = new(), b = new();
            s.ConvertTo(a, MatType.CV_32F);
            refGray.ConvertTo(b, MatType.CV_32F);
            // 去均值，避免直流分量压过平移峰
            Cv2.Subtract(a, new Scalar(Cv2.Mean(a).Val0), a);
            Cv2.Subtract(b, new Scalar(Cv2.Mean(b).Val0), b);

            Point2d shift;
            try
            {
                // window 不能传 null（OpenCvSharp 会抛 "Parameter 'window'"）。
                // 传一个全 1 的 Mat 等价于不施加窗函数（不做 Hann 加权），语义最接近"原始相位相关"。
                using Mat window = new Mat(a.Rows, a.Cols, MatType.CV_32F, Scalar.All(1));
                Point2d s2 = Cv2.PhaseCorrelate(a, b, window, out double response);
                shift = s2;
                if (response < 0.05)
                {
                    how = string.Format("相位相关置信度过低 ({0:F3})", response);
                    return null;
                }
            }
            catch (Exception ex)
            {
                how = "相位相关失败: " + ex.Message;
                return null;
            }

            if (double.IsNaN(shift.X) || double.IsNaN(shift.Y))
            {
                how = "相位相关返回 NaN（图像过平或尺寸过小）";
                return null;
            }

            // PhaseCorrelate(src, ref) 直接给出"把 src 平移多少才能与 ref 对齐"，
            // 也就是"当前图 -> 基准图"变换矩阵的平移项，**不需要再取负**。
            // 实测（内容整体平移 (+20,-14)）：PhaseCorrelate(cur,ref)=(-20,+14)，
            // 直接用它做 warp 残余位移为 0、RMS=0；取负则残余变成 (-40,+28)、RMS 反而更大。
            how = string.Format("相位相关 平移({0:F2},{1:F2})", shift.X, shift.Y);
            Mat m = new(2, 3, MatType.CV_64FC1);
            m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, shift.X);
            m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, shift.Y);
            return m;
        }
    }
}
