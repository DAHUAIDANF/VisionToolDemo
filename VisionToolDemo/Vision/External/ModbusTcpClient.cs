using System;
using System.Net.Sockets;
using System.Threading;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// Modbus TCP 客户端（零依赖，纯 Socket 实现）：
    /// 功能码 01 读线圈 / 03 读保持寄存器 / 05 写单线圈 / 06 写单寄存器。
    /// MBAP 报文头：事务号(2) + 协议号(2=0) + 长度(2) + 单元ID(1) + 功能码(1) + 数据。
    /// </summary>
    public sealed class ModbusTcpClient : IPlcClient
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly byte _unit;
        private TcpClient _tcp;
        private readonly object _lock = new();
        private ushort _txId = 1;

        public string Name => "ModbusTCP";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _tcp != null && _tcp.Connected;

        public ModbusTcpClient(string ip, int port, int unit)
        {
            _ip = string.IsNullOrWhiteSpace(ip) ? "192.168.1.1" : ip.Trim();
            _port = port > 0 ? port : 502;
            _unit = (byte)Math.Clamp(unit, 0, 255);
        }

        public string Connect()
        {
            lock (_lock)
            {
                try
                {
                    Disconnect();
                    var tcp = new TcpClient { NoDelay = true };
                    var ar = tcp.BeginConnect(_ip, _port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(CommHub.TimeoutMs) || !tcp.Connected)
                    {
                        tcp.Close();
                        Status = "连接超时：" + _ip + ":" + _port;
                        return Status;
                    }
                    tcp.EndConnect(ar);
                    _tcp = tcp;
                    Status = "Modbus TCP 已连接 " + _ip + ":" + _port + "（单元 " + _unit + "）";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "连接失败：" + ex.Message;
                    return Status;
                }
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                try { _tcp?.Close(); } catch { }
                _tcp = null;
                Status = "已断开";
            }
        }

        // ---------------- 功能码 ----------------

        public string ReadRegisters(int start, int count, out ushort[] values)
        {
            values = null;
            byte[] data = Request(3, start, Math.Clamp(count, 1, 125), null, 0);
            if (data == null) return Status;
            if (data.Length < 3 || data[1] != 3)
                return "读寄存器响应异常（功能码 " + (data.Length > 1 ? data[1].ToString() : "?") + "）";
            int n = data[2] / 2;
            values = new ushort[n];
            for (int i = 0; i < n; i++)
                values[i] = (ushort)((data[3 + i * 2] << 8) | data[4 + i * 2]);
            return null;
        }

        public string ReadCoils(int start, int count, out bool[] values)
        {
            values = null;
            byte[] data = Request(1, start, Math.Clamp(count, 1, 2000), null, 0);
            if (data == null) return Status;
            if (data.Length < 3 || data[1] != 1)
                return "读线圈响应异常（功能码 " + (data.Length > 1 ? data[1].ToString() : "?") + "）";
            int n = data[2] * 8;
            values = new bool[n];
            for (int i = 0; i < n; i++)
                values[i] = (data[3 + i / 8] & (1 << (i % 8))) != 0;
            return null;
        }

        public string WriteRegister(int addr, ushort value)
        {
            byte[] data = Request(6, addr, value, null, 0);
            if (data == null) return Status;
            if (data.Length < 6 || data[1] != 6)
                return "写寄存器响应异常（功能码 " + (data.Length > 1 ? data[1].ToString() : "?") + "）";
            return null;
        }

        public string WriteCoil(int addr, bool value)
        {
            byte[] data = Request(5, addr, value ? 0xFF00 : 0x0000, null, 0);
            if (data == null) return Status;
            if (data.Length < 6 || data[1] != 5)
                return "写线圈响应异常（功能码 " + (data.Length > 1 ? data[1].ToString() : "?") + "）";
            return null;
        }

        // ---------------- 报文 ----------------

        /// <summary>发请求收响应（同步、带超时）。失败返回 null 且 Status 有原因。</summary>
        private byte[] Request(byte fc, int addr, int value, byte[] payload, int payloadLen)
        {
            lock (_lock)
            {
                try
                {
                    if (!IsConnected) { Status = "PLC 未连接"; return null; }
                    var ns = _tcp.GetStream();

                    // 组装 MBAP + PDU
                    byte[] pdu = new byte[5 + (payload != null ? payload.Length : 0)];
                    pdu[0] = fc;
                    pdu[1] = (byte)(addr >> 8);
                    pdu[2] = (byte)(addr & 0xFF);
                    if (fc == 1 || fc == 3)
                    {
                        pdu[3] = (byte)(value >> 8);
                        pdu[4] = (byte)(value & 0xFF);
                    }
                    else
                    {
                        pdu[3] = (byte)(value >> 8);
                        pdu[4] = (byte)(value & 0xFF);
                    }
                    if (payload != null) Buffer.BlockCopy(payload, 0, pdu, 5, payload.Length);

                    ushort tx = _txId++;
                    byte[] mbap = new byte[7];
                    mbap[0] = (byte)(tx >> 8);
                    mbap[1] = (byte)(tx & 0xFF);
                    mbap[2] = 0; mbap[3] = 0;                       // 协议号
                    mbap[4] = (byte)((pdu.Length + 1) >> 8);
                    mbap[5] = (byte)((pdu.Length + 1) & 0xFF);      // 长度 = 单元ID + PDU
                    mbap[6] = _unit;

                    ns.Write(mbap, 0, mbap.Length);
                    ns.Write(pdu, 0, pdu.Length);
                    ns.Flush();

                    // 读响应头（7 字节）
                    byte[] head = new byte[7];
                    if (!ReadExact(ns, head, 7)) return null;
                    if (head[0] != (byte)(tx >> 8) || head[1] != (byte)(tx & 0xFF))
                    {
                        Status = "响应事务号不匹配（连接可能已错位，建议断开重连）";
                        return null;
                    }
                    int len = (head[4] << 8) | head[5];
                    if (len <= 1)
                    {
                        Status = "响应长度异常";
                        return null;
                    }
                    byte[] body = new byte[len - 1];
                    if (!ReadExact(ns, body, body.Length)) return null;

                    // 异常码：body[0]=功能码|0x80，body[1]=异常码
                    if (body[0] >= 0x80)
                    {
                        Status = "Modbus 异常码 " + body[1] + "：" + ExceptionText(body[1]);
                        return null;
                    }
                    return body;
                }
                catch (Exception ex)
                {
                    Status = "通信错误：" + ex.Message;
                    try { _tcp?.Close(); } catch { }
                    _tcp = null;
                    return null;
                }
            }
        }

        private bool ReadExact(NetworkStream ns, byte[] buf, int count)
        {
            int off = 0;
            var deadline = Environment.TickCount + CommHub.TimeoutMs;
            while (off < count)
            {
                int remain = (int)(deadline - Environment.TickCount);
                if (remain <= 0) { Status = "响应超时"; return false; }
                if (!ns.DataAvailable && !ns.Socket.Poll(remain * 1000, SelectMode.SelectRead))
                {
                    if (ns.Socket.Available == 0) { Status = "响应超时"; return false; }
                }
                int n = ns.Read(buf, off, count - off);
                if (n <= 0) { Status = "连接被关闭"; return false; }
                off += n;
            }
            return true;
        }

        private static string ExceptionText(byte code) => code switch
        {
            1 => "非法功能码",
            2 => "非法数据地址",
            3 => "非法数据值",
            4 => "从站设备故障",
            5 => "确认（处理中）",
            6 => "从站忙",
            _ => "未知(" + code + ")",
        };

        public void Dispose() => Disconnect();
    }
}
