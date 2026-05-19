# EFT DMA Radar — Silk.NET Edition (soapware fork)

A DMA radar overlay for **Escape from Tarkov** built on [Silk.NET](https://github.com/dotnet/Silk.NET), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET), and [SkiaSharp](https://github.com/mono/SkiaSharp). Ships with an embedded ASP.NET Core web radar.

> **Based on [eft-dma-radar-silk](https://github.com/HuiTeab/eft-dma-radar-silk) by [HuiTeab](https://github.com/HuiTeab).** Original work © HuiTeab, licensed under the PolyForm Noncommercial License 1.0.0. This fork extends the original with the additions listed below.

---

## What's new in this fork

### Visual & UI

- **Sidebar readability** — single-word panels now show their full name (Players, Loot, Aimview, Quests, Settings). Primary tab font bumped to 15 px for easier reading at distance or over AnyDesk.
- **Radar window centering** — window auto-centers on the configured target monitor at launch instead of defaulting to (0, 0).
- **Per-window icons** — custom icons embedded in the exe (app icon visible in Explorer/taskbar) and applied individually to the radar window, ESP overlay window, and startup screen.
- **ESP window title** — "ESP Overlay - (F11 Fullscreen)" so the hint is visible without reading docs.

### Status Screen

- **State-aware messaging** — startup sub-line reads `[ INITIALIZING DMA INTERFACE ]` while the DMA card is connecting, then switches to `[ WAITING FOR TARKOV ]` once the card is live and the radar is polling for the game process.
- **DMA stats box** — Skia-drawn box below the banner shows HW MAX throughput and fault/issue counter for the current session.

### Status Bar

- **DMA speed gradient** — the `### MB/s` value in the DMA chip colors red → yellow → green based on current throughput relative to the hardware ceiling. The separator and RT count stay neutral blue.

### Key Door Blips *(new feature)*

Scans the local player's Pockets, Backpack, and SecuredContainer for key items every 30 seconds using batched DMA scatter reads (no impact on the 8 ms realtime path).

- Locked doors for which the player holds the required key are highlighted **cyan** on the radar map instead of red.
- The ESP overlay shows a cyan circle marker and `"KeyName [Xm]"` world-space label for each matching door.
- Toggle: **Settings → Map → Doors → Highlight Key Doors**, or `showKeyDoors` in `config.json`.

### Setup Guide

`SETUP.md` at the repo root — plain-language step-by-step instructions for configuring, building, launching, and understanding the three startup states.

---

## Requirements

- **DMA hardware** supported by [MemProcFS](https://github.com/ufrisk/MemProcFS) (FPGA card, `usb3380`, etc.)
- **Windows 10 / 11 (x64)** — project targets `net10.0-windows`, `PlatformTarget=x64`
- **[.NET 10 SDK / Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**
- Run as **Administrator** (required for DMA device access)
- Native MemProcFS binaries (`vmm.dll`, `leechcore.dll`, `FTD3XX.dll`, …) are copied to the build output automatically from `lib/VmmSharpEx/native/`.

---

## Build & Run

```powershell
git clone https://github.com/soapware/eft-dma-silk.git
cd eft-dma-silk

# Build (Release, x64)
dotnet build eft-dma-radar-silk.sln -c Release

# Run as Administrator
dotnet run --project src-silk\eft-dma-radar.csproj -c Release
```

See **[SETUP.md](SETUP.md)** for the full configuration and launch walkthrough.

Pass `-debug` on the command line for verbose startup logging.

---

## Repo Layout

```
eft-dma-radar-silk/
├── eft-dma-radar-silk.sln       # Visual Studio solution
├── SETUP.md                     # Quick-start guide (this fork)
├── Maps/                        # EFT map SVGs + JSON metadata
├── Resources/                   # Embedded font + default item DB
├── lib/
│   └── VmmSharpEx/              # Managed MemProcFS wrapper + native DLLs
└── src-silk/                    # Radar source (entry: Program.cs → SilkProgram.Main)
    └── assets/
        ├── fonts/               # Cutive Mono, Segoe UI (ImGui)
        └── icons/               # Per-window PNG + ICO icons
```

---

## License

The source code in `src-silk/` is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)** — personal / non-commercial use only.

The component under `lib/VmmSharpEx/` is licensed under **AGPL-3.0** (original MemProcFS wrapper © Ulf Frisk; modifications © Lone DMA, 2025). Redistributors of compiled binaries must satisfy AGPL-3.0 requirements.

---

## Credits

- Original radar: **[HuiTeab](https://github.com/HuiTeab)** — [eft-dma-radar-silk](https://github.com/HuiTeab/eft-dma-radar-silk)
- MemProcFS: **Ulf Frisk** — [https://github.com/ufrisk/MemProcFS](https://github.com/ufrisk/MemProcFS)
- Reference data: [tarkov.dev](https://tarkov.dev/)
