using System;
using System.IO;
using Newtonsoft.Json;

namespace VisionToolDemo.Vision.External
{
    /// <summary>
    /// 采集/通信配置（相机 + PLC + 通信参数）：
    /// JSON 持久化到 %APPDATA%\VisionToolDemo\capture.json，与主题配置同目录。
    /// 采集页面读写；启动时可加载套用。
    /// </summary>
    public sealed class CaptureConfig
    {
        // ---- 相机 ----
        /// <summary>0=USB 1=海康MVS 2=大恒Galaxy</summary>
        public int CameraType { get; set; } = 0;
        public int CameraIndex { get; set; } = 0;
        public int CameraBackend { get; set; } = 0;   // 0=DSHOW 1=MSMF 2=ANY（仅 USB）
        public int CameraWidth { get; set; }
        public int CameraHeight { get; set; }
        public int CameraFps { get; set; }

        // ---- PLC ----
        /// <summary>0=ModbusTCP 1=ModbusRTU 2=西门子S7 3=三菱MC</summary>
        public int PlcType { get; set; } = 0;
        public string PlcIp { get; set; } = "192.168.1.1";
        public int PlcPort { get; set; } = 502;
        public int PlcUnit { get; set; } = 1;
        public string PlcCom { get; set; } = "COM3";
        public int PlcBaud { get; set; } = 9600;
        public int PlcDataBits { get; set; } = 8;
        public int PlcParity { get; set; } = 0;       // 0=None 1=Odd 2=Even
        public int PlcStopBits { get; set; } = 1;
        public int PlcSlaveId { get; set; } = 1;
        // 西门子 S7
        public string S7Ip { get; set; } = "192.168.0.1";
        public int S7Port { get; set; } = 102;
        public int S7Rack { get; set; } = 0;
        public int S7Slot { get; set; } = 1;
        // 三菱 MC
        public string McIp { get; set; } = "192.168.3.250";
        public int McPort { get; set; } = 6000;
        // 通用 TCP / UDP / 串口
        public string TcpIp { get; set; } = "127.0.0.1";
        public int TcpPort { get; set; } = 1024;
        public string UdpIp { get; set; } = "127.0.0.1";
        public int UdpPort { get; set; } = 5000;
        public string SerialCom { get; set; } = "COM3";
        public int SerialBaud { get; set; } = 9600;
        public int SerialDataBits { get; set; } = 8;
        public int SerialParity { get; set; } = 0;
        public int SerialStopBits { get; set; } = 1;

        // ---- 通信参数 ----
        public int TimeoutMs { get; set; } = 1500;
        public int RetryCount { get; set; } = 1;

        // ==================== 持久化 ====================

        public static string ConfigPath()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(dir, "VisionToolDemo", "capture.json");
        }

        /// <summary>加载配置；文件不存在或解析失败时返回默认配置。</summary>
        public static CaptureConfig Load()
        {
            try
            {
                string path = ConfigPath();
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<CaptureConfig>(File.ReadAllText(path));
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            return new CaptureConfig();
        }

        /// <summary>保存配置；返回错误文本，成功返回 null。</summary>
        public string Save()
        {
            try
            {
                string path = ConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
                return null;
            }
            catch (Exception ex)
            {
                return "保存配置失败：" + ex.Message;
            }
        }

        /// <summary>把通信参数套用到 CommHub（连接前调用）</summary>
        public void ApplyToHub()
        {
            CommHub.TimeoutMs = TimeoutMs > 0 ? TimeoutMs : 1500;
            CommHub.RetryCount = Math.Max(0, RetryCount);
        }

        /// <summary>按配置创建相机源（不连接，只创建实例；连接需调 Open）</summary>
        public ICameraSource CreateCamera()
        {
            return CameraType switch
            {
                1 => new HikCameraSource(),
                2 => new DahengCameraSource(),
                _ => new UsbCameraSource(CameraIndex, (OpenCvSharp.VideoCaptureAPIs)CameraBackend,
                    CameraWidth, CameraHeight, CameraFps),
            };
        }

        /// <summary>按配置创建 PLC 客户端（不连接，只创建实例）</summary>
        public IPlcClient CreatePlc()
        {
            return PlcType switch
            {
                1 => new ModbusRtuClient(PlcCom, PlcBaud, PlcDataBits, PlcParity, PlcStopBits, PlcSlaveId),
                _ => new ModbusTcpClient(PlcIp, PlcPort, PlcUnit),
            };
        }

        /// <summary>按配置创建任意协议工控客户端（Modbus / 西门子S7 / 三菱MC）。</summary>
        public object CreatePlcAny()
        {
            return PlcType switch
            {
                2 => new S7Client(S7Ip, S7Port, S7Rack, S7Slot),
                3 => new McClient(McIp, McPort),
                _ => CreatePlc(),
            };
        }

        /// <summary>创建通用 TCP 客户端（不连接）。</summary>
        public GenericTcpClient CreateTcp() => new(TcpIp, TcpPort);

        /// <summary>创建通用 UDP 客户端（不连接）。</summary>
        public GenericUdpClient CreateUdp() => new(UdpIp, UdpPort);

        /// <summary>创建通用串口（不打开）。</summary>
        public SerialPortNative CreateSerial() => new(SerialCom, SerialBaud, SerialDataBits, SerialParity, SerialStopBits);
    }
}
