using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            // 顶栏主题色点：按当前主题刷新高亮；切换主题后本页即时跟随
            ThemeUi.RefreshDots(ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
            ThemeManager.RegisterPage(this);
            Unloaded += (_, _) => CommHub.DisconnectAll();   // 页面离开即断开，避免残留句柄
        }

        /// <summary>顶栏主题色点点击：应用主题并刷新本页色点（全软件风格统一）</summary>
        private void ThemeDot_Click(object sender, MouseButtonEventArgs e)
        {
            ThemeUi.ApplyFromClick(sender as Border, ThemeDot0, ThemeDot1, ThemeDot2, ThemeDot3, ThemeDot4);
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
            CamStatus.Text = "正在连接相机…";
            BtnCamConnect.IsEnabled = false;
            Task.Run(() =>
            {
                try
                {
                    ICameraSource cam;
                    lock (CommHub.Sync)
                    {
                        CommHub.Camera?.Dispose();
                        CommHub.Camera = null;
                    }
                    cam = cfg.CreateCamera();
                    string err = cam.Open();
                    if (err != null)
                    {
                        cam.Dispose();
                        Dispatcher.Invoke(() => { CamStatus.Text = err; BtnCamConnect.IsEnabled = true; });
                        return;
                    }
                    lock (CommHub.Sync) CommHub.Camera = cam;
                    // 连接成功自动取一帧（后台线程取帧并转位图，避免 OpenCV 阻塞 UI）
                    using var frame = CommHub.TryGrabCamera();
                    bool ok = frame != null && !frame.Empty();
                    BitmapSource bmp = ok ? MatImage.ToBitmapSource(frame) : null;
                    string sizeTxt = ok ? string.Format("预览 {0}x{1}", frame.Cols, frame.Rows) : null;
                    string statusTxt;
                    lock (CommHub.Sync) statusTxt = CommHub.Camera?.Status ?? "已取帧";
                    Dispatcher.Invoke(() =>
                    {
                        BtnCamConnect.IsEnabled = true;
                        CamStatus.Text = statusTxt;
                        if (bmp != null)
                        {
                            PreviewImage.Source = bmp;
                            PreviewHint.Text = sizeTxt;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => { CamStatus.Text = "连接异常：" + ex.Message; BtnCamConnect.IsEnabled = true; });
                }
            });
        }

        private void BtnCamDisconnect_Click(object sender, RoutedEventArgs e)
        {
            CamStatus.Text = "正在断开…";
            BtnCamConnect.IsEnabled = false;
            Task.Run(() =>
            {
                lock (CommHub.Sync)
                {
                    CommHub.Camera?.Dispose();
                    CommHub.Camera = null;
                }
                Dispatcher.Invoke(() =>
                {
                    BtnCamConnect.IsEnabled = true;
                    PreviewImage.Source = null;
                    PreviewHint.Text = "点击「取一帧预览」查看相机画面";
                    CamStatus.Text = "相机未连接";
                });
            });
        }

        private void BtnCamGrab_Click(object sender, RoutedEventArgs e)
        {
            CamStatus.Text = "正在取帧…";
            Task.Run(() =>
            {
                using var frame = CommHub.TryGrabCamera();
                bool ok = frame != null && !frame.Empty();
                BitmapSource bmp = ok ? MatImage.ToBitmapSource(frame) : null;
                string sizeTxt = ok ? string.Format("预览 {0}x{1}", frame.Cols, frame.Rows) : null;
                string statusTxt;
                lock (CommHub.Sync) statusTxt = CommHub.Camera?.Status ?? "相机未连接";
                Dispatcher.Invoke(() =>
                {
                    if (bmp != null)
                    {
                        PreviewImage.Source = bmp;
                        PreviewHint.Text = sizeTxt;
                        CamStatus.Text = statusTxt ?? "已取帧";
                    }
                    else CamStatus.Text = statusTxt ?? "相机未连接";
                });
            });
        }

        // ==================== PLC / 工控 ====================

        private void PlcTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRows();

        private void BtnPlcConnect_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            PlcStatus.Text = "正在连接 PLC…";
            BtnPlcConnect.IsEnabled = false;
            Task.Run(() =>
            {
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
                        Dispatcher.Invoke(() => { PlcStatus.Text = err; BtnPlcConnect.IsEnabled = true; });
                        return;
                    }
                    lock (CommHub.Sync)
                    {
                        CommHub.PlcAny = client;
                        if (client is IPlcClient p) CommHub.Plc = p;   // Modbus 同时挂到 Plc（节点用）
                    }
                    string st = StatusOf(client);
                    Dispatcher.Invoke(() => { PlcStatus.Text = st; BtnPlcConnect.IsEnabled = true; });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => { PlcStatus.Text = "连接异常：" + ex.Message; BtnPlcConnect.IsEnabled = true; });
                }
            });
        }

        private void BtnPlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            PlcStatus.Text = "正在断开…";
            BtnPlcConnect.IsEnabled = false;
            Task.Run(() =>
            {
                lock (CommHub.Sync)
                {
                    (CommHub.PlcAny as IDisposable)?.Dispose();
                    CommHub.PlcAny = null;
                    CommHub.Plc = null;
                }
                Dispatcher.Invoke(() =>
                {
                    BtnPlcConnect.IsEnabled = true;
                    PlcStatus.Text = "PLC 未连接";
                    PlcTestResult.Text = "（未测试）";
                });
            });
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
            // 先在 UI 线程取控件值，后台线程只做网络读写
            string addrText = PlcAddrBox.Text.Trim();
            int addrVal = I(PlcAddrBox.Text);
            bool mcWord = addrText.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                       || addrText.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                       || addrText.StartsWith("W", StringComparison.OrdinalIgnoreCase);
            PlcTestResult.Text = "正在读取…";
            Task.Run(() =>
            {
                string result;
                try
                {
                    switch (client)
                    {
                        case IPlcClient plc:
                        {
                            string err = CommHub.ReadPlc(addrVal, false, 1, out object[] vals);
                            result = err != null
                                ? "读取失败：" + err
                                : string.Format("读取 OK：寄存器[{0}] = {1}", addrVal, vals.Length > 0 ? vals[0] : "?");
                            break;
                        }
                        case S7Client s7:
                        {
                            string err = s7.ReadValue(addrText, out byte[] bytes);
                            result = err != null ? "S7 读取失败：" + err : string.Format("S7 读取 OK：{0} = {1}", addrText, FormatS7(bytes, addrText));
                            break;
                        }
                        case McClient mc:
                        {
                            if (mcWord)
                            {
                                string err = mc.ReadWord(addrText, out ushort v1);
                                result = err != null ? "MC 读取失败：" + err : $"MC 读取 OK：{addrText} = {v1}";
                            }
                            else
                            {
                                string err = mc.ReadBit(addrText, out bool v2);
                                result = err != null ? "MC 读取失败：" + err : $"MC 读取 OK：{addrText} = {(v2 ? "ON" : "OFF")}";
                            }
                            break;
                        }
                        default:
                            result = "当前协议不支持读取测试";
                            break;
                    }
                    string st = StatusOf(client);
                    Dispatcher.Invoke(() => { PlcTestResult.Text = result; PlcStatus.Text = st; });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => PlcTestResult.Text = "读取异常：" + ex.Message);
                }
            });
        }

        private void BtnPlcWrite_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Collect();
            cfg.ApplyToHub();
            var client = CurrentPlc();
            if (client == null) { PlcTestResult.Text = "请先连接 PLC"; return; }
            // 先在 UI 线程取控件值
            string addrText = PlcAddrBox.Text.Trim();
            string valText = PlcValBox.Text.Trim();
            int addrVal = I(PlcAddrBox.Text);
            ushort wordVal = (ushort)Math.Clamp(I(PlcValBox.Text), 0, 65535);
            bool mcWord = addrText.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                       || addrText.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                       || addrText.StartsWith("W", StringComparison.OrdinalIgnoreCase);
            PlcTestResult.Text = "正在写入…";
            Task.Run(() =>
            {
                string result;
                try
                {
                    switch (client)
                    {
                        case IPlcClient plc:
                        {
                            string err = CommHub.WritePlc(addrVal, false, wordVal);
                            result = err != null ? "写入失败：" + err : $"写入 OK：寄存器[{addrVal}] = {wordVal}";
                            break;
                        }
                        case S7Client s7:
                        {
                            byte[] v = S7ValueBytes(addrText, valText);
                            if (v == null) { result = "值无法解析（按 S7 类型给十进制值）"; break; }
                            string err = s7.WriteValue(addrText, v);
                            result = err != null ? "S7 写入失败：" + err : $"S7 写入 OK：{addrText} = {valText}";
                            break;
                        }
                        case McClient mc:
                        {
                            string err = mcWord ? mc.WriteWord(addrText, wordVal)
                                : mc.WriteBit(addrText, valText != "0" && !string.Equals(valText, "OFF", StringComparison.OrdinalIgnoreCase));
                            result = err != null ? "MC 写入失败：" + err : $"MC 写入 OK：{addrText} = {valText}";
                            break;
                        }
                        default:
                            result = "当前协议不支持写入测试";
                            break;
                    }
                    string st = StatusOf(client);
                    Dispatcher.Invoke(() => { PlcTestResult.Text = result; PlcStatus.Text = st; });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => PlcTestResult.Text = "写入异常：" + ex.Message);
                }
            });
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
            int gt = GenTypeBox.SelectedIndex;
            GenStatus.Text = "正在连接…";
            BtnGenConnect.IsEnabled = false;
            Task.Run(() =>
            {
                try
                {
                    lock (CommHub.Sync)
                    {
                        CommHub.TcpClient?.Dispose(); CommHub.TcpClient = null;
                        CommHub.UdpClient?.Dispose(); CommHub.UdpClient = null;
                        CommHub.SerialClient?.Dispose(); CommHub.SerialClient = null;
                    }
                    string statusTxt;
                    switch (gt)
                    {
                        case 0:
                            var tcp = cfg.CreateTcp();
                            string e1 = tcp.Connect();
                            if (e1 != null) { tcp.Dispose(); statusTxt = e1; break; }
                            lock (CommHub.Sync) CommHub.TcpClient = tcp;
                            statusTxt = tcp.Status;
                            break;
                        case 1:
                            var udp = cfg.CreateUdp();
                            string e2 = udp.Connect();
                            if (e2 != null) { udp.Dispose(); statusTxt = e2; break; }
                            lock (CommHub.Sync) CommHub.UdpClient = udp;
                            statusTxt = udp.Status;
                            break;
                        default:
                            var sp = cfg.CreateSerial();
                            string e3 = sp.Open();
                            if (e3 != null) { sp.Dispose(); statusTxt = e3; break; }
                            lock (CommHub.Sync) CommHub.SerialClient = sp;
                            statusTxt = "串口已打开 " + cfg.SerialCom;
                            break;
                    }
                    Dispatcher.Invoke(() => { GenStatus.Text = statusTxt; BtnGenConnect.IsEnabled = true; });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => { GenStatus.Text = "连接异常：" + ex.Message; BtnGenConnect.IsEnabled = true; });
                }
            });
        }

        private void BtnGenDisconnect_Click(object sender, RoutedEventArgs e)
        {
            GenStatus.Text = "正在断开…";
            BtnGenConnect.IsEnabled = false;
            Task.Run(() =>
            {
                lock (CommHub.Sync)
                {
                    CommHub.TcpClient?.Dispose(); CommHub.TcpClient = null;
                    CommHub.UdpClient?.Dispose(); CommHub.UdpClient = null;
                    CommHub.SerialClient?.Dispose(); CommHub.SerialClient = null;
                }
                Dispatcher.Invoke(() =>
                {
                    BtnGenConnect.IsEnabled = true;
                    GenStatus.Text = "通用通信未连接";
                    GenRecvHint.Text = "未接收";
                });
            });
        }

        private void BtnGenSend_Click(object sender, RoutedEventArgs e)
        {
            byte[] payload = ParsePayload(GenSendBox.Text, GenHexBox.IsChecked == true);
            if (payload == null)
            {
                GenStatus.Text = "发送内容无法解析：HEX 模式请输入空格分隔的十六进制，如 01 03 00 00 00 01";
                return;
            }
            int gt = GenTypeBox.SelectedIndex;
            GenStatus.Text = "正在发送…";
            Task.Run(() =>
            {
                string statusTxt = null;
                byte[] recv = null;
                try
                {
                    lock (CommHub.Sync)
                    {
                        switch (gt)
                        {
                            case 0:
                                var tcp = CommHub.TcpClient;
                                if (tcp == null || !tcp.IsConnected) { statusTxt = "TCP 未连接"; break; }
                                string e1 = tcp.SendReceive(payload, out byte[] r1);
                                statusTxt = e1 ?? tcp.Status;
                                recv = e1 == null ? r1 : null;
                                break;
                            case 1:
                                var udp = CommHub.UdpClient;
                                if (udp == null || !udp.IsConnected) { statusTxt = "UDP 未连接"; break; }
                                string e2 = udp.SendReceive(payload, out byte[] r2);
                                statusTxt = e2 ?? udp.Status;
                                recv = e2 == null ? r2 : null;
                                break;
                            default:
                                var sp = CommHub.SerialClient;
                                if (sp == null || !sp.IsOpen) { statusTxt = "串口未打开"; break; }
                                string e3 = sp.Write(payload);
                                if (e3 != null) { statusTxt = e3; break; }
                                // 串口发送后尝试读回显（超时内）
                                byte[] buf = new byte[4096];
                                System.Threading.Thread.Sleep(60);
                                int n = sp.Read(buf, buf.Length);
                                statusTxt = n > 0 ? string.Format("串口发送 OK，收到 {0} 字节", n) : "串口发送 OK（无回显）";
                                if (n > 0)
                                {
                                    byte[] r = new byte[n];
                                    Array.Copy(buf, r, n);
                                    recv = r;
                                }
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    statusTxt = "发送异常：" + ex.Message;
                }
                byte[] recvCopy = recv;
                string statusCopy = statusTxt;
                Dispatcher.Invoke(() =>
                {
                    GenStatus.Text = statusCopy;
                    if (recvCopy != null) ShowRecv(recvCopy);
                });
            });
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
