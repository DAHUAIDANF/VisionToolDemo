using System;
using System.Runtime.InteropServices;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// Win32 串口封装（零依赖，P/Invoke）：CreateFile + DCB 配置 + Read/Write。
    /// 仅在 Windows 可用；Linux/云电脑上调用 Open 会返回明确错误。
    /// 覆盖波特率/数据位/校验/停止位的标准配置。
    /// </summary>
    public sealed class SerialPortNative : IDisposable
    {
        private IntPtr _handle = INVALID_HANDLE_VALUE;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private const int GENERIC_READ = unchecked((int)0x80000000);
        private const int GENERIC_WRITE = 0x40000000;
        private const int OPEN_EXISTING = 3;
        private const int FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint PURGE_RXCLEAR = 0x0008;
        private const uint PURGE_TXCLEAR = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint fBinary;      // 位域打包（此处按常见组合固定取值，见 SetupDcb）
            public uint fParity;
            public uint fOutxCtsFlow;
            public uint fOutxDsrFlow;
            public uint fDtrControl;
            public uint fDsrSensitivity;
            public uint fTXContinueOnXoff;
            public uint fOutX;
            public uint fInX;
            public uint fErrorChar;
            public uint fNull;
            public uint fRtsControl;
            public uint fAbortOnError;
            public uint fDummy2;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public char XonChar;
            public char XoffChar;
            public char ErrorChar;
            public char EofChar;
            public char EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, int dwDesiredAccess, int dwShareMode,
            IntPtr lpSecurityAttributes, int dwCreationDisposition, int dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCommState(IntPtr hFile, ref DCB lpDcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommState(IntPtr hFile, ref DCB lpDcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommTimeouts(IntPtr hFile, ref COMMTIMEOUTS lpTimeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PurgeComm(IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>串口名（如 COM3）。Windows 下需写成 \\.\COM3 以支持大于 COM9 的端口。</summary>
        public string PortName { get; }
        public int BaudRate { get; }
        public int DataBits { get; }
        /// <summary>校验：0=None 1=Odd 2=Even</summary>
        public int Parity { get; }
        /// <summary>停止位：1=1位 2=1.5位 3=2位</summary>
        public int StopBits { get; }

        public bool IsOpen => _handle != INVALID_HANDLE_VALUE;

        public SerialPortNative(string port, int baud, int dataBits, int parity, int stopBits)
        {
            PortName = string.IsNullOrWhiteSpace(port) ? "COM1" : port.Trim();
            if (!PortName.StartsWith(@"\\.\")) PortName = @"\\.\" + PortName;
            BaudRate = baud > 0 ? baud : 9600;
            DataBits = dataBits is >= 5 and <= 8 ? dataBits : 8;
            Parity = parity is >= 0 and <= 2 ? parity : 0;
            StopBits = stopBits is >= 1 and <= 3 ? stopBits : 1;
        }

        /// <summary>打开串口。成功返回 null，失败返回错误文本。</summary>
        public string Open()
        {
            if (!OperatingSystem.IsWindows())
                return "串口仅支持 Windows（当前环境 " + (OperatingSystem.IsLinux() ? "Linux" : "非Windows") + "）";
            _handle = CreateFile(PortName, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (_handle == INVALID_HANDLE_VALUE)
                return "打开 " + PortName + " 失败（错误码 " + Marshal.GetLastWin32Error() + "）：端口不存在或已被占用";

            var dcb = new DCB();
            if (!GetCommState(_handle, ref dcb))
            {
                Close();
                return "读取串口状态失败（错误码 " + Marshal.GetLastWin32Error() + "）";
            }
            dcb.BaudRate = (uint)BaudRate;
            dcb.ByteSize = (byte)DataBits;
            dcb.Parity = (byte)(Parity == 1 ? 1 : Parity == 2 ? 2 : 0);   // ODDPARITY=1 EVENPARITY=2 NOPARITY=0
            dcb.StopBits = (byte)(StopBits == 1 ? 0 : StopBits == 3 ? 2 : 1); // ONESTOPBIT=0 TWOSTOPBITS=2 ONE5STOPBITS=1
            dcb.fBinary = 1;
            dcb.fParity = (dcb.Parity != 0) ? 1u : 0u;
            dcb.fDtrControl = 1;   // DTR_CONTROL_ENABLE
            dcb.fRtsControl = 1;   // RTS_CONTROL_ENABLE
            dcb.fOutxCtsFlow = 0;
            dcb.fOutxDsrFlow = 0;
            if (!SetCommState(_handle, ref dcb))
            {
                Close();
                return "配置串口失败（错误码 " + Marshal.GetLastWin32Error() + "）";
            }

            var to = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = 10,
                ReadTotalTimeoutMultiplier = 1,
                ReadTotalTimeoutConstant = (uint)CommHub.TimeoutMs,
                WriteTotalTimeoutMultiplier = 1,
                WriteTotalTimeoutConstant = (uint)CommHub.TimeoutMs,
            };
            SetCommTimeouts(_handle, ref to);
            PurgeComm(_handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
            return null;
        }

        /// <summary>写字节。成功返回 null，失败返回错误文本。</summary>
        public string Write(byte[] data)
        {
            if (!IsOpen) return "串口未打开";
            if (!WriteFile(_handle, data, (uint)data.Length, out uint written, IntPtr.Zero) || written != data.Length)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 0) err = -1;
                return "串口写入失败（错误码 " + err + "）";
            }
            return null;
        }

        /// <summary>读最多 count 字节（阻塞至超时）。返回实际字节数（0=超时无数据）。</summary>
        public int Read(byte[] buf, int count)
        {
            if (!IsOpen) return 0;
            if (ReadFile(_handle, buf, (uint)count, out uint n, IntPtr.Zero)) return (int)n;
            return 0;
        }

        /// <summary>读最多 count 字节到 buf 的 offset 处。返回实际字节数。</summary>
        public int Read(byte[] buf, int offset, int count)
        {
            if (!IsOpen || offset < 0 || count <= 0 || offset + count > buf.Length) return 0;
            byte[] tmp = new byte[count];
            int n = Read(tmp, count);
            if (n > 0) Buffer.BlockCopy(tmp, 0, buf, offset, n);
            return n;
        }

        public void Close()
        {
            if (_handle != INVALID_HANDLE_VALUE)
            {
                try { CloseHandle(_handle); } catch { }
                _handle = INVALID_HANDLE_VALUE;
            }
        }

        public void Dispose() => Close();
    }
}
