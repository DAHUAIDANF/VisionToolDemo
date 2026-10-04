using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 大恒 Galaxy 工业相机源：P/Invoke 对接官方 C 接口（GxIAPI.dll + GxIAPI 依赖）。
    /// 装完大恒 Galaxy 相机 SDK（含驱动与运行库）后自动可用；未装时 Open() 给出明确提示。
    ///
    /// 注意：结构体按官方头文件（GxIAPI.h）定义；SDK 版本差异可能影响结构体布局，
    /// 如遇异常请升级 Galaxy SDK 到较新版本。
    /// </summary>
    public sealed class DahengCameraSource : ICameraSource
    {
        private const string Dll = "GxIAPI.dll";
        private const int GX_STATUS_SUCCESS = 0;
        private const int GX_OPEN_MODE_INDEX = 1;       // 按索引打开（0 起）
        private const int GX_ACCESS_READWRITE = 2;
        private const uint GX_PIXEL_MONO8 = 0x01080001;
        private const uint GX_PIXEL_BAYER_RG8 = 0x01080009;
        private const uint GX_PIXEL_RGB8_PACKED = 0x02180014;
        private const uint GX_PIXEL_BGR8_PACKED = 0x02180015;

        // ==================== 官方结构体 ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct GX_DEVICE_IP_INFO
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] szDeviceIP;          // 设备 IP（字符串）
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] szDeviceMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] szSubNetMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] szGateway;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] szNicIP;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] szNicMAC;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] szDeviceMAC;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GX_OPEN_PARAM
        {
            public uint openMode;
            public uint accessMode;
            public IntPtr pszContent;           // 内容（索引时为索引值字符串）
            public uint pszSize;
            public IntPtr hDevice;              // 返回值：设备句柄
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GX_FRAME_BUFFER
        {
            public IntPtr pImgBuf;              // 图像数据指针
            public uint nWidth;
            public uint nHeight;
            public uint nPixelFormat;
            public uint nFrameID;
            public ulong nTimeStamp;
            public uint nStatus;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public uint[] nReserved;
        }

        // ==================== DllImport ====================

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXInitLib();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXUninitLib();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXUpdateDeviceList(IntPtr pDeviceIPInfo, ref uint pDeviceNum, uint nBufferSize);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXOpenDevice(ref GX_OPEN_PARAM pOpenParam, out IntPtr phDevice);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXStreamOn(IntPtr hDevice);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXGetImage(IntPtr hDevice, ref GX_FRAME_BUFFER pFrameBuffer, uint nTimeout);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXStreamOff(IntPtr hDevice);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GXCloseDevice(IntPtr hDevice);

        // ==================== 实例 ====================

        private IntPtr _handle;
        private readonly object _lock = new();
        private bool _streaming;
        private bool _libInited;

        public string Name => "大恒Galaxy";
        public string Status { get; private set; } = "未连接";
        public bool IsOpen => _handle != IntPtr.Zero;

        public string Open()
        {
            lock (_lock)
            {
                try
                {
                    Close();
                    if (!_libInited)
                    {
                        int r0 = GXInitLib();
                        if (r0 != GX_STATUS_SUCCESS)
                        {
                            Status = "大恒SDK初始化失败，错误码 0x" + r0.ToString("X8");
                            return Status;
                        }
                        _libInited = true;
                    }

                    // 枚举设备
                    uint num = 0;
                    int r = GXUpdateDeviceList(IntPtr.Zero, ref num, 0);
                    if (r != GX_STATUS_SUCCESS)
                    {
                        Status = "大恒SDK枚举失败，错误码 0x" + r.ToString("X8");
                        return Status;
                    }
                    if (num == 0)
                    {
                        Status = "未发现大恒相机（请确认已安装 Galaxy SDK/驱动且相机已连接）";
                        return Status;
                    }

                    // 打开第 1 台（按索引 0）
                    string idx = "0";
                    IntPtr content = Marshal.StringToHGlobalAnsi(idx);
                    var openParam = new GX_OPEN_PARAM
                    {
                        openMode = GX_OPEN_MODE_INDEX,
                        accessMode = GX_ACCESS_READWRITE,
                        pszContent = content,
                        pszSize = (uint)idx.Length,
                    };
                    try
                    {
                        r = GXOpenDevice(ref openParam, out _handle);
                        if (r != GX_STATUS_SUCCESS)
                        {
                            _handle = IntPtr.Zero;
                            Status = "打开大恒设备失败，错误码 0x" + r.ToString("X8") + "（可能被其它软件占用）";
                            return Status;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(content);
                    }

                    r = GXStreamOn(_handle);
                    if (r != GX_STATUS_SUCCESS)
                    {
                        GXCloseDevice(_handle);
                        _handle = IntPtr.Zero;
                        Status = "大恒开始采集失败，错误码 0x" + r.ToString("X8");
                        return Status;
                    }
                    _streaming = true;
                    Status = "大恒相机已连接（第 1 台）";
                    return null;
                }
                catch (DllNotFoundException)
                {
                    Status = "未安装大恒 Galaxy SDK（缺少 GxIAPI.dll）——到大恒官网下载安装 Galaxy 相机驱动/SDK 后重启软件即可用";
                    return Status;
                }
                catch (Exception ex)
                {
                    Status = "大恒相机打开异常：" + ex.Message;
                    return Status;
                }
            }
        }

        public Mat Grab()
        {
            lock (_lock)
            {
                if (!IsOpen) { Status = "大恒相机未打开"; return null; }
                try
                {
                    var frame = new GX_FRAME_BUFFER { nReserved = new uint[16] };
                    int r = GXGetImage(_handle, ref frame, (uint)CommHub.TimeoutMs);
                    if (r != GX_STATUS_SUCCESS || frame.pImgBuf == IntPtr.Zero || frame.nWidth == 0)
                    {
                        Status = "大恒取帧失败，错误码 0x" + r.ToString("X8");
                        return null;
                    }
                    int w = (int)frame.nWidth, h = (int)frame.nHeight;
                    Mat mat = PixelToMat(frame, w, h);
                    if (mat == null)
                    {
                        Status = "大恒不支持的像素格式 0x" + frame.nPixelFormat.ToString("X8");
                        return null;
                    }
                    Status = string.Format("大恒取帧 {0}x{1}", w, h);
                    return mat;
                }
                catch (Exception ex)
                {
                    Status = "大恒取帧异常：" + ex.Message;
                    return null;
                }
            }
        }

        /// <summary>按像素格式把帧缓冲转成 8U BGR Mat（数据拷贝走 Marshal，不持有 SDK 缓冲）</summary>
        private static Mat PixelToMat(GX_FRAME_BUFFER frame, int w, int h)
        {
            switch (frame.nPixelFormat)
            {
                case GX_PIXEL_MONO8:
                    {
                        // 直接包装相机缓冲（不复制），转 BGR 后释放包装
                        using (Mat m = Mat.FromPixelData(h, w, MatType.CV_8UC1, frame.pImgBuf))
                        {
                            Mat bgr = new();
                            Cv2.CvtColor(m, bgr, ColorConversionCodes.GRAY2BGR);
                            return bgr;
                        }
                    }
                case GX_PIXEL_BAYER_RG8:
                    {
                        using (Mat m = Mat.FromPixelData(h, w, MatType.CV_8UC1, frame.pImgBuf))
                        {
                            Mat bgr = new();
                            Cv2.CvtColor(m, bgr, ColorConversionCodes.BayerRG2BGR);
                            return bgr;
                        }
                    }
                case GX_PIXEL_RGB8_PACKED:
                    {
                        using (Mat m = Mat.FromPixelData(h, w, MatType.CV_8UC3, frame.pImgBuf))
                        {
                            Mat bgr = new();
                            Cv2.CvtColor(m, bgr, ColorConversionCodes.RGB2BGR);
                            return bgr;
                        }
                    }
                case GX_PIXEL_BGR8_PACKED:
                    {
                        // 包装缓冲返回会悬垂（相机帧随后释放），必须克隆
                        using (Mat m = Mat.FromPixelData(h, w, MatType.CV_8UC3, frame.pImgBuf))
                            return m.Clone();
                    }
                default:
                    return null;
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                try
                {
                    if (_streaming && _handle != IntPtr.Zero) GXStreamOff(_handle);
                    if (_handle != IntPtr.Zero) GXCloseDevice(_handle);
                }
                catch { }
                _handle = IntPtr.Zero;
                _streaming = false;
                if (_libInited)
                {
                    try { GXUninitLib(); } catch { }
                    _libInited = false;
                }
                Status = "已关闭";
            }
        }

        public void Dispose() => Close();
    }
}
