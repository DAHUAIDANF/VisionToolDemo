using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OpenCvSharp;

namespace VisionToolDemo.Vision
{
    /// <summary>
    /// 视频抽帧：把视频按时间点抽成一张张图，交给算子里程/批量处理用。
    ///
    /// 本工具是**逐帧图像处理平台**，不是播放器：导入视频的意义是"取出若干帧来做检测"，
    /// 所以抽出的帧会落到磁盘（视频同级目录的"<视频名>_视频抽帧"），
    /// 之后可以直接对那个目录跑批量处理（跳动/闪烁/漂移这类时域缺陷就靠它）。
    ///
    /// 定位用 CAP_PROP_POS_MSEC（毫秒）而不是帧号：帧号在多数封装格式里不可靠
    /// （B 帧/可变帧率会让 seek 落偏）。这是沿用旧界面实测过的做法。
    /// </summary>
    public static class VideoImporter
    {
        public sealed class Info
        {
            public bool Ok;
            public string Error = "";
            public double Fps;
            public int FrameCount;
            public double Duration;
            public int Width;
            public int Height;
        }

        /// <summary>探测视频信息（打不开时 Ok=false 并带原因）</summary>
        public static Info Probe(string path)
        {
            var info = new Info();
            try
            {
                using var cap = new VideoCapture(path);
                if (!cap.IsOpened())
                {
                    info.Error = "打不开这个视频（格式不支持或缺少解码器）";
                    return info;
                }
                info.Fps = cap.Get(VideoCaptureProperties.Fps);
                info.FrameCount = (int)cap.Get(VideoCaptureProperties.FrameCount);
                info.Width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                info.Height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                if (info.Fps > 0 && info.FrameCount > 0) info.Duration = info.FrameCount / info.Fps;
                info.Ok = true;
            }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }

        /// <summary>视频默认的抽帧目录（视频同级）</summary>
        public static string DefaultFrameDir(string videoPath)
            => Path.Combine(Path.GetDirectoryName(videoPath) ?? ".",
                Path.GetFileNameWithoutExtension(videoPath) + "_视频抽帧");

        /// <summary>
        /// 抽帧。intervalSeconds &lt;= 0 表示只取起始时间那一帧。
        /// saveDir 非空时把每一帧写成 PNG（文件名带时间戳，便于事后对照时序）。
        /// 返回抽到的帧（**调用方负责 Dispose**）；一帧都没取到时 error 有原因。
        /// </summary>
        public static List<Mat> Grab(string path, double startSeconds, double intervalSeconds,
            int maxFrames, string saveDir, out string error)
        {
            error = null;
            var frames = new List<Mat>();
            maxFrames = Math.Max(1, Math.Min(100000, maxFrames));
            startSeconds = Math.Max(0, startSeconds);

            try
            {
                using var cap = new VideoCapture(path);
                if (!cap.IsOpened())
                {
                    error = "打不开这个视频（格式不支持或缺少解码器）";
                    return frames;
                }

                if (saveDir != null) Directory.CreateDirectory(saveDir);

                if (startSeconds > 0) cap.Set(VideoCaptureProperties.PosMsec, startSeconds * 1000.0);

                // 间隔 <= 0：只取一帧
                if (intervalSeconds <= 0)
                {
                    using var one = new Mat();
                    if (!cap.Read(one) || one.Empty())
                    {
                        error = string.Format(CultureInfo.InvariantCulture,
                            "在 {0:0.###}s 处没取到帧（可能超出视频长度）", startSeconds);
                        return frames;
                    }
                    frames.Add(one.Clone());
                    Save(frames[0], saveDir, startSeconds, 0);
                    return frames;
                }

                double t = startSeconds;
                var info = Probe(path);
                double end = info.Duration > 0 ? info.Duration : double.MaxValue;

                for (int i = 0; i < maxFrames && t <= end + 1e-6; i++)
                {
                    // 每帧重新定位：比"读一帧再跳"更稳（可变帧率下不会累积漂移）
                    cap.Set(VideoCaptureProperties.PosMsec, t * 1000.0);
                    using var frame = new Mat();
                    if (!cap.Read(frame) || frame.Empty()) break;
                    var copy = frame.Clone();
                    frames.Add(copy);
                    Save(copy, saveDir, t, i);
                    t += intervalSeconds;
                }

                if (frames.Count == 0)
                    error = "一帧都没取到（检查起始时间与视频长度）";
                return frames;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return frames;
            }
        }

        private static void Save(Mat frame, string saveDir, double t, int index)
        {
            if (saveDir == null || frame == null || frame.Empty()) return;
            try
            {
                string name = string.Format(CultureInfo.InvariantCulture,
                    "帧_{0:0000}_{1:0.###}s.png", index, t);
                Cv2.ImWrite(Path.Combine(saveDir, name), frame);
            }
            catch { /* 存不下来不该影响抽帧结果 */ }
        }
    }
}
