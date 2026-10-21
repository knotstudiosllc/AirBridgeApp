using System.Drawing.Drawing2D;

namespace AirBridge;

sealed class MainForm : Form
{
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
    readonly Label _detail = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(340, 0) };
    readonly Label _adapter = Value(), _network = Value(), _devices = Value(), _latency = Value();
    readonly TextBox _ssid = new() { Width = 220, MaxLength = 32 };
    readonly TextBox _pass = new() { Width = 220, MaxLength = 63, UseSystemPasswordChar = true };
    readonly CheckBox _showPass = new() { Text = "Show", AutoSize = true };
    readonly Button _toggle = new() { Width = 340, Height = 36, Font = new Font("Segoe UI Semibold", 10f) };
    readonly CheckBox _auto = new() { Text = "Start the network automatically when the Air Bridge is plugged in", AutoSize = true, MaximumSize = new Size(340, 0) };
    readonly CheckBox _login = new() { Text = "Launch at sign-in and keep running in the tray", AutoSize = true };
    readonly LinkLabel _logLink = new() { Text = "Show log", AutoSize = true };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 340, Height = 150, Visible = false, Font = new Font("Consolas", 8.5f) };
    readonly ToolStripMenuItem _trayStart = new("Start network"), _trayStop = new("Stop network");
    bool _exiting;
    int _logCount;

    static Label Value() => new() { AutoSize = true, MaximumSize = new Size(250, 0) };

    public MainForm(Engine engine, bool startHidden)
    {
        _engine = engine;
        Text = "Air Bridge";
        Icon = AppIcon.Create(Color.FromArgb(37, 99, 235));
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        root.Controls.Add(_state);
        root.Controls.Add(_detail);
        root.Controls.Add(Spacer(8));

        var info = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        foreach (var (k, v) in new[] { ("Adapter", _adapter), ("Network", _network), ("Devices", _devices), ("Latency", _latency) })
        {
            info.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 0, 3) });
            v.Margin = new Padding(0, 3, 0, 3);
            info.Controls.Add(v);
        }
        root.Controls.Add(info);
        root.Controls.Add(Spacer(10));

        var form = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        form.Controls.Add(new Label { Text = "Network name", AutoSize = true, Anchor = AnchorStyles.Left });
        form.Controls.Add(_ssid);
        form.Controls.Add(new Label());
        form.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left });
        form.Controls.Add(_pass);
        form.Controls.Add(_showPass);
        root.Controls.Add(form);
        root.Controls.Add(Spacer(6));
        root.Controls.Add(_toggle);
        root.Controls.Add(Spacer(6));
        root.Controls.Add(_auto);
        root.Controls.Add(_login);
        root.Controls.Add(_logLink);
        root.Controls.Add(_log);
        Controls.Add(root);

        _ssid.Text = engine.Settings.Ssid;
        _pass.Text = engine.Settings.Password;
        _auto.Checked = engine.Settings.AutoStart;
        _login.Checked = Autostart.Enabled;

        _ssid.TextChanged += (_, _) => { engine.Settings.Ssid = _ssid.Text.Trim(); engine.Settings.Save(); Render(); };
        _pass.TextChanged += (_, _) => { engine.Settings.Password = _pass.Text; engine.Settings.Save(); Render(); };
        _showPass.CheckedChanged += (_, _) => _pass.UseSystemPasswordChar = !_showPass.Checked;
        _auto.CheckedChanged += (_, _) => { engine.Settings.AutoStart = _auto.Checked; engine.Settings.Save(); };
        _login.CheckedChanged += (_, _) =>
        {
            var err = Autostart.Set(_login.Checked);
            if (err != null) { MessageBox.Show(this, err, "Air Bridge"); _login.Checked = Autostart.Enabled; }
        };
        _logLink.LinkClicked += (_, _) => { _log.Visible = !_log.Visible; _logLink.Text = _log.Visible ? "Hide log" : "Show log"; };
        _toggle.Click += async (_, _) =>
        {
            if (_engine.State is EngineState.Running or EngineState.Starting) await _engine.StopAsync();
            else await _engine.StartAsync();
        };

        _trayStart.Click += async (_, _) => await _engine.StartAsync();
        _trayStop.Click += async (_, _) => await _engine.StopAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Air Bridge", null, (_, _) => ShowWindow());
        menu.Items.Add(_trayStart);
        menu.Items.Add(_trayStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray = new NotifyIcon { Icon = Icon, Text = "Air Bridge", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();

        engine.Changed += () => { if (IsHandleCreated) BeginInvoke(Render); };
        engine.Notify += (msg, err) =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => _tray.ShowBalloonTip(4000, "Air Bridge", msg, err ? ToolTipIcon.Error : ToolTipIcon.Info));
        };

        if (startHidden)
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Load += (_, _) => { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; };
        }
        HandleCreated += (_, _) => Render();
    }

    static Control Spacer(int h) => new Panel { Height = h, Width = 1 };

    public void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    void Render()
    {
        var e = _engine;
        (string text, Color color) = e.State switch
        {
            EngineState.NoDongle => ("Air Bridge not plugged in", Color.Gray),
            EngineState.Idle => ("Ready", Color.FromArgb(37, 99, 235)),
            EngineState.Starting => ("Starting…", Color.FromArgb(180, 83, 9)),
            EngineState.Running => ("Network running", Color.FromArgb(21, 128, 61)),
            EngineState.Stopping => ("Stopping…", Color.FromArgb(180, 83, 9)),
            _ => ("Problem", Color.FromArgb(185, 28, 28)),
        };
        _state.Text = "● " + text;
        _state.ForeColor = color;
        _detail.Text = e.Detail;
        _detail.Visible = e.Detail.Length > 0;

        _adapter.Text = e.DongleInstance == null ? "Not detected" : e.Usb == null ? "Connected" : $"Connected · {e.Usb.Label}";
        _adapter.ForeColor = e.Usb is { SuperSpeed: false } ? Color.FromArgb(185, 28, 28) : SystemColors.ControlText;
        if (e.Usb is { SuperSpeed: false }) _adapter.Text += " (use a USB 3 port)";
        _network.Text = e.State == EngineState.Running
            ? $"{e.Settings.Ssid}" + (e.Channel is { } c ? $" · ch {c.channel}, {c.widthMhz} MHz" : "") + $"\n{e.HotspotMode}"
            : "Off";
        _devices.Text = e.State != EngineState.Running ? "-" :
            e.Clients.Count == 0 ? "None connected" :
            string.Join("\n", e.Clients.Select(c => $"{c.Name ?? c.Mac}{(c.Ip != null ? $"  {c.Ip}" : "")}"));
        _latency.Text = e.LatencyMs is { } ms ? $"{ms} ms" : "-";

        bool busy = e.State is EngineState.Starting or EngineState.Stopping;
        bool on = e.State is EngineState.Running or EngineState.Starting;
        _toggle.Text = on ? "Stop network" : "Start network";
        _toggle.Enabled = !busy && (on || (e.DongleInstance != null && e.Settings.IsComplete));
        _ssid.Enabled = _pass.Enabled = !on;
        _trayStart.Enabled = !on && e.DongleInstance != null;
        _trayStop.Enabled = on;
        _tray.Text = $"Air Bridge: {text}";
        _tray.Icon = AppIcon.Create(color);

        lock (e.LogLines)
        {
            if (e.LogLines.Count != _logCount)
            {
                _logCount = e.LogLines.Count;
                _log.Lines = e.LogLines.TakeLast(200).ToArray();
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
        }
    }
}

static class AppIcon
{
    static readonly Dictionary<int, Icon> Cache = new();

    /// <summary>A small Wi-Fi glyph on a colored disc, drawn at runtime (no icon file to ship).</summary>
    public static Icon Create(Color color)
    {
        if (Cache.TryGetValue(color.ToArgb(), out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 7, 9, 18, 18, 225, 90);
            g.DrawArc(pen, 11, 13, 10, 10, 225, 90);
            g.FillEllipse(Brushes.White, 14, 20, 4, 4);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        Cache[color.ToArgb()] = icon;
        return icon;
    }
}
