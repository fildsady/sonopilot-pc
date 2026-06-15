/*
 * MainWindow.xaml.cs — PicoAudioCore Vendor Console
 *
 * ใช้ LibUsbDotNet 3.x ผ่าน WinUSB driver (ติดผ่าน Zadig)
 *
 * ต่างจาก HID branch (HidSharp):
 *   HID  : HidSharp.HidStream.ReadAsync()  — async, callback-driven
 *          ขนาด report ตายตัว 64+1 bytes (report ID prefix)
 *   Vendor: UsbEndpointReader.Read()       — sync bulk read
 *          ขนาดยืดหยุ่น ส่งได้มากกว่า 64 bytes ต่อครั้ง
 *          ไม่มี report ID prefix — raw bytes ตรงๆ
 *
 * Protocol เหมือน HID ทุกอย่าง (byte[0]=CMD) เพื่อเปรียบเทียบได้ตรงๆ
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace PicoVendorConsole;

public partial class MainWindow : Window
{
    const int VID = 0x2E8A;
    const int PID = 0xC0DE;
    const int BUF_LEN = 64;
    const int READ_TIMEOUT_MS = 500;

    const byte CMD_PING    = 0x01;
    const byte CMD_ECHO    = 0x02;
    const byte CMD_LED_ON  = 0x03;
    const byte CMD_LED_OFF = 0x04;
    const byte CMD_INFO    = 0x05;

    UsbDevice?         _device;
    UsbEndpointWriter? _writer;  /* bulk OUT — PC → Pico */
    UsbEndpointReader? _reader;  /* bulk IN  — Pico → PC */
    CancellationTokenSource? _cts;
    bool _connected;
    bool _autoReconnect;

    public MainWindow() { InitializeComponent(); Log("PicoAudioCore Vendor Console ready."); }

    void Log(string msg) => Dispatcher.Invoke(() => {
        TbLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        LogScroll.ScrollToBottom();
    });
    void LogSend(string msg) => Log($"→ {msg}");
    void LogRecv(string msg) => Log($"← {msg}");

    // ── Connect / Disconnect ──────────────────────────────────────────
    private void BtnConnect_Click(object sender, RoutedEventArgs e) {
        if (_connected) Disconnect();
        else Connect();
    }

    void Connect() {
        _autoReconnect = ChkAutoReconnect.IsChecked == true;
        if (!TryConnect())
            Log("Device not found — plug in Pico and install WinUSB via Zadig first.");
    }

    bool TryConnect() {
        /* LibUsbDotNet ค้นหา device ด้วย VID/PID
           ต้องติด WinUSB driver ก่อน ไม่งั้น Find() จะ return null */
        var finder = new UsbDeviceFinder(VID, PID);
        _device = UsbDevice.OpenUsbDevice(finder);
        if (_device == null) return false;

        /* Vendor class ใช้ endpoint หมายเลข 1 (ตรงกับ descriptor ใน firmware)
           WriteEndpoint(1) = EP1 OUT (0x01) — PC ส่งออก
           ReadEndpoint(1)  = EP1 IN  (0x81) — PC รับเข้า          */
        _writer = _device.OpenEndpointWriter(WriteEndpointID.Ep01);
        _reader = _device.OpenEndpointReader(ReadEndpointID.Ep01, BUF_LEN);

        _connected = true;
        _cts = new CancellationTokenSource();
        SetUiConnected(true);
        Log($"Connected (WinUSB bulk) — {VID:X4}:{PID:X4}");
        Task.Run(() => ReadLoop(_cts.Token));
        return true;
    }

    void Disconnect(bool stopAutoReconnect = true) {
        if (stopAutoReconnect) _autoReconnect = false;
        _cts?.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _device?.Close();
        _reader = null; _writer = null; _device = null;
        _connected = false;
        SetUiConnected(false);
        if (stopAutoReconnect) Log("Disconnected.");
    }

    async Task ReconnectLoop() {
        Log("Device lost — retrying every 2s…");
        while (_autoReconnect) {
            await Task.Delay(2000);
            if (!_autoReconnect) break;
            if (TryConnect()) { Log("Reconnected."); return; }
        }
    }

    void SetUiConnected(bool on) => Dispatcher.Invoke(() => {
        StatusDot.Fill     = on ? Brushes.LightGreen
                                : new SolidColorBrush(Color.FromRgb(0xf3,0x8b,0xa8));
        TbStatus.Text      = on ? "Connected" : "Disconnected";
        BtnConnect.Content = on ? "Disconnect" : "Connect";
        BtnPing.IsEnabled = BtnInfo.IsEnabled = on;
        BtnLedOn.IsEnabled = BtnLedOff.IsEnabled = on;
        BtnSend.IsEnabled = TbInput.IsEnabled = on;
    });

    // ── Read loop ────────────────────────────────────────────────────
    /*
     * Vendor bulk read ต่างจาก HID:
     *   HID  : ReadAsync ได้เลย รอ interrupt endpoint
     *   Vendor: Read() แบบ sync ใส่ timeout — poll ใน background thread
     *
     * ใส่ timeout (READ_TIMEOUT_MS) แทน block ตลอด
     * เพื่อให้ตรวจ CancellationToken ได้สม่ำเสมอ
     */
    async Task ReadLoop(CancellationToken ct) {
        var buf = new byte[BUF_LEN];
        try {
            while (!ct.IsCancellationRequested) {
                int transferred;
                var err = _reader!.Read(buf, READ_TIMEOUT_MS, out transferred);

                if (ct.IsCancellationRequested) break;

                /* timeout = ปกติ ไม่มีข้อมูลแค่นั้น วนต่อ */
                if (err == ErrorCode.IoTimedOut) continue;

                if (err != ErrorCode.Ok || transferred < 1) {
                    throw new Exception($"Read error: {err}");
                }

                byte cmd = buf[0];
                string payload = System.Text.Encoding.UTF8
                                       .GetString(buf, 1, Math.Max(0, transferred - 1))
                                       .TrimEnd('\0');
                string label = cmd switch {
                    CMD_PING    => "PING",
                    CMD_ECHO    => "ECHO",
                    CMD_LED_ON  => "LED_ON",
                    CMD_LED_OFF => "LED_OFF",
                    CMD_INFO    => "INFO",
                    0xFF        => "ERROR",
                    _           => $"0x{cmd:X2}"
                };
                LogRecv($"[{label}] {payload}");
            }
        } catch (Exception ex) when (_autoReconnect) {
            Log($"Connection lost ({ex.Message})");
            Dispatcher.Invoke(() => Disconnect(stopAutoReconnect: false));
            await ReconnectLoop();
        } catch (Exception ex) {
            Log($"Read error: {ex.Message}");
            Dispatcher.Invoke(() => Disconnect());
        }
    }

    // ── Send ─────────────────────────────────────────────────────────
    /*
     * Vendor bulk write — ส่ง raw bytes ตรงๆ ไม่มี report ID prefix
     * ต่างจาก HID ที่ต้องใส่ byte 0x00 (report ID) นำหน้าทุกครั้ง
     */
    void Send(byte cmd, string? text = null) {
        if (_writer == null) return;
        var buf = new byte[BUF_LEN];
        buf[0] = cmd;
        if (text != null) {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int len = Math.Min(bytes.Length, BUF_LEN - 1);
            Array.Copy(bytes, 0, buf, 1, len);
        }
        int transferred;
        var err = _writer.Write(buf, 2000, out transferred);
        if (err != ErrorCode.Ok)
            Log($"Send error: {err}");
    }

    // ── Button handlers ───────────────────────────────────────────────
    private void BtnPing_Click   (object s, RoutedEventArgs e) { LogSend("PING");    Send(CMD_PING); }
    private void BtnInfo_Click   (object s, RoutedEventArgs e) { LogSend("INFO");    Send(CMD_INFO); }
    private void BtnLedOn_Click  (object s, RoutedEventArgs e) { LogSend("LED ON");  Send(CMD_LED_ON); }
    private void BtnLedOff_Click (object s, RoutedEventArgs e) { LogSend("LED OFF"); Send(CMD_LED_OFF); }

    private void BtnSend_Click(object sender, RoutedEventArgs e) => SendEcho();
    private void TbInput_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) SendEcho();
    }

    void SendEcho() {
        string txt = TbInput.Text.Trim();
        if (txt.Length == 0) return;
        LogSend($"ECHO \"{txt}\"");
        Send(CMD_ECHO, txt);
        TbInput.Clear();
    }

    protected override void OnClosed(EventArgs e) {
        _autoReconnect = false;
        _cts?.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _device?.Close();
        UsbDevice.Exit();
        base.OnClosed(e);
    }
}
