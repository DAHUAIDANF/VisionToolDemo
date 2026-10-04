using System;
using System.Collections.Generic;
using OpenCvSharp;
using VideoCapture = OpenCvSharp.VideoCapture;
using VideoCaptureAPIs = OpenCvSharp.VideoCaptureAPIs;
using VideoCaptureProperties = OpenCvSharp.VideoCaptureProperties;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 相机统一接口：无论 USB/内置相机、海康 MVS、大恒 Galaxy，都走
    /// Open → Grab → Close 三步。视觉算子、自动化节点、采集页面共用同一实现。
    /// </summary>
    public interface ICameraSource : IDisposable
    {
        /// <summary>相机类型名（USB相机/海康MVS/大恒Galaxy）</summary>
        string Name { get; }

        /// <summary>连接状态描述（最近一次操作结果）</summary>
        string Status { get; }

        /// <summary>是否已打开</summary>
        bool IsOpen { get; }

        /// <summary>打开相机。成功返回 null，失败返回可读错误文本。</summary>
        string Open();

        /// <summary>关闭相机（幂等）</summary>
        void Close();

        /// <summary>抓取一帧 8U BGR 图。失败返回 null（Status 里有原因）。</summary>
        Mat Grab();
    }

    /// <summary>
    /// 全局外部通信中枢：相机/PLC 的共享连接都挂在这里，
    /// 采集页面负责连接管理，视觉算子/自动化节点直接取用，互相同步。
    /// </summary>
    public static class CommHub
    {
        /// <summary>当前相机源（null = 未连接）。访问前先拿 Sync 锁。</summary>
        public static ICameraSource Camera;

        /// <summary>当前 PLC 客户端（null = 未连接）。访问前先拿 Sync 锁。</summary>
        public static IPlcClient Plc;

        /// <summary>当前任意协议 PLC/工控连接（IPlcClient / S7Client / McClient）。访问前先拿 Sync 锁。</summary>
        public static object PlcAny;

        /// <summary>通用 TCP 客户端（自定义协议）。访问前先拿 Sync 锁。</summary>
        public static GenericTcpClient TcpClient;

        /// <summary>通用 UDP 客户端。访问前先拿 Sync 锁。</summary>
        public static GenericUdpClient UdpClient;

        /// <summary>通用串口（裸收发，非 Modbus）。访问前先拿 Sync 锁。</summary>
        public static SerialPortNative SerialClient;

        /// <summary>断开所有外部连接（窗口关闭时调用，避免残留句柄）</summary>
        public static void DisconnectAll()
        {
            lock (Sync)
            {
                try { Camera?.Dispose(); } catch { }
                Camera = null;
                try { Plc?.Dispose(); } catch { }
                Plc = null;
                try { (PlcAny as IDisposable)?.Dispose(); } catch { }
                PlcAny = null;
                try { TcpClient?.Dispose(); } catch { }
                TcpClient = null;
                try { UdpClient?.Dispose(); } catch { }
                UdpClient = null;
                try { SerialClient?.Dispose(); } catch { }
                SerialClient = null;
            }
        }

        /// <summary>通信同步锁（采集页 UI 线程与工作流执行线程共用）</summary>
        public static readonly object Sync = new();

        /// <summary>通信超时（毫秒）</summary>
        public static int TimeoutMs = 1500;

        /// <summary>失败自动重试次数</summary>
        public static int RetryCount = 1;

        /// <summary>从已连接的相机抓一帧；未连接时返回 null（不抛异常）</summary>
        public static Mat TryGrabCamera()
        {
            lock (Sync)
            {
                var cam = Camera;
                if (cam == null || !cam.IsOpen) return null;
                for (int i = 0; i <= RetryCount; i++)
                {
                    Mat m = cam.Grab();
                    if (m != null && !m.Empty()) return m;
                    if (i < RetryCount) System.Threading.Thread.Sleep(50);
                }
                return null;
            }
        }

        /// <summary>写保持寄存器/线圈到已连接的 PLC；未连接返回错误文本，成功返回 null。</summary>
        public static string WritePlc(int addr, bool coil, ushort value)
        {
            lock (Sync)
            {
                var plc = Plc;
                if (plc == null || !plc.IsConnected) return "PLC 未连接";
                string e = coil ? plc.WriteCoil(addr, value != 0) : plc.WriteRegister(addr, value);
                if (e != null && RetryCount > 0)
                {
                    for (int i = 0; i < RetryCount; i++)
                    {
                        System.Threading.Thread.Sleep(30);
                        e = coil ? plc.WriteCoil(addr, value != 0) : plc.WriteRegister(addr, value);
                        if (e == null) break;
                    }
                }
                return e;
            }
        }

        /// <summary>从已连接的 PLC 读寄存器/线圈；失败返回错误文本，成功返回 null 且 values 有值。</summary>
        public static string ReadPlc(int addr, bool coil, int count, out object[] values)
        {
            values = null;
            lock (Sync)
            {
                var plc = Plc;
                if (plc == null || !plc.IsConnected) return "PLC 未连接";
                string e;
                if (coil)
                {
                    bool[] bits = null;
                    e = plc.ReadCoils(addr, Math.Max(1, count), out bits);
                    if (e == null)
                    {
                        values = new object[bits.Length];
                        for (int i = 0; i < bits.Length; i++) values[i] = bits[i] ? 1 : 0;
                    }
                }
                else
                {
                    ushort[] regs = null;
                    e = plc.ReadRegisters(addr, Math.Max(1, count), out regs);
                    if (e == null)
                    {
                        values = new object[regs.Length];
                        for (int i = 0; i < regs.Length; i++) values[i] = regs[i];
                    }
                }
                return e;
            }
        }
    }

    /// <summary>
    /// USB / 内置相机源：OpenCV VideoCapture 封装（DSHOW/MSMF/ANY 后端）。
    /// </summary>
    public sealed class UsbCameraSource : ICameraSource
    {
        private readonly int _index;
        private readonly VideoCaptureAPIs _api;
        private readonly int _width, _height, _fps;
        private VideoCapture _cap;
        private readonly object _lock = new();

        public string Name => "USB相机";
        public string Status { get; private set; } = "未连接";
        public bool IsOpen => _cap != null && !_cap.IsDisposed && _cap.IsOpened();

        public UsbCameraSource(int index, VideoCaptureAPIs api, int width, int height, int fps)
        {
            _index = Math.Max(0, index);
            _api = api;
            _width = Math.Max(0, width);
            _height = Math.Max(0, height);
            _fps = Math.Max(0, fps);
        }

        public string Open()
        {
            lock (_lock)
            {
                try
                {
                    Close();
                    var cap = new VideoCapture(_index, _api);
                    if (!cap.IsOpened())
                    {
                        cap.Dispose();
                        Status = "打开失败：相机 " + _index + " 不可用（未插好/被占用/后端不支持）";
                        return Status;
                    }
                    if (_width > 0) cap.Set(VideoCaptureProperties.FrameWidth, _width);
                    if (_height > 0) cap.Set(VideoCaptureProperties.FrameHeight, _height);
                    if (_fps > 0) cap.Set(VideoCaptureProperties.Fps, _fps);
                    _cap = cap;
                    Status = string.Format("已连接 {0}（后端 {1}）", _index, ApiName(_api));
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "打开相机异常：" + ex.Message;
                    return Status;
                }
            }
        }

        public Mat Grab()
        {
            lock (_lock)
            {
                if (!IsOpen) { Status = "相机未打开"; return null; }
                try
                {
                    using Mat frame = new();
                    if (!_cap.Read(frame) || frame.Empty())
                    {
                        Status = "取帧失败（相机可能被拔出或休眠）";
                        return null;
                    }
                    Status = string.Format("取帧 {0}x{1}", frame.Cols, frame.Rows);
                    return frame.Clone();   // 独立拷贝：调用方负责释放
                }
                catch (Exception ex)
                {
                    Status = "取帧异常：" + ex.Message;
                    return null;
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                try { _cap?.Dispose(); } catch { }
                _cap = null;
                Status = "已关闭";
            }
        }

        public void Dispose() => Close();

        private static string ApiName(VideoCaptureAPIs api) => api switch
        {
            VideoCaptureAPIs.DSHOW => "DSHOW",
            VideoCaptureAPIs.MSMF => "MSMF",
            _ => "ANY",
        };

        /// <summary>可用的 USB 相机索引（探测 0~7，返回能打开的那些）</summary>
        public static List<int> Probe()
        {
            var found = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.ANY);
                    if (cap.IsOpened()) found.Add(i);
                }
                catch { }
            }
            return found;
        }
    }
}
