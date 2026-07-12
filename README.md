# HALO BAR 🚧

> A Windows 11 floating media/taskbar widget inspired by the Dynamic Island concept — built natively with WinUI 3 (.NET 10) and Fluent Design. **Not** an Apple clone — the goal is something that feels like it was designed by Microsoft for Windows 11.

**This project is actively in development.** Core features are fully functional, and layout states are continuously refined.

## Status

| Feature | Status |
|---|---|
| Now Playing (media session) | ✅ Working |
| Album art | ✅ Working |
| Waveform / progress indicator | ✅ Working — 5-bar animated visualizer (collapsed) & progress timeline (expanded) |
| Taskbar Width-Awareness | ✅ Working — Auto-shrinks / expands based on taskbar crowding (170, 250, 320 DIPs) |
| Fullscreen Suppression | ✅ Working — Completely hides window/acrylic in fullscreen mode |
| Clipboard history | ✅ Working — Live active item displaying with clear & paste actions |
| Focus Session | ✅ Working — Pomodoro timer countdown with play/pause and round tracker |
| Quick Tasks | ✅ Working — Checklist task list with instant keyboard text addition |
| File Shelf | ✅ Working — Drag-and-drop file stash shelf with direct launch and delete |
| System Stats | ✅ Working — Live CPU sparklines, RAM progress tracking, GPU simulated sparklines, Disk capacity free space gauges |
| Battery status | ✅ Working |
| Weather | ✅ Working — Real weather data integration (Seattle/Open-Meteo API) with 3-day forecast details |
| True acrylic/Mica backdrop | ✅ Working |
| Hover-to-expand dashboard | ✅ Working — Fully redesigned 3-row grid layout |

## Responsive Taskbar Shrink (Width-Awareness)

Halo Bar continuously monitors the available space on the Windows 11 taskbar (on a 150ms tick). When more applications are opened, the taskbar buttons crowd the island, triggering an automatic animated shrink sequence:
- **Full Width (320 DIPs)**: Displays album art, track title, artist name, visualizer waveform, and playback controls.
- **Moderate Crowding (250 DIPs)**: Artist name slides and fades out; title, visualizer, and controls remain visible.
- **Heavy Crowding (170 DIPs)**: Song title also fades out, leaving only the album art, waveform visualizer, and controls visible at consistent sizing.

Built-in hysteresis (down-shift thresholds at `330`/`260` DIPs, up-shift thresholds at `350`/`280` DIPs) prevents layout flickering.

## Layout and Expansion Behavior

When the user clicks the capsule, it expands into a premium wide dashboard.
- **Taskbar Clearance**: The expanded dashboard (800 × 480 DIPs) lifts upwards to sit directly above the taskbar, preventing blocking taskbar clicks.
- **Inverse Visibility**: The collapsed capsule vanishes entirely when expanded to avoid transparent click-blocking regions.

## Built With

- WinUI 3 / .NET 10
- Windows App SDK 1.8
- Windows Composition APIs (Desktop Acrylic / Mica)
- MVVM architecture
- Windows.Media.Control (system media session)
- Open-Meteo API (weather forecasting)

## Architecture

- `Controls/` — reusable UI controls
- `Services/` — IslandController, MediaService, ClipboardService, BatteryService, WeatherService, VolumeService, WindowService
- `ViewModels/` — MVVM view models per widget
- `Widgets/` — individual widget views (expanded dashboard, task checklist, file shelf, stats canvas)
- `Views/` — MainWindow shell

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

## Contributing

Not actively seeking contributors yet while the core UI is still being figured out, but feel free to open issues if something breaks.
