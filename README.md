# HALO BAR 🚧

> A Windows 11 floating media/taskbar widget inspired by the Dynamic Island concept — built natively with WinUI 3 (.NET 10) and Fluent Design. **Not** an Apple clone — the goal is something that feels like it was designed by Microsoft for Windows 11.

**This project is actively in development.** Core features are fully functional, and layout states are continuously refined.

## Status

| Feature | Status |
|---|---|
| Now Playing (media session) | ✅ Working |
| Album art | ✅ Working |
| Waveform / progress indicator | ✅ Working — 5-bar animated visualizer |
| Taskbar Width-Awareness | ✅ Working — Auto-shrinks / expands based on taskbar crowding |
| Fullscreen Suppression | ✅ Working — Completely hides window/acrylic in fullscreen mode |
| Clipboard history | ✅ Working — UI being refined |
| Battery status | ✅ Working |
| Charging indicator | 🚧 Planned |
| Weather | ⚠️ Placeholder data — real API not yet wired |
| Bluetooth | ⚠️ Placeholder — real device state not yet wired |
| Volume HUD | 🚧 Planned |
| True acrylic/Mica backdrop | ✅ Working |
| Hover-to-expand dashboard | ✅ Working — layout being refined |

## Responsive Taskbar Shrink (Width-Awareness)

Halo Bar continuously monitors the available space on the Windows 11 taskbar (on a 150ms tick). When more applications are opened, the taskbar buttons crowd the island, triggering an automatic animated shrink sequence:
- **Full Width (320 DIPs)**: Displays album art, track title, artist name, visualizer waveform, and playback controls.
- **Moderate Crowding (250 DIPs)**: Artist name slides and fades out; title, visualizer, and controls remain visible.
- **Heavy Crowding (170 DIPs)**: Song title also fades out, leaving only the album art, waveform visualizer, and controls visible at consistent sizing.

Built-in hysteresis (down-shift thresholds at `330`/`260` DIPs, up-shift thresholds at `350`/`280` DIPs) prevents layout flickering.

## Why this exists

Most "Dynamic Island for Windows" projects either fake transparency with opacity tricks or don't feel native at all. This one is trying to get the real Windows Composition APIs (Acrylic/Mica), real system data (actual media sessions, actual battery/Bluetooth state), and Fluent Design spacing/typography right — even if it takes longer to get there.

## Built With

- WinUI 3 / .NET 10
- Windows App SDK 1.8
- Windows Composition APIs (Desktop Acrylic / Mica)
- MVVM architecture
- Windows.Media.Control (system media session)
- Windows.Devices.Bluetooth *(integration pending)*

## Architecture

- `Controls/` — reusable UI controls
- `Services/` — IslandController, MediaService, ClipboardService, BatteryService, WeatherService, BluetoothService, VolumeService, WindowService
- `ViewModels/` — MVVM view models per widget
- `Widgets/` — individual widget views
- `Views/` — MainWindow

## Getting Started

### Prerequisites
- Windows 11
- Visual Studio 2022+ with Windows App SDK workload
- .NET 10 SDK

### Build & Run
```bash
git clone https://github.com/pruthviraj-bev/Halo-Bar.git
cd Halo-Bar
# Open DynamicIsland.csproj in Visual Studio, F5
```

## Known Issues

- Weather and Bluetooth widgets currently show placeholder data — real API integration not yet complete
- Some widgets may not match final visual design yet — actively iterating on Fluent styling

## Roadmap

- [ ] Real weather API integration
- [ ] Real Bluetooth paired-device state
- [ ] Volume HUD overlay
- [ ] Charging state indicator
- [ ] Package as installable app

## Contributing

Not actively seeking contributors yet while the core UI is still being figured out, but feel free to open issues if something breaks.
