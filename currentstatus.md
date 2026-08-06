# Project Current Status

## Overview
- **Project**: *Halo Bar* (namespace `DynamicIsland`) – a Windows 11 floating media/taskbar widget inspired by the Dynamic Island concept, built natively with WinUI 3 (.NET 10) and Fluent Design.
- **Stack**: WinUI 3 / .NET 10 / Windows App SDK 1.8, CommunityToolkit.Mvvm, Windows Composition APIs (Desktop Acrylic), `Windows.Media.Control` (SMTC), Open-Meteo API.
- **Key components**: `Views/MainWindow.xaml(.cs)` (shell), `Widgets/ExpandedDashboard.xaml(.cs)` (expanded dashboard, 1205 lines code-behind), `Services/IslandController.cs` (widget priority-stack coordinator), `Services/WindowService.cs` (taskbar docking, z-order, spring animations, fullscreen suppression).
- **Current focus**: The expanded dashboard is mid-redesign in the working tree (see below).
- **Last commit**: `143dd6b` — "Expansion Layout Behavior Added".

---

## All services (working tree)

Nine services are instantiated in `App.xaml.cs` (static composition root) and exposed via `App.*`:

| Service | Role |
|---|---|
| `IslandController` | Widget priority-stack coordinator; lifecycle management |
| `WindowService` | Sole mutator of window geometry, docking, z-order, acrylic, fullscreen suppression |
| `MediaService` | SMTC session manager; media state + source pinning |
| `ClipboardService` | Clipboard change events + history store (30-day retention, not wired to scheduler) |
| `BatteryService` | Polled battery thresholds (Low ≤20%, Critical ≤10%, fire-once per downward transition, silent on launch) |
| `VolumeService` | Polled system volume via COM (150 ms, meaningful-change events) |
| `WeatherService` | Open-Meteo 30-min poll, hardcoded New Delhi (28.61, 77.20) |
| `BluetoothService` | 5-s poll, adapter + device enumeration; **no widget consumes this yet** |
| `FocusSessionStore` | Focus session JSON persistence; seeds one "Focus" session (1500 s) on first run |

---

## Working-tree feature status

### Pill / shell

| Feature | Status |
|---|---|
| Dynamic Island pill (collapsed capsule, taskbar-docked) | ✅ Working |
| True acrylic / Mica backdrop (`IsInputActive` forced true) | ✅ Working |
| Taskbar width-awareness (320 / 250 / 170 DIP tiers + hysteresis) | ✅ Working |
| Fullscreen suppression (window hidden in fullscreen) | ✅ Working |
| Hover-to-expand / click-to-expand dashboard (auto-collapse, lazy) | ✅ Working |
| Click-expand toggle via PointerPressed → IslandController | ✅ Working |
| Inverse visibility (pill hidden when dashboard expanded) | ✅ Working |
| WeatherCollapsedWidget returned as taskbar anchor in MainWindow | ⚠️ Present as placeholder (hardcoded "28°C Partly Cloudy", not bound to WeatherService) |

### Pill transient widgets

| Widget | Priority | Profile | Status |
|---|---|---|---|
| Media (persistent) | 10 | Expanded | ✅ Fully wired (SMTC → MediaWidgetViewModel → XAML) |
| Clipboard (transient) | 20 | Collapsed | ✅ Transient widget, auto-dismiss, clear/paste actions |
| Battery (transient) | 15 | Expanded | ✅ Transient snapshot widget, per-event |
| Volume (transient) | 18 | Collapsed | ✅ Transient snapshot widget, per-event |
| Alert (reserved) | 30 | — | 🔲 Registered but no concrete implementation |

### Expanded dashboard — what the XAML renders

| Section | In XAML? | Notes |
|---|---|---|
| **Now Playing** | ✅ Rendered | Album art, title/artist, seek slider, play/pause/prev/next/repeat, volume slider |
| **Focus Session** | ✅ Rendered | Full ring timer, arc progress, play/pause/reset/settings, session dot switcher, H/M/S number boxes |
| **Clipboard / File Shelf** | ✅ Rendered | "Clipboard" card with All/Pinned filter, swipe-to-delete, pin/unpin, thumbnails |
| **Stats** | ⚠️ Placeholder only | 2×2 grid with static labels ("RAM", "CPU", "GPU", "DISK"); no real data binding |
| **Footer strip** | ✅ Rendered | Hardcoded `"31°C"` text + Settings icon |
| **Weather card** | ❌ Not in XAML | `WeatherService` data properties exist in code-behind; not rendered |
| **Tasks card** | ❌ Not in XAML | `Tasks` collection + handlers exist in code-behind; no XAML UI |
| **File Shelf (drop zone)** | ❌ Not in XAML | `StashedFiles` collection + handlers exist in code-behind; no XAML UI |

### Expanded dashboard — code-behind (1205 lines)

The code-behind contains full logic for: focus timer (ring drag, session switch, settings, persistence), tasks (add/check/uncheck), file shelf (drag-drop/click/delete), stats (RAM via `GetPerformanceInfo`, simulated CPU/GPU, real battery via `BatteryService`, real storage via `DriveInfo`, volume sync, playback time interpolation), and weather data properties. **The XAML and code-behind are out of sync** — the XAML was redesigned as a cleaner layout, but the code-behind still has all the old feature logic.

---

## Working-tree status (uncommitted, in progress)

| File | Status | Notes |
|---|---|---|
| `Widgets/ExpandedDashboard.xaml` | Modified | 4-section layout (Stats placeholders, Focus Session, Clipboard, Now Playing, footer). Old sections removed. |
| `Widgets/ExpandedDashboard.xaml.cs` | Modified | 1205 lines. All old feature logic still present (focus timer, tasks, file shelf, stats, weather). Out of sync with XAML. |
| `Widgets/PomodoroTimerWidget.xaml/.cs` | Untracked | Static placeholder stub, no timer logic. Not an `IIslandWidget`; not registered in IslandController. |
| `Controls/` (folder) | Untracked | `AppIcon.xaml/.cs`, `AppIconKind.cs`, `AppIcons.cs` (generated, do not hand-edit). |
| `Models/FocusSession.cs` | Untracked | Focus session persistence model. |
| `Services/FocusSessionStore.cs` | Untracked | Focus session JSON persistence; seeds default session. |
| `Helpers/ClipboardHistoryStore.cs` | Untracked | Clipboard history + image persistence to `%LOCALAPPDATA%`. |
| `Helpers/ClipboardItemConverters.cs` | Untracked | `ClipboardImagePathConverter`, `ClipboardTypeIconConverter`. |
| `Helpers/FocusProgressToArcConverter.cs` | Untracked | Progress arc geometry converter (0–1 → 100×100 arc). |
| `currentstatus.md` | Untracked | This file. |
| `README.md` | Modified | (committed state reference) |
| All `Services/*.cs` (except IslandController, WindowService) | Modified | |
| `ViewModels/MediaWidgetViewModel.cs` | Modified | |
| `ViewModels/VolumeWidgetViewModel.cs` | Modified | |
| `ViewModels/BatteryWidgetViewModel.cs` | Modified | |
| `Widgets/MediaWidget.xaml` | Modified | |
| `Widgets/VolumeWidget.xaml` | Modified | |
| `Widgets/BatteryWidget.xaml` | Modified | |
| `Widgets/ClipboardWidget.xaml` | Modified | |
| `Widgets/WeatherCollapsedWidget.xaml` | Modified | |
| `Helpers/MotionConfig.cs` | Modified | |
| `Helpers/Logger.cs` | Modified | |

---

## Known issues / follow-ups

1. **Dashboard XAML ↔ code-behind mismatch**: Focus Session and Now Playing are rendered; Stats, Tasks, File Shelf, Weather, and Bluetooth are not. Either restore the missing XAML sections (code-behind is ready) or prune the dead code-behind.
2. **Weather**: `WeatherService` hardcodes New Delhi (28.61, 77.20); README claims Seattle. `WeatherCollapsedWidget` is hardcoded and not bound to the service. Dashboard weather card not present in XAML.
3. **PomodoroTimerWidget**: Decide whether it becomes a real `IIslandWidget` or is removed.
4. **Bluetooth**: `BluetoothService` runs (5-s poll, device enumeration) but no widget consumes it.
5. **Clipboard retention**: `CleanupExpiredItems` (30-day retention) exists but is never called on a schedule — history grows unbounded.
6. **Sole-mutator violation**: `ClipboardWidget.xaml.cs` calls raw `WindowService.StartSizeAnimation(320, 180)` instead of `WindowService.SetProfile()`.
7. **Dead / stale code**: `WindowService.StartDrag/UpdateDrag/EndDrag` are no-ops; `MainWindowViewModel.MainPageViewModel` is unused boilerplate; `WindowProfileExtensions.ToDimensions` (220×40 / 360×96) is stale vs `SetProfile`'s real pixels.
8. **Exception masking**: `App.xaml.cs` sets `e.Handled = true` on all unhandled exceptions — swallows crashes after logging.
9. **gen_icons.py missing**: `AppIcons.cs` (generated icon geometry registry) has no source script in the repo.
10. **README drift**: README claims Focus Session, Quick Tasks, File Shelf, System Stats, and Weather as dashboard features. Only Focus Session is currently rendered. Stats is placeholder-only. Others are absent from the XAML.
11. **GetTaskbarContent returns WeatherCollapsedWidget when expanded**: Pill is hidden when expanded, so this is dead code.

---

*Status snapshot as of the current working tree — reflects both committed state and uncommitted in-progress changes.*
