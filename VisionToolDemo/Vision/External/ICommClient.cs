using System;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 通用通信客户端接口（西门子 S7 / 三菱 MC / 自定义 TCP / UDP 等）。
    /// Modbus 走 IPlcClient（寄存器/线圈语义），非 Modbus 协议走本接口。
    /// Connect 成功返回 null，失败返回可读错误文本。
    /// </summary>
    public interface ICommClient : IDisposable
    {
        string Name { get; }
        string Status { get; }
        bool IsConnected { get; }
        string Connect();
        void Disconnect();
    }
}
