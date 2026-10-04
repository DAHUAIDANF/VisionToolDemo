using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 海康 MVS 工业相机源：P/Invoke 对接官方 C 接口（MvCameraControl.dll）。
    /// 装完海康机器视觉 SDK（MVS）后自动可用，无需在此项目里再引 SDK 的任何库；
    /// 未装 SDK 时 Open() 给出明确提示，不崩程序。
    ///
    /// 注意：结构体按官方头文件（MvCameraControl.h）定义；SDK 主版本差异可能影响
    /// 个别结构体布局，如遇 GetOneFrame 异常请升级 SDK 到较新版本。
    /// </summary>
    public sealed class HikCameraSource : ICameraSource
    {
        private const string Dll = "MvCameraControl.dll";
        private const uint MV_OK = 0;
        private const uint MV_GIGE_DEVICE = 0x00000001;
        private const uint MV_USB_DEVICE = 0x00000004;
        // 常见像素格式（Gvsp）
        private const uint Pixel_Mono8 = 0x01080001;
        private const uint Pixel_BayerRG8 = 0x01080009;
        private const uint Pixel_RGB8_Packed = 0x02180014;
        private const uint Pixel_BGR8_Packed = 0x02180015;

        // ==================== 官方结构体 ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_CC_DEVICE_INFO_LIST
        {
            public uint nDeviceNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public IntPtr[] pDeviceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_FRAME_OUT_INFO_EX
        {
            public ushort nWidth;
            public ushort nHeight;
            public uint nEncodeType;
            public uint nFrameLen;
            public uint nFrameNum;
            public uint nTimeStamp;
            public uint nPixelType;
            public ushort nPadding;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public ushort[] nRes;
            public uint nBayerPixelType;
            public uint nHDRPixelType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] nRes2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] nRes3;
        }

        // ==================== DllImport ====================

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_EnumDevices(uint nTLayerType, ref MV_CC_DEVICE_INFO_LIST pstDevList);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_CreateHandle(out IntPtr handle, IntPtr pstDevInfo);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_OpenDevice(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_StartGrabbing(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_GetOneFrameTimeout(IntPtr handle, byte[] pData, uint nDataSize,
            ref MV_FRAME_OUT_INFO_EX pstFrameInfo, uint nMsec);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_StopGrabbing(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_CloseDevice(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint MV_CC_DestroyHandle(IntPtr handle);

        // ==================== 实例 ====================

        private IntPtr _handle;
        private readonly object _lock = new();
        private bool _grabbing;

        public string Name => "海康MVS";
        public string Status { get; private set; } = "未连接";
        public bool IsOpen => _handle != IntPtr.Zero;

        public string Open()
        {
            lock (_lock)
            {
                try
                {
                    Close();
                    // 枚举 GIGE + USB 设备
                    var list = new MV_CC_DEVICE_INFO_LIST { pDeviceInfo = new IntPtr[256] };
                    uint r = MV_CC_EnumDevices(MV_GIGE_DEVICE | MV_USB_DEVICE, ref list);
                    if (r != MV_OK)
                    {
                        Status = "海康SDK枚举失败，错误码 0x" + r.ToString("X8");
                        return Status;
                    }
                    if (list.nDeviceNum == 0 || list.pDeviceInfo[0] == IntPtr.Zero)
                    {
                        Status = "未发现海康相机（请确认已安装 MVS SDK 且相机已连接/供电）";
                        return Status;
                    }

                    r = MV_CC_CreateHandle(out _handle, list.pDeviceInfo[0]);
                    if (r != MV_OK)
                    {
                        _handle = IntPtr.Zero;
                        Status = "创建句柄失败，错误码 0x" + r.ToString("X8");
                        return Status;
                    }
                    r = MV_CC_OpenDevice(_handle);
                    if (r != MV_OK)
                    {
                        MV_CC_DestroyHandle(_handle);
                        _handle = IntPtr.Zero;
                        Status = "打开设备失败，错误码 0x" + r.ToString("X8") + "（可能被其它软件占用）";
                        return Status;
                    }
                    r = MV_CC_StartGrabbing(_handle);
                    if (r != MV_OK)
                    {
                        MV_CC_CloseDevice(_handle);
                        MV_CC_DestroyHandle(_handle);
                        _handle = IntPtr.Zero;
                        Status = "开始采集失败，错误码 0x" + r.ToString("X8");
                        return Status;
                    }
                    _grabbing = true;
                    Status = "海康相机已连接（第 1 台，GigE/USB 自动枚举）";
                    return null;
                }
                catch (DllNotFoundException)
                {
                    Status = "未安装海康 MVS SDK（缺少 MvCameraControl.dll）——到海康官网下载安装 MVS 后重启软件即可用";
                    return Status;
                }
                catch (Exception ex)
                {
                    Status = "海康相机打开异常：" + ex.Message;
                    return Status;
                }
            }
        }

        public Mat Grab()
        {
            lock (_lock)
            {
                if (!IsOpen) { Status = "海康相机未打开"; return null; }
                try
                {
                    // 先取一帧拿尺寸：分配 4MB 缓冲（常见相机一帧远小于此，足够）
                    var info = new MV_FRAME_OUT_INFO_EX
                    {
                        nRes = new ushort[4],
                        nRes2 = new uint[4],
                        nRes3 = new byte[4],
                    };
                    byte[] buf = new byte[4 * 1024 * 1024];
                    uint r = MV_CC_GetOneFrameTimeout(_handle, buf, (uint)buf.Length, ref info, (uint)CommHub.TimeoutMs);
                    if (r != MV_OK || info.nWidth == 0 || info.nHeight == 0 || info.nFrameLen == 0)
                    {
                        Status = "取帧失败，错误码 0x" + r.ToString("X8");
                        return null;
                    }

                    Mat mat = PixelToMat(buf, info);
                    if (mat == null)
                    {
                        Status = "不支持的像素格式 0x" + info.nPixelType.ToString("X8");
                        return null;
                    }
                    Status = string.Format("海康取帧 {0}x{1}", info.nWidth, info.nHeight);
                    return mat;
                }
                catch (Exception ex)
                {
                    Status = "海康取帧异常：" + ex.Message;
                    return null;
                }
            }
        }

        /// <summary>按像素格式把原始帧数据转成 8U BGR Mat</summary>
        private static Mat PixelToMat(byte[] buf, MV_FRAME_OUT_INFO_EX info)
        {
            int w = info.nWidth, h = info.nHeight;
            switch (info.nPixelType)
            {
                case Pixel_Mono8:
                    {
                        Mat m = new Mat(h, w, MatType.CV_8UC1);
                        Marshal.Copy(buf, 0, m.Data, w * h);
                        Mat bgr = new();
                        Cv2.CvtColor(m, bgr, ColorConversionCodes.GRAY2BGR);
                        m.Dispose();
                        return bgr;
                    }
                case Pixel_BayerRG8:
                    {
                        Mat m = new Mat(h, w, MatType.CV_8UC1);
                        Marshal.Copy(buf, 0, m.Data, w * h);
                        Mat bgr = new();
                        Cv2.CvtColor(m, bgr, ColorConversionCodes.BayerRG2BGR);
                        m.Dispose();
                        return bgr;
                    }
                case Pixel_RGB8_Packed:
                case Pixel_BGR8_Packed:
                    {
                        bool rgb = info.nPixelType == Pixel_RGB8_Packed;
                        Mat m = new Mat(h, w, MatType.CV_8UC3);
                        Marshal.Copy(buf, 0, m.Data, w * h * 3);
                        if (rgb)
                        {
                            Mat bgr = new();
                            Cv2.CvtColor(m, bgr, ColorConversionCodes.RGB2BGR);
                            m.Dispose();
                            return bgr;
                        }
                        return m;
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
                    if (_grabbing && _handle != IntPtr.Zero) MV_CC_StopGrabbing(_handle);
                    if (_handle != IntPtr.Zero) MV_CC_CloseDevice(_handle);
                    if (_handle != IntPtr.Zero) MV_CC_DestroyHandle(_handle);
                }
                catch { }
                _handle = IntPtr.Zero;
                _grabbing = false;
                Status = "已关闭";
            }
        }

        public void Dispose() => Close();
    }
}
