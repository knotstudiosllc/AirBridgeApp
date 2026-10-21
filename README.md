# Air Bridge

A small tray app that runs the D-Link DWA-F18 "VR Air Bridge" as a Wi-Fi access point without the Meta Horizon Link app. You can use it with ALVR, Virtual Desktop or anything else that streams over a local network.

## Requirements
- Windows 10 2004 or newer, or Windows 11.
- The DWA-F18 plugged into a **USB 3** port, ideally a rear motherboard port with no hub in between.
- **No Meta software needed.** Air Bridge talks to the Realtek driver directly (the RtkIhv library in `..\RtkIhv`), sending the same commands Meta's `RealtekWiFi.dll` does. If Meta Horizon Link is installed, its Realtek tuning values are read from your install; otherwise the same values are built in. If Meta's app is installed, quit it before using Air Bridge, because the two fight over the dongle.

## Use
1. Run `AirBridge.exe` and accept the admin prompt. The Realtek driver control path needs admin.
2. Pick a network name and password (8+ characters), then press **Start network**.
3. Join that network from the headset.

The access point works the same way Meta's own software does:
- If the PC has internet, it runs a **Mobile Hotspot** on the Air Bridge and shares that internet. That keeps the headset from dropping the network.
- If the PC has no internet, it falls back to a **Wi-Fi Direct** access point. Headsets may disconnect from it after about 13 seconds.

## Background use
- Closing the window keeps Air Bridge running in the tray. Use the tray icon's menu to open it, start or stop the network, or exit.
- **Start the network automatically when the Air Bridge is plugged in** brings the network up on plug-in and back up after a replug.
- **Launch at sign-in** adds a logon scheduled task ("Air Bridge", highest privileges). It starts the app in the tray with no admin prompt. Untick it to remove the task.

Settings are stored in `%APPDATA%\AirBridge\settings.json`; the password is encrypted with Windows DPAPI. The log is `%APPDATA%\AirBridge\airbridge.log`.

## Build
```bash
dotnet publish -c Release -o publish
```

AirBridge.csproj references `..\RtkIhv\RtkIhv.csproj`, so RtkIhv must be checked out next to this folder.

`.github/workflows/build.yml` does the same on GitHub Actions: it checks out this repo and RtkIhv side by side, publishes the self-contained single-file `AirBridge.exe`, uploads it as a build artifact, and attaches it to a GitHub Release when a `v*` tag is pushed (the tag sets the version). RtkIhv is expected at `<owner>/RtkIhv`; set the `RTKIHV_REPO` repository variable to use another name, and the `RTKIHV_TOKEN` secret (a token with read access) if that repo is private.
