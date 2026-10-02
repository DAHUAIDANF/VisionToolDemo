using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 畸变 / FOV 标定：用棋盘格或圆点阵列标定内参+畸变系数，输出 FOV 与畸变指标。
    ///
    /// 为什么需要它：镜头畸变直接影响测量类算子的精度 —— 同一尺寸的物体在画面中心与
    /// 边缘测出来不一样。标定后既能给出畸变量化指标（TV 畸变/桶形），也能生成校正映射
    /// 供后续按"无畸变"坐标测量。
    ///
    /// 输出：
    ///   · 焦距 fx/fy、主点 cx/cy
    ///   · 畸变系数 k1,k2,p1,p2,k3 与总均方重投影误差（标定质量）
    ///   · TV 畸变（画面角点相对理想位置的径向偏差百分比）——判定镜头是否合格
    ///   · 水平/垂直 FOV（度）
    /// </summary>
    public class DistortionFovTask : IVisionTask, IResultReporter
    {
        public string TaskName => "畸变FOV标定";

        public string LastSummary { get; private set; } = "";

        public bool Ok { get; private set; }
        /// <summary>水平/垂直视场角（度）</summary>
        public double FovH { get; private set; } = double.NaN;
        public double FovV { get; private set; } = double.NaN;
        /// <summary>TV 畸变（%，正=枕形，负=桶形）</summary>
        public double TvDistortion { get; private set; } = double.NaN;
        /// <summary>重投影 RMS 误差（像素），越小标定越可信</summary>
        public double ReprojectionError { get; private set; } = double.NaN;
        public double Fx { get; private set; } = double.NaN;
        public double Fy { get; private set; } = double.NaN;

        /// <summary>最近一次标定得到的 3x3 内参矩阵（供外部生成校正映射）</summary>
        public Mat CameraMatrix { get; private set; }
        public Mat DistCoeffs { get; private set; }
        /// <summary>检测到的标定板图案（用于显示）</summary>
        public Point2f[] LastCorners { get; private set; }

        public TaskParamDesc[] ParamDescriptions => new[]
        {
            new TaskParamDesc
            {
                ParamName = "棋盘内角点列",
                Min = 3, Max = 30, DefaultValue = 9,
                DisplayFormat = "cols:{0}",
                Group = "标定板",
                Tip = "棋盘格**内部**角点的列数（不是格子数）。标准 10x7 棋盘通常填 9x6。"
            },
            new TaskParamDesc
            {
                ParamName = "棋盘内角点行",
                Min = 3, Max = 30, DefaultValue = 6,
                DisplayFormat = "rows:{0}",
                Group = "标定板",
                Tip = "棋盘格内部角点的行数。行列填反会检测不到，两值互换再试即可。"
            },
            new TaskParamDesc
            {
                ParamName = "方格边长mm",
                Min = 1, Max = 1000, DefaultValue = 10,
                DisplayFormat = "sq:{0}mm",
                Group = "标定板",
                Tip = "单个方格的物理边长。只影响焦距的物理单位，不影响畸变与 FOV。"
            },
            new TaskParamDesc
            {
                ParamName = "亚像素精化",
                Min = 0, Max = 1, DefaultValue = 1,
                DisplayFormat = "subpix:{0}",
                Group = "标定",
                Tip = "开：用 cornerSubPix 把角点精化到亚像素（强烈建议开，否则焦距误差可达数像素）。"
            },
            new TaskParamDesc
            {
                ParamName = "去畸变输出",
                Min = 0, Max = 1, DefaultValue = 0,
                DisplayFormat = "undistort:{0}",
                Group = "输出",
                Tip = "开：额外输出一张去畸变图（便于目视确认标定效果）。关闭则只画角点。"
            }
        };

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            Ok = false;
            FovH = FovV = TvDistortion = ReprojectionError = Fx = Fy = double.NaN;
            if (srcMat == null || srcMat.Empty())
            {
                LastSummary = "畸变FOV: 输入为空";
                return srcMat?.Clone();
            }

            int cols = paramValues[0], rows = paramValues[1];
            double squareMm = Math.Max(1, paramValues[2]);
            bool subpix = paramValues[3] == 1;
            bool undistortOut = paramValues[4] == 1;

            using Mat gray = VisionHelper.ToGray(srcMat);
            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            var corners = new List<Point2f>();
            bool found = Cv2.FindChessboardCorners(gray, new Size(cols, rows), out Point2f[] cornersArr,
                ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck);
            if (!found)
            {
                // 换用圆点阵列再试一次（有些标定板是圆点）
                found = Cv2.FindCirclesGrid(gray, new Size(cols, rows), out cornersArr,
                    FindCirclesGridFlags.SymmetricGrid);
                if (found) LastSummary = "(识别为圆点阵列) ";
            }

            if (!found || cornersArr == null || cornersArr.Length == 0)
            {
                LastSummary += "畸变FOV: 未检测到标定板图案（检查行列数是否与实际一致）";
                MatDraw.DrawText(dst, "未检测到标定板", 8, 24, Scalar.Red, 16);
                return dst;
            }

            if (subpix)
            {
                // cornerSubPix 要求输入为浮点单通道；窗口取角点邻域
                using Mat grayF = new Mat();
                gray.ConvertTo(grayF, MatType.CV_32FC1);
                Cv2.CornerSubPix(grayF, cornersArr, new Size(5, 5), new Size(-1, -1),
                    new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.01));
            }

            LastCorners = cornersArr;
            Cv2.DrawChessboardCorners(dst, new Size(cols, rows), cornersArr, true);

            // —— 构造物点（Z=0 平面），单位 mm ——
            var objPts = new List<Point3f>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    objPts.Add(new Point3f((float)(c * squareMm), (float)(r * squareMm), 0));

            // 逐点填入 Mat（Mat.FromArray 对 double[][] 支持不佳，直接 Set 更稳）
            var imgPts = cornersArr.Select(p => new Point2f(p.X, p.Y)).ToArray();
            using Mat objMat = new Mat(objPts.Count, 1, MatType.CV_32FC3);
            for (int i = 0; i < objPts.Count; i++)
                objMat.Set(i, 0, objPts[i]);
            using Mat imgMat = new Mat(imgPts.Length, 1, MatType.CV_32FC2);
            for (int i = 0; i < imgPts.Length; i++)
                imgMat.Set(i, 0, imgPts[i]);

            using Mat cam = new Mat();
            using Mat dist = new Mat();
            double rms;
            try
            {
                // 单张标定板：外参（rvec/tvec）此处用不到，直接丢弃（标定内参与畸变系数即可）
                rms = Cv2.CalibrateCamera(
                    new[] { objMat }, new[] { imgMat }, gray.Size(), cam, dist,
                    out Mat[] _, out Mat[] _2, CalibrationFlags.None,
                    new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 1e-6));
            }
            catch (Exception ex)
            {
                LastSummary += "畸变FOV: 标定失败 — " + ex.Message;
                return dst;
            }

            ReprojectionError = rms;
            Fx = cam.At<double>(0, 0);
            Fy = cam.At<double>(1, 1);

            // —— 退化检测 ——
            // 平面靶标正对拍摄时，焦距与物距**数学上不可分离**（homography 只有 8 自由度），
            // 标定会给出 RMS≈0 但 fx 任意大的解族。实测一张正对棋盘得到 fx=4.18e6、RMS=0。
            // 此时 FOV/TV 畸变都是假的，必须明确告知，而不是输出看似成功的数值。
            // 判据：真实镜头的焦距通常在图像边长的 0.2~20 倍之间。
            double imgSize = gray.Cols;
            bool degenerate = Fx > imgSize * 50 || Fx < imgSize * 0.05;
            if (degenerate)
            {
                FovH = FovV = TvDistortion = double.NaN;
                LastSummary = string.Format(
                    "畸变FOV: 焦距不可辨识（fx={0:E2}，超出合理范围）。" +
                    "平面靶标正对拍摄时焦距与物距不可分离，请**倾斜拍摄靶标**（建议倾斜 20°~45°）" +
                    "或多姿态多张再标定。重投影误差 {1:F3}px 在此情形下不能作为标定有效的依据。",
                    Fx, ReprojectionError);
                MatDraw.DrawText(dst, "标定退化: 焦距不可辨识", 8, 22, Scalar.Red, 16);
                MatDraw.DrawText(dst, "请倾斜靶标 20-45 度再标定", 8, 46, Scalar.Red, 13);
                return dst;
            }

            CameraMatrix?.Dispose(); CameraMatrix = cam.Clone();
            DistCoeffs?.Dispose(); DistCoeffs = dist.Clone();

            // 把标定结果发布到全局，供"畸变校正"等下游算子直接取用，
            // 免得用户手工把 9 个数字从一个算子抄到另一个算子。
            CalibrationStore.Set(Fx, Fy, cam.At<double>(0, 2), cam.At<double>(1, 2),
                [dist.At<double>(0, 0), dist.At<double>(0, 1), dist.At<double>(0, 2),
                 dist.At<double>(0, 3), dist.At<double>(0, 4)],
                squareMm > 0 ? squareMm / (Fx / cam.At<double>(0, 0)) : 0);

            // —— FOV：用图像对角/半边与焦距推算 ——
            double w = gray.Cols, h = gray.Rows;
            FovH = 2 * Math.Atan(w / (2 * Fx)) * 180 / Math.PI;
            FovV = 2 * Math.Atan(h / (2 * Fy)) * 180 / Math.PI;

            // —— TV 畸变：把实际角点与"理想针孔投影"比较，取画面角点处的径向偏差 ——
            using Mat undist = new Mat();
            Cv2.UndistortPoints(imgMat, undist, cam, dist);
            // undistortPoints 输出归一化坐标，乘焦距回到像素
            if (undist.Rows > 0)
            {
                double cx = cam.At<double>(0, 2), cy = cam.At<double>(1, 2);
                double maxDev = 0, maxR = 0;
                for (int i = 0; i < undist.Rows; i++)
                {
                    double ux = undist.At<double>(i, 0) * Fx + cx;
                    double uy = undist.At<double>(i, 1) * Fy + cy;
                    double rx = imgPts[i].X - cx, ry = imgPts[i].Y - cy;
                    double rOrig = Math.Sqrt((rx * rx) + (ry * ry));
                    double uxr = ux - cx, uyr = uy - cy;
                    double rUnd = Math.Sqrt((uxr * uxr) + (uyr * uyr));
                    if (rUnd < 1e-6) continue;
                    double dev = (rOrig - rUnd) / rUnd * 100.0;   // 百分比
                    if (rOrig > maxR) { maxR = rOrig; maxDev = dev; }   // 取最外圈角点
                }
                TvDistortion = maxDev;
            }

            if (undistortOut)
            {
                using Mat newCam = new Mat();
                using Mat map1 = new Mat(), map2 = new Mat();
                // 用 getOptimalNewCameraMatrix + initUndistortRectifyMap 得到校正映射
                Mat optCam = Cv2.GetOptimalNewCameraMatrix(cam, dist, gray.Size(), 1.0, gray.Size(),
                    out Rect validRoi);
                using Mat remapped = new Mat();
                Cv2.InitUndistortRectifyMap(cam, dist, null, optCam, gray.Size(), MatType.CV_16SC2, map1, map2);
                Cv2.Remap(srcMat, remapped, map1, map2, InterpolationFlags.Linear);
                optCam.Dispose();
                dst.Dispose();
                dst = remapped.Clone();
                // 画出有效区边界（校正后仍合法的区域）
                if (validRoi.Width > 0)
                    Cv2.Rectangle(dst, validRoi, Scalar.Lime, 2);
            }

            string quality = ReprojectionError < 0.5 ? "优" : ReprojectionError < 1.0 ? "良" : "差(建议重新拍摄标定板)";
            LastSummary += string.Format("畸变FOV: f=({0:F1},{1:F1}) FOV={2:F1}°x{3:F1}° TV畸变={4:F2}% 重投影RMS={5:F3}px({6})",
                Fx, Fy, FovH, FovV, TvDistortion, ReprojectionError, quality);
            Cv2.PutText(dst, string.Format("FOV {0:F1}x{1:F1}deg  TV {2:F2}%  RMS {3:F2}px",
                FovH, FovV, TvDistortion, ReprojectionError),
                new Point(8, 24), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            return dst;
        }
    }
}
