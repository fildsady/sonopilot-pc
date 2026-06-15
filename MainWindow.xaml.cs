using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HidSharp;

namespace PicoHidConsole;

public partial class MainWindow : Window
{
    const int VID = 0x2E8A;
    const int PID = 0xC0DE;
    const int REPORT_LEN = 64;

    // Command IDs — must match firmware
    const byte CMD_PING    = 0x01;
    const byte CMD_ECHO    = 0x02;
    const byte CMD_LED_ON  = 0x03;
    const byte CMD_LED_OFF = 0x04;
    const byte CMD_INFO    = 0x05;

    HidDevice?  _device;
    HidStream?  _stream;
    CancellationTokenSource? _cts;
    bool _connected;
    bool _autoReconnect;

    public MainWindow() { InitializeComponent(); Log("PicoAudioCore HID Console ready."); }

    // ── Logging ──────────────────────────────────────────────────────────────
    void Log(string msg) {
        Dispatcher.Invoke(() => {
            TbLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            LogScroll.ScrollToBottom();
        });
    }

    void LogSend(string msg) => Log($"→ {msg}");
    void LogRecv(string msg) => Log($"← {msg}");

    // ── Connect / Disconnect ──────────────────────────────────────────────────
    private void BtnConnect_Click(object sender, RoutedEventArgs e) {
        if (_connected) Disconnect();
        else Connect();
    }

    void Connect() {
        _autoReconnect = ChkAutoReconnect.IsChecked == true;
        if (!TryConnect())
            Log("Device not found — plug in Pico and try again.");
    }

    bool TryConnect() {
        var list = DeviceList.Local;
        _device  = list.GetHidDeviceOrNull(VID, PID);
        if (_device == null) return false;

        try {
            _stream = _device.Open();
            _stream.ReadTimeout  = Timeout.Infinite;
            _stream.WriteTimeout = 2000;
        } catch {
            _stream = null;
            return false;
        }

        _connected = true;
        _cts = new CancellationTokenSource();
        SetUiConnected(true);
        Log($"Connected → {_device.GetProductName()}");
        Task.Run(() => ReadLoop(_cts.Token));
        return true;
    }

    void Disconnect(bool stopAutoReconnect = true) {
        if (stopAutoReconnect) _autoReconnect = false;
        _cts?.Cancel();
        _stream?.Close();
        _stream = null;
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
        StatusDot.Fill  = on ? Brushes.LightGreen
                             : new SolidColorBrush(Color.FromRgb(0xf3,0x8b,0xa8));
        TbStatus.Text   = on ? "Connected" : "Disconnected";
        BtnConnect.Content = on ? "Disconnect" : "Connect";
        BtnPing.IsEnabled = BtnInfo.IsEnabled = on;
        BtnLedOn.IsEnabled = BtnLedOff.IsEnabled = on;
        BtnSend.IsEnabled = TbInput.IsEnabled = on;
    });

    // ── Read loop (background thread) ────────────────────────────────────────
    async Task ReadLoop(CancellationToken ct) {
        var buf = new byte[REPORT_LEN + 1]; // HidSharp prepends report ID byte
        try {
            while (!ct.IsCancellationRequested) {
                int n = await _stream!.ReadAsync(buf, 0, buf.Length, ct);
                if (n < 2) continue;
                // buf[0] = report ID (0), buf[1] = cmd, buf[2..] = payload
                byte cmd  = buf[1];
                string payload = System.Text.Encoding.UTF8
                                       .GetString(buf, 2, Math.Max(0, n - 2))
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
        } catch (OperationCanceledException) {
        } catch (Exception ex) when (_autoReconnect) {
            Log($"Connection lost ({ex.Message})");
            Dispatcher.Invoke(() => Disconnect(stopAutoReconnect: false));
            await ReconnectLoop();
        } catch (Exception ex) {
            Log($"Read error: {ex.Message}");
            Dispatcher.Invoke(() => Disconnect());
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────
    void Send(byte cmd, string? text = null) {
        if (_stream == null) return;
        var report = new byte[REPORT_LEN + 1]; // +1 for report ID
        report[0] = 0;   // report ID
        report[1] = cmd;
        if (text != null) {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int len = Math.Min(bytes.Length, REPORT_LEN - 2);
            Array.Copy(bytes, 0, report, 2, len);
        }
        try { _stream.Write(report); }
        catch (Exception ex) { Log($"Send error: {ex.Message}"); }
    }

    // ── Button handlers ───────────────────────────────────────────────────────
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

    protected override void OnClosed(EventArgs e) { _cts?.Cancel(); _stream?.Close(); base.OnClosed(e); }
}
