using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace RP2350Player;

public partial class MainWindow : Window
{
    private readonly SerialService   _serial      = new();
    private readonly DispatcherTimer _clockTimer  = new();
    private readonly DispatcherTimer _beatTimer   = new();
    private bool   _connected   = false;
    private bool   _paused      = false;
    private string _repeatMode  = "";
    private bool   _autoPlay    = true;
    private bool   _volumeSync  = false;
    private bool   _eqSync      = false;
    private bool   _mono        = false;

    private const int EqBands = 32;
    private static readonly float[] EqFreqs = {
        16, 20, 25, 31.5f, 40, 50, 63, 80,
        100, 125, 160, 200, 250, 315, 400, 500,
        630, 800, 1000, 1250, 1600, 2000, 2500, 3150,
        4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
    };
    private readonly Slider[]    _eqSliders  = new Slider[EqBands];
    private readonly TextBlock[] _eqDbLabels = new TextBlock[EqBands];

    private const int MixLayers = 9;
    private const int MixMainLayer = 8;
    private static readonly string[] LayerLabel =
    {
        "FLAC 0","FLAC 1","FLAC 2",
        "WAV 0","WAV 1","WAV 2","WAV 3","WAV 4",
        "MAIN"
    };
    private readonly TextBox[]   _layerPath   = new TextBox[MixLayers];
    private readonly Slider[]    _layerVol    = new Slider[MixLayers];
    private readonly TextBlock[] _layerVolLbl = new TextBlock[MixLayers];
    private readonly Ellipse[]   _layerDot    = new Ellipse[MixLayers];
    private readonly Button[]    _layerPlay   = new Button[MixLayers];
    private readonly Button[]    _layerStop   = new Button[MixLayers];

    private const int HotkeyCount = 20;
    private readonly TextBox[] _hkBoxes = new TextBox[HotkeyCount];
    private static readonly string HotkeyFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkeys.json");
    private static readonly string LastPortFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "lastport.txt");
    private static readonly string WindowStateFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "windowstate.json");

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowState();
        BuildHotkeySlots();
        LoadHotkeys();
        BuildEqSliders();
        BuildMixerPanel();
        RefreshPorts();

        _serial.LineReceived += OnLineReceived;
        _serial.Disconnected += () => Dispatcher.Invoke(() =>
        {
            Log("[ERR] Board disconnected");
            SetConnected(false);
        });

        /* PC clock display — every second */
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) =>
            TbPcTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clockTimer.Start();

        /* Heartbeat — ask board for status every second */
        _beatTimer.Interval = TimeSpan.FromSeconds(1);
        int _beatCount = 0;
        _beatTimer.Tick += (_, _) =>
        {
            if (!_connected) return;
            _serial.Send("status");
            if (++_beatCount % 3 == 0) _serial.Send("layer list");
        };
    }

    // ── Connection ────────────────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var ports = SerialService.GetPortNames();
        CbPort.Items.Clear();
        foreach (var p in ports) CbPort.Items.Add(p);
        if (CbPort.Items.Count == 0) return;

        string last = File.Exists(LastPortFile) ? File.ReadAllText(LastPortFile).Trim() : "";
        int idx = CbPort.Items.IndexOf(last);
        CbPort.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            _beatTimer.Stop();
            _serial.Close();
            SetConnected(false);
            return;
        }
        if (CbPort.SelectedItem is not string port) return;
        if (_serial.Open(port))
        {
            File.WriteAllText(LastPortFile, port);
            SetConnected(true);
            _beatTimer.Start();
            _serial.Send("version");
        }
        else
            Log($"[ERR] Cannot open {port}");
    }

    private void SetConnected(bool on)
    {
        _connected = on;
        if (!on) { _beatTimer.Stop(); TbFirmware.Text = ""; _volumeSync = false; _eqSync = false; _mono = false; UpdateMonoToggleUI(false); }
        BtnConnect.Content    = on ? "Disconnect" : "Connect";
        StatusDot.Fill        = on
            ? Brushes.LightGreen
            : new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
        BtnPrev.IsEnabled     = on;
        BtnPlay.IsEnabled     = on;
        BtnPause.IsEnabled    = on;
        BtnStop.IsEnabled     = on;
        BtnNext.IsEnabled     = on;
        BtnGoto.IsEnabled      = on;
        BtnSyncTime.IsEnabled  = on;
        SlVolume.IsEnabled     = on;
        BtnRepeatOne.IsEnabled = on;
        BtnRepeatAll.IsEnabled = on;
        BtnRepeatOff.IsEnabled = on;
        BtnAutoPlay.IsEnabled  = on;
        BtnEqReset.IsEnabled = on;
        foreach (var s in _eqSliders) if (s != null) s.IsEnabled = on;
        MonoToggleBorder.IsEnabled = on;
        for (int i = 0; i < MixLayers; i++)
        {
            if (_layerPlay[i] != null) _layerPlay[i].IsEnabled = on && i < MixMainLayer;
            if (_layerStop[i] != null) _layerStop[i].IsEnabled = on && i < MixMainLayer;
            if (_layerVol[i]  != null) _layerVol[i].IsEnabled  = on;
            if (!on && _layerDot[i] != null)
                _layerDot[i].Fill = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a));
        }
        if (!on)
        {
            TbNowPlaying.Text  = "— Not connected —";
            TbElapsed.Text     = "--:--";
            TbTrackInfo.Text   = "";
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────

    private void BtnPrev_Click (object sender, RoutedEventArgs e) => _serial.Send("prev");
    private void BtnNext_Click (object sender, RoutedEventArgs e) => _serial.Send("next");
    private void BtnStop_Click (object sender, RoutedEventArgs e) => _serial.Send("stop");

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        _paused = false;
        BtnPause.Content = "⏸  Pause";
        _serial.Send("play");
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        BtnPause.Content = _paused ? "▶  Resume" : "⏸  Pause";
        _serial.Send(_paused ? "pause" : "play");
    }

    private void BtnGoto_Click(object sender, RoutedEventArgs e) => SendGoto();
    private void TbGoto_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendGoto();
    }

    private void SendGoto()
    {
        var name = TbGoto.Text.Trim();
        if (string.IsNullOrEmpty(name) || !_connected) return;
        _serial.Send($"goto {name}");
    }

    // ── Playback mode ─────────────────────────────────────────────────────

    private void BtnRepeatOne_Click(object sender, RoutedEventArgs e) => _serial.Send("mode repeat_one");
    private void BtnRepeatAll_Click(object sender, RoutedEventArgs e) => _serial.Send("mode repeat_all");
    private void BtnRepeatOff_Click(object sender, RoutedEventArgs e) => _serial.Send("mode repeat_off");
    private void BtnAutoPlay_Click (object sender, RoutedEventArgs e) =>
        _serial.Send(_autoPlay ? "mode autoplay_off" : "mode autoplay_on");

    private static readonly SolidColorBrush BrushActive  = new(Color.FromRgb(0x89, 0xb4, 0xfa)); // blue accent
    private static readonly SolidColorBrush BrushInactive= new(Color.FromRgb(0x31, 0x32, 0x44)); // dark

    private void UpdateModeButtons()
    {
        BtnRepeatOne.Background = _repeatMode == "one" ? BrushActive : BrushInactive;
        BtnRepeatAll.Background = _repeatMode == "all" ? BrushActive : BrushInactive;
        BtnRepeatOff.Background = _repeatMode == "off" ? BrushActive : BrushInactive;
        BtnRepeatOne.Foreground = _repeatMode == "one" ? new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)) : Brushes.White;
        BtnRepeatAll.Foreground = _repeatMode == "all" ? new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)) : Brushes.White;
        BtnRepeatOff.Foreground = _repeatMode == "off" ? new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e)) : Brushes.White;

        BtnAutoPlay.Background = _autoPlay ? BrushActive : BrushInactive;
        BtnAutoPlay.Foreground = _autoPlay
            ? new SolidColorBrush(Color.FromRgb(0x1e,0x1e,0x2e))
            : Brushes.White;
        BtnAutoPlay.Content = _autoPlay ? "▶  Auto On" : "⏸  Auto Off";
    }

    // ── Time sync ─────────────────────────────────────────────────────────

    private void BtnSyncTime_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _serial.Send($"date {now:yyyy-MM-dd HH:mm:ss}");
        Log($"[PC] Synced → {now:yyyy-MM-dd HH:mm:ss}");
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────

    private void BuildHotkeySlots()
    {
        for (int i = 0; i < HotkeyCount; i++)
        {
            int idx = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var label = new TextBlock
            {
                Text = $"{i + 1}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var tb = new TextBox
            {
                Background  = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)),
                Foreground  = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
                BorderThickness = new Thickness(1),
                Padding  = new Thickness(4, 3, 4, 3),
                FontSize = 11,
                ToolTip  = $"Hotkey {i + 1}: filename without extension"
            };
            tb.TextChanged += (_, _) => SaveHotkeys();
            Grid.SetColumn(tb, 1);
            _hkBoxes[i] = tb;

            var btn = new Button
            {
                Content         = "▶",
                FontSize        = 11,
                Background      = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                Foreground      = Brushes.White,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(4, 0, 0, 0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                ToolTip         = $"Play hotkey {i + 1}"
            };
            btn.Click += (_, _) =>
            {
                var name = _hkBoxes[idx].Text.Trim();
                if (!string.IsNullOrEmpty(name) && _connected)
                    _serial.Send($"goto {name}");
            };
            Grid.SetColumn(btn, 2);

            row.Children.Add(label);
            row.Children.Add(tb);
            row.Children.Add(btn);
            HotkeyPanel.Children.Add(row);
        }
    }

    private void SaveHotkeys()
    {
        var names = new string[HotkeyCount];
        for (int i = 0; i < HotkeyCount; i++)
            names[i] = _hkBoxes[i].Text;
        File.WriteAllText(HotkeyFile, JsonSerializer.Serialize(names));
    }

    private void LoadHotkeys()
    {
        if (!File.Exists(HotkeyFile)) return;
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(HotkeyFile));
            if (names == null) return;
            for (int i = 0; i < HotkeyCount && i < names.Length; i++)
                _hkBoxes[i].Text = names[i] ?? "";
        }
        catch { }
    }

    // ── Serial receive ────────────────────────────────────────────────────

    private void OnLineReceived(string line)
    {
        Dispatcher.Invoke(() =>
        {
            /* STATUS lines are high-frequency — don't spam the log */
            if (line.StartsWith("STATUS "))
                ParseStatus(line);
            else if (line.StartsWith("VERSION "))
                ParseVersion(line);
            else if (line.StartsWith("LAYER "))
                ParseLayerStatus(line);
            else
            {
                Log(line);
                ParsePlayingLine(line);
            }
        });
    }

    private void SlVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TbVolume == null) return;
        int v = (int)SlVolume.Value;
        TbVolume.Text = $"{v}%";
        if (_connected && _volumeSync)
            _serial.Send($"volume {v}");
    }

    private static string EqDb(double v)
    {
        double db = (v - 100) * 0.12;
        return db >= 0 ? $"+{db:F1}" : $"{db:F1}";
    }

    private static string FreqLabel(float f) =>
        f >= 1000 ? $"{f / 1000:0.##}k" : $"{(int)f}";

    private void BuildEqSliders()
    {
        for (int i = 0; i < EqBands; i++)
        {
            int idx = i;
            var sp = new StackPanel { Width = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0) };

            var dbLbl = new TextBlock {
                Text = " 0.0", FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _eqDbLabels[idx] = dbLbl;

            var sl = new Slider {
                Orientation = Orientation.Vertical, Height = 80,
                Minimum = 0, Maximum = 200, Value = 100,
                IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Center,
                SmallChange = 1, LargeChange = 10
            };
            sl.ValueChanged += (_, _) => {
                if (_eqDbLabels[idx] == null) return;
                _eqDbLabels[idx].Text = EqDb(sl.Value);
                if (_connected && _eqSync)
                    _serial.Send($"eq band {idx} {(int)sl.Value}");
            };
            _eqSliders[idx] = sl;

            var freqLbl = new TextBlock {
                Text = FreqLabel(EqFreqs[idx]),
                Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            sp.Children.Add(dbLbl);
            sp.Children.Add(sl);
            sp.Children.Add(freqLbl);
            EqPanel.Children.Add(sp);
        }
    }

    private void BtnEqReset_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "Reset all 32 EQ bands to flat (0 dB)?",
            "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        _eqSync = false;
        foreach (var s in _eqSliders) if (s != null) s.Value = 100;
        _eqSync = true;
        _serial.Send("eq reset");
    }

    private void ParseLayerStatus(string line)
    {
        /* LAYER id=X active=yes|no vol=Y (main) */
        string GetField(string key)
        {
            int i = line.IndexOf(key + "=");
            if (i < 0) return "";
            int start = i + key.Length + 1;
            int end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }
        if (!int.TryParse(GetField("id"), out int id)) return;
        if (id < 0 || id >= MixLayers) return;
        bool active = GetField("active") == "yes";
        UpdateLayerDot(id, active);
        if (int.TryParse(GetField("vol"), out int vol) && _layerVol[id] != null)
            _layerVol[id].Value = vol;
    }

    private void ParseVersion(string line)
    {
        string Get(string key)
        {
            int i = line.IndexOf(key + "=");
            if (i < 0) return "";
            int start = i + key.Length + 1;
            int end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }
        var fw    = Get("firmware");
        var board = Get("board");
        var build = line.Contains("build=") ? line[(line.IndexOf("build=") + 6)..].TrimEnd() : "";
        TbFirmware.Text = $"{board}  fw {fw}  ({build})";
        Log($"[FW] {board} firmware {fw} built {build}");
    }

    /* Parse: STATUS elapsed=12345 track=2 total=8 state=playing file=song.flac */
    private void ParseStatus(string line)
    {
        string Get(string key)
        {
            int i = line.IndexOf(key + "=");
            if (i < 0) return "";
            int start = i + key.Length + 1;
            if (start < line.Length && line[start] == '"')
            {
                start++;
                int end = line.IndexOf('"', start);
                return end < 0 ? line[start..] : line[start..end];
            }
            else
            {
                int end = line.IndexOf(' ', start);
                return end < 0 ? line[start..] : line[start..end];
            }
        }

        var elapsedMs = long.TryParse(Get("elapsed"), out var ms) ? ms : -1;
        var track     = Get("track");
        var total     = Get("total");
        var state     = Get("state");
        var file      = Get("file");
        var repeat    = Get("repeat");
        var autoplay  = Get("autoplay");

        if (elapsedMs >= 0)
        {
            long secs = elapsedMs / 1000;
            TbElapsed.Text = $"{secs / 60:D2}:{secs % 60:D2}";
        }

        if (!string.IsNullOrEmpty(track) && !string.IsNullOrEmpty(total))
            TbTrackInfo.Text = $"Track {track} / {total}  [{state}]";

        /* update now-playing name only when it changes */
        if (!string.IsNullOrEmpty(file))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            if (TbNowPlaying.Text != $"♪  {stem}")
                TbNowPlaying.Text = $"♪  {stem}";
        }

        /* file info panel */
        var samplehz = Get("samplehz");
        var ext = !string.IsNullOrEmpty(file)
            ? System.IO.Path.GetExtension(file).TrimStart('.').ToUpper() : "";
        if (!string.IsNullOrEmpty(ext))
            TbFileCodec.Text = ext;
        if (!string.IsNullOrEmpty(samplehz) && long.TryParse(samplehz, out var hz))
            TbFileSampleRate.Text = hz >= 1000 ? $"{hz / 1000.0:0.###} kHz" : $"{hz} Hz";

        /* sync mono toggle from board (every status update) */
        var monoStr = Get("mono");
        if (!string.IsNullOrEmpty(monoStr))
        {
            bool isMono = monoStr == "on";
            if (isMono != _mono) { _mono = isMono; UpdateMonoToggleUI(isMono); }
        }

        /* sync volume slider from board (only once after connect) */
        var volStr = Get("vol");
        if (!_volumeSync && !string.IsNullOrEmpty(volStr) && int.TryParse(volStr, out var v))
        {
            _volumeSync = true;
            SlVolume.Value = v;
        }

        /* sync EQ sliders from board (only once after connect) */
        if (!_eqSync)
        {
            var eqStr = Get("eq");
            if (!string.IsNullOrEmpty(eqStr))
            {
                var parts = eqStr.Split(',');
                if (parts.Length == EqBands)
                {
                    _eqSync = true;
                    for (int i = 0; i < EqBands; i++)
                        if (int.TryParse(parts[i], out var ev) && _eqSliders[i] != null)
                            _eqSliders[i].Value = ev;
                }
            }
        }

        /* reflect pause state on button */
        bool boardPaused = state == "paused";
        if (boardPaused != _paused)
        {
            _paused = boardPaused;
            BtnPause.Content = _paused ? "▶  Resume" : "⏸  Pause";
        }

        /* reflect mode buttons */
        bool modeChanged = false;
        if (!string.IsNullOrEmpty(repeat) && repeat != _repeatMode)
        { _repeatMode = repeat; modeChanged = true; }
        bool newAuto = autoplay == "on";
        if (!string.IsNullOrEmpty(autoplay) && newAuto != _autoPlay)
        { _autoPlay = newAuto; modeChanged = true; }
        if (modeChanged) UpdateModeButtons();
    }

    /* Parse: "[MP3] Playing: 0:/song.mp3  44100 Hz  2 ch" */
    private void ParsePlayingLine(string line)
    {
        if (!line.Contains("] Playing:")) return;
        int idx   = line.IndexOf("Playing:") + 8;
        var token = line[idx..].Trim().Split(' ')[0];
        var stem  = System.IO.Path.GetFileNameWithoutExtension(token);
        TbNowPlaying.Text = $"♪  {stem}";
    }

    private void Log(string text)
    {
        TbLog.Text += text + "\n";
        LogScroll.ScrollToEnd();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TbLog.Text = "";

    // ── Mixer / MultiLayer ────────────────────────────────────────────────
    private void BuildMixerPanel()
    {
        for (int i = 0; i < MixLayers; i++)
        {
            int idx = i;
            bool isMain = (i == MixMainLayer);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            /* status dot */
            var dot = new Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a))
            };
            Grid.SetColumn(dot, 0);
            _layerDot[i] = dot;

            /* label */
            var lbl = new TextBlock
            {
                Text = LayerLabel[i], Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isMain ? FontWeights.Bold : FontWeights.Normal
            };
            Grid.SetColumn(lbl, 1);

            /* path box */
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
                BorderThickness = new Thickness(1), FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                IsReadOnly = isMain, Opacity = isMain ? 0.5 : 1.0,
                ToolTip = isMain ? "Main player — controlled by playlist" : "Filename on SD root (e.g. bass.flac)"
            };
            Grid.SetColumn(tb, 2);
            _layerPath[i] = tb;

            /* Play button */
            var btnPlay = new Button
            {
                Content = "Play", Style = (Style)FindResource("DarkBtn"),
                FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
                IsEnabled = false, Visibility = isMain ? Visibility.Collapsed : Visibility.Visible
            };
            btnPlay.Click += (_, _) =>
            {
                var fname = _layerPath[idx].Text.Trim();
                if (!string.IsNullOrEmpty(fname))
                    _serial.Send($"layer {idx} play {fname}");
            };
            Grid.SetColumn(btnPlay, 3);
            _layerPlay[i] = btnPlay;

            /* Stop button */
            var btnStop = new Button
            {
                Content = "Stop", Style = (Style)FindResource("DarkBtn"),
                FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
                IsEnabled = false, Visibility = isMain ? Visibility.Collapsed : Visibility.Visible
            };
            btnStop.Click += (_, _) => _serial.Send($"layer {idx} stop");
            Grid.SetColumn(btnStop, 4);
            _layerStop[i] = btnStop;

            /* Vol label */
            var volLbl = new TextBlock
            {
                Text = "100", Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
                FontSize = 10, Width = 28, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(volLbl, 5);
            _layerVolLbl[i] = volLbl;

            /* Vol slider */
            var sl = new Slider
            {
                Minimum = 0, Maximum = 200, Value = 100,
                IsEnabled = false, VerticalAlignment = VerticalAlignment.Center
            };
            sl.ValueChanged += (_, e) =>
            {
                if (_layerVolLbl[idx] != null) _layerVolLbl[idx].Text = ((int)e.NewValue).ToString();
                if (_connected) _serial.Send($"layer {idx} vol {(int)e.NewValue}");
            };
            Grid.SetColumn(sl, 6);
            _layerVol[i] = sl;

            /* Vol value label */
            var volUnit = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86)),
                FontSize = 9, Text = "/200", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            Grid.SetColumn(volUnit, 7);

            row.Children.Add(dot);
            row.Children.Add(lbl);
            row.Children.Add(tb);
            row.Children.Add(btnPlay);
            row.Children.Add(btnStop);
            row.Children.Add(volLbl);
            row.Children.Add(sl);
            row.Children.Add(volUnit);

            MixerPanel.Children.Add(row);
        }
    }

    private void UpdateLayerDot(int id, bool active)
    {
        if (id < 0 || id >= MixLayers || _layerDot[id] == null) return;
        _layerDot[id].Fill = active
            ? new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1))
            : new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a));
    }

    private void UpdateMonoToggleUI(bool mono)
    {
        MonoToggleBorder.Background = new SolidColorBrush(
            mono ? Color.FromRgb(0x89, 0xb4, 0xfa) : Color.FromRgb(0x45, 0x47, 0x5a));
        TbMonoLabel.Text      = mono ? "MONO" : "STEREO";
        TbMonoLabel.Foreground = new SolidColorBrush(
            mono ? Color.FromRgb(0x1e, 0x1e, 0x2e) : Color.FromRgb(0xa6, 0xad, 0xc8));
        MonoThumb.HorizontalAlignment = mono ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        MonoThumb.Margin = mono ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
    }

    private void MonoToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_connected) return;
        _mono = !_mono;
        UpdateMonoToggleUI(_mono);
        _serial.Send(_mono ? "mono on" : "mono off");
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowStateFile)) return;
            var json = File.ReadAllText(WindowStateFile);
            var doc  = JsonDocument.Parse(json).RootElement;
            Width        = doc.GetProperty("w").GetDouble();
            Height       = doc.GetProperty("h").GetDouble();
            Left         = doc.GetProperty("x").GetDouble();
            Top          = doc.GetProperty("y").GetDouble();
            WindowState  = doc.GetProperty("maximized").GetBoolean()
                           ? WindowState.Maximized : WindowState.Normal;
        }
        catch { }
    }

    private void SaveWindowState()
    {
        try
        {
            var s = WindowState;
            var w = s == WindowState.Maximized ? RestoreBounds.Width  : Width;
            var h = s == WindowState.Maximized ? RestoreBounds.Height : Height;
            var x = s == WindowState.Maximized ? RestoreBounds.Left   : Left;
            var y = s == WindowState.Maximized ? RestoreBounds.Top    : Top;
            var json = $"{{\"w\":{w},\"h\":{h},\"x\":{x},\"y\":{y},\"maximized\":{(s == WindowState.Maximized ? "true" : "false")}}}";
            File.WriteAllText(WindowStateFile, json);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowState();
        _clockTimer.Stop();
        _beatTimer.Stop();
        _serial.Dispose();
        base.OnClosed(e);
    }
}
