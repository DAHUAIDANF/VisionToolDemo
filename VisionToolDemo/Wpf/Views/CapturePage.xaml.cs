using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VisionToolDemo.Vision.External;

namespace VisionToolDemo.Wpf.Views
{
    /// <summary>
    /// 采集与通信页：相机（USB/海康MVS/大恒Galaxy）、
    /// PLC/工控（Modbus TCP / Modbus RTU / 西门子S7 / 三菱MC）、
    /// 通用通信（TCP / UDP / 串口 自定义协议）的连接管理与收发测试。
    /// 连接状态全局共享（CommHub）；参数持久化 %APPDATA%\VisionToolDemo\capture.json。
    /// </summary>
    public partial class CapturePage : UserControl
    {
        private CaptureConfig _cfg;

        public CapturePage()
        {
            InitializeComponent();
            for (int i = 0; i < 8; i++) CamIndexBox.Items.Add(i.ToString());
            CamIndexBox.SelectedIndex = 0;
            CamBackendBox.SelectedIndex = 0;
            CamTypeBox.SelectedIndex = 0;
            PlcTypeBox.SelectedIndex = 0;
            PlcBaudBox.SelectedIndex = 0;    // 9600
            PlcDataBox.SelectedIndex = 1;    // 8
            PlcParityBox.SelectedIndex = 0;  // 无
            PlcStopBox.SelectedIndex = 0;    // 1
            GenTypeBox.SelectedIndex = 0;
            GenBaudBox.SelectedIndex = 0;
            GenDataBox.SelectedIndex = 1;
            GenParityBox.SelectedIndex = 0;
            GenStopBox.SelectedIndex = 0;
            LoadConfig(new CaptureConfig());
            UpdateRows();
            Unloaded += (_, _) => CommHub.DisconnectAll();   // 页面离开即断开，避免残留句柄
        }

        // ==================== 配置读写 ====================

        private void LoadConfig(CaptureConfig cfg)
        {
            _cfg = cfg;
            CamTypeBox.SelectedIndex = Math.Clamp(cfg.CameraType, 0, 2);
            CamIndexBox.SelectedIndex = Math.Clamp(cfg.CameraIndex, 0, 7);
            CamBackendBox.SelectedIndex = Math.Clamp(cfg.CameraBackend, 0, 2);
            CamWBox.Text = cfg.CameraWidth.ToString();
            CamHBox.Text = cfg.CameraHeight.ToString();
            CamFpsBox.Text = cfg.CameraFps.ToString();

            PlcTypeBox.SelectedIndex = Math.Clamp(cfg.PlcType, 0, 3);
            PlcIpBox.Text = cfg.PlcIp;
            PlcPortBox.Text = cfg.PlcPort.ToString();
            PlcUnitBox.Text = cfg.PlcUnit.ToString();
            PlcComBox.Text = cfg.PlcCom;
            PlcBaudBox.SelectedIndex = BaudIndex(cfg.PlcBaud);
            PlcDataBox.SelectedIndex = cfg.PlcDataBits == 7 ? 0 : 1;
            PlcParityBox.SelectedIndex = Math.Clamp(cfg.PlcParity, 0, 2);
            PlcStopBox.SelectedIndex = cfg.PlcStopBits >= 2 ? 1 : 0;
            PlcSlaveBox.Text = cfg.PlcSlaveId.ToString();
            S7IpBox.Text = cfg.S7Ip;
            S7PortBox.Text = cfg.S7Port.ToString();
            S7RackBox.Text = cfg.S7Rack.ToString();
            S7SlotBox.Text = cfg.S7Slot.ToString();
            McIpBox.Text = cfg.McIp;
            McPortBox.Text = cfg.McPort.ToString();

            GenTypeBox.SelectedIndex = 0;
            GenTcpIpBox.Text = cfg.TcpIp;
            GenTcpPortBox.Text = cfg.TcpPort.ToString();
            GenUdpIpBox.Text = cfg.UdpIp;
            GenUdpPortBox.Text = cfg.UdpPort.ToString();
            GenComBox.Text = cfg.SerialCom;
            GenBaudBox.SelectedIndex = BaudIndex(cfg.SerialBaud);
            GenDataBox.SelectedIndex = cfg.SerialDataBits == 7 ? 0 : 1;
            GenParityBox.SelectedIndex = Math.Clamp(cfg.SerialParity, 0, 2);
            GenStopBox.SelectedIndex = cfg.SerialStopBits >= 2 ? 1 : 0;

            TimeoutBox.Text = cfg.TimeoutMs.ToString();
            RetryBox.Text = cfg.RetryCount.ToString();
        }

        /// <summary>从界面控件收集当前配置（连接前调用）</summary>
        private CaptureConfig Collect()
        {
            var cfg = _cfg ?? new CaptureConfig();
            cfg.CameraType = CamTypeBox.SelectedIndex;
            cfg.CameraIndex = int.TryParse(CamIndexBox.SelectedItem as string, out int ci) ? ci : 0;
            cfg.CameraBackend = CamBackendBox.SelectedIndex;
            cfg.CameraWidth = I(CamWBox.Text);
            cfg.CameraHeight = I(CamHBox.Text);
            cfg.CameraFps = I(CamFpsBox.Text);

            cfg.PlcType = PlcTypeBox.SelectedIndex;
            cfg.PlcIp = PlcIpBox.Text.Trim();
            cfg.PlcPort = I(PlcPortBox.Text, 502);
            cfg.PlcUnit = I(PlcUnitBox.Text, 1);
            cfg.PlcCom = PlcComBox.Text.Trim();
            cfg.PlcBaud = BaudFromIndex(PlcBaudBox.SelectedIndex);
            cfg.PlcDataBits = PlcDataBox.SelectedIndex == 0 ? 7 : 8;
            cfg.PlcParity = Math.Clamp(PlcParityBox.SelectedIndex, 0, 2);
            cfg.PlcStopBits = PlcStopBox.SelectedIndex == 0 ? 1 : 2;
            cfg.PlcSlaveId = I(PlcSlaveBox.Text, 1);
            cfg.S7Ip = S7IpBox.Text.Trim();
            cfg.S7Port = I(S7PortBox.Text, 102);
            cfg.S7Rack = I(S7RackBox.Text);
            cfg.S7Slot = I(S7SlotBox.Text, 1);
            cfg.McIp = McIpBox.Text.Trim();
            cfg.McPort = I(McPortBox.Text, 6000);

            cfg.TcpIp = GenTcpIpBox.Text.Trim();
            cfg.TcpPort = I(GenTcpPortBox.Text, 1024);
            cfg.UdpIp = GenUdpIpBox.Text.Trim();
            cfg.UdpPort = I(GenUdpPortBox.Text, 5000);
            cfg.SerialCom = GenComBox.Text.Trim();
            cfg.SerialBaud = BaudFromIndex(GenBaudBox.SelectedIndex);
            cfg.SerialDataBits = GenDataBox.SelectedIndex == 0 ? 7 : 8;
            cfg.SerialParity = Math.Clamp(GenParityBox.SelectedIndex, 0, 2);
            cfg.SerialStopBits = GenStopBox.SelectedIndex == 0 ? 1 : 2;

            cfg.TimeoutMs = I(TimeoutBox.Text, 1500);
            cfg.RetryCount = I(RetryBox.Text, 1);
            return cfg;
        }

        private static int I(string s, int def = 0)
            => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        private static int BaudIndex(int baud) => baud switch
        {
            19200 => 1,
            38400 => 2,
            57600 => 3,
            115200 => 4,
            _ => 0,
        };

        private static int BaudFromIndex(int idx) => idx switch
        {
            1 => 19200,
            2 => 38400,
            3 => 57600,
            4 => 115200,
            _ => 9600,
        };

        private void UpdateRows()
        {
            UsbRow.Visibility = CamTypeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            int pt = PlcTypeBox.SelectedIndex;
            TcpRow.Visibility = pt == 0 ? Visibility.Visible : Visibility.Collapsed;
            RtuRow.Visibility = pt == 1 ? Visibility.Visible : Visibility.Collapsed;
            S7Row.Visibility = pt == 2 ? Visibility.Visible : Visibility.Collapsed;
            McRow.Visibility = pt == 3 ? Visibility.Visible : Visibility.Collapsed;
            PlcAddrHint.Text = pt switch
            {
                2 => "S7 地址：DB1.DBW0 / DB1.DBD4 / MW20 / M0.0 / QB2 / VB10",
                3 => "MC 地址：D100（字）/ M10 / X10 / Y10（位）",
                _ => "Modbus 地址：0~65535（读保持寄存器 / 线圈）",
            };

            int gt = GenTypeBox.SelectedIndex;
            GenTcpRow.Visibility = gt == 0 ? Visibility.Visible : Visibility.Collapsed;
            GenUdpRow.Visibility = gt == 1 ? Visibility.Visible : Visibility.Collapsed;
            GenSerialRow.Visibility = gt == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 相机 ====================

        private void CamTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRows();

        private void BtnCamConnect_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            try
            {
                lock (CommHub.Sync)
                {
                    CommHub.Camera?.Dispose();
                    CommHub.Camera = null;
                }
                var cam = cfg.CreateCamera();
                string err = cam.Open();
                if (err != null)
                {
                    cam.Dispose();
                    CamStatus.Text = err;
                    return;
                }
                lock (CommHub.Sync) CommHub.Camera = cam;
                CamStatus.Text = cam.Status;
                BtnCamGrab_Click(sender, e);   // 连接成功自动取一帧
            }
            catch (Exception ex)
            {
                CamStatus.Text = "连接异常：" + ex.Message;
            }
        }

        private void BtnCamDisconnect_Click(object sender, RoutedEventArgs e)
        {
            lock (CommHub.Sync)
            {
                CommHub.Camera?.Dispose();
                CommHub.Camera = null;
            }
            PreviewImage.Source = null;
            PreviewHint.Text = "点击「取一帧预览」查看相机画面";
            CamStatus.Text = "相机未连接";
        }

        private void BtnCamGrab_Click(object sender, RoutedEventArgs e)
        {
            using var frame = CommHub.TryGrabCamera();
            if (frame == null || frame.Empty())
            {
                lock (CommHub.Sync)
                    CamStatus.Text = CommHub.Camera?.Status ?? "相机未连接";
                return;
            }
            PreviewImage.Source = MatImage.ToBitmapSource(frame);
            PreviewHint.Text = string.Format("预览 {0}x{1}", frame.Cols, frame.Rows);
            lock (CommHub.Sync)
                CamStatus.Text = CommHub.Camera?.Status ?? "已取帧";
        }

        // ==================== PLC / 工控 ====================

        private void PlcTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRows();

        private void BtnPlcConnect_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            try
            {
                lock (CommHub.Sync)
                {
                    (CommHub.PlcAny as IDisposable)?.Dispose();
                    CommHub.PlcAny = null;
                    CommHub.Plc = null;
                }
                object client = cfg.CreatePlcAny();
                string err = client switch
                {
                    IPlcClient plc => plc.Connect(),
                    ICommClient comm => comm.Connect(),
                    _ => "未知客户端类型",
                };
                if (err != null)
                {
                    (client as IDisposable)?.Dispose();
                    PlcStatus.Text = err;
                    return;
                }
                lock (CommHub.Sync)
                {
                    CommHub.PlcAny = client;
                    if (client is IPlcClient p) CommHub.Plc = p;   // Modbus 同时挂到 Plc（节点用）
                }
                PlcStatus.Text = StatusOf(client);
            }
            catch (Exception ex)
            {
                PlcStatus.Text = "连接异常：" + ex.Message;
            }
        }

        private void BtnPlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            lock (CommHub.Sync)
            {
                (CommHub.PlcAny as IDisposable)?.Dispose();
                CommHub.PlcAny = null;
                CommHub.Plc = null;
            }
            PlcStatus.Text = "PLC 未连接";
            PlcTestResult.Text = "（未测试）";
        }

        private static string StatusOf(object client) => client switch
        {
            IPlcClient p => p.Status,
            ICommClient c => c.Status,
            _ => "已连接",
        };

        /// <summary>当前连接的任意协议客户端（未连接返回 null）</summary>
        private static object CurrentPlc()
        {
            lock (CommHub.Sync) return CommHub.PlcAny;
        }

        private void BtnPlcTest_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            var client = CurrentPlc();
            if (client == null) { PlcTestResult.Text = "请先连接 PLC"; return; }
            try
            {
                switch (client)
                {
                    case IPlcClient plc:
                    {
                        int addr = I(PlcAddrBox.Text);
                        string err = CommHub.ReadPlc(addr, false, 1, out object[] vals);
                        PlcTestResult.Text = err != null
                            ? "读取失败：" + err
                            : string.Format("读取 OK：寄存器[{0}] = {1}", addr, vals.Length > 0 ? vals[0] : "?");
                        break;
                    }
                    case S7Client s7:
                    {
                        string addr = PlcAddrBox.Text.Trim();
                        string err = s7.ReadValue(addr, out byte[] bytes);
                        if (err != null) { PlcTestResult.Text = "S7 读取失败：" + err; break; }
                        PlcTestResult.Text = string.Format("S7 读取 OK：{0} = {1}", addr, FormatS7(bytes, addr));
                        break;
                    }
                    case McClient mc:
                    {
                        string addr = PlcAddrBox.Text.Trim();
                        bool word = addr.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                                 || addr.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                                 || addr.StartsWith("W", StringComparison.OrdinalIgnoreCase);
                        if (word)
                        {
                            string err = mc.ReadWord(addr, out ushort v);
                            PlcTestResult.Text = err != null ? "MC 读取失败：" + err : $"MC 读取 OK：{addr} = {v}";
                        }
                        else
                        {
                            string err = mc.ReadBit(addr, out bool v);
                            PlcTestResult.Text = err != null ? "MC 读取失败：" + err : $"MC 读取 OK：{addr} = {(v ? "ON" : "OFF")}";
                        }
                        break;
                    }
                    default:
                        PlcTestResult.Text = "当前协议不支持读取测试";
                        break;
                }
                PlcStatus.Text = StatusOf(client);
            }
            catch (Exception ex)
            {
                PlcTestResult.Text = "读取异常：" + ex.Message;
            }
        }

        private void BtnPlcWrite_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            var client = CurrentPlc();
            if (client == null) { PlcTestResult.Text = "请先连接 PLC"; return; }
            try
            {
                switch (client)
                {
                    case IPlcClient plc:
                    {
                        int addr = I(PlcAddrBox.Text);
                        ushort val = (ushort)Math.Clamp(I(PlcValBox.Text), 0, 65535);
                        string err = CommHub.WritePlc(addr, false, val);
                        PlcTestResult.Text = err != null ? "写入失败：" + err : $"写入 OK：寄存器[{addr}] = {val}";
                        break;
                    }
                    case S7Client s7:
                    {
                        string addr = PlcAddrBox.Text.Trim();
                        byte[] val = S7ValueBytes(addr, PlcValBox.Text.Trim());
                        if (val == null) { PlcTestResult.Text = "值无法解析（按 S7 类型给十进制值）"; break; }
                        string err = s7.WriteValue(addr, val);
                        PlcTestResult.Text = err != null ? "S7 写入失败：" + err : $"S7 写入 OK：{addr} = {PlcValBox.Text.Trim()}";
                        break;
                    }
                    case McClient mc:
                    {
                        string addr = PlcAddrBox.Text.Trim();
                        bool word = addr.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                                 || addr.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                                 || addr.StartsWith("W", StringComparison.OrdinalIgnoreCase);
                        string err;
                        if (word) err = mc.WriteWord(addr, (ushort)Math.Clamp(I(PlcValBox.Text), 0, 65535));
                        else err = mc.WriteBit(addr, PlcValBox.Text.Trim() != "0" && !string.Equals(PlcValBox.Text.Trim(), "OFF", StringComparison.OrdinalIgnoreCase));
                        PlcTestResult.Text = err != null ? "MC 写入失败：" + err : $"MC 写入 OK：{addr} = {PlcValBox.Text.Trim()}";
                        break;
                    }
                    default:
                        PlcTestResult.Text = "当前协议不支持写入测试";
                        break;
                }
                PlcStatus.Text = StatusOf(client);
            }
            catch (Exception ex)
            {
                PlcTestResult.Text = "写入异常：" + ex.Message;
            }
        }

        /// <summary>把 UI 文本按 S7 地址类型转写入字节（小端序低字节在前）。</summary>
        private static byte[] S7ValueBytes(string address, string text)
        {
            if (!int.TryParse(text.Trim(), out int v)) return null;
            string t = address.Trim().ToUpperInvariant();
            int need;
            if (t.Contains(".DBX") || t.StartsWith("MB") || t.StartsWith("IB") || t.StartsWith("QB") || t.StartsWith("VB")
                || (t.StartsWith("M") && t.Contains(".")) || (t.StartsWith("I") && t.Contains(".")) || (t.StartsWith("Q") && t.Contains(".")))
                need = 1;
            else if (t.Contains(".DBW") || t.StartsWith("MW") || t.StartsWith("IW") || t.StartsWith("QW") || t.StartsWith("VW")
                     || t.StartsWith("M") || t.StartsWith("I") || t.StartsWith("Q"))
                need = 2;
            else if (t.Contains(".DBD") || t.StartsWith("MD") || t.StartsWith("ID") || t.StartsWith("QD") || t.StartsWith("VD"))
                need = 4;
            else need = 2;   // 默认按字
            byte[] bytes = new byte[need];
            for (int i = 0; i < need; i++) bytes[i] = (byte)((uint)v >> (8 * i));
            return bytes;
        }

        /// <summary>把 S7 读到的原始字节按地址类型显示为十进制。</summary>
        private static string FormatS7(byte[] b, string address)
        {
            if (b == null || b.Length == 0) return "空";
            string t = address.Trim().ToUpperInvariant();
            if (b.Length == 1) return b[0].ToString();
            if (b.Length == 2) return ((b[0] << 8) | b[1]).ToString();
            uint v = 0;
            for (int i = 0; i < 4 && i < b.Length; i++) v = (v << 8) | b[i];
            return v.ToString();
        }

        // ==================== 通用通信 ====================

        private void GenTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRows();

        private void BtnGenConnect_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            try
            {
                lock (CommHub.Sync)
                {
                    CommHub.TcpClient?.Dispose(); CommHub.TcpClient = null;
                    CommHub.UdpClient?.Dispose(); CommHub.UdpClient = null;
                    CommHub.SerialClient?.Dispose(); CommHub.SerialClient = null;
                }
                switch (GenTypeBox.SelectedIndex)
                {
                    case 0:
                        var tcp = cfg.CreateTcp();
                        string e1 = tcp.Connect();
                        if (e1 != null) { tcp.Dispose(); GenStatus.Text = e1; return; }
                        lock (CommHub.Sync) CommHub.TcpClient = tcp;
                        GenStatus.Text = tcp.Status;
                        break;
                    case 1:
                        var udp = cfg.CreateUdp();
                        string e2 = udp.Connect();
                        if (e2 != null) { udp.Dispose(); GenStatus.Text = e2; return; }
                        lock (CommHub.Sync) CommHub.UdpClient = udp;
                        GenStatus.Text = udp.Status;
                        break;
                    default:
                        var sp = cfg.CreateSerial();
                        string e3 = sp.Open();
                        if (e3 != null) { sp.Dispose(); GenStatus.Text = e3; return; }
                        lock (CommHub.Sync) CommHub.SerialClient = sp;
                        GenStatus.Text = "串口已打开 " + cfg.SerialCom;
                        break;
                }
            }
            catch (Exception ex)
            {
                GenStatus.Text = "连接异常：" + ex.Message;
            }
        }

        private void BtnGenDisconnect_Click(object sender, RoutedEventArgs e)
        {
            lock (CommHub.Sync)
            {
                CommHub.TcpClient?.Dispose(); CommHub.TcpClient = null;
                CommHub.UdpClient?.Dispose(); CommHub.UdpClient = null;
                CommHub.SerialClient?.Dispose(); CommHub.SerialClient = null;
            }
            GenStatus.Text = "通用通信未连接";
            GenRecvHint.Text = "未接收";
        }

        private void BtnGenSend_Click(object sender, RoutedEventArgs e)
        {
            byte[] payload = ParsePayload(GenSendBox.Text, GenHexBox.IsChecked == true);
            if (payload == null)
            {
                GenStatus.Text = "发送内容无法解析：HEX 模式请输入空格分隔的十六进制，如 01 03 00 00 00 01";
                return;
            }
            lock (CommHub.Sync)
            {
                switch (GenTypeBox.SelectedIndex)
                {
                    case 0:
                        var tcp = CommHub.TcpClient;
                        if (tcp == null || !tcp.IsConnected) { GenStatus.Text = "TCP 未连接"; break; }
                        string e1 = tcp.SendReceive(payload, out byte[] r1);
                        GenStatus.Text = e1 ?? tcp.Status;
                        if (e1 == null) ShowRecv(r1);
                        break;
                    case 1:
                        var udp = CommHub.UdpClient;
                        if (udp == null || !udp.IsConnected) { GenStatus.Text = "UDP 未连接"; break; }
                        string e2 = udp.SendReceive(payload, out byte[] r2);
                        GenStatus.Text = e2 ?? udp.Status;
                        if (e2 == null) ShowRecv(r2);
                        break;
                    default:
                        var sp = CommHub.SerialClient;
                        if (sp == null || !sp.IsOpen) { GenStatus.Text = "串口未打开"; break; }
                        string e3 = sp.Write(payload);
                        if (e3 != null) { GenStatus.Text = e3; break; }
                        // 串口发送后尝试读回显（超时内）
                        byte[] buf = new byte[4096];
                        System.Threading.Thread.Sleep(60);
                        int n = sp.Read(buf, buf.Length);
                        GenStatus.Text = n > 0 ? string.Format("串口发送 OK，收到 {0} 字节", n) : "串口发送 OK（无回显）";
                        if (n > 0)
                        {
                            byte[] r = new byte[n];
                            Array.Copy(buf, r, n);
                            ShowRecv(r);
                        }
                        break;
                }
            }
        }

        private static byte[] ParsePayload(string text, bool hex)
        {
            if (hex)
            {
                var parts = (text ?? "").Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                byte[] b = new byte[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
                return b;
            }
            return Encoding.UTF8.GetBytes(text ?? "");
        }

        private void ShowRecv(byte[] data)
        {
            if (data == null || data.Length == 0) { GenRecvHint.Text = "空响应"; return; }
            var sb = new StringBuilder();
            sb.Append("HEX: ");
            foreach (byte b in data) sb.Append(b.ToString("X2")).Append(' ');
            string ascii = Encoding.UTF8.GetString(data);
            bool printable = true;
            foreach (char c in ascii)
                if (c < 32 && c != '\r' && c != '\n' && c != '\t') { printable = false; break; }
            if (printable) sb.Append("\n文本: ").Append(ascii);
            GenRecvBox.Text = sb.ToString();
            GenRecvHint.Text = data.Length + " 字节";
        }

        // ==================== 通信参数 ====================

        private void BtnSaveCfg_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            string err = cfg.Save();
            CfgStatus.Text = err ?? "已保存到 %APPDATA%\\VisionToolDemo\\capture.json";
            _cfg = cfg;
        }

        private void BtnLoadCfg_Click(object sender, RoutedEventArgs e)
        {
            LoadConfig(new CaptureConfig());
            CfgStatus.Text = "已恢复默认（未保存，点「保存配置」生效）";
        }
    }
}
