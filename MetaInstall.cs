using System.Text;

namespace AirBridge;

/// <summary>
/// Optional: reads Meta's Realtek tuning JSON from a local Meta Horizon Link install. Air Bridge
/// drives the dongle itself (RtkIhv) and has the same values built in, so Meta isn't required.
/// </summary>
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
                if (File.Exists(Path.Combine(d, "OculusAppFramework.dll"))) return d;
            // Meta stores its install base here when installed to a custom location
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus");
                if (k?.GetValue("Base") is string b)
                {
                    var d = Path.Combine(b, "Support", "oculus-runtime");
                    if (File.Exists(Path.Combine(d, "OculusAppFramework.dll"))) return d;
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
