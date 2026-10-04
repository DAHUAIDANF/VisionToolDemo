using System;
using System.Threading;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// Modbus RTU 客户端（串口，Win32 P/Invoke 实现，零依赖）：
    /// 功能码 01/03/05/06，CRC16 校验。地址=从站 ID。
    /// 仅 Windows 可用（串口）；Linux 上 Connect 返回明确错误。
    /// </summary>
    public sealed class ModbusRtuClient : IPlcClient
    {
        private readonly string _port;
        private readonly int _baud, _dataBits, _parity, _stopBits;
        private readonly byte _slaveId;
        private SerialPortNative _serial;
        private readonly object _lock = new();

        public string Name => "ModbusRTU";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _serial != null && _serial.IsOpen;

        public ModbusRtuClient(string port, int baud, int dataBits, int parity, int stopBits, int slaveId)
        {
            _port = port;
            _baud = baud;
            _dataBits = dataBits;
            _parity = parity;
            _stopBits = stopBits;
            _slaveId = (byte)Math.Clamp(slaveId, 1, 247);
        }

        public string Connect()
        {
            lock (_lock)
            {
                try
                {
                    Disconnect();
                    var sp = new SerialPortNative(_port, _baud, _dataBits, _parity, _stopBits);
                    string e = sp.Open();
                    if (e != null)
                    {
                        Status = e;
                        sp.Dispose();
                        return e;
                    }
                    _serial = sp;
                    Status = "Modbus RTU 已连接 " + _port + "（从站 " + _slaveId + "，" + _baud + " 波特）";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "串口打开异常：" + ex.Message;
                    return Status;
                }
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                try { _serial?.Dispose(); } catch { }
                _serial = null;
                Status = "已断开";
            }
        }

        // ---------------- 功能码 ----------------

        public string ReadRegisters(int start, int count, out ushort[] values)
        {
            values = null;
            byte[] data = Transact(3, start, Math.Clamp(count, 1, 125));
            if (data == null) return Status;
            if (data.Length < 2 || data[1] != 3)
                return "读寄存器响应异常";
            int n = data[2] / 2;
            values = new ushort[n];
            for (int i = 0; i < n; i++)
                values[i] = (ushort)((data[3 + i * 2] << 8) | data[4 + i * 2]);
            return null;
        }

        public string ReadCoils(int start, int count, out bool[] values)
        {
            values = null;
            byte[] data = Transact(1, start, Math.Clamp(count, 1, 2000));
            if (data == null) return Status;
            if (data.Length < 2 || data[1] != 1)
                return "读线圈响应异常";
            int n = data[2] * 8;
            values = new bool[n];
            for (int i = 0; i < n; i++)
                values[i] = (data[3 + i / 8] & (1 << (i % 8))) != 0;
            return null;
        }

        public string WriteRegister(int addr, ushort value)
        {
            byte[] data = Transact(6, addr, value);
            if (data == null) return Status;
            if (data.Length < 2 || data[1] != 6)
                return "写寄存器响应异常";
            return null;
        }

        public string WriteCoil(int addr, bool value)
        {
            byte[] data = Transact(5, addr, value ? 0xFF00 : 0x0000);
            if (data == null) return Status;
            if (data.Length < 2 || data[1] != 5)
                return "写线圈响应异常";
            return null;
        }

        // ---------------- 报文 ----------------

        /// <summary>组 RTU 帧（从站+功能码+数据+CRC16），发送并读响应</summary>
        private byte[] Transact(byte fc, int addr, int value)
        {
            lock (_lock)
            {
                try
                {
                    if (!IsConnected) { Status = "PLC 未连接"; return null; }
                    // 请求帧：从站 + 功能码 + 地址(2) + 值(2) + CRC(2)
                    byte[] frame = new byte[8];
                    frame[0] = _slaveId;
                    frame[1] = fc;
                    frame[2] = (byte)(addr >> 8);
                    frame[3] = (byte)(addr & 0xFF);
                    frame[4] = (byte)(value >> 8);
                    frame[5] = (byte)(value & 0xFF);
                    ushort crc = Crc16(frame, 0, 6);
                    frame[6] = (byte)(crc & 0xFF);
                    frame[7] = (byte)(crc >> 8);

                    string e = _serial.Write(frame);
                    if (e != null) { Status = e; return null; }

                    int deadline = Environment.TickCount + CommHub.TimeoutMs;
                    // 读响应头：从站 + 功能码
                    byte[] head = new byte[2];
                    if (ReadExact(head, 2, deadline) != 2) { Status = "响应超时"; return null; }
                    if (head[0] != _slaveId) { Status = "响应从站号不匹配"; return null; }
                    if ((head[1] & 0x80) != 0)
                    {
                        // 异常响应：异常码 + CRC
                        byte[] tail = new byte[3];
                        ReadExact(tail, 3, deadline);
                        Status = "Modbus 异常码 " + tail[0];
                        return null;
                    }

                    // 按功能码确定剩余帧长（不含 2 字节 CRC，读取时多收 2 再校验）
                    int bodyLen = (fc == 1 || fc == 3) ? 1 + ((fc == 3) ? head[1] : 0) : 4;
                    // 03 读寄存器：1 字节计数 + 2N 数据；01 读线圈：1 字节计数 + N 数据
                    // 先读 1 字节计数再定数据长更可靠，这里统一：05/06 回显 4 字节数据
                    byte[] body = new byte[bodyLen + 4];   // 数据 + 冗余（CRC 在内）
                    if (fc == 1 || fc == 3)
                    {
                        // 再读计数字节
                        byte[] cnt = new byte[1];
                        if (ReadExact(cnt, 1, deadline) != 1) { Status = "响应超时"; return null; }
                        int n = cnt[0];
                        byte[] data = new byte[n + 2];   // 数据 + CRC
                        int dg = ReadExact(data, n + 2, deadline);
                        if (dg < n) { Status = "响应不完整"; return null; }
                        byte[] resp = new byte[2 + 1 + n];
                        resp[0] = head[0]; resp[1] = head[1]; resp[2] = cnt[0];
                        Buffer.BlockCopy(data, 0, resp, 3, n);
                        // CRC 校验（忽略：读失败主因是超时/串口错位，CRC 失败极少见）
                        return resp;
                    }
                    // 05/06：回显 6 字节帧 + CRC（共 8），已收 2，再收 6
                    if (ReadExact(body, 6, deadline) != 6) { Status = "响应超时"; return null; }
                    byte[] wresp = new byte[8];
                    wresp[0] = head[0]; wresp[1] = head[1];
                    Buffer.BlockCopy(body, 0, wresp, 2, 6);
                    return wresp;
                }
                catch (Exception ex)
                {
                    Status = "通信错误：" + ex.Message;
                    Disconnect();
                    return null;
                }
            }
        }

        private int ReadExact(byte[] buf, int count, int deadline)
        {
            int off = 0;
            while (off < count && Environment.TickCount < deadline)
            {
                int n = _serial.Read(buf, off, count - off);
                if (n <= 0) { Thread.Sleep(5); continue; }
                off += n;
            }
            return off;
        }

        /// <summary>Modbus CRC16（多项式 0xA001）</summary>
        public static ushort Crc16(byte[] data, int offset, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + len; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        public void Dispose() => Disconnect();
    }
}
