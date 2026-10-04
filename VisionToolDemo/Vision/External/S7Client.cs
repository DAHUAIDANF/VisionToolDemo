using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 西门子 S7 客户端（S7Comm 协议，TCP 102，零依赖）：
    /// 支持 S7-300/400/1200/1500 与 S7-200（V 区映射 DB1）。
    /// 地址格式：MB10 / MW20 / MD24 / M0.0；IB/IW/ID；QB/QW/QD；
    ///          DB1.DBB0 / DB1.DBW0 / DB1.DBD4 / DB1.DBX0.0；VB10（S7-200 V→DB1）。
    /// 读：ReadValue(地址) 返回字节数组；写：WriteValue(地址, 字节数组)。
    /// 注意：协议按 S7Comm 标准实现，未接真实 PLC 实测时请先用样例工具抓包核对。
    /// </summary>
    public sealed class S7Client : ICommClient
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly int _rack, _slot;
        private TcpClient _tcp;
        private NetworkStream _ns;
        private readonly object _lock = new();
        private ushort _pduRef = 1;

        public string Name => "西门子S7";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _tcp != null && _tcp.Connected;

        public S7Client(string ip, int port, int rack, int slot)
        {
            _ip = string.IsNullOrWhiteSpace(ip) ? "192.168.0.1" : ip.Trim();
            _port = port > 0 ? port : 102;
            _rack = Math.Clamp(rack, 0, 7);
            _slot = Math.Clamp(slot, 0, 31);
        }

        // ==================== 连接 ====================

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
                        Status = "S7 连接超时：" + _ip + ":" + _port;
                        return Status;
                    }
                    tcp.EndConnect(ar);
                    _tcp = tcp;
                    _ns = tcp.GetStream();

                    // 1) COTP Connect Request（22 字节，标准握手）
                    byte[] cr =
                    {
                        0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00,
                        0x00, 0x01, 0x00, 0xC0, 0x01, 0x0A, 0xC0, 0x01,
                        0x09, 0xC2, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x01,
                    };
                    _ns.Write(cr, 0, cr.Length);
                    _ns.Flush();
                    byte[] crResp = ReadFrame();
                    if (crResp == null) { Status = "S7 握手无响应"; return Status; }
                    // 成功确认：TPKT 03 00 00 17 11 D0 ...（或 11 C0）

                    // 2) 协商 PDU 大小（请求 480 字节）
                    byte[] setup =
                    {
                        0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80,
                        0x32, 0x01, 0x00, 0x00, 0x04, 0x00, 0x00, 0x08,
                        0x00, 0x00, 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01,
                        0x00, 0x0E, 0x00, 0x03,
                    };
                    _ns.Write(setup, 0, setup.Length);
                    _ns.Flush();
                    if (ReadFrame() == null) { Status = "S7 协商无响应"; return Status; }

                    Status = "S7 已连接 " + _ip + ":" + _port + "（机架" + _rack + "/槽" + _slot + "）";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "S7 连接失败：" + ex.Message;
                    try { _tcp?.Close(); } catch { }
                    _tcp = null;
                    return Status;
                }
            }
        }

        /// <summary>读完整 TPKT 帧（03 00 长度 载荷）。失败返回 null。</summary>
        private byte[] ReadFrame()
        {
            try
            {
                int deadline = Environment.TickCount + CommHub.TimeoutMs;
                byte[] head = new byte[4];
                int off = 0;
                while (off < 4 && Environment.TickCount < deadline)
                {
                    if (!_ns.DataAvailable && !_ns.Socket.Poll(50 * 1000, SelectMode.SelectRead))
                    { System.Threading.Thread.Sleep(5); continue; }
                    int n = _ns.Read(head, off, 4 - off);
                    if (n <= 0) return null;
                    off += n;
                }
                if (off < 4) return null;
                int len = (head[2] << 8) | head[3];
                if (len < 2 || len > 8192) return null;
                byte[] body = new byte[len];
                off = 0;
                while (off < len && Environment.TickCount < deadline)
                {
                    if (!_ns.DataAvailable && !_ns.Socket.Poll(50 * 1000, SelectMode.SelectRead))
                    { System.Threading.Thread.Sleep(5); continue; }
                    int n = _ns.Read(body, off, len - off);
                    if (n <= 0) return null;
                    off += n;
                }
                if (off < len) return null;
                byte[] full = new byte[4 + len];
                Buffer.BlockCopy(head, 0, full, 0, 4);
                Buffer.BlockCopy(body, 0, full, 4, len);
                return full;
            }
            catch { return null; }
        }

        // ==================== 地址解析 ====================

        internal enum Area { Db = 0x81, Mk = 0x82, Pe = 0x83, Pa = 0x84 }

        internal struct S7Addr
        {
            public Area Area;
            public int Db;
            public byte Transport;   // 1=位 2=字节 4=字 6=双字
            public int ByteAddr;
            public int Bit;
        }

        private static readonly Regex ReDb = new(@"^DB(\d+)\.(DBX|DBB|DBW|DBD)(\d+)(?:\.(\d))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReMk = new(@"^(M|MB|MW|MD)(\d+)(?:\.(\d))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RePe = new(@"^(I|IB|IW|ID)(\d+)(?:\.(\d))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RePa = new(@"^(Q|QB|QW|QD)(\d+)(?:\.(\d))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReV = new(@"^V(B|W|D)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>解析 S7 地址；失败返回错误文本（addr 形如 DB1.DBW0 / MW20 / M0.0）。</summary>
        internal static string Parse(string text, out S7Addr a)
        {
            a = default;
            text = (text ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(text)) return "地址为空";
            Match m;

            if ((m = ReDb.Match(text)).Success)
            {
                a.Area = Area.Db;
                a.Db = int.Parse(m.Groups[1].Value);
                string t = m.Groups[2].Value;
                a.Transport = t switch { "DBX" => (byte)1, "DBB" => (byte)2, "DBW" => (byte)4, _ => (byte)6 };
                a.ByteAddr = int.Parse(m.Groups[3].Value);
                a.Bit = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
                if (a.Transport == 1 && m.Groups[4].Success && a.Bit > 7) return "位地址 0~7";
            }
            else if ((m = ReMk.Match(text)).Success)
            {
                a.Area = Area.Mk;
                string t = m.Groups[1].Value;
                a.Transport = t switch { "M" => (byte)1, "MB" => (byte)2, "MW" => (byte)4, _ => (byte)6 };
                a.ByteAddr = int.Parse(m.Groups[2].Value);
                a.Bit = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            }
            else if ((m = RePe.Match(text)).Success)
            {
                a.Area = Area.Pe;
                string t = m.Groups[1].Value;
                a.Transport = t switch { "I" => (byte)1, "IB" => (byte)2, "IW" => (byte)4, _ => (byte)6 };
                a.ByteAddr = int.Parse(m.Groups[2].Value);
                a.Bit = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            }
            else if ((m = RePa.Match(text)).Success)
            {
                a.Area = Area.Pa;
                string t = m.Groups[1].Value;
                a.Transport = t switch { "Q" => (byte)1, "QB" => (byte)2, "QW" => (byte)4, _ => (byte)6 };
                a.ByteAddr = int.Parse(m.Groups[2].Value);
                a.Bit = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            }
            else if ((m = ReV.Match(text)).Success)
            {
                // S7-200 的 V 区映射到 DB1
                a.Area = Area.Db;
                a.Db = 1;
                a.Transport = m.Groups[1].Value switch { "B" => (byte)2, "W" => (byte)4, _ => (byte)6 };
                a.ByteAddr = int.Parse(m.Groups[2].Value);
                a.Bit = 0;
            }
            else
            {
                return "无法识别的 S7 地址：" + text + "（支持 MB/MW/MD/M0.0、IB/IW、QB/QW、DB1.DBW0、VB10）";
            }
            if (a.ByteAddr > 0xFFFF) return "字节地址超范围";
            return null;
        }

        // ==================== 读写 ====================

        /// <summary>读一个地址。成功返回 null 且 bytes 为原始字节；失败返回错误文本。</summary>
        public string ReadValue(string address, out byte[] bytes)
        {
            bytes = null;
            if (!IsConnected) { Status = "S7 未连接"; return Status; }
            string e = Parse(address, out S7Addr a);
            if (e != null) { Status = e; return e; }
            lock (_lock)
            {
                try
                {
                    byte[] item = MakeItem(a, 0);
                    byte[] req = BuildRequest(0x04, item, null);
                    byte[] resp = Roundtrip(req);
                    if (resp == null) return Status;
                    string ee = ParseReadData(resp, a, out bytes);
                    Status = ee ?? ("S7 读 " + address + " OK（" + (bytes?.Length ?? 0) + " 字节）");
                    return ee;
                }
                catch (Exception ex)
                {
                    Status = "S7 读异常：" + ex.Message;
                    return Status;
                }
            }
        }

        /// <summary>写一个地址。data 按目标类型给字节数（位=1字节，字节=1，字=2，双字=4，小端序取低字节）。</summary>
        public string WriteValue(string address, byte[] data)
        {
            if (!IsConnected) { Status = "S7 未连接"; return Status; }
            string e = Parse(address, out S7Addr a);
            if (e != null) { Status = e; return e; }
            int need = a.Transport == 1 || a.Transport == 2 ? 1 : a.Transport == 4 ? 2 : 4;
            if (data == null || data.Length == 0) return "写入数据为空";
            byte[] value = new byte[need];
            for (int i = 0; i < need && i < data.Length; i++) value[i] = data[i];
            lock (_lock)
            {
                try
                {
                    byte[] item = MakeItem(a, value.Length);
                    byte[] req = BuildRequest(0x05, item, value);
                    byte[] resp = Roundtrip(req);
                    if (resp == null) return Status;
                    // 写响应：数据区首字节返回码（0x00=成功，0xFF 等为错误）
                    byte rc = ReadReturnCode(resp);
                    if (rc != 0x00)
                    {
                        Status = "S7 写失败：返回码 0x" + rc.ToString("X2") + "（" + ErrorText(rc) + "）";
                        return Status;
                    }
                    Status = "S7 写 " + address + " OK";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "S7 写异常：" + ex.Message;
                    return Status;
                }
            }
        }

        // ---------------- 报文 ----------------

        private byte[] MakeItem(S7Addr a, int writeLen)
        {
            // item：spec 0x12, len 0x0A, syntax 0x10, transport, len(2), db(2), area(1), addr(3)
            byte[] it = new byte[12];
            it[0] = 0x12;
            it[1] = 0x0A;
            it[2] = 0x10;
            it[3] = a.Transport;
            int len = a.Transport == 1 ? 1 : a.Transport == 2 ? 1 : a.Transport == 4 ? 2 : 4;
            it[4] = (byte)(len >> 8);
            it[5] = (byte)(len & 0xFF);
            it[6] = (byte)(a.Db >> 8);
            it[7] = (byte)(a.Db & 0xFF);
            it[8] = (byte)a.Area;
            int addr = a.ByteAddr * 8 + a.Bit;
            it[9] = (byte)(addr >> 16);
            it[10] = (byte)(addr >> 8);
            it[11] = (byte)(addr & 0xFF);
            return it;
        }

        /// <summary>function：0x04 读 / 0x05 写；writeData 为 null 表示读。</summary>
        private byte[] BuildRequest(byte function, byte[] item, byte[] writeData)
        {
            bool write = writeData != null;
            int paramsLen = 2 + item.Length;   // function + itemCount + item
            int dataLen = write ? 1 + 2 + 1 + 2 + writeData.Length : 0;
            // 写数据区：返回码占位(1) + 数据长度(2) + transport(1) + 长度(2) + 数据
            int total = 4 + 3 + 10 + paramsLen + dataLen;
            byte[] pkt = new byte[4 + 3 + 10 + paramsLen + dataLen];
            int p = 0;
            pkt[p++] = 0x03; pkt[p++] = 0x00;
            pkt[p++] = (byte)(total >> 8); pkt[p++] = (byte)(total & 0xFF);   // TPKT 长度
            pkt[p++] = 0x02; pkt[p++] = 0xF0; pkt[p++] = 0x80;                 // COTP DT
            pkt[p++] = 0x32; pkt[p++] = 0x01;                                  // 协议ID + Job
            ushort refId = _pduRef++;
            pkt[p++] = 0x00; pkt[p++] = 0x00;
            pkt[p++] = (byte)(refId >> 8); pkt[p++] = (byte)(refId & 0xFF);
            pkt[p++] = (byte)(paramsLen >> 8); pkt[p++] = (byte)(paramsLen & 0xFF);
            pkt[p++] = (byte)(dataLen >> 8); pkt[p++] = (byte)(dataLen & 0xFF);
            pkt[p++] = function; pkt[p++] = 0x01;                             // function + itemCount
            Buffer.BlockCopy(item, 0, pkt, p, item.Length); p += item.Length;
            if (write)
            {
                pkt[p++] = 0x00;                                             // 返回码占位
                pkt[p++] = (byte)((1 + 2 + writeData.Length) >> 8);          // 数据长度（transport+len+data）
                pkt[p++] = (byte)((1 + 2 + writeData.Length) & 0xFF);
                pkt[p++] = item[3];                                          // transport 与 item 一致
                pkt[p++] = (byte)(writeData.Length >> 8);
                pkt[p++] = (byte)(writeData.Length & 0xFF);
                Buffer.BlockCopy(writeData, 0, pkt, p, writeData.Length);
            }
            return pkt;
        }

        private byte[] Roundtrip(byte[] req)
        {
            _ns.Write(req, 0, req.Length);
            _ns.Flush();
            return ReadFrame();
        }

        private static byte ReadReturnCode(byte[] resp)
        {
            // resp = TPKT(4) + COTP(3) + header(10) + params + data(返回码,transport,len,data)
            if (resp == null || resp.Length < 17) return 0xFF;
            int headerEnd = 4 + 3 + 10;
            int dataLen = (resp[headerEnd - 2] << 8) | resp[headerEnd - 1];
            if (dataLen <= 0 || resp.Length <= headerEnd + 1) return 0x00;
            return resp[headerEnd];
        }

        private static string ParseReadData(byte[] resp, S7Addr a, out byte[] bytes)
        {
            bytes = null;
            if (resp == null || resp.Length < 18) return "S7 响应过短";
            int headerEnd = 4 + 3 + 10;
            int dataLen = (resp[headerEnd - 2] << 8) | resp[headerEnd - 1];
            if (dataLen <= 0 || resp.Length < headerEnd + dataLen)
                return "S7 响应数据长度异常";
            int d = headerEnd;
            byte rc = resp[d++];
            if (rc != 0x00)
                return "S7 读失败：返回码 0x" + rc.ToString("X2") + "（" + ErrorText(rc) + "）";
            // dataLen>2 时：transport + len + data
            if (dataLen < 4) return "S7 读响应无数据";
            byte transport = resp[d++];
            int len = (resp[d] << 8) | resp[d + 1];
            d += 2;
            if (len <= 0 || d + len > resp.Length) return "S7 读数据长度异常";
            bytes = new byte[len];
            Buffer.BlockCopy(resp, d, bytes, 0, len);
            return null;
        }

        private static string ErrorText(byte rc) => rc switch
        {
            0x05 => "数据块不存在/长度不符",
            0x06 => "对象不存在",
            0x0A => "对象处于不一致状态",
            0x81 => "无法访问该地址（地址越界/块未加载）",
            0xD2 => "写入的数据被拒绝（长度/类型不符）",
            0xD4 => "写保护",
            0xE0 => "找不到数据块",
            _ => "S7 错误码",
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
