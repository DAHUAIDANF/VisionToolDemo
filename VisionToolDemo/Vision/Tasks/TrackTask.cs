using System;
using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// 目标跟踪算子：用 OpenCV 内置追踪器（KCF / CSRT / MIL）对「框选目标」逐帧跟踪。
    ///
    /// 使用方式（视觉页）：
    ///   · 把「目标跟踪」加入算子链 → 参数区出现「框选目标」行；
    ///   · 先在主图上用鼠标拖一个框框住目标 → 点「框选目标」完成初始化；
    ///   · 之后每执行一次（视频帧 / 实时预览帧 / 批量图片）都会自动跟踪并画出目标框。
    ///
    /// 三种追踪器（参数[0] 选择）：
    ///   · 0 KCF： 快、通用，默认；
    ///   · 1 CSRT：精度更高、更稳，速度稍慢；
    ///   · 2 MIL： 更鲁棒到光照/形变，速度较快。
    ///
    /// 行为契约：
    ///   · 未框选目标 → 防御返回原图拷贝 + 摘要提示；
    ///   · 首帧（有初始框）→ 初始化追踪器并画绿框；
    ///   · 后续帧 → Update 跟踪；成功画绿框 + 摘要坐标；失败（目标丢失）画红框提示
    ///     "目标丢失"，并保留上一帧位置；
    ///   · 「重置」可清空追踪状态重新框选。
    ///
    /// 状态存在任务实例上（链步复用同一实例，视频预览与批量共用同一目标）。
    /// </summary>
    public class TrackTask : IVisionTask, IResultReporter
    {
        public string TaskName => "目标跟踪";

        // ============================== 状态（任务实例上） ==============================

        /// <summary>追踪器实例（初始化后创建；重置/换类型时重建）</summary>
        private Tracker _tracker;

        /// <summary>当前追踪器类型（0 KCF / 1 CSRT / 2 MIL；与参数[0]同步）</summary>
        private int _type = -1;

        /// <summary>是否已初始化（Init 成功）</summary>
        private bool _inited;

        /// <summary>最近一次目标框（原图坐标；丢失时保留上一帧位置画红框）</summary>
        private Rect _lastRect;

        /// <summary>追踪是否成功（最近一帧 Update 结果；丢失显示红框）</summary>
        private bool _trackOk;

        /// <summary>已经处理的总帧数（摘要显示用）</summary>
        public int FrameCount { get; private set; }

        /// <summary>最近失败/提示原因（摘要与排障用）</summary>
        private string _hint = "";

        /// <summary>最近一次 Execute 的摘要（提示标签 / 运行完成弹窗共用）</summary>
        public string LastSummary { get; private set; } = "";

        /// <summary>IResultReporter 兼容占位</summary>
        public string LastZxingError => "";

        /// <summary>最近一次目标框（视觉页/预览窗口读取画框用；null=还没有）</summary>
        public Rect? LastBox => _inited ? _lastRect : null;

        /// <summary>最近一帧是否跟踪成功（预览窗口画框颜色用）</summary>
        public bool TrackOk => _trackOk;

        // ============================== 参数 ==============================

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc
            {
                // 追踪器类型：KCF 快/CSRT 准/MIL 鲁棒
                ParamName = "追踪器",
                Min = 0,
                Max = 2,
                DefaultValue = 0,
                DisplayFormat = "{0}",
                ForceOdd = false,
                Tip = "追踪器类型：0 KCF（快、通用，默认）；1 CSRT（更准更稳，稍慢）；\\n" +
                      "2 MIL（对光照/形变更鲁棒）。换类型后需重新框选目标。"
            },
            new TaskParamDesc
            {
                // 目标丢失后是否自动重新初始化：0=提示丢失并保留上一框；1=用上一框位置自动重初始化
                ParamName = "丢失处理",
                Min = 0,
                Max = 1,
                DefaultValue = 0,
                DisplayFormat = "{0}",
                ForceOdd = false,
                Tip = "目标丢失（跟丢）时：0 = 提示『目标丢失』并保留上一帧位置画红框；\\n" +
                      "1 = 自动用上一帧位置重新初始化追踪器（目标短暂消失时更稳）。"
            },
        ];

        // ============================== 公开操作（视觉页专用行调用） ==============================

        /// <summary>框选目标并初始化追踪器：rect 为原图坐标像素框。返回是否成功。</summary>
        public bool SetTarget(Mat frame, Rect rect, int type)
        {
            if (frame == null || frame.Empty() || rect.Width < 2 || rect.Height < 2)
            {
                _hint = "目标框无效（太窄/越界）";
                return false;
            }
            try
            {
                // 先释放旧追踪器再按类型创建
                _tracker?.Dispose();
                _tracker = CreateTracker(type);
                if (_tracker == null) { _hint = "追踪器创建失败"; return false; }
                // 框裁剪到图内（防止初始化越界崩溃）
                var r = new Rect(
                    Math.Clamp(rect.X, 0, frame.Cols - 2),
                    Math.Clamp(rect.Y, 0, frame.Rows - 2),
                    Math.Min(rect.Width, frame.Cols - Math.Clamp(rect.X, 0, frame.Cols - 2)),
                    Math.Min(rect.Height, frame.Rows - Math.Clamp(rect.Y, 0, frame.Rows - 2)));
                if (r.Width < 2 || r.Height < 2) { _hint = "目标框被裁剪后过小"; return false; }
                _tracker.Init(frame, r);
                _type = type;
                _inited = true;
                _lastRect = r;
                _trackOk = true;
                FrameCount = 0;
                _hint = "";
                LastSummary = string.Format("目标跟踪: 已框选初始化（{0},{1} {2}x{3}）", r.X, r.Y, r.Width, r.Height);
                return true;
            }
            catch (Exception ex)
            {
                _hint = "初始化异常：" + ex.Message;
                _tracker?.Dispose();
                _tracker = null;
                _inited = false;
                return false;
            }
        }

        /// <summary>重置跟踪（清空追踪器与目标框，等待重新框选）</summary>
        public void ResetTracking()
        {
            _tracker?.Dispose();
            _tracker = null;
            _inited = false;
            _trackOk = false;
            FrameCount = 0;
            _hint = "";
            LastSummary = "目标跟踪: 已重置，请重新框选目标";
        }

        // ============================== 执行 ==============================

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            if (srcMat?.Empty() != false) return new Mat();

            int type = paramValues?.Length > 0 ? Math.Clamp(paramValues[0], 0, 2) : 0;
            int lostMode = paramValues?.Length > 1 ? Math.Clamp(paramValues[1], 0, 1) : 0;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            // —— 未框选目标：不执行，明确提示 ——
            if (!_inited || _tracker == null)
            {
                LastSummary = "目标跟踪: 还没框选目标（参数区点「框选目标」，先在主图上拖框）";
                return dst;
            }

            try
            {
                // 类型参数变了（用户切追踪器）→ 重建追踪器（用上一次目标框重新初始化）
                if (type != _type)
                {
                    _type = type;
                    _tracker?.Dispose();
                    _tracker = CreateTracker(type);
                    if (_tracker != null) _tracker.Init(srcMat, _lastRect);
                }

                FrameCount++;
                var rect = _lastRect;
                bool ok = _tracker.Update(srcMat, ref rect);
                _trackOk = ok;
                if (ok)
                {
                    _lastRect = rect;
                    DrawBox(dst, rect, true);
                    LastSummary = string.Format("目标跟踪: 第 {0} 帧 目标 ({1},{2}) {3}x{4}",
                        FrameCount, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    // 丢失：按丢失处理模式自动重初始化，或用上一帧位置画红框提示
                    if (lostMode == 1 && _lastRect.Width > 2 && _lastRect.Height > 2)
                    {
                        _tracker?.Dispose();
                        _tracker = CreateTracker(type);
                        if (_tracker != null) _tracker.Init(srcMat, _lastRect);
                        DrawBox(dst, _lastRect, true);
                        LastSummary = string.Format("目标跟踪: 第 {0} 帧 目标消失，已自动重初始化（{1},{2}）",
                            FrameCount, _lastRect.X, _lastRect.Y);
                    }
                    else
                    {
                        DrawBox(dst, _lastRect, false);
                        LastSummary = string.Format("目标跟踪: 第 {0} 帧 目标丢失（保留上一位置 {1},{2} {3}x{4}）",
                            FrameCount, _lastRect.X, _lastRect.Y, _lastRect.Width, _lastRect.Height);
                    }
                }
            }
            catch (Exception ex)
            {
                _hint = ex.Message;
                LastSummary = "目标跟踪: 失败（" + ex.Message + "）";
            }
            return dst;
        }

        // ============================== 工具 ==============================

        /// <summary>按类型创建追踪器（null=类型不支持）</summary>
        private static Tracker CreateTracker(int type) => type switch
        {
            1 => TrackerCSRT.Create(),
            2 => TrackerMIL.Create(),
            _ => TrackerKCF.Create(),
        };

        /// <summary>在原图上画目标框（绿=跟踪成功，红=丢失），带角标文字</summary>
        private static void DrawBox(Mat dst, Rect r, bool ok)
        {
            var color = ok ? new Scalar(0, 220, 0) : new Scalar(0, 0, 255);
            int thickness = Math.Max(2, dst.Cols / 300);
            Cv2.Rectangle(dst, new OpenCvSharp.Point(r.X, r.Y),
                new OpenCvSharp.Point(r.X + r.Width, r.Y + r.Height), color, thickness, LineTypes.AntiAlias);
            // 左上角小标签（黑色底 + 彩色字，中文交给上层文本，这里只画纯色块示意）
            Cv2.PutText(dst, ok ? "TRACK" : "LOST",
                new OpenCvSharp.Point(Math.Max(0, r.X), Math.Max(12, r.Y - 4)),
                HersheyFonts.HersheySimplex, 0.45, ok ? new Scalar(0, 220, 0) : new Scalar(0, 0, 255),
                2, LineTypes.AntiAlias);
        }
    }
}
