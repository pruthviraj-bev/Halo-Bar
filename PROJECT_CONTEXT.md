# PROJECT_CONTEXT.md

**Canonical engineering handbook for Halo Bar.** This file is the source of truth.
Every future AI session should begin here. It is deliberately not a conversation
log — it preserves decisions, architecture, failures, and current reality so a
fresh engineer (human or AI) can contribute immediately.

Maintained alongside: `README.md` (user-facing), `currentstatus.md` (historical
status snapshot, partially stale — see Lessons Learned), `*-Migration-PR*.md`
(per-PR validation logs).

---

# Project Overview

## Identity

- **Project name:** Halo Bar (the app window is titled "DynamicIsland"; the
  solution/namespace is `DynamicIsland`; the GitHub repo is
  `pruthviraj-bev/Halo-Bar`).
- **Platform:** Windows 11, native WinUI 3 (`net10.0-windows10.0.26100.0`,
  min version 17763), unpackaged (`WindowsPackageType=None`), single project.
- **Stack:** Windows App SDK 1.8, CommunityToolkit.Mvvm 8.4.2, Windows
  Composition (Desktop Acrylic), `Windows.Media.Control` (SMTC), Open-Meteo API,
  raw Win32 P/Invoke (DWM, user32, shell32, psapi).

## Vision

A floating, taskbar-embedded "Dynamic Island" for Windows 11 that surfaces
system and media information at a glance. It is explicitly **not an Apple
clone** — the stated goal is "something that feels like it was designed by
Microsoft for Windows 11," built natively with Fluent design.

## Purpose

Provide glanceable, ambient information (now playing, clipboard captures,
battery, volume, weather, focus timer, system stats) directly on the taskbar,
and expand into a rich dashboard when the user needs detail — all without
stealing focus, appearing in Alt+Tab, or fighting the shell for z-order.

## Elevator pitch

A tiny always-on-top, borderless, acrylic pill docked at the left edge of the
Windows 11 taskbar. It hosts a priority stack of transient widgets (media,
clipboard, battery, volume) that appear/dismiss as events happen, adapts its
width as the taskbar crowds, hides entirely during fullscreen, and expands on
click/hover into an 800×664 dashboard of cards.

## Long-term goals

1. A polished, stable, genuinely useful taskbar widget that behaves like a
   first-party Windows 11 feature.
2. A clean extensible widget architecture where adding a new widget is a small,
   isolated change (see `IIslandWidget`, `WidgetPriority`, `WidgetCard`).
3. A consistent design-token system (spacing, radius, type, color, motion,
   elevation) that replaces the current mixture of tokens and hardcoded values.
4. Correctness and calm behavior over feature count — the design language
   repeatedly favors "frozen/honest geometry over nervous geometry."

---

# Design Philosophy

Every major engineering decision in this repo traces back to one of these
principles. They are applied deliberately and repeatedly; do not fight them
without a written reason.

- **Glanceable first, interactive second.** The collapsed pill must be readable
  in under a second. Interactivity (hover-to-expand, click-to-expand) is a
  layer on top, never at the expense of the at-a-glance state.
- **Sole mutator / single source of truth.** Exactly one component owns each
  concern. `WindowService` is the only thing that writes window geometry;
  `CompactLayoutController` is the only thing that computes compact width;
  `IslandController` is the only thing that calls `WindowService.SetProfile`.
  Widgets must **never** touch `WindowService` directly. Violations are tracked
  as debt (see Known Issues).
- **Calm, predictable animations; never nervous geometry.** Debounce, hysteresis,
  signal-loss freeze, and pointer-freeze are all used so the pill adapts once
  and feels settled, never jittering with Explorer churn. "Frozen geometry is
  infinitely better than nervous geometry."
- **Honest degradation.** The compact width is `min(available, ideal)` with **no
  lower floor** — on a crowded taskbar the pill genuinely shrinks rather than
  re-overlapping the tray. Similarly, signal loss freezes rather than guesses.
- **Simplicity before cleverness.** Stated explicitly for the anchor strategy
  ("single, stable measurement signal"), for layouts, and for the shell
  migration work. When two mechanisms exist (e.g. `x:Bind` + manual toggles),
  the project prefers the mechanism that is easiest to reason about, even if
  less "elegant."
- **Shared chrome, isolated behavior.** The `WidgetCard` shell owns all card
  chrome (border, radius, padding, hover/press/focus states). Widgets own their
  content and behavior only. Widget-specific logic must never leak into the
  shell (verified per PR).
- **Preservation over polish during refactors.** Migration PRs are strictly
  "shell only — no feature additions, no visual polish." Anything noticed but
  out of scope is named in the report, not fixed inline.
- **Explicit markers over structural matching.** Reaching into visual-tree
  internals by shape (e.g. "any Border with a TranslateTransform") is a
  landmine; anchor with explicit `Tag` markers instead.
- **Predictable interaction semantics.** Transient widgets auto-dismiss on
  fixed durations; the expanded dashboard auto-collapses on idle; pointer over
  the dashboard cancels collapse. Every timer has an explicit purpose.

---

# Architecture Overview

## Major layers and responsibility

```
Win32 / Shell (taskbar, DWM, fullscreen, z-order)
        │  P/Invoke (user32, dwmapi, shell32, psapi)
        ▼
App.xaml.cs  ──  static composition root: creates services, then Window,
                 CompactLayoutController, WindowService. Wiring order matters
                 (IslandController before MainWindow; ApplyDwmAttributes while
                 window is still hidden).
        │
        ├── IslandController      coordinator: widget priority stack, expansion
        │     │                   state, auto-dismiss/collapse timers. Only
        │     │                   caller of WindowService.SetProfile.
        │     ▼
        ├── WindowService         SOLE window-geometry writer: profile
        │     │                   resolution, spring animation, taskbar
        │     │                   docking/anchor, z-order guard, fullscreen
        │     │                   suppression, DWM styling.
        │     ▼
        ├── CompactLayoutController  SOLE compact-geometry authority: measures
        │     │                   the app-strip boundary, feeds IAnchorStrategy,
        │     │                   publishes settled widths.
        │     ▼
        ├── IAnchorStrategy / FixedHomeAnchorStrategy  pure layout logic:
        │     TaskbarSnapshot → AnchorResult (X, width). No measurement here.
        │
        ├── Services*             MediaService, ClipboardService,
        │                         BatteryService, VolumeService, WeatherService,
        │                         BluetoothService, FocusSessionStore — each
        │                         publishes events the IslandController consumes.
        │
        ├── ViewModels            thin MVVM projections (CommunityToolkit).
        │
        └── Views / Widgets / Controls   XAML: MainWindow shell (pill +
                                        dashboard), per-widget UserControls,
                                        WidgetCard + AppIcon reusable controls.
```

## Dependency direction

`App` (composition root) → `IslandController` → `WindowService` /
`CompactLayoutController` → `IAnchorStrategy`. Widgets depend only on
`IIslandWidget` (contract) and services via `App.*` statics. Widgets must not
depend on `WindowService`. ViewModels never touch `WindowService`.

## Data flow

- **Services** own the truth (`MediaService.CurrentState`, `ClipboardService
  .History`, `BatteryService.CurrentState`, `VolumeService.ReadCurrentState()`)
  and raise events (`MediaStateChanged`, `ClipboardChanged`,
  `NotificationRequired`, `WeatherUpdated`, `BluetoothUpdated`).
- **IslandController** subscribes to service events on the UI thread
  (`DispatcherQueue.TryEnqueue`) and mutates the widget stack.
- **MainWindowViewModel** projects controller state (`ActiveControlChanged`,
  `IsExpandedChanged`) into bindable properties for MainWindow XAML. It is a
  read-only projection — it never mutates the controller.
- **Widget ViewModels** (e.g. `MediaWidgetViewModel`) subscribe directly to
  services for live updates and expose `[RelayCommand]`s.

## Event flow

1. OS/service event (e.g. clipboard copy, media session change, volume change,
   battery threshold).
2. Service marshals to UI thread where needed and raises its event.
3. `IslandController` handler enqueues a stack mutation: removes an existing
   widget of the same type, `Push`es a fresh one (or reuses `MediaWidget`),
   with an optional auto-dismiss duration.
4. `Commit()` publishes `ActiveControlChanged`, manages the auto-dismiss timer,
   and calls `ApplyWindowProfile()`.
5. `WindowService.SetProfile(profile)` dedupes on resolved target size and
   runs the spring animation.

## Rendering / geometry flow

- `CompactLayoutController` polls the taskbar every 150 ms, measures the
  app-strip left edge, resolves the anchor via `IAnchorStrategy`, debounces
  (120 ms settle), applies hysteresis (20 DIP), honors pointer-freeze, and
  raises `WidthChanged` only for settled changes.
- `WindowService` animates `width`/`height` via two `SpringSimulation`s driven
  by `CompositionTarget.Rendering`. **X is never animated** — it is stateless,
  derived every frame from `CompactLayoutController.HaloX` via
  `GetAnchoredPosition()` (so no animation can restore a stale X). The bottom
  edge never moves; growth lifts the window above the taskbar.
- `ApplyGeometry` is the single funnel for every `MoveAndResize`; a `[MOVE]`
  log line makes any geometry write that bypasses the anchor immediately
  visible.

## Key architectural assumptions

- The taskbar is at the **bottom** of the **primary** monitor; secondary
  monitors are out of scope (`CompactLayoutController` docstring).
- Start/Search in icon mode expose **no HWND**, so the only reliable,
  measurable boundary is the app-button strip's left edge
  (`MSTaskSwWClass.Left == ReBarWindow32.Left` in the current layout).
- The pill anchors at **X = 0** (screen left edge) and never moves; only width
  responds to crowding.
- The app is **dark-theme-only and never switches themes** (no
  `RequestedTheme`/`ElementTheme` usage). Concrete dark `SolidColorBrush`
  values in `Tokens.xaml` are therefore safe; a `ThemeResource`-alias approach
  was tried and throws at runtime (see Tokens.xaml comments).
- WinUI/Windows App SDK behaviors observed: WinEventHook delivery to the XAML
  dispatcher is not guaranteed (hence the 150 ms z-order guard timer);
  synthetic input is unreliable against WinUI controls (hence mandatory manual
  verification for migration PRs).

## Frozen (decided) architecture

- Pill permanently docked at taskbar left edge, X=0, no drag/free-positioning.
- `WindowService` is the sole geometry mutator; `CompactLayoutController` the
  sole compact-width authority; `IslandController` the only SetProfile caller.
- Compact→compact widget switches never resize the window (stateless compact
  geometry).
- Fullscreen suppression hides the whole window (so acrylic is removed).
- Focus card main↔settings swap is an in-place Visibility swap inside
  `WidgetCard` slots — **not** the overlay slot (deliberate, per PR 1).
- The `OneTime` `ItemsSource` rebind trick in Focus settings save is load-bearing
  and must survive verbatim.

---

# Project Structure

```
DynamicIsland.csproj      Single WinUI 3 project; unpackaged; net10.0-windows10.0.26100.0
App.xaml / App.xaml.cs    Composition root; merges resource dictionaries; global exception hooks
app.manifest              Application manifest
Package.appxmanifest      Packaging manifest (unused for unpackaged run)
README.md                 User-facing status/usage doc
currentstatus.md          HISTORICAL status snapshot (stale — see Lessons Learned)
PROJECT_CONTEXT.md        This file
*-Migration-PR*.md        Per-PR migration validation logs (PR 1 Focus, PR 2 Clipboard)
04-WidgetCard-Validation.md  Live validation log for the WidgetCard shell
build.log / msbuild_diag.log  Build diagnostics artifacts (not source)

Controls/     Reusable UI controls. AppIcon (icon component + AppIcons.cs geometry
              registry + AppIconKind enum), WidgetCard (shared card shell).
              AppIcons.cs is GENERATED — do not hand-edit; source generator script
              (gen_icons.py) is MISSING from the repo (known debt).
Helpers/      Stateless utilities & immutable snapshots: Logger, SpringSimulation,
              MotionConfig, WindowProfile, WidgetPriority, NotificationDuration,
              converters (ClipboardImagePathConverter, FocusProgressToArcConverter,
              NullToVisibility, PlayPauseIcon, etc.), persistence stores
              (ClipboardHistoryStore, FocusSessionStore is in Services/),
              state records (MediaState, BatteryState, VolumeState).
              PERSISTENCE IS SPLIT INCONSISTENTLY: FocusSessionStore lives in
              Services/, ClipboardHistoryStore in Helpers/. Both write to
              %LOCALAPPDATA%\DynamicIsland.
Interfaces/   IIslandWidget — the widget lifecycle contract.
Models/       FocusSession (JSON-persisted model).
Services/     IslandController, WindowService, CompactLayoutController,
              IAnchorStrategy/TaskbarSnapshot/FixedHomeAnchorStrategy,
              MediaService, ClipboardService, BatteryService, VolumeService,
              WeatherService, BluetoothService, FocusSessionStore.
ViewModels/   MainWindowViewModel, MediaWidgetViewModel, ClipboardWidgetViewModel,
              BatteryWidgetViewModel, VolumeWidgetViewModel, MainPageViewModel
              (dead/unused — debt).
Views/        MainWindow — the taskbar widget shell window (pill + dashboard).
Widgets/      Per-widget UserControls: MediaWidget, ClipboardWidget, BatteryWidget,
              VolumeWidget, WeatherCollapsedWidget, PomodoroTimerWidget (dead stub),
              ExpandedDashboard (the click-to-expand dashboard), plus model types
              TaskItem, StashedFile.
Resources/    ThemeResources.xaml (theme brushes + accent), Tokens.xaml (design tokens).
Assets/       App icons (logos, splash).
Properties/   launchSettings.json, publish profiles.
```

What belongs where: a service publishes system/domain events; a ViewModel
projects state for binding; a Widget renders one concern; `Controls` holds
reusable chrome; `Helpers` holds pure logic/records that several layers share.
What does not belong: widget-specific logic in `Controls`; raw
`WindowService` calls in widgets; window-geometry math outside
`CompactLayoutController`/`WindowService`; feature logic in `MainWindow`.

---

# Important Components

## App (App.xaml.cs) — composition root

- **Purpose:** create every service exactly once and wire startup order so the
  first visible frame is fully styled.
- **Responsibilities:** static singletons (`App.MediaService`, …,
  `App.IslandController`, `App.WindowService`, `App.CompactLayoutController`,
  `App.DispatcherQueue`); global unhandled-exception hooks; startup sequencing.
- **Life cycle:** one `App` per process; services live for the process lifetime.
- **Design rationale:** static service locator is pragmatic for a single-window
  app and is used pervasively (`App.X` from widgets/viewmodels). Startup order
  is load-bearing and documented inline:
  1. Capture `DispatcherQueue` first (before any async).
  2. Initialize services; **create `IslandController` before `MainWindow`**
     (its ViewModel subscribes to `ActiveControlChanged` in its constructor).
  3. Create `MainWindow`, then `CompactLayoutController`, then `WindowService`
     (WindowService consumes the layout controller passively).
  4. **`ApplyDwmAttributes` while the window is still hidden** so the first
     present is already borderless/toolwindow/owner-anchored — otherwise a
     default-styled opaque first frame flashes.
  5. `CompactLayoutController.Start()` BEFORE first placement so the pill is
     anchored in the free zone, not at x=0.
  6. `WindowService.InitializeWindow(...)`, wire fullscreen events, `Activate`.
- **Known limitation:** `AppDomain.CurrentDomain.UnhandledException`,
  `UnhandledException`, and `UnobservedTaskException` hooks all **swallow**
  crashes after logging (`e.Handled = true`). Intentional for a widget app, but
  it masks bugs; see Known Issues.

## IslandController — widget-stack coordinator

- **Purpose:** the single brain deciding which widget is visible and at what
  window profile.
- **Responsibilities:** maintain the priority-sorted widget stack; publish
  `ActiveControlChanged` / `IsExpandedChanged`; manage the click-expansion
  state, hover-based auto-collapse, and per-widget auto-dismiss timers; lazily
  create `MediaWidget` and `ExpandedDashboard`; create transient
  `ClipboardWidget`/`BatteryWidget`/`VolumeWidget` per event; be the **only**
  caller of `WindowService.SetProfile`.
- **Public behavior:** `NotifyIslandClick`, `NotifyMouseEnter`,
  `NotifyMouseLeave`, `NotifyFocusLost`, `Dismiss<T>()`,
  `DismissClipboard()`, `ApplyWindowProfile()`.
- **Internal behavior:** `Push` (suspend top, add, sort desc by priority,
  activate, commit), `Pop` (deactivate, resume new top, commit). `Commit`
  publishes the active control and re-arms timers. `ApplyWindowProfile`
  dedupes inside `SetProfile`.
- **Stack rules:** index 0 is highest priority. `WidgetPriority`:
  Media=10, Battery=15, Volume=18, Clipboard=20, Alert=30. The default ambient
  widget (WeatherCollapsedWidget, priority `Default`=0) is pushed first in the
  constructor and sits at the bottom.
- **Interaction model:** click toggles expansion and arms a 6 s auto-collapse;
  hovering cancels it; leaving re-arms 2 s; focus loss collapses immediately.
- **Known limitation:** `_mouseIsOver`/`IsExpandedChanged`/`Dashboard`
  ownership is split between IslandController and MainWindowViewModel (the VM
  creates a second `ExpandedDashboard` when expansion first fires — see
  MainWindowViewModel). Duplicate instantiation is possible (see Known Issues).

## WindowService — sole window-geometry writer

- **Purpose:** owns every pixel the window occupies: borderless/toolwindow/DWM
  styling, taskbar docking, spring size animation, z-order, fullscreen
  suppression.
- **Key invariants (documented in-code):**
  - Window is permanently docked in the taskbar's free zone; X and width come
    from `CompactLayoutController` (`HaloX`/`HaloWidth`), never from a
    screen-relative constant.
  - No drag, no free positioning, no inertia (`StartDrag/UpdateDrag/EndDrag`
    are permanent no-ops).
  - Z-order re-asserted by a 150 ms `HWND_TOPMOST` guard timer (WinEventHook
    delivery is unreliable; ~1 µs/call cost).
  - `WS_EX_TOOLWINDOW` hides from Alt+Tab/taskbar; `WS_EX_NOACTIVATE` prevents
    focus steal.
- **Animation:** two `SpringSimulation`s (width, height) driven by
  `CompositionTarget.Rendering` with dt clamping (0.03 s) and settle snap.
  **X is stateless** — `GetAnchoredPosition` derives it each frame; growth
  progress interpolates between collapsed height and 664 to lift the window
  above the taskbar.
- **Fullscreen suppression:** `SHQueryUserNotificationState` (D3D fullscreen /
  presentation mode) + borderless-window fallback; shell-host windows
  (explorer, StartMenuExperienceHost, SearchHost, Widgets,
  ShellExperienceHost, `XamlExplorerHostIslandWindow`, `Progman`, `WorkerW`)
  are explicitly excluded so Start/Search overlays don't blink the pill. All
  transitions are **debounced 200 ms** against taskbar churn; the window is
  `Hide()`n (not just geometry-hidden) so acrylic is torn down.
- **Profile resolution:** `Collapsed` = controller width × live taskbar height;
  `Expanded` = 800×664 (DIPs). `SetProfile` dedupes on the resolved target so
  compact→compact content changes are no-ops.
- **Life cycle:** created in App.OnLaunched; z-order timer + WinEvent hook are
  cleaned up via finalizer (unhook).
- **Known limitations:** taskbar height detection falls back to 48 DIP; the
  hardcoded expanded size `800×664` sits in three places
  (`ResolveProfileSize`, `GetAnchoredPosition` constant, `WindowProfile`
  extensions) and can drift.

## CompactLayoutController — compact-geometry authority

- **Purpose:** decide how wide the collapsed pill should be and where it sits,
  publishing only genuinely settled changes. "A state machine, not a
  measurement passthrough."
- **Pipeline:** Measure → Validate → Clamp → Hysteresis → Debounce →
  State machine → `WidthChanged`.
- **Measures:** `MSTaskListWClass` / `MSTaskSwWClass` left edge (recursive
  `FindWindowEx` walk, tolerant of Win11 24H2 nesting), converted to DIPs.
  Returns -1 on signal loss.
- **Stability:** 150 ms poll; 120 ms settle debounce (browser-resize-observer
  pattern); 20 DIP hysteresis; pointer-over-taskbar freeze with 150 ms idle
  resume; signal loss freezes last geometry forever (never guess/reset/hide).
- **Anchor strategy:** delegates to `IAnchorStrategy`
  (`FixedHomeAnchorStrategy` default). X = 0 fixed; width =
  `min(ideal 350, max(0, appStripLeft - SafetyMargin 12 - UnmeasuredIconBuffer 40))`.
  The 40 DIP buffer covers Start/Search visuals that are not reliably
  measurable (icon mode exposes no HWND). No lower floor → honest degradation.
- **Design tokens:** `CompactIdealWidth = 350`, `SafetyMargin = 12`,
  `HysteresisDips = 20`, poll 150 ms, settle 120 ms, pointer-idle 150 ms.
- **State:** `Stable / Measuring / Waiting / Animating`
  (`CompactLayoutState`).
- **Known limitations:** primary monitor only; Start/Search width is guessed
  via a constant buffer, not measured.

## IAnchorStrategy / FixedHomeAnchorStrategy / TaskbarSnapshot

- **Purpose:** pure layout logic. `IAnchorStrategy.Resolve(TaskbarSnapshot)`
  returns `AnchorResult(X, MaxAvailableWidth)` in DIPs. Strategies never
  measure — they receive an already-converted snapshot.
- **Design rationale:** swapping a strategy changes anchoring without touching
  the controller, WindowService, or widgets. Two dead strategies were removed
  in commit `2911960`; only `FixedHomeAnchorStrategy` remains.
- **Snapshot:** `TaskbarSnapshot(double AppStripLeft)` — the single stable
  boundary.

## WidgetCard — shared card shell (Controls/)

- **Purpose:** the shared chrome for every dashboard card. Owns border, radius,
  padding, and the Default/Hover/Pressed/Focused interaction states.
- **Public contract:** four content slots — `HeaderContent`, `BodyContent`,
  `FooterContent`, `OverlayContent` — as dependency properties. All visuals are
  built exclusively from `Resources/Tokens.xaml`.
- **Internal behavior:** header row is a fixed 40 px (`GridLength(40)` from a
  spec constant); padding is materialized in code from the `Spacing.L` token
  because WinUI cannot do resource-time Double→Thickness conversion; storyboard
  durations come from the `Motion.Micro` token via `FindName`.
- **Interaction states:** pointer handlers on the root set `_isPointerOver` /
  `_isPressed`; `VisualStateManager.GoToState` picks Pressed > Focused > Hover
  > Default. Raised surface (tint) + raised border (elevation) + focus ring are
  layered Borders toggled by opacity.
- **Design rationale:** a widget earns validation only after real-click runtime
  verification (`04-WidgetCard-Validation.md`). Slots replaced per-widget
  chrome; widget behavior must never leak into the shell.
- **Known limitations / gotchas:** pointer events from child controls (ring
  drag, dot taps, NumberBoxes) **bubble** to the card's root handlers, so the
  Pressed visual fires during interaction — accepted, not suppressed (a
  suppression opt-out would be a shell change, currently out of scope).
  `Elevation.*` styles are placeholders (border-thickness stand-ins) until real
  shadow resources exist.

## MediaService (SMTC)

- **Purpose:** query and control the system media session via
  `GlobalSystemMediaTransportControlsSessionManager`.
- **Key mechanism:** `EffectiveSession` = user pin (`_selectedSession`) else the
  OS current session. A user pin suspends auto-follow; `OnCurrentSessionChanged`
  keeps `_currentSession` fresh but does not re-wire events while pinned.
  `ValidatePinnedSession` (called from `SessionsChanged` and the dashboard's
  1-second `TickValidation`) reverts to auto-follow when the pinned session
  dies.
- **Empty-state handling:** null metadata or a session missing from a fresh
  `GetSessions()` snapshot publishes an empty `MediaState`, which makes
  `IslandController` pop the MediaWidget instead of lingering until the lagging
  `CurrentSessionChanged` event.
- **Sources:** prev/next source cycling with event re-wiring; repeat toggling
  cycles None→List→Track→None.
- **Known limitation:** `SessionTracing = true` — heavy `[SESSION]` diagnostic
  logging is still enabled and must be removed once the session-lifecycle
  tracing is confirmed (documented in-code).

## ClipboardService

- **Purpose:** monitor the system clipboard, keep a persistent multi-item
  history (MRU-first), and support re-copy, pin, delete, retention cleanup.
- **Key mechanisms:**
  - `QueryAsync` serialized by a `SemaphoreSlim` (screenshot tools fire
    `ContentChanged` twice; without the lock duplicates slip in).
  - Busy-clipboard retry on `0x800401D0` (one retry after 100 ms).
  - **Single-shot image streams:** screenshot tools serve their delay-rendered
    bitmap exactly once; a second `OpenReadAsync` throws
    `RPC_S_SERVER_UNAVAILABLE`. Therefore the stream is read **exactly once**
    into bytes; everything downstream works from materialized bytes. Unreadable
    captures are skipped entirely (no blank-thumbnail cards).
  - **Content hash** = SHA-256 of the decoded Bgra8/premultiplied pixel buffer
    (visual content, not encoded bytes), so re-capture dedup works across
    format negotiation.
  - MRU dedup searches the **entire** history (also swallows the service's own
    ReCopy writes, whose `ContentChanged` is delivered async after
    `SetContent`). Re-copying an existing item moves it to top instead of
    duplicating (and avoids orphaned image files).
  - ReCopy for images prefers a fresh file-backed stream; falls back to the
    cached live `ImageStreamRef` only when no file was persisted.
- **Persistence:** `ClipboardHistoryStore` → `%LOCALAPPDATA%\DynamicIsland\
  clipboard\history.json` + `images\` + `settings.json`; image entries store
  absolute file paths; `settings.json` persists the retention period.
- **Retention:** `RetentionDays` (0 = keep forever; pinned items exempt) is
  loaded at startup, applied by a 6-hour cleanup timer, and selectable from the
  clipboard card's retention dropdown — `SetRetentionDays` prunes immediately
  on change. Expired items are removed with their image files.

## BatteryService

- **Purpose:** monitor aggregate battery; fire `NotificationRequired` only on
  meaningful category transitions. Categories: Charging, Discharging, Low
  (≤20%), Critical (≤10%).
- **Suppression rules:** silent on launch (no notification because the app
  started low); fire only on category change; charging/discharging debounced
  800 ms (settled state re-read after window); Low/Critical fire once per
  downward transition; recovery does not fire.
- **State:** `BatteryState(ChargePercent, IsCharging, IsLow, IsCritical)`;
  `IsCharging` treats any external power as charging (many systems report Idle
  near full).
- **Durations:** Critical → 8 s, Low → 6 s, other → 4 s (`NotificationDuration`).

## VolumeService

- **Purpose:** poll system output volume (150 ms) via COM
  (`IMMDeviceEnumerator` → `IAudioEndpointVolume`), firing only on meaningful
  change (mute toggle or ≥1% volume delta). Also exposes `SetVolume`/`SetMute`.
- **P/Invoke-heavy:** hand-declared COM interfaces (`MMDeviceEnumeratorComObject`,
  `IMMDeviceEnumerator`, `IMMDevice`, `IAudioEndpointVolume`). Cleanup releases
  COM objects.
- **Known limitation:** poll-only (no `RegisterControlChangeNotify`); a 150 ms
  poll that writes a volume HUD per change is noisy in the log (volume change
  events are frequent).

## WeatherService

- **Purpose:** fetch current + 3-day forecast from Open-Meteo; 30-minute poll
  (initial fetch + timer).
- **Known issue:** coordinates hardcoded to **New Delhi (28.61, 77.20)** while
  README claims Seattle. AQI is a mock derived from wind/humidity. `ForecastDay`
  records day/icon/temp-range; the first day is labeled "Today".

## BluetoothService (V1 — event-driven rewrite)

- **Purpose:** single source of truth for Bluetooth state: adapter status,
  paired devices (classic + BLE), connection/presence state, and a single
  battery percentage where Windows exposes it.
- **Architecture (frozen):** `BluetoothService` (facade + cache) consumes
  `BluetoothDeviceWatcher` (AEP `DeviceWatcher`, paired filter, both protocol
  GUIDs; translates `DeviceInformation` → domain snapshots, marshals to the UI
  dispatcher) and `BluetoothBatteryService` (GATT 0x180F fallback, on-demand
  only). Domain models live in `Models/Bluetooth/` (`BluetoothDeviceInfo`,
  `BluetoothBatteryInfo`, `BluetoothDeviceType`, `BluetoothConnectionState`,
  `BluetoothAdapterStatus`) — no Windows objects ever reach the UI.
- **Adapter status:** radio-driven (`Radio.GetRadiosAsync` +
  `Radio.StateChanged`) → `NoAdapter / Disabled / Initializing / Ready`; the
  radio toggle also starts/stops the watcher, and unexpected watcher stops
  self-heal through the same path. **No polling.**
- **Battery:** AEP `System.Devices.BatteryLife` arrives with snapshots; GATT
  0x180F is attempted once per session for connected BLE devices with no AEP
  battery. Unavailable battery renders as nothing — never a fake 0.
- **Events:** coarse `BluetoothUpdated` (existing consumers) + granular
  `DeviceChanged`.
- **Dashboard:** the Bluetooth card binds to `App.BluetoothService.Devices`
  with honest empty states (no adapter / off / scanning / no paired devices).
- **Runtime verification pending (hardware tests):** GATT in the unpackaged
  build, `BatteryLife` on real devices, AEP `IsConnected` updates for classic
  devices, Bluetooth off/on lifecycle, startup render after
  `EnumerationCompleted`.

## FocusSessionStore / FocusSession / Focus ring & settings (in ExpandedDashboard)

- **Purpose:** persist named focus sessions to
  `%LOCALAPPDATA%\DynamicIsland\focus_sessions.json`; seed exactly one default
  session (`"Focus"`, 1500 s) on first run. Never throws.
- **Focus UI (lives in ExpandedDashboard code-behind):** ring with arc progress
  (`FocusProgressToArcConverter`), pill-angle readout, ring drag to set duration
  (drag disabled while running), dot switcher, H:M:S NumberBox settings, session
  add/edit/save, all gated while the timer is running. Ring drag persists the
  chosen duration once on release.
- **The `OneTime` rebind trick (load-bearing):** `x:Bind FocusSessions`
  defaults to OneTime, so saving a new session requires
  `FocusDotsControl.ItemsSource = null; FocusDotsControl.ItemsSource =
  _focusSessions;` — verified live that sessions appear without restart. Must
  never be "simplified" away.
- **Duration model:** 1–1440 minutes; shared conversion core
  (`DurationToFraction` / `FractionToDurationSeconds` / `ApplyDurationSeconds`)
  used by both the ring and the settings boxes.

## MainWindow / MainWindowViewModel

- **Purpose:** the taskbar widget shell. Two regions: the collapsed pill
  (Row 1, inside the taskbar) and the expanded dashboard (Row 0, above the
  taskbar). Inverse visibility — the pill vanishes when expanded to avoid a
  transparent click-blocking region.
- **Routing:** left-click → `NotifyIslandClick`; pointer enter/exit →
  `NotifyMouseEnter`/`NotifyMouseLeave`; deactivated → `NotifyFocusLost`.
- **Backdrop:** `DesktopAcrylicController` with `IsInputActive` **forced true**
  so the acrylic never goes solid gray on deactivation. DWM corner rounding is
  also applied.
- **ViewModel:** read-only projection of controller state
  (`ActiveControlChanged` → `ActiveWidget`; `IsExpandedChanged` →
  `IsExpanded`/`ExpandedVisibility`/`PillVisibility`; `Dashboard` created on
  first expansion). `SyncFromController` pulls the initial control published
  before subscription.
- **Known issue:** `Dashboard` is created by the VM on first expansion AND by
  `IslandController.Commit()` (`_dashboardWidget`) — two instantiations of
  `ExpandedDashboard` exist conceptually (one is discarded). See Known Issues.

## Transient widgets (Media, Clipboard, Battery, Volume, Weather)

- Each implements `IIslandWidget`. `MediaWidget` is persistent (pushed on media
  start, popped on empty state) and handles its own collapsed/expanded visual
  states. Clipboard/Battery/Volume are created fresh per event with an
  auto-dismiss duration and removed on `IslandController` dismiss timers.
  `WeatherCollapsedWidget` is the ambient default (priority 0) — bound to
  `WeatherService` (temp/condition/icon refresh via `WeatherUpdated`; neutral
  "—/Unavailable" when the service has no data).

## ExpandedDashboard — the dashboard (Widgets/, 1214-line code-behind)

- **Purpose:** the click-to-expand dashboard hosting all cards.
- **Current XAML renders:** Bluetooth devices card (WidgetCard, data-bound to
  App.BluetoothService), Focus Session card (WidgetCard), Clipboard card
  (WidgetCard, search + retention), Now Playing section, footer strip with live
  CPU/RAM/DISK stats + weather + settings icon. Window size 780×640 in XAML vs.
  800×664 applied by WindowService.
- **Code-behind is LARGELY OUT OF SYNC with the XAML:** it still contains full
  logic for stats (real RAM via `GetPerformanceInfo`, simulated CPU/GPU,
  battery, real storage via `DriveInfo`), weather properties, tasks, and file
  shelf — but the XAML no longer renders Tasks, File Shelf, Weather, or real
  Stats (dummy `CpuGraphLine`/`GpuGraphLine` Polyline elements exist only to
  satisfy code-behind references). This is the single biggest known debt.
- **Focus ring geometry + drag math** and the **OneTime rebind trick** are
  hard-won, verified, and explicitly "do not touch" per PR 1.

---

# Feature Documentation

## Collapsed pill + taskbar width-awareness

- **Purpose:** glanceable taskbar presence that yields space when crowded.
- **UX:** the pill occupies the free zone at the left edge; when apps crowd the
  taskbar, the pill shrinks smoothly (spring animation). Width tiers were a
  *former* feature (320/250/170 DIP with content fading); the current design
  adapts width continuously to `min(available, ideal)`.
- **Architecture:** `CompactLayoutController` → `FixedHomeAnchorStrategy` →
  `WindowService` spring animation. (README still describes the old fixed-tier
  content fade behavior — README drift, see Known Issues.)
- **Edge cases:** signal loss freezes geometry; pointer over taskbar freezes
  geometry; hysteresis prevents flicker; no lower width floor.
- **Status:** ✅ working (committed).

## Hover/click expand + auto-collapse

- **UX:** hover cancels auto-collapse; click toggles expansion; idle (6 s) or
  focus loss collapses; the expanded dashboard lifts above the taskbar so it
  never blocks taskbar clicks; the pill hides while expanded.
- **Architecture:** `IslandController` timers + `NotifyMouseEnter/Leave/FocusLost`
  + `MainWindowViewModel` visibility projections + `WindowService` anchoring.
- **Status:** ✅ working.

## Fullscreen suppression

- **UX:** the whole window (pill) disappears in fullscreen video/games and
  returns on exit.
- **Architecture:** 200 ms-debounced detection in `WindowService.ForceAboveTaskbar`
  (D3D fullscreen via `SHQueryUserNotificationState`, borderless-window
  fallback with shell-host exclusions); `AppWindow.Hide/Show` so acrylic is
  fully removed.
- **Status:** ✅ working.

## Media pill (persistent widget)

- **Purpose:** show and control the active media session from the taskbar.
- **UX:** album art, title, artist, 5-bar waveform (static visuals; the
  animation tick is now a no-op), prev/play-pause/next; expanded dashboard has
  seek slider, source switcher, repeat, volume, mute.
- **Architecture:** `MediaService` → `MediaWidgetViewModel` (live updates) →
  `MediaWidget.xaml`; `IslandController` manages stack presence.
- **Edge cases:** empty state pops the widget; pinned source validation; dead
  sessions detected via snapshot re-check.
- **Known bugs:** `MediaWidgetViewModel.LoadThumbnailAsync` still has `[DEBUG]`
  logging; `SessionTracing` still on.
- **Status:** ✅ working.

## Clipboard transient pill + history card

- **Purpose:** surface the last copied item as a transient pill; keep a
  persistent searchable history in the dashboard.
- **UX:** pill appears on copy (2 s transient), hover-expandable to a preview
  whose **width matches the compact pill**; the big pill is cursor-driven (no
  countdown — stays while the cursor is present, collapses on leave). Re-copy
  shows a 2 s "• Copied" confirmation; Delete removes the entry from history;
  Dismiss/Delete/Re-copy all collapse the window back to compact. Dashboard card
  shows thumbnails/titles with All/Pinned filter, a **search box** (live filter
  over title/full text/file names, with clear button), pin/unpin,
  swipe-to-reveal delete (single-step, no confirmation), tap-to-re-copy, and a
  **retention dropdown** that auto-deletes items older than 7/15/30/90 days or
  keeps everything (persisted; pinned items exempt).
- **Architecture:** `ClipboardService` → `IslandController` → `ClipboardWidget`
  (pill) and `ExpandedDashboard` (history card, inside `WidgetCard` since PR 2).
- **Edge cases:** duplicate suppression (full-history MRU dedup), single-shot
  image streams, busy-clipboard retry, self-copy suppression (ReCopy's own
  `ContentChanged` never spawns a fresh pill), empty state.
- **Known bugs/debt:** `ClipboardWidget.ExpandWidget` still calls
  `App.WindowService.StartSizeAnimation(...)` directly — a **sole-mutator
  violation** (should route through `SetProfile`).
- **Status:** ✅ pill working; ✅ history card (migrated into WidgetCard, PR 2).

## Battery / Volume transient HUDs

- **Purpose:** notify on battery threshold/charging transitions and volume
  changes.
- **UX:** battery expands automatically (`AutoExpand=true`, Expanded profile —
  though the widget's own XAML has Collapsed/Expanded visual states that switch
  on height); volume is a collapsed HUD.
- **Architecture:** services → `NotificationRequired` → IslandController pushes
  a fresh widget with a `NotificationDuration`.
- **Known bug:** BatteryWidget requests `WindowProfile.Expanded` (800×664) —
  the whole dashboard — for a small battery note; likely intended to be a
  compact/small expansion. See Open Questions.
- **Status:** ✅ working.

## Focus Session card

- **Purpose:** Pomodoro-style focus timer with multiple named sessions.
- **UX:** ring with arc progress and pill handle, countdown readout, play/pause/
  reset, dot switcher, settings (name + H:M:S), session persistence. Ring drag
  adjusts duration (disabled while running). All settings interactions gated
  while running.
- **Architecture:** dashboard code-behind + `FocusSessionStore` +
  `FocusProgressToArcConverter` + `WidgetCard` shell (since PR 1).
- **Known edge cases:** `DurationSeconds=0` in persisted data makes reset "do
  nothing" (a data artifact, not a code bug); OneTime rebind trick required for
  new sessions.
- **Status:** ✅ working (migrated into WidgetCard, PR 1). Future: a proper
  FocusEngine was scoped as Phase 3 but not started.

## System Monitoring (stats card)

- **Status:** ✅ FROZEN (renders compactly in the dashboard footer).
- **Implemented:** CPU (real, via `GetSystemTimes`), RAM (`GetPerformanceInfo`),
  Storage (`DriveInfo`) — compact CPU/RAM/DISK readouts in the footer strip
  (moved from the left-column 3-cell card; that slot now hosts the Bluetooth
  devices card).
- **Deferred:** GPU utilization, battery, historical graphs/sparklines,
  per-core metrics.
- **Notes:** GPU is intentionally omitted to preserve UI responsiveness — an
  early WMI probe (`ManagementObjectSearcher`) ran synchronously on the
  dispatcher thread every 3rd tick and caused visible stutter; it was reverted
  from the slice. A dedicated GPU slice may revisit with a background-thread
  update strategy. Battery omitted because the OS taskbar already shows it.
  The old simulated CPU/GPU wobble and dead graph polyline infrastructure were
  removed — no fabricated metrics remain.
- **Scope guardrail:** do not modify except for bug fixes.

## Dashboard sections NOT in XAML (code-behind only)

- Tasks (checklist), File Shelf (drag-and-drop stash), Weather card, Bluetooth
  card — logic exists in `ExpandedDashboard.xaml.cs` but the XAML was
  redesigned without them. Decision needed: restore or prune.
- (Stats sparklines were removed as dead code during System Monitoring; the
  live 3-cell CPU/RAM/DISK card is rendered — see System Monitoring above.)

---

# Technical Decisions (chronological engineering log)

| Date | Decision | Reasoning | Alternatives / rejected |
|------|----------|-----------|-------------------------|
| (early) | Single-project unpackaged WinUI 3 app | Fast iteration, no packaging ceremony; `dotnet run` support via SDK BuildTools.WinApp. | MSIX packaging (not chosen). |
| (early) | Static service composition root on `App` | Single-window app; pervasive `App.X` access is pragmatic. | DI container (not introduced). |
| (early) | `WindowService` = sole geometry mutator; widgets never call it | Prevent layout fights and animation conflicts; one funnel for MoveAndResize. | (enforced as an invariant; one violation remains: ClipboardWidget). |
| (early) | `CompactLayoutController` = sole compact-width authority, state machine with debounce/hysteresis/freeze | "Calm, not nervous" layout; browser-resize-observer pattern. | Direct measurement passthrough (rejected: jitter). |
| (early) | Pill anchored at X=0, only width adapts | Fixed home position is simplest and most stable. | Adaptive X anchoring strategies (dead code removed in `2911960`). |
| (early) | Spring physics for size animation; X stateless per frame | Smooth, settles predictably; stateless X can never restore a stale position. | Linear/cubic easings (not chosen); animated X (rejected). |
| (early) | Fullscreen suppression hides window entirely | Removes acrylic surface; avoids semi-transparent ghosts. | Geometry-only hide (rejected). |
| 2026-08-07 | `WidgetCard` shared shell with 4 content slots; widget-specific logic must not leak in | Shared chrome, isolated behavior; validated per widget with real clicks. | Per-widget chrome (rejected); `WidgetCard.Validation.md` (file never existed — validation logs live in `*-Migration-PR*.md`). |
| 2026-08-07 | PR 1: Focus card chrome → WidgetCard slots; **main/settings swap stays an in-place Visibility swap, not the Overlay slot** | Preserve behavior exactly; overlay slot not needed. | Overlay slot conversion (rejected, PR 1). |
| 2026-08-07 | PR 2: Clipboard card → WidgetCard slots; `GetRevealTargets` hardened with explicit `Tag="ClipboardFrontCard"` | Structural matching would be unsafe once nested in the shared shell. | — |
| (recent) | `FixedHomeAnchorStrategy` single anchor strategy; removed dead strategies | Simplest correct anchor; swap-by-interface kept for future. | Multiple strategies (reduced to one). |
| (recent) | Fullscreen detection excludes shell-host processes + 200 ms debounce | Otherwise Start/Search/taskbar overlays blink the pill. | — |
| (recent) | Focus duration ring drag uses shortest-path delta across the 0/2π seam | Crossing the ring top must not teleport the handle. | Raw angle delta (rejected: jumps). |
| 2026-08-07 | "06-ExpandedDashboard-Discrepancy-and-Risk-Report" reviewed against actual files and resolved for Phase 2 | That report was written from a mismatched pair of pasted files (a slider-based settings UX that the committed XAML never had) and explicitly gated Phase 2 pending confirmation. Confirmed against real code: the H:M:S `NumberBox` trio IS the current settings input (wired to `FocusSettingsNumberBox_ValueChanged`, `ExpandedDashboard.xaml:183-194`/`.cs:1051`); `FocusSettingsHeaderText` and `CurrentSessionName` ARE bound in XAML (`:117`, `:129`). No slider exists. The one real residual it flagged — `GetRevealTargets` needing an explicit marker before Clipboard nested into WidgetCard — is already shipped (`Tag="ClipboardFrontCard"`, PR 2). Two minor items still open: `CpuGraphLine`/`GpuGraphLine` null-check asymmetry (dead code) and the orphaned Stats/Weather/Tasks/FileShelf props (#1). **Conclusion: nothing blocks starting further WidgetCard migration.** | Comparing against the wrong snapshot (rejected); trusting the process-report over the repo (rejected). |
| 2026-08-08 | Location resolution + **manual city override wins** (`LocationService` Geolocator→IP `ipwho.is`→last-known; manual `IsManual` flag persisted in `location.json`; override UI = `LocationSettingsPopup` city search via Open-Meteo geocoding) | A Belagavi user is IP-geolocated to Mumbai (~700 km off) — auto-geo is too coarse for a real temp. Manual override is authoritative; `LoadLastKnown()` restores the flag on restart. | Hardcoded New Delhi (rejected: wrong for everyone except there); IP-only (rejected: too coarse). |
| 2026-08-08 | Click-expansion toggle no-ops while already expanded | The root click toggle (:53) is now a no-op when expanded (`IslandController.NotifyIslandClick` gates on `IsExpanded`), so stray presses inside the dashboard (NumberBox spins, text fields, cards — none of which suppress the bubbled press) can never collapse it mid-interaction. Hold-awake could not stop this path (separate code path, never gated). | Per-element hop suppression (rejected: whack-a-mole); expanded-clicks still collapse (rejected). |
| 2026-08-08 | Text input via temporary lift of `WS_EX_NOACTIVATE` (`WindowService.SetTextInputActive` + `App.Window.Activate()` while a field has focus) | `WS_EX_NOACTIVATE` windows never receive keyboard focus, so Focus H:M:S and location search TextBoxes/NumberBoxes could not be typed into. Lift on `GotFocus`, restore on `LostFocus`. | Remove the flag permanently (rejected: steals focus from the user's app); rely on popups (rejected: the dashboard fields live in the dock window). |
| 2026-08-08 | Click-outside-to-close via a low-level `WH_MOUSE_LL` hook (`MouseClickedOutside` → `IslandController.NotifyFocusLost()`) | The dock is `WS_EX_NOACTIVATE`, so it is never foreground — clicking back into the previous app never raises a foreground change, making a foreground hook unreliable. The mouse hook hit-tests each press against the dock rect; awake-hold guards settings surfaces. | Foreground-change event (rejected: misses the common case); auto-collapse-only (rejected: UX). |
| 2026-08-08 | Survive-awake while settings surfaces are open: `BeginAwake()`/`EndAwake()` hold counter suppresses every auto-collapse path until released; any actual collapse resets the hold | The gear `Flyout` is a separate HWND — entering it fires `PointerExited` → 2s collapse while editing; no surface kept the island alive. One hold counter gates `NotifyMouseLeave` arming, the auto-collapse tick, and the click-outside path uniformly. | Per-surface timers (rejected: fragile); setting a flag only on the flyout open (rejected: missed the Focus H:M:S in-place surface and the hook path). |
| 2026-08-08 | Click-outside must not collapse on the press that *opens* a surface — `NotifyFocusLost()` also returns while `_mouseIsOver` | `WH_MOUSE_LL` fires on `WM_LBUTTONDOWN`, *before* the control's `Click` handler runs `BeginAwake()`, and hit-tests against the dock's current (still-animating/small) rect — so the gear press could be classified "outside" and collapse the island instantly. A press while the pointer is over the dock is never a dismissal. | Relocating the hold into the click handler (rejected: runs after the hook); press-rect delay hack (rejected). |
| 2026-08-08 | Clipboard big pill is cursor-driven only — **no auto-dismiss countdown while expanded** | `OnPointerExited` re-armed a 3 s `Brief` timer even for the expanded preview, so the big pill could time out mid-interaction. Now the expanded state holds while the cursor is present and only collapses on leave (600 ms grace); the 2 s transient applies to the small pill only. | Timer while expanded (rejected); immediate collapse on exit (rejected: cursor can't reach the action ribbon). |
| 2026-08-08 | Re-copy shows a 2 s "Copied" confirmation instead of dismissing instantly, and never spawns a fresh pill | ReCopy's own `SetContent` fires `ContentChanged` async → dedup re-pushed a new transient pill, interrupting feedback. `ClipboardService` stamps `_selfCopyUtc` at `SetContent`; `QueryAsync` skips `ClipboardChanged` for a match within 1.5 s (self-healing — a real capture is never suppressed). The widget collapses to the small "• Copied" pill for `NotificationDuration.Short` (2 s) via `ShowCopiedConfirmation`, then pops. | Suppress-forever flag (rejected: can stick and swallow real captures); immediate dismiss (rejected: no feedback). |
| 2026-08-08 | Delete button = **remove entry from history** (OS clipboard untouched) | Users expected the trash icon to delete the captured item, not wipe the OS clipboard. `ClearCommand` → `DeleteCommand` → `ClipboardService.RemoveFromHistory` (removes from `History`, deletes the persisted image file, nulls `CurrentItem` if it matched). | Clear the OS clipboard (rejected: surprising, and history entry lingered). |
| 2026-08-08 | Dismiss / Delete / Re-copy always collapse the window back to compact | The legacy preview resized the window via raw `StartSizeAnimation`, so popping the widget left the window stuck at the expanded size showing the media/weather pill. `ClipboardWidget.OnDeactivated()` now restores the compact geometry (and the expanded width uses the live compact width so the big pill matches the collapsed pill). | Rely on `SetProfile` in `Commit` (rejected: dedupe no-ops can leave the legacy target stale). |
| 2026-08-07 | Dashboard reconciliation verdicts (STEP 1 investigation): **restore Stats, Weather, Quick Tasks; prune File Shelf** | All four orphaned features were traced in `ExpandedDashboard.xaml.cs`. Stats (real RAM/battery/storage, simulated CPU/GPU) and Weather (fully service-wired) are live-but-invisible → pure-XAML restore. Tasks handlers are complete, never rendered → cheap restore. File Shelf is a power-user duplicate of Explorer/Clipboard with zero persistence and mock seed → weakest value/effort, prune. Bluetooth card already gone from the earlier redesign. (Later superseded: System Monitoring freeze, footer-weather, Quick Tasks deferred, File Shelf pruned.) | Restore File Shelf (rejected: duplicates Explorer, worst value/effort); prune Stats/Weather (rejected: they are live, service-backed data). |

---

# Lessons Learned (never remove these)

1. **`currentstatus.md` went stale.** It was written against an earlier working
   tree (it lists untracked files that are now committed, e.g. `Controls/`,
   `Models/FocusSession.cs`, `FocusSessionStore`, `ClipboardHistoryStore`) and
   predates the WidgetCard PRs (PR 1/PR 2), so its "working-tree status"
   section no longer matches reality. **Lesson:** a status doc must be updated
   in lockstep with commits, or clearly labeled as a historical snapshot.
   Keep this file as the living record; treat `currentstatus.md` as history.

2. **WinUI has no resource-time Double→Thickness conversion.** `RowSpacing`
   consumes `x:Double` tokens, but `Margin="{StaticResource Spacing.S}"`
   throws. Workaround: materialize `Thickness`/`GridLength` from token values
   in code (`WidgetCard.xaml.cs` `Token()` helper), or hardcode the value with
   a comment. (Also: an earlier revision of Tokens used
   `ThemeResource ResourceKey` aliases, which cannot be resolved at runtime by
   consumers and threw at XAML load — concrete `SolidColorBrush` values are
   used instead.)

3. **Splitting a pre-shell card into WidgetCard slots loses row spacing.** The
   shell's slot rows have no spacing. Restore gaps with margins on the **Auto**
   cells (bottom margin on header, top margin on footer), NOT on the star-row
   body — a margin on a vertically-centered element is half-absorbed into the
   centering offset (8 px renders as ~4 px).

4. **Structure-matched code-behind needs an explicit marker before nesting in a
   shell.** `GetRevealTargets` previously matched "any Border with a
   TranslateTransform whose parent Grid has a Button child"; once inside
   WidgetCard it could match the shell's chrome. Fix: `Tag="ClipboardFrontCard"`
   + keep the transform check as an invariant guard. Applies to any reach-into-
   template code.

5. **WidgetCard's Pressed visual state fires during child interaction.** Pointer
   events from ring drags, dot taps, and NumberBoxes bubble to the card's root
   handlers. Expected, not a defect; the ring still renders/drags correctly
   under a flattened card. Not suppressed (would need a shell opt-out API).
   Users confirmed controls still work.

6. **Clipboard bitmap streams are single-shot.** Screenshot tools serve their
   delay-rendered bitmap once; a second read throws `RPC_S_SERVER_UNAVAILABLE`,
   leaving no file and a dead cached stream. Fix: read bytes exactly once,
   persist, hash decoded pixels, and skip unreadable captures entirely.

7. **Duplicate clipboard events race.** Screenshot tools can fire
   `ContentChanged` twice within milliseconds; without a `SemaphoreSlim` both
   calls pass the dedup search before either inserts. Serialize clipboard
   queries.

8. **`x:Bind` ItemsSource defaults to OneTime.** Saving a new focus session
   silently does nothing on restart until you force
   `ItemsSource = null; ItemsSource = list;` (the "OneTime rebind trick").
   Verified live; treat as load-bearing.

9. **Test-data artifacts can look like code bugs.** A hand-crafted focus session
   with `DurationSeconds=0` made the timer read 00:00 and reset "do nothing" —
   a data artifact, not a bug. The reset handler was correct; the data was
   fixed to 1860.

10. **Synthetic input is unreliable against WinUI controls.** All migration
    verification is done with real clicks and human observation. Screenshot
    side-by-side capture also proved hostile to synthetic tooling — direct
    human verification is preferred.

11. **Apply DWM styling before the first show.** Running `ApplyDwmAttributes`
    after `Activate()` caused a default-styled opaque first frame (black
    flash). Style while hidden, then present.

12. **WinEventHook delivery is not guaranteed on the XAML dispatcher.** The
    150 ms `HWND_TOPMOST` guard timer is the reliable mechanism to stay above
    the taskbar; the WinEvent hook is a secondary immediate push.

13. **Fullscreen detection flaps.** `IsFullscreenModeActive()` churns for a few
    frames; a 200 ms confirm window prevents hide/show blink. Shell-host
    windows (explorer, Start/Search, Widgets) must be excluded or the pill
    blinks whenever the Start menu opens.

14. **Async `async void` event handlers that reach into the visual tree must
    guard against `null`** (e.g. `UpdateRepeatVisual`, `UpdateFocusDotsVisual`).
    The codebase consistently null-checks; keep doing so.

15. **`WS_EX_NOACTIVATE` makes every text field dead** — a non-activating window
    can never receive keyboard focus, so TextBox/NumberBox inside it can't be
    typed into. To support typing, temporarily clear the flag while a field has
    focus (`GotFocus`/`LostFocus`) and call `Activate()`; the finalizer-style
    concern of the permanent NOACTIVATE still applies normally in the dock's
    non-typing state.

16. **A click-toggle attached to the window root becomes a landmine once child
    content is interactive.** The expanded dashboard lives in the same RootGrid
    as the pill's "click to expand" handler, so any press not sunk by the child
    (NumberBox spins, text input) bubbled to the root and collapsed the island.
    Result anchoring: gate `NotifyIslandClick()` to no-op when already expanded.

17. **A `WS_EX_NOACTIVATE` topmost dock can't detect outside clicks via a
    foreground/activation hook** — its window is never foreground, so clicking
    back into the app you were using produces "no event" for the common case. A
    low-level `WH_MOUSE_LL` hook that hit-tests each press against the dock's
    rect is the reliable signal (guard with the awake-hold so open flyouts are
    safe).

18. **A low-level mouse hook that opens a surface races itself on press-DOWN.**
    The `WH_MOUSE_LL` hook fires during `WM_LBUTTONDOWN`, which is *before* the
    control's `Click` handler runs — so `BeginAwake()` in the click handler is
    always too late to stop that same press from being classified as a click
    outside the dock, and the hook hit-tests against the dock's *current* rect
    (small / mid-expand-animation on first open). Fix: gate `NotifyFocusLost()`
    on `_mouseIsOver` — a press while the pointer is over the dock is never
    "clicked outside."

---

# Current State (always reflect reality)

## Completed / committed (HEAD `02b812c`; this snapshot commits the clipboard interaction fixes)

- ✅ Collapsed pill, acrylic backdrop, taskbar docking, z-order guard.
- ✅ Taskbar width-awareness (adaptive compact width via CompactLayoutController).
- ✅ Fullscreen suppression (debounced, shell-host aware).
- ✅ Hover/click expand with auto-collapse; inverse visibility (pill hidden when
  expanded).
- ✅ Media, Clipboard, Battery, Volume transient/persistent widgets.
- ✅ Focus Session card with ring, drag, dots, settings, persistence.
- ✅ Clipboard history card (in WidgetCard, PR 2) with pin/filter/reveal-delete/
  re-copy.
- ✅ Focus card migrated into WidgetCard shell (PR 1) — zero behavior change,
  verified live.
- ✅ `GetRevealTargets` hardening (`Tag="ClipboardFrontCard"`).
- ✅ Fixed-home anchoring via app-strip boundary; dead anchor strategies removed.
- ✅ Design token system (`Resources/Tokens.xaml`) + `WidgetCard` shell.
- ✅ `AppIcon`/`AppIcons.cs` generated icon registry (but generator script missing).
- ✅ System Monitoring freeze (`7a874dd`): real CPU (`GetSystemTimes`) / RAM
  (`GetPerformanceInfo`) / disk (`DriveInfo`) as a live 3-cell card; simulated
  CPU/GPU & sparkline polyline infra removed.
- ✅ Weather slice — real coordinates + manual override + footer binding
  (committed in this snapshot):
  - `LocationService` (Geolocator → IP `ipwho.is` → last-known, persisted to
    `%LOCALAPPDATA%\DynamicIsland\location.json`); hardcoded New Delhi removed.
  - Manual city override via `LocationSettingsPopup` (toggle + Open-Meteo
    geocoding search); `IsManual` flag persisted & restored.
  - Footer temp/condition bound to `WeatherService`.
- ✅ Interaction fixes (committed in this snapshot): stray presses inside the
  expanded dashboard no longer collapse it; Focus & location text fields accept
  keyboard input (temporary `WS_EX_NOACTIVATE` lift); click-anywhere-outside now
  closes the island immediately (`WH_MOUSE_LL` → `NotifyFocusLost`).
- ✅ Survive-awake for settings surfaces (`_awakeHoldCount` /
  `BeginAwake`/`EndAwake`): the gear flyout and Focus H:M:S editing keep the
  island open — every auto-collapse path (mouse-leave arming, tick, click-outside)
  is gated, and any actual collapse resets the hold.
- ✅ Click-outside race fix: `NotifyFocusLost()` also returns while `_mouseIsOver`,
  so the press that *opens* a surface (it lands outside the still-small/animated
  dock rect before `BeginAwake()` runs) can no longer collapse the island
  instantly. Verified live.
- ✅ Clipboard transient-pill interaction fixes (committed in this snapshot):
  - Big pill is **cursor-driven only** — no auto-dismiss countdown while
    expanded; it stays while the cursor is present and collapses on leave
    (600 ms grace). The 2 s transient (`Short`) applies to the small pill.
  - Re-copy shows a **2 s "• Copied" confirmation** (`ShowCopiedConfirmation`)
    and dismisses; `ClipboardService._selfCopyUtc` suppresses the async
    `ContentChanged` re-push so no fresh pill interrupts it.
  - **Delete removes the entry from history** (`RemoveFromHistory`) — deletes
    the persisted image file, OS clipboard untouched.
  - Dismiss / Delete / Re-copy all collapse the window back to compact
    (`OnDeactivated` restores geometry), and the expanded width now matches the
    live compact pill width.
- ✅ Clipboard search + retention (this session):
  - **Search:** clipboard card search box filters the list live across title,
    full text, and file names (case-insensitive), with a clear button and a
    "No matching items" empty state.
  - **Auto-delete:** retention dropdown (Keep forever / 7 / 15 / 30 / 90 days)
    persists to `clipboard/settings.json` via `ClipboardHistoryStore`;
    `ClipboardService.SetRetentionDays` prunes immediately on change and a
    6-hour `DispatcherTimer` keeps pruning while the app stays open. Pinned
    items stay exempt. Resolves the long-standing "retention never scheduled"
    debt.
- ✅ Dashboard relayout: CPU/RAM/DISK stats moved to the footer strip; the left
  column slot now hosts a **hardcoded Bluetooth devices card** (3 compact sample
  rows: Headphone/Mouse/Bluetooth icons, Connected/Available statuses — real
  BluetoothService wiring is the next step; kept slim so the Focus card below
  is not squeezed).
- ✅ **Bluetooth V1 (event-driven):** replaced the 5-second polling service with
  the frozen architecture — AEP watcher (paired, classic + BLE) + radio
  lifecycle + central cache + GATT battery fallback + platform-independent
  models (`Models/Bluetooth/`); the dashboard card is now data-bound with
  honest empty states and 4 converters. Hardware verification pending (see
  BluetoothService section).

## In progress

- **Bluetooth V1 — implemented, hardware verification pending** (run the
  frozen runtime tests on the laptop: GATT unpackaged, `BatteryLife` on the
  real devices, AEP `IsConnected` toggle, Bluetooth off/on, startup render).
- Next slice after that: **Quick Tasks persistence** (tasks reset each launch).
- Repo is still ahead of origin (Focus/Clipboard/anchors/System Monitoring/
  Weather + this snapshot) — push is pending.

## Blocked / deferred

- ExpandedDashboard reconciliation: fully realized via System Monitoring freeze
  (stats), footer+override weather, and (next) Quick Tasks persistence; File
  Shelf pruned.
- FocusEngine (Phase 3) — scoped, not started.
- Clipboard delete-confirmation UX (single-step vs two-step discrepancy parked).
- `UpdateFilterVisual` → XAML visual states / WidgetCard Interaction States
  vocabulary (parked).
- PomodoroTimerWidget fate (make it an `IIslandWidget` or delete).

## Abandoned / removed

- Multi-width-tier taskbar content (320/250/170 with content fades) — replaced
  by continuous adaptive width. README still describes the old behavior.
- Alternative anchor strategies (only `FixedHomeAnchorStrategy` remains).
- Drag/free-positioning (`WindowService.StartDrag/UpdateDrag/EndDrag` are
  permanent no-ops).

---

# Active Task

- **Objective:** this snapshot records + commits the **clipboard interaction
  fixes**: big pill is cursor-driven with no countdown, Re-copy shows a 2 s
  "Copied" confirmation without spawning a fresh pill, Delete removes the entry
  from history, and Dismiss/Delete/Re-copy all collapse the window back to
  compact (expanded width now matches the compact pill). Next:
  **Quick Tasks persistence**.
- The ExpandedDashboard reconciliation investigation below (STEP 1) is retained
  as history. Its per-feature verdicts played out as: Stats → the live System
  Monitoring 3-cell card (`7a874dd`); Weather → footer + manual override (this
  slice); Quick Tasks → next slice (persistence); File Shelf → pruned; Bluetooth
  card → absent from the redesign.
- **Investigation verdicts (per-feature, from the "STEP 1 report"):**
  - **Stats — RESTORE.** Fully functional and live but invisible:
    `UpdateStats()` runs every 1 s via `_updateTimer`
    (`ExpandedDashboard.xaml.cs:349-352`) writing real RAM
    (`GetPerformanceInfo`, `:455-466`), real battery (`App.BatteryService`,
    `:479-489`), real storage (`DriveInfo("C")`, `:493-504`), and *simulated*
    CPU/GPU random walks (`:469-476`), plus sparkline points into
    `CpuGraphLine`/`GpuGraphLine`. All 13 backing properties are public + INPC.
    The XAML shows only the static 2×2 placeholder (`:100-105`, nothing bound)
    and the sparklines sit in a Collapsed "dummy" Canvas (`:82-86`). Restore is
    XAML-only: replace the placeholder with live-bound cells and un-collapse the
    sparklines. CPU/GPU % remain simulated walks (wiring existing properties
    only — real measurement is new logic, name-don't-fix).
  - **Weather — RESTORE.** Fully wired and live: ctor subscribes to
    `App.WeatherService.WeatherUpdated` + forces an initial call
    (`:338-341`); `OnWeatherUpdated` (`:363-408`) populates temp/condition/
    icon + 3-day forecast + available/unavailable visibility, all INPC.
    `WeatherService` is real (Open-Meteo, 30-min poll). Zero XAML consumes it;
    the footer hardcodes "31°C" (`:384`). Restore as a card bound to the
    existing properties (incl. available/unavailable states) and fold the
    footer hardcode into it. Flag, don't-fix: New Delhi coordinates.
  - **Quick Tasks — RESTORE (with data flag).** Partially wired, XAML never
    existed. `Tasks` seeded with two mock items (`:331-332`);
    `AddTaskTextBox_KeyDown` (Enter-to-add, `:1127`) and
    `Task_Checked`/`Task_Unchecked` (`:1198-1212`) are complete but no control
    references them. Not even in the initial commit. Restore as a small card
    (TextBox + CheckBox list) wired to the existing handlers — cheap and
    self-contained. Flag, don't-fix: no persistence (tasks reset each launch)
    and the seed mocks ("Review PR #42", "Sync design tokens") — whether to
    drop the seeds is a data call, not logic.
  - **File Shelf — PRUNE.** Full handlers, no XAML ever, mock seed
    (`StashedFiles` + visibilities `:268-271`, drag-over/drop `:1142-1165`,
    click-to-launch `:1167`, delete `:1184`; seed `hero_shot.png` `:335`).
    Weakest fit (duplicates Explorer; Clipboard already covers file captures;
    zero persistence; worst value/effort). Prune = delete the handlers +
    `StashedFiles` + seed + `Widgets/StashedFile.cs` (model used nowhere else).
  - **Bluetooth card:** already pruned by the earlier redesign (old handlers
    gone from code-behind) — consistent dead-code-free state, not part of this
    reconciliation.
  - **Original dashboard (from `091bdde`):** Now Playing + Clipboard / Weather +
    Bluetooth / 4-up stats row. The redesign's actual regressions are exactly
    **Stats (now static placeholders)** and **Weather (gone)**;
    Tasks/FileShelf were never rendered in any commit. Old XAML bindings
    (e.g. `BatteryGlyph`, `WeatherGlyph`) don't exist in current code-behind —
    restore must bind to current properties (`BatteryPercentText`,
    `BatteryStatusText`, `BatteryIconKind`, `WeatherIconKind`, etc.).
    Placeholder says GPU/DISK; original row was RAM/CPU/Battery/Storage — a
    layout decision (dashboard is ~780 wide; lean 4-up matching the original).
- **Approved plan:** restore Stats + Weather + Tasks into XAML (binding to
  existing code-behind, no new logic), prune File Shelf. Execute in small,
  separate commits, one feature per commit.
- **Files involved:** `Widgets/ExpandedDashboard.xaml` (391 lines) and
  `Widgets/ExpandedDashboard.xaml.cs` (1214 lines).
- **Constraints:** preserve the Focus ring/drag math, the OneTime rebind trick,
  and the Clipboard reveal/delete behavior (`Tag="ClipboardFrontCard"`). No
  feature additions; no new geometry/WindowService logic; no window resizing
  (Expanded stays 800×664; dashboard XAML is 780×640).
- **Known risks:** touching Focus code-behind can break the verified drag/rebind
  behaviors; adding XAML sections changes dashboard height/content and
  interacts with the hardcoded window size.

---

# Next Recommended Tasks (prioritized)

1. **Reconcile ExpandedDashboard XAML ↔ code-behind** — **in progress (STEP 2).**
   Investigation (STEP 1) is complete. Decided per-feature: **restore** real
   Stats (live properties already exist: RAM via `GetPerformanceInfo`, battery
   via `BatteryService`, storage via `DriveInfo`, simulated CPU/GPU walks),
   **restore** the Weather card (bound to `App.WeatherService`, folding in the
   hardcoded "31°C" footer), **restore** Quick Tasks (wired to existing
   handlers), **prune** File Shelf (delete handlers + `StashedFiles` +
   `StashedFile.cs`). This is the biggest mismatch and blocks a coherent
   dashboard. Do it in small, validated PRs — one feature per commit.
2. **Push the 4 unpushed commits** (PR 1, PR 2, anchor cleanups) or confirm
   they're intentionally local.
3. **Migrate the remaining dashboard cards (Media, Battery, …) into
   `WidgetCard` slots**, following the `04-WidgetCard-Validation.md` checklist
   (real-click verification per card). Consider migrating Now Playing too, and
   the footer strip.
4. **Resolve the widget-profile model.** `BatteryWidget` requests
   `WindowProfile.Expanded` (the full 800×664 dashboard) for a small
   notification — likely wrong; the Expanded profile has no "small expanded"
   variant. Decide whether transient widgets should have a compact-expanded
   profile.
5. **Fix the sole-mutator violation:** `ClipboardWidget.ExpandWidget` calls
   `StartSizeAnimation(320, 180)` directly; route through
   `WindowService.SetProfile` (or a dedicated small-profile path).
6. **Schedule clipboard retention cleanup** (e.g. daily timer) and add
   Settings UI for `RetentionDays`.
7. **Bind `WeatherCollapsedWidget` to `WeatherService`** and fix the New
   Delhi/Seattle mismatch (move location to settings or auto-detect); surface
   the weather card in the dashboard.
8. **Wire `BluetoothService` to a widget** (or disable the poll) and decide
   `PomodoroTimerWidget`'s fate.
9. **Resolve duplication:** `MainWindowViewModel.Dashboard` vs
   `IslandController._dashboardWidget` both instantiate `ExpandedDashboard`.
   Pick one owner.
10. **Remove `SessionTracing` / `[DEBUG]` logging** once session lifecycle is
    confirmed. Delete dead code (`MainPageViewModel`,
    `WindowProfileExtensions.ToDimensions` stale branch), remove the orphaned
    `SectionBorderStyle`, restore `gen_icons.py`, and update README to match
    the current adaptive-width + dashboard reality.

---

# Frozen Decisions (do not change without explicit instruction)

- **Sole mutators:** WindowService owns geometry; CompactLayoutController owns
  compact width; IslandController owns the widget stack and profile decisions.
  Widgets never call WindowService.
- **Pill home:** X = 0, taskbar-docked, primary monitor, bottom taskbar;
  no drag/free positioning; no Alt+Tab/taskbar presence
  (`WS_EX_TOOLWINDOW`); never steal focus (`WS_EX_NOACTIVATE`).
- **Compact geometry is stateless:** compact→compact widget switches never
  resize the window.
- **X is never animated** (stateless, derived per frame from HaloX).
- **Fullscreen hides the whole window** (acrylic removed), debounced 200 ms,
  shell hosts excluded.
- **Focus main/settings swap is an in-place Visibility swap** inside WidgetCard
  slots — not the overlay slot.
- **The OneTime `ItemsSource` rebind trick survives verbatim.**
- **Ring drag math and the arc converter** (`FocusProgressToArcConverter`,
  `AngleFromFocusRingPoint`, `DurationToFraction`/`FractionToDurationSeconds`)
  are hard-won — do not refactor casually.
- **Migration PR rule:** shell-only, no feature additions, no visual polish;
  anything noticed gets named in the report, not fixed inline.
- **Validation rule:** real-click runtime verification, not just a clean build.

---

# Open Questions

- **Dashboard mismatch:** restore the code-behind-backed sections or prune the
  dead logic? — **RESOLVED (2026-08-07):** restore Stats, Weather, Quick Tasks;
  prune File Shelf. See Active Task / Next Recommended Tasks #1. (Remaining
  sub-decisions: 2×2 vs 4-up stats layout; whether to keep the two mock seed
  tasks.)
- **Battery/transient widget profile:** should a small transient notification
  really open the full 800×664 Expanded profile? Is a "small expanded" profile
  (or overlay HUD) warranted?
- **Delete UX:** single-step delete (current) vs a two-step confirmation (an
  earlier plan described two-step). Parked for its own design conversation.
- **Filter toggle:** `UpdateFilterVisual` uses manual Foreground/FontWeight
  toggling; should it move into XAML visual states or WidgetCard's interaction
  vocabulary?
- **WidgetCard Pressed bubbling:** pointer events from child controls fire the
  card's Pressed state. Accept as-is, or add a shell opt-out API?
- **Weather location:** New Delhi hardcode vs Seattle (README) vs
  auto-detection/settings. AQI is a mock — replace with real data?
- **Window size authority:** 800×664 lives in three places (WindowService,
  GetAnchoredPosition constant, WindowProfile extensions) and the dashboard
  XAML says 780×640. Centralize into one design constant?
- **Taskbar height:** fixed fallback of 48 DIP when detection fails; top- and
  auto-hide taskbars are unsupported. Future scope?

---

# Known Issues

| # | Symptom | Root cause | Workaround | Planned fix | Priority |
|---|---------|-----------|------------|-------------|----------|
| 1 | Quick Tasks card absent from dashboard (handlers exist, never rendered) | XAML section never existed; tasks reset each launch | None | Next slice: Quick Tasks persistence + card | Medium |
| 2 | `ClipboardWidget.ExpandWidget` resizes window to 320×180 directly | Sole-mutator violation (raw `StartSizeAnimation`) | None | Route through `SetProfile` | High |
| 3 | Unhandled exceptions are swallowed (`e.Handled = true`) after logging | Deliberate "attempt to recover" in App.xaml.cs | Check `%LOCALAPPDATA%\DynamicIsland\logs\app.log` | Consider partial crash recovery / dialog for dev builds | Medium |
| 4 | Clipboard history grows unbounded | `CleanupExpiredItems` never scheduled | None | Daily timer + Settings UI | Medium |
| 5 | `WeatherCollapsedWidget` showed hardcoded "28°C Partly Cloudy" | **Resolved** — bound to `WeatherService` (temp/condition/icon), neutral fallback when unavailable | None | Done | Medium → closed |
| 6 | Weather coordinates hardcoded (New Delhi) | **Resolved** — real `LocationService` (auto IP + manual override, `location.json`). Pending: README town example refresh | None | README refresh | Low |
| 7 | `MediaService.SessionTracing` and `[DEBUG]` logs flood app.log | Leftover diagnostics | None | Remove after confirmation | Low |
| 8 | Duplicate `ExpandedDashboard` instantiation path | VM and IslandController both create it | None (only one wins via binding) | Single owner | Low |
| 9 | `PomodoroTimerWidget` is a dead static stub | Never finished; not an `IIslandWidget` | Not registered | Decide/delete | Low |
| 10 | Bluetooth poll runs with no consumer | Feature ahead of UI | None | Wire or disable | Low |
| 11 | `gen_icons.py` missing; `AppIcons.cs` is generated | Generator script not committed | Hand-edit only as last resort | Restore script | Low |
| 12 | README drift (width tiers, dashboard features, Seattle) | README not updated after redesign | None | Refresh README | Low |
| 13 | Dead code: `StartDrag/UpdateDrag/EndDrag`, `MainPageViewModel`, stale `ToDimensions` branch, orphaned `SectionBorderStyle`, dead dashboard handlers | Accumulated | None | Prune | Low |
| 14 | Battery widget opens full 800×664 dashboard for a small note | PreferredProfile = Expanded | None | Small-expanded profile (Open Question) | Medium |

---

# Testing Status

## What has been tested (per PR logs + code state)

- PR 1 (Focus shell): expand, countdown tick, pause/resume, reset→31:00,
  settings swap, H:M:S + rename, **new session visible without restart** (OneTime
  rebind), dot switching. Real clicks, human-verified.
- PR 2 (Clipboard shell): history display, reveal/close/delete (hardened
  GetRevealTargets gate passed standalone first), All/Pinned filter, pin/unpin,
  re-copy, empty state. Real clicks, human-verified.
- The `Tag="ClipboardFrontCard"` change alone was gate-tested before the shell
  move.
- System Monitoring (3-cell CPU/RAM/DISK) verified live; simulated GPU/battery
  dropped after confirmed UI stutter.
- Weather+interaction (real clicks, human-verified): manual city search picks
  a location and the footer temp updates; auto toggle returns location to
  automatic; persisted manual city survives restart (`location.json`); NumberBox
  spin/typing and location search in a **no-collapse** expanded dashboard;
  clicking anywhere outside the dock closes it immediately; auto-collapse
  (leave >2s / idle 6s) still works; open flyout holds the island awake.
- Build is clean (0 errors / 0 warnings) per PR reports.

## What still requires testing / verification

- Media, Battery, and remaining dashboard cards inside the WidgetCard shell
  (checklist has placeholders).
- Any change to Focus ring/drag math, the arc converter, or the OneTime rebind
  must be re-verified live (the exact checks are listed in
  `Focus-Migration-PR1.md`).
- Fullscreen suppression, width-awareness, and auto-collapse were verified in
  earlier sessions; re-verify after dashboard/geometry changes.

## Known edge cases to exercise manually

- Focus session with `DurationSeconds = 0` in persisted JSON (reset appears to
  "do nothing" — data artifact).
- Rapid plug/unplug battery (800 ms debounce), low→critical transitions,
  launch-at-low (must be silent).
- Clipboard capture during active drags; re-copy of images (single-shot stream);
  files whose paths no longer resolve.
- Taskbar signal loss (Explorer restart / auto-hide) — geometry must freeze,
  never hide.
- Fullscreen via browser video (borderless) vs games (D3D) vs Start/Search
  overlays (must NOT hide).
- Crowded taskbar shrinking below 20 DIP hysteresis step (no flicker).

## Manual verification steps (recurring)

Run the app, then: copy text/image/files and confirm the pill + history; change
volume (pill + dashboard slider sync); open a focus session and drive the ring
drag/settings; hover and click the pill to expand/collapse; open a fullscreen
video and confirm the pill hides and returns; open several apps to crowd the
taskbar and watch the width adapt.

---

# Coding Standards

- **Naming:** classes PascalCase; private fields `_camelCase`; enums PascalCase;
  XAML `x:Name` camelCase; XAML element names (e.g. `FocusRingGrid`,
  `ClipboardEmptyText`) descriptive and unique per control.
- **MVVM:** CommunityToolkit.Mvvm `[ObservableProperty]` (partial properties on
  .NET 10) and `[RelayCommand]`. ViewModels are projections; the controller and
  services are the source of truth. Widget ViewModels receive immutable
  snapshot records (`MediaState`, `BatteryState`, `VolumeState`) — "no live
  update path here" is the norm for transient widgets.
- **State ownership:** services own data; `IslandController` owns widget stack +
  expansion; `CompactLayoutController` owns compact geometry; `WindowService`
  owns window geometry; `MainWindowViewModel` is a read-only projection.
- **Threading:** all UI-thread work marshals via `DispatcherQueue.TryEnqueue`.
  Services marshal OS/background events before mutating state or firing events.
  Timers are `DispatcherQueueTimer` (UI thread) except Win32/COM internals.
- **Window-service access:** never call `WindowService` from widgets;
  `IslandController.SetProfile` is the entry point.
- **Error handling:** services catch and log (`Logger.Error`) and fail soft
  (never throw out of event handlers). Stores "never throw." Unhandled
  exceptions are logged then swallowed at the App level.
- **Logging:** static thread-safe `Logger` writes to
  `%LOCALAPPDATA%\DynamicIsland\logs\app.log`. Follow the existing `[TAG]`
  prefixes (`[MOVE]`, `[COMPACT_WIDTH]`, `[SESSION]`, `[CRASH]`, `[ANCHOR-…]`).
- **Animations:** token durations from `Resources/Tokens.xaml` (`Motion.*`);
  spring physics via `SpringSimulation`/`MotionConfig` for geometry; predictable
  settle with snap.
- **Design tokens:** consume `Spacing.*`, `Radius.*`, `Semantic.*`, `Type.*`,
  `Motion.*`, `Icon.*`, `Control.*`. No inline raw values in new `WidgetCard`
  content. Known exception: `x:Double` tokens cannot feed `Thickness` — see
  Lessons Learned #2.
- **Comments:** the codebase uses heavy XML doc + `// ──` section banners.
  Keep the pattern; comment WHY (invariants, load-bearing tricks) not just WHAT.
- **Verification:** real-click runtime verification for UI changes; build clean
  (0 warnings) expected.
- **No hand-editing `AppIcons.cs`** (generated; restore the generator script).

---

# Glossary

- **Pill / capsule:** the collapsed taskbar widget window.
- **Halo Bar:** the product name; code namespace is `DynamicIsland`.
- **Island widget:** an `IIslandWidget` implementation pushed onto the stack.
- **The stack:** IslandController's priority-sorted list of active widgets;
  index 0 is visible.
- **WindowProfile:** `Collapsed` (pill) or `Expanded` (800×664 dashboard).
- **Compact geometry:** width/height of the pill, owned by
  `CompactLayoutController`.
- **Anchor strategy:** pure layout logic resolving `TaskbarSnapshot` →
  `AnchorResult(X, width)`.
- **WidgetCard:** shared card shell (header/body/footer/overlay slots +
  interaction states).
- **Design tokens:** `Resources/Tokens.xaml` — the frozen scale (spacing,
  radius, semantic colors, elevation, type, motion, icon, control sizes).
- **Semantic.* / Elevation.*:** token families in Tokens.xaml (semantic brushes;
  elevation styles are placeholders pending shadows).
- **SMTC:** System Media Transport Controls (`Windows.Media.Control`).
- **EffectiveSession:** the media session controls act on (user pin or current).
- **Auto-dismiss:** transient widget removal after `NotificationDuration`.
- **Auto-collapse:** dashboard collapse after idle/focus loss.
- **Hysteresis:** minimum published width delta (20 DIP) to avoid flicker.
- **Signal loss:** taskbar measurement unavailable → freeze geometry.
- **OneTime rebind trick:** forcing `ItemsSource = null; ItemsSource = list;`
  because `x:Bind` is OneTime.
- **Sole-mutator rule:** only WindowService writes window geometry.
- **MRU dedup:** clipboard duplicate detection across the whole history,
  moving re-copies to the top.
- **RPC_S_SERVER_UNAVAILABLE (0x800706BA):** symptom of re-reading a
  single-shot clipboard image stream.
- **Clipboard busy (0x800401D0):** transient clipboard contention, retried once.
- **`FocusProgressFraction`:** 0→1 elapsed fraction; the ring fills clockwise
  from the top.

---

## If you are a new AI joining this project

You are now the engineer on **Halo Bar** — a Windows 11 taskbar "Dynamic
Island" built with WinUI 3 / .NET 10 / Windows App SDK 1.8. The working tree is
clean at commit `42ebe3b` (4 commits ahead of `origin/main`, unpushed). The app
is fundamentally working: a docked acrylic pill hosts a priority stack of
transient widgets (media, clipboard, battery, volume) and expands on click into
an 800×664 dashboard of cards.

**What is important:** respect the sole-mutator rules (WindowService =
geometry, CompactLayoutController = compact width, IslandController = the stack
and the only caller of SetProfile). Never change the Focus ring drag math, the
arc converter, or the OneTime `ItemsSource` rebind trick. Migrations of card
chrome into the `WidgetCard` shell must be behavior-preserving and verified with
real clicks. Preserve the hard-won lessons in the Lessons Learned section.

**What should not be changed without discussion:** anything listed in Frozen
Decisions — especially the X=0 fixed-home anchoring, stateless compact geometry,
fullscreen-hide behavior, the Focus in-place Visibility swap, and the shell-only
migration rule.

**The next engineering objective:** reconcile `ExpandedDashboard`'s XAML with
its 1214-line code-behind (restore Tasks / File Shelf / Weather / real Stats
cards or prune the dead logic), then continue migrating the remaining cards
(Media, Battery) into `WidgetCard` slots, fixing the ClipboardWidget
sole-mutator violation, and cleaning up the accumulated debt (SessionTracing,
dead code, README drift, missing gen_icons.py). See Next Recommended Tasks for
the prioritized order and Known Issues for the debt inventory.
