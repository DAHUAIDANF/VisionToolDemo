using System;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// PLC 统一接口：Modbus TCP / Modbus RTU / 自定义 TCP 都实现同一组读写。
    /// 方法返回 null = 成功，非 null = 可读错误文本。
    /// </summary>
    public interface IPlcClient : IDisposable
    {
        /// <summary>协议名（ModbusTCP/ModbusRTU/自定义TCP）</summary>
        string Name { get; }

        /// <summary>最近状态描述</summary>
        string Status { get; }

        /// <summary>是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>连接。成功返回 null，失败返回错误文本。</summary>
        string Connect();

        /// <summary>断开（幂等）</summary>
        void Disconnect();

        /// <summary>读保持寄存器（FC03）：start 起始地址，count 数量。</summary>
        string ReadRegisters(int start, int count, out ushort[] values);

        /// <summary>读线圈（FC01）。</summary>
        string ReadCoils(int start, int count, out bool[] values);

        /// <summary>写单个保持寄存器（FC06）。</summary>
        string WriteRegister(int addr, ushort value);

        /// <summary>写单个线圈（FC05）。</summary>
        string WriteCoil(int addr, bool value);
    }
}
