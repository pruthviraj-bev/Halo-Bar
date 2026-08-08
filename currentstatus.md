# Project Current Status

## Overview
- **Project**: *Halo Bar* (namespace `DynamicIsland`) – a Windows 11 floating media/taskbar widget inspired by the Dynamic Island concept, built natively with WinUI 3 (.NET 10) and Fluent Design.
- **Stack**: WinUI 3 / .NET 10 / Windows App SDK 1.8, CommunityToolkit.Mvvm, Windows Composition APIs (Desktop Acrylic), `Windows.Media.Control` (SMTC), Open-Meteo API.
- **Key components**: `Views/MainWindow.xaml(.cs)` (shell), `Widgets/ExpandedDashboard.xaml(.cs)` (expanded dashboard), `Services/IslandController.cs` (widget priority-stack coordinator + awake-hold/auto-collapse), `Services/WindowService.cs` (taskbar docking, z-order, spring animations, fullscreen suppression, `WH_MOUSE_LL` click-outside hook).
- **Current focus**: Clipboard transient-pill interaction fixes are committed (2s "Copied" message, big pill no countdown, dismiss/delete collapse, delete = remove from history). Next slice: **Quick Tasks persistence**.
- **Last commit**: `02b812c` — "Weather: bind ambient pill (WeatherCollapsedWidget) to WeatherService". (Next: the clipboard interaction commit.)

---

## All services (working tree)

Nine services are instantiated in `App.xaml.cs` (static composition root) and exposed via `App.*`:

| Service | Role |
|---|---|
| `IslandController` | Widget priority-stack coordinator; expand/collapse state machine; awake-hold (`BeginAwake`/`EndAwake`) so settings surfaces survive; click-gate + click-outside guards |
| `WindowService` | Sole mutator of window geometry, docking, z-order, acrylic, fullscreen suppression, low-level mouse hook |
| `MediaService` | SMTC session manager; media state + source pinning |
| `ClipboardService` | Clipboard change events + history store (30-day retention, not wired to scheduler) |
| `BatteryService` | Polled battery thresholds (Low ≤20%, Critical ≤10%, fire-once per downward transition, silent on launch) |
| `VolumeService` | Polled system volume via COM (150 ms, meaningful-change events) |
| `WeatherService` | Open-Meteo 30-min poll; real coordinates via `LocationService` (Geolocator→IP→last-known) + manual city override |
| `BluetoothService` | 5-s poll, adapter + device enumeration; **no widget consumes this yet** |
| `FocusSessionStore` | Focus session JSON persistence; seeds one "Focus" session (1500 s) on first run |

---

## Working-tree feature status

### Pill / shell

| Feature | Status |
|---|---|
| Dynamic Island pill (collapsed capsule, taskbar-docked) | ✅ Working |
| True acrylic / Mica backdrop (`IsInputActive` forced true) | ✅ Working |
| Taskbar width-awareness (adaptive compact width via CompactLayoutController) | ✅ Working |
| Fullscreen suppression (window hidden in fullscreen, debounced) | ✅ Working |
| Hover-to-expand / click-to-expand dashboard (auto-collapse, lazy) | ✅ Working |
| Click-expand toggle gated — stray presses inside the expanded dashboard can't collapse it | ✅ Working |
| Click-anywhere-outside closes the island immediately (`WH_MOUSE_LL` → `NotifyFocusLost`) | ✅ Working |
| Settings surfaces survive-awake (gear flyout, Focus H:M:S) — no collapse while open | ✅ Working |
| Inverse visibility (pill hidden when dashboard expanded) | ✅ Working |
| WeatherCollapsedWidget as taskbar anchor | ✅ Working — bound to WeatherService (ambient temp) |

### Pill transient widgets

| Widget | Priority | Profile | Status |
|---|---|---|---|
| Media (persistent) | 10 | Expanded | ✅ Fully wired (SMTC → MediaWidgetViewModel → XAML) |
| Clipboard (transient) | 20 | Collapsed | ✅ Transient widget; 2s "Copied" message on re-copy, hover-expand (width = compact pill), no countdown on the big pill, delete removes from history |
| Battery (transient) | 15 | Expanded | ✅ Transient snapshot widget, per-event |
| Volume (transient) | 18 | Collapsed | ✅ Transient snapshot widget, per-event |
| Alert (reserved) | 30 | — | 🔲 Registered but no concrete implementation |

### Expanded dashboard — what the XAML renders

| Section | In XAML? | Notes |
|---|---|---|
| **Now Playing** | ✅ Rendered | Album art, title/artist, seek slider, play/pause/prev/next/repeat, volume slider |
| **Focus Session** | ✅ Rendered | Full ring timer, arc progress, play/pause/reset/settings, session dot switcher, H/M:S number boxes, persistence |
| **Clipboard / File Shelf** | ✅ Rendered | "Clipboard" card with All/Pinned filter, swipe-to-delete, pin/unpin, thumbnails (in WidgetCard shell) |
| **Stats** | ✅ Rendered | Live 3-cell card: real CPU (`GetSystemTimes`), RAM (`GetPerformanceInfo`), storage (`DriveInfo`) — System Monitoring freeze |
| **Footer strip** | ✅ Rendered | Live weather temp/condition bound to `WeatherService` + Settings gear (location override flyout) |
| **Weather card** | ❌ Not in XAML | Weather is surfaced via the footer strip + ambient pill (a full card was not restored) |
| **Tasks card** | ❌ Not in XAML | `Tasks` collection + handlers exist in code-behind; **next slice = Quick Tasks persistence + XAML** |
| **File Shelf (drop zone)** | ❌ Pruned | Handlers + `StashedFiles` + `StashedFile.cs` removed (duplicates Explorer/Clipboard) |

### Expanded dashboard — code-behind

Live code-behind (`ExpandedDashboard.xaml.cs`) contains: focus timer (ring drag, session switch, settings, persistence), tasks (add/check/uncheck — not yet rendered), stats (real CPU/RAM/storage), weather footer props, volume sync, playback time interpolation, and the awake-hold wiring for the gear flyout and Focus settings. The XAML/code-behind are now in sync for the rendered sections.

---

## Committed snapshot (clipboard interaction fixes)

| File | Change |
|---|---|
| `Services/ClipboardService.cs` | `RemoveFromHistory` (delete = remove entry + image file, OS clipboard untouched); self-copy timestamp so ReCopy's own `ContentChanged` never spawns a fresh pill |
| `Services/IslandController.cs` | Clipboard push is now a 2s transient (`Short`); added `ShowCopiedFeedback`, `RenewAutoDismiss`, `CancelAutoDismiss` |
| `ViewModels/ClipboardWidgetViewModel.cs` | `Delete` command (was `Clear`); `ReCopy` shows the 2s "Copied" confirmation instead of dismissing instantly |
| `Widgets/ClipboardWidget.xaml` | Delete button bound to `DeleteCommand`, tooltip "Delete from history" |
| `Widgets/ClipboardWidget.xaml.cs` | Big pill has no countdown (cursor-driven only); leave-grace collapse; `OnDeactivated` restores compact geometry; expand width matches the compact pill; `ShowCopiedConfirmation` (2s "• Copied" then dismiss) |

---

## Known issues / follow-ups

1. **Quick Tasks persistence**: tasks reset each launch and have no XAML — next slice (persist + render the existing handlers).
2. **Clipboard retention**: `CleanupExpiredItems` (30-day retention) exists but is never called on a schedule — history grows unbounded.
3. **Sole-mutator violation**: `ClipboardWidget.xaml.cs` calls raw `WindowService.StartSizeAnimation(...)` instead of `WindowService.SetProfile()` (width now matches the compact pill, but the direct call remains).
4. **Bluetooth**: `BluetoothService` runs (5-s poll, device enumeration) but no widget consumes it.
5. **PomodoroTimerWidget**: Decide whether it becomes a real `IIslandWidget` or is removed.
6. **`gen_icons.py` missing**: `AppIcons.cs` (generated icon geometry registry) has no source script in the repo.
7. **README drift**: README still describes the old multi-width-tier behavior and stale feature list; needs a pass against the frozen dashboard.
8. **Dead / stale code**: `WindowService.StartDrag/UpdateDrag/EndDrag` are permanent no-ops; `MainWindowViewModel.MainPageViewModel` unused; `WindowProfileExtensions.ToDimensions` stale.
9. **Exception masking**: `App.xaml.cs` sets `e.Handled = true` on all unhandled exceptions — swallows crashes after logging.
10. **FocusEngine (Phase 3)**: scoped, not started.
11. **Repo ahead of origin**: multiple local commits pending push.

---

*Status snapshot as of the current working tree — reflects both committed state and uncommitted in-progress changes.*
