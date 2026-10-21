using System.Net.NetworkInformation;
using RtkIhv;

namespace AirBridge;

enum EngineState { NoDongle, Idle, Starting, Running, Stopping, Error }

/// <summary>
/// Owns the dongle lifecycle: the Realtek start-up sequence (RtkIhv, no Meta DLL), the access point, plug/unplug
/// handling and auto-start. All public methods are safe to call from the UI thread.
/// </summary>
sealed class Engine : IDisposable
{
    readonly SemaphoreSlim _op = new(1, 1);
    readonly Hotspot _hotspot = new();
    readonly System.Threading.Timer _watch;
    RtkWifi? _rtk;
    bool _switchingUsb;
    string? _stationName;

    public Settings Settings { get; }
    public EngineState State { get; private set; } = EngineState.NoDongle;
    public string Detail { get; private set; } = "";
    public string? DongleInstance { get; private set; }
    public Device.UsbLink? Usb { get; private set; }
    public (string band, int channel, int widthMhz)? Channel { get; private set; }
    public IReadOnlyList<Hotspot.Client> Clients { get; private set; } = Array.Empty<Hotspot.Client>();
    public long? LatencyMs { get; private set; }
    public string HotspotMode => _hotspot.Mode;
    public readonly List<string> LogLines = new();

    public event Action? Changed;
    public event Action<string, bool>? Notify; // message, isError

    public Engine(Settings settings)
    {
        Settings = settings;
        _hotspot.Log += Log;
        _watch = new System.Threading.Timer(_ => Watch(), null, 0, 2000);
    }

    public void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {msg}";
        lock (LogLines)
        {
            LogLines.Add(line);
            if (LogLines.Count > 500) LogLines.RemoveAt(0);
        }
        try { File.AppendAllText(Path.Combine(Settings.Dir, "airbridge.log"), line + Environment.NewLine); } catch { }
        Changed?.Invoke();
    }

    void Set(EngineState s, string detail = "")
    {
        State = s;
        Detail = detail;
        Changed?.Invoke();
    }

    int _tick;

    void Watch()
    {
        try
        {
            var inst = Device.FindInstance();
            if (_switchingUsb) return; // the dongle disappears on purpose during the USB 3 switch
            if (inst == null && DongleInstance != null)
            {
                DongleInstance = null;
                Usb = null;
                Log("Air Bridge unplugged.");
                _ = StopAsync(unplugged: true);
            }
            else if (inst != null && DongleInstance == null)
            {
                DongleInstance = inst;
                Usb = Device.ReadUsbLink(inst);
                Log($"Air Bridge plugged in ({Usb?.Label ?? "USB speed unknown"}).");
                if (State is EngineState.NoDongle or EngineState.Error) Set(EngineState.Idle);
                if (Settings.AutoStart && Settings.IsComplete) _ = StartAsync();
            }

            if (++_tick % 5 == 0 && DongleInstance != null) Usb = Device.ReadUsbLink(DongleInstance);
            if (State == EngineState.Running)
            {
                Clients = _hotspot.Clients();
                var ip = Clients.FirstOrDefault(c => c.Ip != null)?.Ip;
                LatencyMs = ip == null ? null : PingOnce(ip);
                if (!_hotspot.Running && _op.CurrentCount == 1)
                {
                    Log("Access point went down; restarting it.");
                    _ = RestartApAsync();
                }
            }
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log($"Watcher error: {e.Message}");
        }
    }

    static long? PingOnce(string ip)
    {
        try
        {
            using var p = new Ping();
            var r = p.Send(ip, 500);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    RtkWifi EnsureRtk()
    {
        if (_rtk != null) return _rtk;
        _rtk = new RtkWifi(LoadConfig());
        _rtk.Log += Log;
        _rtk.CalibrationStep += (ch, level, outcome) => Log($"Calibration channel {ch} level {level}: {outcome}");
        return _rtk;
    }

    /// <summary>Meta's tuning JSON when Meta Horizon Link is installed, otherwise the same values built in.</summary>
    RtkConfig LoadConfig()
    {
        try
        {
            if (MetaInstall.RuntimeDir is { } dir && MetaInstall.ExtractSdkConfig(dir) is { } json)
            {
                Log("Using the Realtek tuning from your Meta Horizon Link install.");
                return RtkConfig.FromMetaJson(json);
            }
        }
        catch (Exception e) { Log($"Couldn't read Meta's Realtek tuning ({e.Message}); using built-in values."); }
        return new RtkConfig();
    }

    /// <summary>Runs a Realtek call; on 0x7 (AutoConfig off on the dongle) retries with it briefly on.</summary>
    uint Sdk(Func<uint> call)
    {
        uint rc = call();
        if (rc == RtkStatus.IHV_UNAVAILABLE && _stationName != null && Shell.AutoConfigEnabled(_stationName) == false)
        {
            Log("WLAN AutoConfig is off on the Air Bridge; enabling it.");
            Shell.SetAutoConfig(_stationName, true);
            rc = call();
        }
        return rc;
    }

    static void Expect(uint rc, string step)
    {
        if (rc != RtkStatus.OK)
            throw new InvalidOperationException($"{step} failed (0x{rc:x} {RtkStatus.Name(rc)})");
    }

    public async Task StartAsync()
    {
        if (!await _op.WaitAsync(0)) return;
        try
        {
            if (State == EngineState.Running) return;
            if (DongleInstance == null) { Set(EngineState.NoDongle, "Plug in the Air Bridge."); return; }
            if (!Settings.IsComplete) { Set(EngineState.Idle, "Set a network name and an 8+ character password."); return; }
            if (Shell.MetaServiceRunning())
                Log("Warning: Meta's OVRService is running and may take the dongle back. Quit Meta Horizon Link.");

            Set(EngineState.Starting, "Opening the Air Bridge…");
            var rtk = EnsureRtk();
            _stationName = Device.StationInterface()?.Name;

            await Task.Run(() => Expect(Sdk(rtk.Initialize), "Initialize"));

            Set(EngineState.Starting, "Switching to USB 3…");
            _switchingUsb = true;
            try
            {
                uint result = await rtk.UsbSwitchAsync();
                if (result == RtkWifi.USB_SWITCH_DONE)
                {
                    Log("Dongle re-enumerated on USB 3; waiting for it to settle.");
                    for (int i = 0; i < 30 && Device.FindInstance() == null; i++) await Task.Delay(500);
                    await Task.Delay(5000);
                }
                else if (result != RtkWifi.USB_SWITCH_NOT_NEEDED)
                {
                    Log($"USB switch reported {result}; continuing on the current link.");
                }
            }
            finally { _switchingUsb = false; }
            DongleInstance = Device.FindInstance();
            Usb = DongleInstance == null ? null : Device.ReadUsbLink(DongleInstance);
            _stationName = Device.StationInterface()?.Name;

            Set(EngineState.Starting, "Configuring the radio…");
            await Task.Run(() => Expect(Sdk(rtk.ConfigSoftAp), "ConfigSoftAp"));

            Set(EngineState.Starting, "Starting the network…");
            await StartApCoreAsync();

            await Task.Run(() =>
            {
                uint rc = Sdk(() => rtk.FbFeatureConfig(true));
                if (rc != RtkStatus.OK) { Thread.Sleep(2000); rc = Sdk(() => rtk.FbFeatureConfig(true)); }
                if (rc != RtkStatus.OK) Log("Low-latency (FB) mode couldn't be enabled; the network still works without it.");
                Channel = rtk.GetCurrentChannel() is { } ch ? (ch.BandLabel, ch.Channel, ch.WidthMhz) : null;
            });

            Set(EngineState.Running, _hotspot.Mode);
            Notify?.Invoke($"\"{Settings.Ssid}\" is up on the Air Bridge.", false);
        }
        catch (Exception e)
        {
            Log($"Start failed: {e.Message}");
            Set(EngineState.Error, e.Message);
            Notify?.Invoke(e.Message, true);
            try { await _hotspot.StopAsync(); } catch { }
        }
        finally { _op.Release(); }
    }

    async Task StartApCoreAsync()
    {
        var station = Device.StationInterface() ?? throw new InvalidOperationException("Air Bridge network adapter not found.");
        await _hotspot.StartAsync(Settings.Ssid, Settings.Password, Guid.Parse(station.Id));
    }

    async Task RestartApAsync()
    {
        if (!await _op.WaitAsync(0)) return;
        try { await StartApCoreAsync(); }
        catch (Exception e) { Log($"AP restart failed: {e.Message}"); Set(EngineState.Error, e.Message); }
        finally { _op.Release(); }
    }

    public async Task StopAsync(bool unplugged = false)
    {
        await _op.WaitAsync();
        try
        {
            if (State is not (EngineState.Running or EngineState.Starting or EngineState.Error)) return;
            Set(EngineState.Stopping, "Stopping…");
            await _hotspot.StopAsync();
            if (_rtk != null)
            {
                var rtk = _rtk;
                await Task.Run(() =>
                {
                    if (!unplugged) rtk.FbFeatureConfig(false);
                    rtk.Deinitialize();
                });
            }
            Channel = null;
            Clients = Array.Empty<Hotspot.Client>();
            LatencyMs = null;
            Set(DongleInstance == null ? EngineState.NoDongle : EngineState.Idle);
        }
        catch (Exception e)
        {
            Log($"Stop error: {e.Message}");
            Set(EngineState.Idle);
        }
        finally { _op.Release(); }
    }

    public void Dispose()
    {
        _watch.Dispose();
        // off the UI thread, so StopAsync's continuations can't deadlock against this Wait
        Task.Run(() => StopAsync()).Wait(TimeSpan.FromSeconds(10));
        _rtk?.Dispose();
    }
}
