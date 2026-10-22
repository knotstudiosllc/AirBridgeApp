using Windows.Devices.WiFiDirect;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.Security.Credentials;

namespace AirBridge;

/// <summary>
/// The access point itself, the same two ways Meta does it: Windows Mobile Hotspot bound to the
/// Air Bridge when there is internet to share (the Quest drops "no internet" networks after ~13 s),
/// otherwise a Wi-Fi Direct legacy soft AP.
/// </summary>
sealed class Hotspot
{
    NetworkOperatorTetheringManager? _tether;
    WiFiDirectAdvertisementPublisher? _publisher;

    public string Mode { get; private set; } = "";
    public bool Running => _tether?.TetheringOperationalState == TetheringOperationalState.On
                           || _publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started;

    public event Action<string>? Log;

    public async Task StartAsync(string ssid, string passphrase, Guid stationAdapterId)
    {
        await StopAsync();
        var inet = NetworkInformation.GetInternetConnectionProfile();
        if (inet != null)
        {
            // The dongle's WinRT NetworkAdapter is only reachable through a connection profile bound to it.
            var adapter = NetworkInformation.GetConnectionProfiles()
                .Select(p => p.NetworkAdapter)
                .FirstOrDefault(a => a != null && a.NetworkAdapterId == stationAdapterId);
            if (adapter != null)
            {
                await StartTetheringAsync(NetworkOperatorTetheringManager.CreateFromConnectionProfile(inet, adapter), ssid, passphrase);
                Mode = $"Mobile Hotspot (sharing {inet.ProfileName})";
                return;
            }
            Log?.Invoke("Air Bridge has no Windows network profile yet; falling back to Wi-Fi Direct (no internet).");
        }
        else
        {
            Log?.Invoke("No internet connection to share; using Wi-Fi Direct (the Quest may drop it after ~13 s).");
        }
        StartWifiDirect(ssid, passphrase);
        Mode = "Wi-Fi Direct (no internet)";
    }

    async Task StartTetheringAsync(NetworkOperatorTetheringManager mgr, string ssid, string passphrase)
    {
        if (mgr.TetheringOperationalState == TetheringOperationalState.On)
            await mgr.StopTetheringAsync();
        // Windows' "power saving" turns the hotspot off after 5 minutes with no client, which tears
        // down the tuned AP before the headset ever joins. Off while we run; RestoreIdleTimeout puts it back.
        if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())
        {
            await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();
            _restoreIdleTimeout = true;
            Log?.Invoke("Turned off Mobile Hotspot's 5-minute idle shutdown while Air Bridge runs.");
        }
        var cfg = mgr.GetCurrentAccessPointConfiguration();
        cfg.Ssid = ssid;
        cfg.Passphrase = passphrase;
        if (cfg.IsBandSupported(TetheringWiFiBand.FiveGigahertz)) cfg.Band = TetheringWiFiBand.FiveGigahertz;
        await mgr.ConfigureAccessPointAsync(cfg);
        var r = await mgr.StartTetheringAsync();
        if (r.Status != TetheringOperationStatus.Success)
            throw new InvalidOperationException($"Mobile Hotspot failed: {r.Status} {r.AdditionalErrorMessage}");
        _tether = mgr;
        Log?.Invoke("Mobile Hotspot started.");
    }

    void StartWifiDirect(string ssid, string passphrase)
    {
        var pub = new WiFiDirectAdvertisementPublisher();
        var adv = pub.Advertisement;
        adv.IsAutonomousGroupOwnerEnabled = true;
        // No P2P listen state, or the radio keeps hopping to 2.4 GHz social channels.
        adv.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.None;
        adv.LegacySettings.IsEnabled = true;
        adv.LegacySettings.Ssid = ssid;
        adv.LegacySettings.Passphrase = new PasswordCredential { Password = passphrase };
        pub.Start();
        _publisher = pub;
        Log?.Invoke($"Wi-Fi Direct AP {pub.Status}.");
    }

    public async Task StopAsync()
    {
        if (_tether != null)
        {
            try { await _tether.StopTetheringAsync(); } catch { }
            _tether = null;
            Log?.Invoke("Mobile Hotspot stopped.");
        }
        if (_publisher != null)
        {
            try { _publisher.Stop(); } catch { }
            _publisher = null;
            Log?.Invoke("Wi-Fi Direct AP stopped.");
        }
        Mode = "";
    }

    bool _restoreIdleTimeout;

    public async Task RestoreIdleTimeout()
    {
        if (!_restoreIdleTimeout) return;
        _restoreIdleTimeout = false;
        try { await NetworkOperatorTetheringManager.EnableNoConnectionsTimeoutAsync(); } catch { }
    }

    public record Client(string Mac, string? Ip, string? Name);

    public IReadOnlyList<Client> Clients()
    {
        try
        {
            if (_tether == null) return Array.Empty<Client>();
            return _tether.GetTetheringClients().Select(c => new Client(
                c.MacAddress,
                c.HostNames.FirstOrDefault(h => h.Type == Windows.Networking.HostNameType.Ipv4)?.CanonicalName,
                c.HostNames.FirstOrDefault(h => h.Type == Windows.Networking.HostNameType.DomainName)?.DisplayName)).ToList();
        }
        catch { return Array.Empty<Client>(); }
    }
}
