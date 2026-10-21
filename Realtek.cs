using System.Runtime.InteropServices;
using System.Text;

namespace AirBridge;

/// <summary>
/// RealtekWiFi.dll from the user's Meta Horizon Link install, driven the way Meta's
/// AriManager does it (see re/realtekwifi_reverse.md). Nothing from Meta is bundled:
/// the DLL and its JSON tuning config are both read from the local install.
/// </summary>
sealed class RealtekSdk : IDisposable
{
    public const uint OK = 0x00, IHV_UNAVAILABLE = 0x07, PENDING = 0x13;
    const uint EVENT_USB_SWITCH = 0x60001;

    public static readonly Dictionary<uint, string> StatusNames = new()
    {
        [0x00] = "OK", [0x01] = "ALREADY_REGISTERED", [0x06] = "CALLBACK_NULL",
        [0x07] = "IHV_UNAVAILABLE", [0x0E] = "INVALID_SETTING", [0x0F] = "SETTINGS_UNREADABLE",
        [0x10] = "INI_NOT_FOUND", [0x11] = "JSON_FORMAT_ERROR", [0x12] = "DEVICE_BUSY",
        [0x13] = "PENDING", [0x15] = "DRIVER_VERSION_MISMATCH",
    };

    [StructLayout(LayoutKind.Sequential)]
    struct SdkConfig
    {
        public byte Enable;        // Meta passes 1
        public uint SettingType;   // 0 = INI path, 1 = JSON text
        public IntPtr SettingVal;  // char*
        public byte Unk10;         // Meta passes 1
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint FnPtrArg(ref SdkConfig cfg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint FnVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint FnU32(uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint FnBuf(IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint FnCb(EventCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void EventCallback(uint type, IntPtr data, uint len);

    readonly IntPtr _lib;
    readonly FnPtrArg _preConfig;
    readonly FnVoid _init, _deinit, _usbSwitch, _configSoftAp, _unregister;
    readonly FnU32 _fb;
    readonly FnCb _register;
    readonly FnBuf _getChannel;
    readonly EventCallback _cb; // kept alive for the DLL
    IntPtr _configMem;
    TaskCompletionSource<uint>? _usbWait;

    public event Action<string>? Log;

    public RealtekSdk(string dllPath)
    {
        _lib = NativeLibrary.Load(dllPath);
        T Get<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, n));
        _preConfig = Get<FnPtrArg>("RealtekWiFiSDKPreConfig");
        _init = Get<FnVoid>("RealtekWiFiInitialize");
        _deinit = Get<FnVoid>("RealtekWiFiDeinitialize");
        _usbSwitch = Get<FnVoid>("RealtekWiFiUsbSwitch");
        _configSoftAp = Get<FnVoid>("RealtekWiFiConfigSoftAp");
        _unregister = Get<FnVoid>("RealtekWiFiUnRegisterCallBack");
        _fb = Get<FnU32>("RealtekWiFiFBFeatureConfig");
        _register = Get<FnCb>("RealtekWiFiRegisterCallBack");
        _getChannel = Get<FnBuf>("RealtekWiFiGetCurrentChannelSetting");
        _cb = OnEvent;
    }

    void OnEvent(uint type, IntPtr data, uint len)
    {
        uint first = data != IntPtr.Zero && len >= 4 ? (uint)Marshal.ReadInt32(data) : uint.MaxValue;
        if (type == EVENT_USB_SWITCH)
        {
            Log?.Invoke($"USB switch event: {first}");
            // 0 = switching (device re-enumerates), 1 = already in the right mode, 2 = complete (Meta checks == 2)
            if (first != 0) _usbWait?.TrySetResult(first);
        }
    }

    uint Call(string name, Func<uint> fn)
    {
        uint rc = fn();
        Log?.Invoke($"{name} -> 0x{rc:x} ({StatusNames.GetValueOrDefault(rc, "?")})");
        return rc;
    }

    public uint PreConfig(string json)
    {
        if (_configMem != IntPtr.Zero) Marshal.FreeHGlobal(_configMem);
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        _configMem = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _configMem, bytes.Length);
        var cfg = new SdkConfig { Enable = 1, SettingType = 1, SettingVal = _configMem, Unk10 = 1 };
        return Call("SDKPreConfig", () => _preConfig(ref cfg));
    }

    public uint Initialize() => Call("Initialize", () => _init());
    public uint RegisterCallback() => Call("RegisterCallBack", () => _register(_cb));
    public uint ConfigSoftAp() => Call("ConfigSoftAp", () => _configSoftAp());
    public uint FbMode(bool on) => Call($"FBFeatureConfig({(on ? 1 : 0)})", () => _fb(on ? 1u : 0u));
    public uint Unregister() => Call("UnRegisterCallBack", () => _unregister());
    public uint Deinitialize() => Call("Deinitialize", () => _deinit());

    /// <summary>Returns the switch result (1 = already right, 2 = switched and re-enumerated), or null on timeout.</summary>
    public async Task<(uint rc, uint? result)> UsbSwitchAsync(TimeSpan timeout)
    {
        _usbWait = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint rc = Call("UsbSwitch", () => _usbSwitch());
        if (rc != PENDING) return (rc, null);
        var done = await Task.WhenAny(_usbWait.Task, Task.Delay(timeout));
        return (rc, done == _usbWait.Task ? _usbWait.Task.Result : null);
    }

    /// <summary>Inferred layout: [band (1 = 5 GHz), ?, primary channel, width (0/1/2/3 = 20/40/80/160)].</summary>
    public (string band, int channel, int widthMhz)? GetChannel()
    {
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
            if (_getChannel(buf) != OK) return null;
            int band = Marshal.ReadInt32(buf, 0), ch = Marshal.ReadInt32(buf, 8), bw = Marshal.ReadInt32(buf, 12);
            string b = band switch { 0 => "2.4 GHz", 1 => "5 GHz", 2 => "6 GHz", _ => $"band {band}" };
            int w = bw switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, _ => bw };
            return (b, ch, w);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_configMem != IntPtr.Zero) Marshal.FreeHGlobal(_configMem);
        // RealtekWiFi.dll is left loaded: unloading it while its threads may still run is unsafe.
    }
}

static class MetaInstall
{
    static readonly string[] RuntimeDirs =
    {
        @"C:\Program Files\Meta Horizon\Support\oculus-runtime",
        @"C:\Program Files\Oculus\Support\oculus-runtime",
    };

    public static string? RuntimeDir
    {
        get
        {
            foreach (var d in RuntimeDirs)
                if (File.Exists(Path.Combine(d, "RealtekWiFi.dll"))) return d;
            // Meta stores its install base here when installed to a custom location
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus");
                if (k?.GetValue("Base") is string b)
                {
                    var d = Path.Combine(b, "Support", "oculus-runtime");
                    if (File.Exists(Path.Combine(d, "RealtekWiFi.dll"))) return d;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Pulls Meta's Realtek tuning JSON (the blob that starts with "AMPDU") out of
    /// OculusAppFramework.dll, where Meta embeds it as a string.
    /// </summary>
    public static string? ExtractSdkConfig(string runtimeDir)
    {
        var path = Path.Combine(runtimeDir, "OculusAppFramework.dll");
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes("{\n  \"AMPDU\"");
        int start = data.AsSpan().IndexOf(marker);
        if (start < 0) return null;
        int end = Array.IndexOf(data, (byte)0, start);
        if (end < 0) return null;
        return Encoding.ASCII.GetString(data, start, end - start);
    }
}
