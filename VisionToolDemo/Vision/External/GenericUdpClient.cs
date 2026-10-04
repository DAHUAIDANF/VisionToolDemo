using System;
using System.Net;
using System.Net.Sockets;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 通用 UDP 客户端（自定义私有协议）：连接目标后收发原始字节。
    /// 适合 UDP 广播/点对点的私有工控协议。
    /// </summary>
    public sealed class GenericUdpClient : ICommClient
    {
        private readonly string _ip;
        private readonly int _port;
        private UdpClient _udp;
        private IPEndPoint _remote;

        public string Name => "UDP";
        public string Status { get; private set; } = "未连接";
        public bool IsConnected => _udp != null;

        public GenericUdpClient(string ip, int port)
        {
            _ip = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
            _port = port > 0 ? port : 5000;
        }

        public string Connect()
        {
            try
            {
                Disconnect();
                _remote = new IPEndPoint(IPAddress.Parse(_ip), _port);
                _udp = new UdpClient();
                _udp.Connect(_remote);
                Status = "UDP 已连接 " + _ip + ":" + _port;
                return null;
            }
            catch (Exception ex)
            {
                Status = "UDP 连接失败：" + ex.Message;
                return Status;
            }
        }

        /// <summary>发送并等待一次响应。返回错误文本，成功返回 null。</summary>
        public string SendReceive(byte[] data, out byte[] response)
        {
            response = null;
            try
            {
                if (!IsConnected) { Status = "UDP 未连接"; return Status; }
                _udp.Send(data, data.Length);
                var ar = _udp.BeginReceive(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(CommHub.TimeoutMs))
                {
                    Status = "UDP 响应超时";
                    return Status;
                }
                IPEndPoint from = new(_remote.Address, _remote.Port);
                response = _udp.EndReceive(ar, ref from);
                Status = "UDP 收发 OK（" + response.Length + " 字节）";
                return null;
            }
            catch (Exception ex)
            {
                Status = "UDP 通信错误：" + ex.Message;
                return Status;
            }
        }

        /// <summary>只发送（广播/单向）。</summary>
        public string Send(byte[] data)
        {
            try
            {
                if (!IsConnected) { Status = "UDP 未连接"; return Status; }
                _udp.Send(data, data.Length);
                Status = "UDP 发送 OK（" + data.Length + " 字节）";
                return null;
            }
            catch (Exception ex)
            {
                Status = "UDP 发送错误：" + ex.Message;
                return Status;
            }
        }

        public void Disconnect()
        {
            try { _udp?.Close(); } catch { }
            _udp = null;
            Status = "已断开";
        }

        public void Dispose() => Disconnect();
    }
}
