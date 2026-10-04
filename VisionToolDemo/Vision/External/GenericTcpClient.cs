using System;
using System.Net.Sockets;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 通用 TCP 客户端（自定义私有协议）：连接后收发原始字节。
    /// 适合各种基于 TCP 的私有工控协议（厂商自定义报文、扫码枪、机器人等）。
    /// </summary>
    public sealed class GenericTcpClient : ICommClient
    {
        private readonly string _ip;
        private readonly int _port;
        private TcpClient _tcp;
        private NetworkStream _ns;
        private readonly object _lock = new();

        public string Name => "TCP";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _tcp != null && _tcp.Connected;

        public GenericTcpClient(string ip, int port)
        {
            _ip = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
            _port = port > 0 ? port : 1024;
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
                        Status = "TCP 连接超时：" + _ip + ":" + _port;
                        return Status;
                    }
                    tcp.EndConnect(ar);
                    _tcp = tcp;
                    _ns = tcp.GetStream();
                    Status = "TCP 已连接 " + _ip + ":" + _port;
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "TCP 连接失败：" + ex.Message;
                    return Status;
                }
            }
        }

        /// <summary>发送并读取一次响应（读到数据或超时为止）。返回错误文本，成功返回 null。</summary>
        public string SendReceive(byte[] data, out byte[] response)
        {
            response = null;
            lock (_lock)
            {
                try
                {
                    if (!IsConnected) { Status = "TCP 未连接"; return Status; }
                    _ns.Write(data, 0, data.Length);
                    _ns.Flush();
                    using var ms = new System.IO.MemoryStream();
                    int deadline = Environment.TickCount + CommHub.TimeoutMs;
                    byte[] buf = new byte[4096];
                    bool gotAny = false;
                    while (Environment.TickCount < deadline)
                    {
                        if (_ns.DataAvailable)
                        {
                            int n = _ns.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                            gotAny = true;
                        }
                        else if (gotAny) break;   // 已收到数据且此刻无新数据 → 视为一次完整响应
                        else System.Threading.Thread.Sleep(5);
                    }
                    if (!gotAny)
                    {
                        Status = "TCP 响应超时";
                        return Status;
                    }
                    response = ms.ToArray();
                    Status = "TCP 收发 OK（" + response.Length + " 字节）";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "TCP 通信错误：" + ex.Message;
                    return Status;
                }
            }
        }

        /// <summary>只发送（不等待响应）。</summary>
        public string Send(byte[] data)
        {
            lock (_lock)
            {
                try
                {
                    if (!IsConnected) { Status = "TCP 未连接"; return Status; }
                    _ns.Write(data, 0, data.Length);
                    _ns.Flush();
                    Status = "TCP 发送 OK（" + data.Length + " 字节）";
                    return null;
                }
                catch (Exception ex)
                {
                    Status = "TCP 发送错误：" + ex.Message;
                    return Status;
                }
            }
        }

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
