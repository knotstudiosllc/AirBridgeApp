using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AirBridge;

/// <summary>Finds the DWA-F18 and reads its USB link speed via the parent hub's IOCTLs.</summary>
static partial class Device
{
    public const string InstancePrefix = @"USB\VID_2001&PID_3323";
    public const string DescriptionMatch = "DWA-F18";

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint prop, out uint type, byte[]? buffer, ref uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string id, char[] buffer, uint len, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);

    static readonly Guid UsbHubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint CM_DRP_LOCATION_INFORMATION = 0x0E;
    const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;

    /// <summary>Instance id of the plugged-in dongle, or null.</summary>
    public static string? FindInstance()
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, "USB", flags) != 0) return null;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != 0) return null;
        return new string(buf).Split('\0').FirstOrDefault(d => d.StartsWith(InstancePrefix, StringComparison.OrdinalIgnoreCase));
    }

    public static NetworkInterface? StationInterface() =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Description.Contains(DescriptionMatch));

    public record UsbLink(bool SuperSpeed, bool SuperSpeedPlus, int Port, string Label);

    /// <summary>Reads whether the dongle is running at USB 3 (the V1 speed field can't express SuperSpeed).</summary>
    public static UsbLink? ReadUsbLink(string instance)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, instance, 0) != 0) return null;
            uint len = 0;
            CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_LOCATION_INFORMATION, out _, null, ref len, 0);
            var locBuf = new byte[len];
            if (CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_LOCATION_INFORMATION, out _, locBuf, ref len, 0) != 0) return null;
            var m = PortRegex().Match(System.Text.Encoding.Unicode.GetString(locBuf));
            if (!m.Success) return null;
            int port = int.Parse(m.Groups[1].Value);

            if (CM_Get_Parent(out uint hub, dev, 0) != 0) return null;
            var idBuf = new char[512];
            if (CM_Get_Device_IDW(hub, idBuf, (uint)idBuf.Length, 0) != 0) return null;
            string hubId = new string(idBuf).TrimEnd('\0');

            var cls = UsbHubInterface;
            if (CM_Get_Device_Interface_List_SizeW(out uint ilen, ref cls, hubId, 0) != 0) return null;
            var ibuf = new char[ilen];
            if (CM_Get_Device_Interface_ListW(ref cls, hubId, ibuf, ilen, 0) != 0) return null;
            string path = new string(ibuf).Split('\0')[0];

            using var h = CreateFileW(path, 0x40000000, 0x2, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception();
            // USB_NODE_CONNECTION_INFORMATION_EX_V2: port, length, protocols-to-query (USB 1.1|2.0|3.x), flags
            var inBuf = new byte[16];
            BitConverter.GetBytes((uint)port).CopyTo(inBuf, 0);
            BitConverter.GetBytes(16u).CopyTo(inBuf, 4);
            BitConverter.GetBytes(7u).CopyTo(inBuf, 8);
            var outBuf = new byte[16];
            if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, inBuf, 16, outBuf, 16, out _, IntPtr.Zero))
                throw new Win32Exception();
            uint fl = BitConverter.ToUInt32(outBuf, 12);
            bool ss = (fl & 1) != 0, ssp = (fl & 4) != 0;
            string label = ssp ? "USB 3 (10 Gb/s)" : ss ? "USB 3 (5 Gb/s)" : "USB 2 (480 Mb/s)";
            return new UsbLink(ss, ssp, port, label);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"Port_#(\d+)")]
    private static partial Regex PortRegex();
}

static class Shell
{
    public static (int code, string output) Run(string file, string args, int timeoutMs = 20000)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return (p.HasExited ? p.ExitCode : -1, o.Trim());
    }

    /// <summary>WLAN AutoConfig on the dongle's station interface; the Realtek IHV path returns 0x7 while it's off.</summary>
    public static bool? AutoConfigEnabled(string iface)
    {
        var (_, o) = Run("netsh", "wlan show settings");
        var line = o.Split('\n').FirstOrDefault(l => l.Contains($"\"{iface}\""));
        return line == null ? null : line.Contains("enabled");
    }

    public static void SetAutoConfig(string iface, bool on) =>
        Run("netsh", $"wlan set autoconfig enabled={(on ? "yes" : "no")} interface=\"{iface}\"");

    public static bool MetaServiceRunning()
    {
        var (_, o) = Run("sc", "query OVRService", 5000);
        return o.Contains("RUNNING");
    }
}
