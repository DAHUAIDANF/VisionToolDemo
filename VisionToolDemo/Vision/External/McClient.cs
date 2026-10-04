using System;
using System.Net.Sockets;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 三菱 MC 协议客户端（3E 帧 Binary，TCP，零依赖）：
    /// 支持 Q / L / iQ-R 系列，读写 D(字) / R / W 与 M / B / X / Y(位)。
    /// 地址格式：D100（字）、M10 / X10 / Y10 / B10（位）、W10。
    /// 读：ReadWord(地址)/ReadBit(地址)；写：WriteWord/WriteBit。
    /// 注意：按 MC 3E Binary 标准实现，未接真实 PLC 实测时请先用样例工具核对。
    /// </summary>
    public sealed class McClient : ICommClient
    {
        private readonly string _ip;
        private readonly int _port;
        private TcpClient _tcp;
        private NetworkStream _ns;
        private readonly object _lock = new();

        public string Name => "三菱MC";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _tcp != null && _tcp.Connected;

        public McClient(string ip, int port)
        {
            _ip = string.IsNullOrWhiteSpace(ip) ? "192.168.3.250" : ip.Trim();
            _port = port > 0 ? port : 6000;
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
                        Status = "MC 连接超时：" + _ip + ":" + _port;
                        return Status;
                    }
                    tcp.EndConnect(ar);
                    _tcp = tcp;
                    _ns = tcp.GetStream();
                    Status = "MC 已连接 " + _ip + ":" + _port;
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "MC 连接失败：" + ex.Message;
                    return Status;
                }
            }
        }

        // ==================== 地址解析 ====================

        internal enum McType { D = 0xA8, R = 0xAF, W = 0xB4, M = 0x90, B = 0xA0, X = 0x9C, Y = 0x9D }

        /// <summary>解析三菱地址；返回类型与地址值。word=true 时要求字元件（D/R/W）。</summary>
        internal static string Parse(string text, bool word, out McType type, out int addr)
        {
            type = default;
            addr = 0;
            text = (text ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(text)) return "地址为空";
            char head = text[0];
            McType t = head switch
            {
                'D' => McType.D,
                'R' => McType.R,
                'W' => McType.W,
                'M' => McType.M,
                'B' => McType.B,
                'X' => McType.X,
                'Y' => McType.Y,
                _ => (McType)0,
            };
            if ((int)t == 0) return "无法识别的三菱地址（支持 D/R/W/M/B/X/Y，如 D100、M10、X10）";
            if (word && t is not (McType.D or McType.R or McType.W))
                return "此处需要字元件（D/R/W），如 D100";
            if (!word && t is (McType.D or McType.R or McType.W))
                return "此处需要位元件（M/B/X/Y），如 M10";
            if (!int.TryParse(text.Substring(1), out addr) || addr < 0 || addr > 0xFFFF)
                return "地址数值无效：" + text;
            return null;
        }

        // ==================== 读写 ====================

        /// <summary>读一个 16 位字（D100）。</summary>
        public string ReadWord(string address, out ushort value)
        {
            value = 0;
            string e = Parse(address, true, out McType t, out int addr);
            if (e != null) { Status = e; return e; }
            byte[] data = Transact(0x0401, 0x0000, (byte)1, t, addr, 1, null);
            if (data == null) return Status;
            if (data.Length < 2)
            {
                Status = "MC 读响应过短";
                return Status;
            }
            value = (ushort)(data[0] | (data[1] << 8));
            Status = "MC 读 " + address + " = " + value;
            return null;
        }

        /// <summary>写一个 16 位字（D100）。</summary>
        public string WriteWord(string address, ushort value)
        {
            string e = Parse(address, true, out McType t, out int addr);
            if (e != null) { Status = e; return e; }
            byte[] data = { (byte)(value & 0xFF), (byte)(value >> 8) };
            byte[] resp = Transact(0x1401, 0x0000, (byte)1, t, addr, 1, data);
            if (resp == null) return Status;
            Status = "MC 写 " + address + " = " + value;
            return null;
        }

        /// <summary>读一个位（M10 / X10 / Y10）。</summary>
        public string ReadBit(string address, out bool value)
        {
            value = false;
            string e = Parse(address, false, out McType t, out int addr);
            if (e != null) { Status = e; return e; }
            byte[] data = Transact(0x0402, 0x0001, (byte)8, t, addr, 8, null);
            if (data == null) return Status;
            value = data.Length > 0 && (data[0] & 0x01) != 0;
            Status = "MC 读 " + address + " = " + (value ? "ON" : "OFF");
            return null;
        }

        /// <summary>写一个位（M10 / X10 / Y10）。</summary>
        public string WriteBit(string address, bool value)
        {
            string e = Parse(address, false, out McType t, out int addr);
            if (e != null) { Status = e; return e; }
            byte[] data = { (byte)(value ? 0x10 : 0x00) };   // 位写：ON=0x10 OFF=0x00
            byte[] resp = Transact(0x1402, 0x0001, (byte)8, t, addr, 8, data);
            if (resp == null) return Status;
            Status = "MC 写 " + address + " = " + (value ? "ON" : "OFF");
            return null;
        }

        // ---------------- 报文 ----------------

        /// <summary>
        /// 3E 帧 Binary 请求/响应。
        /// cmd：0x0401 读字 / 0x0402 读位 / 0x1401 写字 / 0x1402 写位；
        /// sub：字单位 0x0000 / 位单位 0x0001；devSize：字=1 位=8。
        /// </summary>
        private byte[] Transact(ushort cmd, ushort sub, byte devSize, McType type, int addr, int points, byte[] payload)
        {
            lock (_lock)
            {
                try
                {
                    if (!IsConnected) { Status = "MC 未连接"; return null; }
                    // 请求：头7 + 长度2 + 定时2 + 命令2 + 子命令2 + 软元件(1+1+2) + 点数2 + 数据
                    int dataLen = payload?.Length ?? 0;
                    int total = 7 + 2 + 2 + 2 + 2 + 4 + 2 + dataLen;
                    byte[] req = new byte[total];
                    int p = 0;
                    req[p++] = 0x50; req[p++] = 0x00;          // 子头
                    req[p++] = 0x00;                           // 网络号
                    req[p++] = 0xFF;                           // PC 号
                    req[p++] = 0x03; req[p++] = 0xFF;          // IO
                    req[p++] = 0x00;                           // 站号
                    int bodyLen = 2 + 2 + 2 + 4 + 2 + dataLen; // 定时+命令+子命令+软元件+点数+数据
                    req[p++] = (byte)(bodyLen >> 8); req[p++] = (byte)(bodyLen & 0xFF);
                    req[p++] = 0x00; req[p++] = 0x0A;          // 监视定时器 10×250ms
                    req[p++] = (byte)(cmd >> 8); req[p++] = (byte)(cmd & 0xFF);
                    req[p++] = (byte)(sub >> 8); req[p++] = (byte)(sub & 0xFF);
                    req[p++] = devSize;                        // 软元件点数(设备单位)
                    req[p++] = (byte)type;                     // 类型
                    req[p++] = (byte)(addr & 0xFF); req[p++] = (byte)(addr >> 8);
                    req[p++] = (byte)(points >> 8); req[p++] = (byte)(points & 0xFF);
                    if (payload != null)
                    {
                        Buffer.BlockCopy(payload, 0, req, p, payload.Length);
                        p += payload.Length;
                    }

                    _ns.Write(req, 0, req.Length);
                    _ns.Flush();

                    // 响应：头7 + 长度2 + 结束码2 + 数据
                    byte[] head = new byte[9];
                    int got = 0, deadline = Environment.TickCount + CommHub.TimeoutMs;
                    while (got < 9 && Environment.TickCount < deadline)
                    {
                        if (!_ns.DataAvailable && !_ns.Socket.Poll(50 * 1000, SelectMode.SelectRead))
                        { System.Threading.Thread.Sleep(5); continue; }
                        int n = _ns.Read(head, got, 9 - got);
                        if (n <= 0) { Status = "MC 连接被关闭"; return null; }
                        got += n;
                    }
                    if (got < 9) { Status = "MC 响应超时"; return null; }
                    if (head[0] != 0xD0) { Status = "MC 响应子头异常"; return null; }
                    int rlen = (head[7] << 8) | head[8];
                    if (rlen < 2) { Status = "MC 响应长度异常"; return null; }
                    int body = rlen - 2;   // 去掉结束码
                    byte[] rest = new byte[body + 2];   // 数据 + 结束码
                    got = 0;
                    while (got < rest.Length && Environment.TickCount < deadline)
                    {
                        if (!_ns.DataAvailable && !_ns.Socket.Poll(50 * 1000, SelectMode.SelectRead))
                        { System.Threading.Thread.Sleep(5); continue; }
                        int n = _ns.Read(rest, got, rest.Length - got);
                        if (n <= 0) { Status = "MC 连接被关闭"; return null; }
                        got += n;
                    }
                    if (got < rest.Length) { Status = "MC 响应不完整"; return null; }
                    ushort endCode = (ushort)((rest[body] << 8) | rest[body + 1]);
                    if (endCode != 0)
                    {
                        Status = "MC 结束码 0x" + endCode.ToString("X4") + "（" + EndCodeText(endCode) + "）";
                        return null;
                    }
                    if (body > 0)
                    {
                        byte[] data = new byte[body];
                        Buffer.BlockCopy(rest, 0, data, 0, body);
                        return data;
                    }
                    return Array.Empty<byte>();
                }
                catch (Exception ex)
                {
                    Status = "MC 通信错误：" + ex.Message;
                    return null;
                }
            }
        }

        private static string EndCodeText(ushort c) => c switch
        {
            0xC051 => "长度异常",
            0xC052 => "软元件地址/点数超范围",
            0xC055 => "写入对象软元件不存在",
            0xC056 => "数据异常",
            0xC059 => "命令异常",
            0xC05B => "命令与子命令组合错误",
            0xC061 => "软元件不存在",
            0xC064 => "禁止访问",
            0xC0D0 => "请求被取消",
            _ => "错误",
        };

        public void Disconnect()
        {
            lock (_lock)
            {
                try { _ns?.Dispose(); } catch { }
                try { _tcp?.Close(); } catch { }
                _ns = null;
                _tcp = null;
                Status = "已断开";
            }
        }

        public void Dispose() => Disconnect();
    }
}
