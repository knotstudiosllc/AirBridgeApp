using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AirBridge;

/// <summary>Per-user settings in %APPDATA%\AirBridge; the password is DPAPI-encrypted.</summary>
sealed class Settings
{
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirBridge");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public string Ssid { get; set; } = "VR_Air_Bridge";
    public string ProtectedPassword { get; set; } = "";
    public bool AutoStart { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = Protect(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete => Ssid.Length is >= 1 and <= 32 && Password.Length is >= 8 and <= 63;

    public static Settings Load()
    {
        Directory.CreateDirectory(Dir);
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    // ---- DPAPI (CurrentUser scope)
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    static byte[]? Transform(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "AirBridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            LocalFree(output.Data);
            return result;
        }
        finally { h.Free(); }
    }

    static string Protect(string s) =>
        s.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(s), true) ?? Array.Empty<byte>());

    static string Unprotect(string s)
    {
        if (s.Length == 0) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(s), false) ?? Array.Empty<byte>()); }
        catch { return ""; }
    }
}

/// <summary>"Launch at sign-in": a logon scheduled task with highest privileges, so there's no UAC prompt.</summary>
static class Autostart
{
    const string Task = "Air Bridge";

    public static bool Enabled => Shell.Run("schtasks", $"/Query /TN \"{Task}\"").code == 0;

    public static string? Set(bool on)
    {
        var (code, output) = on
            ? Shell.Run("schtasks", $"/Create /TN \"{Task}\" /TR \"\\\"{Environment.ProcessPath}\\\" --background\" " +
                                    $"/SC ONLOGON /RU \"{Environment.UserDomainName}\\{Environment.UserName}\" /IT /RL HIGHEST /F")
            : Shell.Run("schtasks", $"/Delete /TN \"{Task}\" /F");
        return code == 0 ? null : output;
    }
}
