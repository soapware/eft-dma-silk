# Silk Radar — Setup & Launch Guide

---

## Prerequisites

| Requirement | Notes |
|---|---|
| DMA card seated in PCIe slot | On the **target machine** (the one running EFT) |
| USB 3.0 cable connected | From DMA card to the **operator machine** (this PC) |
| Windows 10 / 11 x64 | On both machines |
| [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | On the operator machine |
| Run as Administrator | Required — DMA device access needs elevated permissions |

---

## Step 1 — Build (one-time)

Open PowerShell as Administrator. Replace `<install-dir>` with the folder you cloned or extracted the project to:

```powershell
cd <install-dir>
dotnet build eft-dma-radar-silk.sln -c Release
```

Output lands in `src-silk\bin\Release\net10.0-windows7.0\`. All native DLLs (`vmm.dll`, `leechcore.dll`, `FTD3XX.dll`, etc.) are copied automatically — nothing to move manually.

---

## Step 2 — Configure

The config file is created automatically on first launch:

```
%AppData%\eft-dma-radar-silk\config.json
```

Open it in any text editor. The fields you'll typically need to change:

| JSON key | What it does | Default |
|---|---|---|
| `deviceStr` | DMA device type | `"fpga"` |
| `radarTargetScreen` | Which monitor the radar opens on (0 = primary) | `1` |
| `espTargetScreen` | Which monitor the ESP overlay opens on | `0` |
| `targetFps` | Radar frame rate cap | `60` |
| `webRadarEnabled` | Enables the browser-based web radar | `true` |
| `webRadarPort` | Port the web radar listens on | `7224` |

**CaptainDMA 75T (FT601 USB):** leave `deviceStr` as `"fpga"`. Only change it if you're using a different device type (e.g. `"usb3380"`).

**Single monitor setup:** set both `radarTargetScreen` and `espTargetScreen` to `0`.

Window positions are saved automatically when you close each window and restored on next launch. To reset a window to its default monitor position, delete the `radarWindowX` / `radarWindowY` (or `espWindowX` / `espWindowY`) keys from `config.json`, or change the target screen setting in the ESP tab of the Settings panel.

---

## Step 3 — Launch

Run as Administrator from the project directory:

```powershell
cd <install-dir>
dotnet run --project src-silk\eft-dma-radar.csproj -c Release
```

Alternatively, launch the built `.exe` directly from the output folder as Administrator.

To enable verbose DMA logs (useful for diagnosing connection issues):

```powershell
dotnet run --project src-silk\eft-dma-radar.csproj -c Release -- -debug
```

---

## Step 4 — What You'll See

Everything from here is **automatic** — no hotkeys or manual attach steps required.

### Phase 1 — DMA Connecting

```
[ INITIALIZING DMA INTERFACE o ]
```

The app is establishing the link to the DMA card. If USB warm-up is needed, this takes up to ~10 seconds. The status bar at the bottom shows live MB/s read speed once the card responds.

**If this hangs:** replug the USB cable from the DMA card, wait 5 seconds, then relaunch.

---

### Phase 2 — Waiting for EFT

```
[ WAITING FOR TARKOV o ]
```

The DMA card is live. The app is polling for `EscapeFromTarkov.exe` on the target machine every 3 seconds.

- Launch EFT on the target machine
- On **first run after a game update**, IL2CPP offsets are dumped from live game memory — this takes ~15–30 seconds and is cached automatically for future sessions

---

### Phase 3 — In Raid

The radar map opens automatically when you drop into a raid.

- **Radar window** — players, loot, exfils, doors, etc. rendered on the map
- **ESP overlay** — second window on your configured monitor (press **F11** to fullscreen it)
- **Status bar** — live player counts, DMA read speed (gradient: red = low, green = healthy), energy/hydration, map name
- **Key doors** — locked doors you hold the required key for are highlighted cyan on the map and in the ESP overlay

When the raid ends the radar goes dormant and returns to Phase 2 waiting for the next raid.

---

## Web Radar (optional)

While in a raid, any device on your local network can pull up the map in a browser:

```
http://<operator-pc-ip>:7224
```

Works on phones and tablets too (touch-optimized). The web radar has its own independent settings and presets from the desktop window.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Stuck on `INITIALIZING DMA INTERFACE` | Replug the DMA USB cable, wait 5s, relaunch |
| Stuck on `WAITING FOR TARKOV` | Confirm EFT is running on the target machine; check PCIe card is fully seated |
| Map briefly shows "unknown" | Normal — happens during the loading screen before the map ID is readable; auto-corrects once fully in raid |
| First launch slow after EFT update | IL2CPP offset dump runs once and caches; subsequent launches are instant |
| Offsets wrong after EFT update | Delete `%AppData%\eft-dma-radar-silk\il2cpp_offsets.json` and relaunch to force a fresh dump |
| DMA speed shows red during a raid | Throughput is low — check USB 3.0 connection quality; reseat the PCIe card |
| Window opens on wrong monitor | Change the target screen in Settings → ESP tab (ESP) or edit `radarTargetScreen` in config.json (radar); window positions reset when the target is changed |
