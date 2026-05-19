# EFT (Silk.NET) — soapware fork

A DMA radar overlay for **Escape from Tarkov** built on [Silk.NET](https://github.com/dotnet/Silk.NET), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET), and [SkiaSharp](https://github.com/mono/SkiaSharp). Ships with an embedded ASP.NET Core web radar.

> **Fork of [eft-dma-radar-silk](https://github.com/HuiTeab/eft-dma-radar-silk) by [HuiTeab](https://github.com/HuiTeab).** Original work © HuiTeab, PolyForm Noncommercial License 1.0.0.

See **[SETUP.md](SETUP.md)** for the full build, configuration, and launch guide.

---

## What's in this fork

### ESP Visuals Customization

A dedicated **ESP Visuals** panel (sidebar → Visuals) gives per-feature control over the overlay:

- **Feature toggles** — name label, health bar, weapon name, distance label, highlight target, each individually switchable
- **Per-feature color pickers** — inline color swatches with a full RGBA picker for every element
- **Box styles** — Corners, Full rectangle, or Top+Bottom bars, with a corner-fraction slider and line-thickness slider
- **ESP window opacity** — a single slider (10–100%) dims the entire overlay window via the OS layered-window API
- **Name font selector** — switch between Regular, Consolas, and Cutive Mono for player name labels
- **Health color thresholds** — independently configure the high / mid / low health bar colors

### Key Door Blips

Scans the local player's inventory on raid entry and again whenever items are picked up or dropped (event-driven, not a fixed timer). Locked doors for which the player holds the required key are highlighted **cyan** on the radar map and marked with a labeled world-space indicator in the ESP overlay. Toggle in **Settings → Map → Doors → Highlight Key Doors**.

### Startup Console

The debug console is replaced with a clean status panel showing each startup phase:

- **DMA connection** — attempt lines shift green → yellow → orange → red as retries mount, with actionable hint text at each threshold
- **DMA stats** — hardware benchmark speed shown immediately on connect, transitions to live read speed once scatter reads begin; both use the same red→yellow→green gradient
- **Wave animation** — the status-screen wave speed is linked to DMA throughput so card performance is visible at a glance
- **Error handling** — failure messages print once, then cycle a blinking dot in-place instead of flooding the console

### Persistent Window State

Both the radar and ESP overlay windows remember their last position, monitor, size, and F11 fullscreen state across sessions. Windows open exactly where they were left.

### UI & Visual Polish

- **Magenta accent** (`#ff00f5`) replaces the original teal throughout buttons, chips, and the web radar
- **Cutive Mono font** for status banners and the DMA stats box
- **Custom per-window icons** embedded in the exe and applied to the radar, ESP overlay, and startup screen
- **DMA speed gradient** on the status bar chip — the MB/s readout colors red→yellow→green relative to the hardware ceiling
- **Sidebar labels** use full readable names (Filter, History, Visuals, etc.)

---

## Requirements

- **DMA hardware** supported by [MemProcFS](https://github.com/ufrisk/MemProcFS) (FPGA card, `usb3380`, etc.)
- **Windows 10 / 11 (x64)** — targets `net10.0-windows`, `PlatformTarget=x64`
- **[.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**
- Run as **Administrator** (DMA device access requires elevation)

---

## Build & Run

```powershell
git clone https://github.com/soapware/eft-dma-silk.git
cd eft-dma-silk
dotnet build eft-dma-radar-silk.sln -c Release
dotnet run --project src-silk\eft-dma-radar.csproj -c Release
```

Pass `-debug` for verbose startup logging.

---

## License

`src-silk/` — **PolyForm Noncommercial License 1.0.0** (personal / non-commercial use only).  
`lib/VmmSharpEx/` — **AGPL-3.0** (MemProcFS wrapper © Ulf Frisk; modifications © Lone DMA, 2025).

---

## Credits

- Original: **[HuiTeab](https://github.com/HuiTeab)** — [eft-dma-radar-silk](https://github.com/HuiTeab/eft-dma-radar-silk)
- MemProcFS: **Ulf Frisk** — [github.com/ufrisk/MemProcFS](https://github.com/ufrisk/MemProcFS)
- Reference data: [tarkov.dev](https://tarkov.dev/)
