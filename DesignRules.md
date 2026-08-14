# Halo Bar — Design Rules

Authoritative design-system reference for the Halo Bar dynamic island.
Every rule below is either **CONFIRMED** (already implemented in V1 and frozen as
source of truth) or **PROPOSED** (required for the V1 REDESIGN). Proposed rules are
binding once a redesign pass lands, not before. This file is the single source of
truth for design decisions; individual XAML files may not silently override it.

---

## 0. Purpose

- One file a developer can open to answer "what are the design rules here?"
- The contract that keeps Halo Bar "glanceable first, interactive second" across
  all future passes.
- The place where design decisions are recorded, so passes stop rediscovering and
  re-arguing them.
- The checklist a pass is validated against before it can merge.

Scope: everything visual — tokens, layout, materials, components, interaction,
animation, content density, accessibility. Out of scope: behavior (drag/drop,
clipboard, Bluetooth, audio, focus session logic), which lives in `currentstatus.md`
and the services.

---

## 1. Design Philosophy

CONFIRMED (from `PROJECT_CONTEXT.md`, validated against the V1 implementation):

1. **Glanceable first, interactive second.** A user must understand the Halo surface
   in a glance; interaction is layered on top, never at its expense.
2. **Calm, predictable animations; never nervous geometry.** The 280/230 ms
   expansion and 120 ms micro-interactions are the language. Nothing bounces, no
   spring physics, no jitter.
3. **Simplicity before cleverness.** The simplest layout that is honest about what
   it shows. Controlled asymmetry is allowed; chaotic asymmetry is not.
4. **Shared chrome, isolated behavior.** One card shell (WidgetCard) owns all card
   chrome; widgets supply only content. Components never reinvent borders, paddings,
   headers, or focus visuals.
5. **Preservation over polish.** Prefer a working, honest, slightly imperfect surface
   over a beautiful but fragile one. Don't change behavior to improve looks.
6. **Sole mutator / single source of truth.** One place owns state; one place owns
   rules. This document is the second one.

PROPOSED (redesign direction, ratified by the owner):

7. **Quiet native glass.** The Halo surface is a translucent dark glass pane over the
   desktop. Wallpaper is intentionally visible; readability never depends on it.
8. **Tinted glass, not white glass.** The Halo surface has exactly **one
   backdrop/acrylic layer**. Surfaces darken toward the wallpaper (outer ≈15% black,
   inner ≈20% black) rather than layering white-alpha fills. Cards are **not**
   independent glass panes: their tint is a translucent surface treatment painted
   over the same single Halo backdrop. The ≈15% / ≈20% figures are **starting
   values**, tuned per wallpaper, not immutable.
9. **Hierarchy by surface, not by border.** Surface tint and opacity express
   hierarchy. Borders are a thin finishing stroke, not a design tool.
10. **Orange is not in the palette.** The orange seen in screenshots is the desktop
    wallpaper. The app palette never uses orange/brown.

CONFIRMED (accent identity, ratified 2026-08-14):

11. **Azure is the restrained interactive accent of Halo Bar — not the dominant
    color of the interface.** The interface still reads primarily as glass + neutral
    surfaces + white/gray typography, with Azure appearing as a controlled
    interaction/state signal (§3.4.1).

---

## 2. Rule Hierarchy

When rules conflict, the **lower-numbered priority wins**: 1 = strongest, 5 = weakest.
A lower number is more general and more binding than a higher one.

| Priority | Rule category | Example |
|---|---|---|
| 1 | Global design rule | "Glanceable first, interactive second" |
| 2 | Semantic token | `Spacing.L`, `Semantic.Text.Primary` |
| 3 | Component rule | WidgetCard shell owns card chrome |
| 4 | Context-specific exception | "Bluetooth row icon is 12 DIP because the label follows" |
| 5 | One-off value | A `Margin` of 7 that exists in one place only |

A **one-off value** is the weakest rule: it is permitted only where the four levels
above cannot express the intent, and only when it carries an inline `<!-- reason -->`
comment or a documented decision-log entry.

A **context-specific exception** (4) is subordinate to semantic tokens (2): it may
deviate only where the token level cannot express the intent, and only when it is
justified against the philosophy (1). If it can be expressed as a token, it becomes
a token.

---

## 3. Design Tokens

Tokens live in `Resources/Tokens.xaml` (the frozen token scale) and
`Resources/ThemeResources.xaml` (theme brushes). They are consumed via
`{StaticResource ...}`. All numbers below are DIP unless stated.

### 3.1 Spacing — `Spacing.*`

CONFIRMED scale (frozen in `Tokens.xaml`):

| Token | Value | Intended use |
|---|---|---|
| `Spacing.XS` | 4 | intra-control gaps, icon-dot gaps, tiny padding |
| `Spacing.S` | 8 | icon↔text gap, control padding, small card gutters |
| `Spacing.M` | 12 | control grouping, card internal section gaps, row spacing |
| `Spacing.L` | 16 | card padding (WidgetCard uses it), outer dashboard padding, card-to-card gap |
| `Spacing.XL` | 24 | section separation, popup padding, large grouping |
| `Spacing.XXL` | 32 | major sections, empty-state breathing room |

Rules:
- Between-token values (6, 7, 10, 14, 18, 22, 40…) are a **level-5 one-off** unless
  promoted to an exception (§9). The WidgetCard header row height (40) is a
  structural constant, not spacing (§5.2).
- A value only earns a new token when used in ≥3 independent places.

### 3.2 Corner Radius — `Radius.*`

CONFIRMED scale (`Tokens.xaml`) plus the structural Halo radius:

| Token | Value | Intended use |
|---|---|---|
| `Radius.Small` | 8 | controls, buttons, thumbnails, small tiles |
| `Radius.Medium` | 12 | popovers/flyouts, small cards, in-card tiles |
| `Radius.Large` | 16 | cards (WidgetCard shell) |
| `Radius.Capsule` | 999 | fully rounded pills/chips |
| RegionRadiusDip | 24 (structural) | the Halo surface region itself — confirmed constant |

Rules:
- **Circular controls** (icon buttons, play buttons) use `radius = height / 2`,
  never an arbitrary value. This is the only sanctioned "extra" radius.
- Radii of 6 (thumbnails, delete strip, badge), 13 (row icons), 15/16/19 (buttons),
  4 (filter toggles) are level-5 values today and are audit findings (§13).
- Do not round everything: hierarchy is expressed by spacing and typography, not by
  progressively rounder corners.

### 3.3 Typography — `Type.*`

CONFIRMED base scale (`Tokens.xaml`):

| Token | Size / Weight | Role |
|---|---|---|
| `Type.Display` | 30 Bold | rare hero moments (session complete), ≥XXL separation above/below |
| `Type.LargeNumber` | 26 Bold | hero values: Focus timer, big percentages |
| `Type.Title` | 16 SemiBold | card titles, primary card content lines |
| `Type.Body` | 13 Normal | default body / list item text |
| `Type.Section` | 12 SemiBold | section titles inside a card |
| `Type.Caption` | 11 Medium | metadata, timestamps, status |
| `Type.Micro` | 10 Medium | tertiary/status text — the smallest body size; **no smaller Micro exists** |

PROPOSED roles (no token today; land when the redesign lands):

| Role | Size / Weight | Usage |
|---|---|---|
| `Type.Secondary` | 12 Normal | secondary/descriptive lines |
| `Type.Label` | 10 SemiBold | uppercase eyebrows/headers with `CharacterSpacing=50` — the confirmed header convention |

Naming clarity:
- `Type.Micro` is **exclusively** the confirmed **10 DIP** token. There is **no
  9 DIP Micro role**; the earlier 9 DIP proposal is withdrawn to avoid ambiguity.
  Tertiary/status text uses `Type.Micro` (10) or `Type.Caption` (11) — no additional
  typography level is created.
- `Type.Label` shares the 10 DIP size with `Type.Micro` but is a distinct role,
  distinguished by weight (SemiBold), case (uppercase), and `CharacterSpacing=50`
  — never by size alone.

Rules:
- Font family: system default (Segoe UI Variable). Do **not** set `FontFamily` unless
  introducing a deliberate display face.
- Numeric hero values: use `Type.LargeNumber`; prefer tabular/stable digit widths
  where the platform allows. "Never below 9 DIP" is reading guidance, not a token —
  the smallest token is `Type.Micro` at 10 DIP.
- Uppercase + `CharacterSpacing=50` is reserved for `Type.Label` eyebrows — the
  confirmed header style ("BLUETOOTH DEVICES", "NOW PLAYING", "FOCUS SESSION").
- Inline `FontSize` in XAML is forbidden where a `Type.*` role applies (§13).

### 3.4 Color & Surfaces — `Semantic.*`

CONFIRMED roles (`Tokens.xaml` / `ThemeResources.xaml`):

| Role | Value | Use |
|---|---|---|
| `Text.Primary` | `#FFFFFFFF` | primary text (brushes: `TextPrimaryBrush`) |
| `Text.Secondary` | `#B3FFFFFF` | secondary text (brush: `TextSecondaryBrush`) |
| `Color.Accent.Primary` | `#5B9CFF` | **the primary interaction accent — Halo Azure — CONFIRMED 2026-08-14 (replaces purple `#FF8B5CF6`)** |
| `Color.Accent.Hover` | `#70AAFF` | hover lift of the accent |
| `Color.Accent.Pressed` | `#4A8FEF` | pressed accent |
| `Color.Accent.Subtle` | Azure @ ~10–12% opacity | faint active-state surface/indicator (NEW) |
| `Surface.Raised` | `#15FFFFFF` | raised fill (hover/detail tiles) |
| `Surface.Glass` | `#08FFFFFF` | faint glass fill — **confirmed token, currently unconsumed** |
| `Border.Default` | `#26FFFFFF` | default 1px stroke |
| `Border.Subtle` | `#14FFFFFF` | faint 1px stroke |
| `State.Success` | `#FF4ADE80` | success |
| `State.Warning` | `#FFFFB900` | warning |
| `State.Danger` | `#FFFF5F52` | error/destructive |
| `State.Muted` | `#59FFFFFF` | muted/disabled fill |
| `IslandBackgroundBrush` | Transparent | Halo surface is transparent so the acrylic shows through — confirmed |

Naming: the design-system roles above use the semantic `Color.*` hierarchy
(`Color.Accent.*`). Implementation naming follows the project's existing token
convention (`Semantic.*`): `Semantic.Accent.Primary`, `Semantic.Accent.Hover`,
`Semantic.Accent.Pressed`, and `Semantic.Accent.Subtle` (the latter is NEW). No
value-based names (e.g. `PurpleBrush`, `BlueBrush`, `HaloBlue`) are permitted.

PROPOSED additions (the redesign's tinted-glass surface model — starting values):

| Role | Value | Use |
|---|---|---|
| `Surface.HaloTint` | black @ ~15% (α≈0x26) | the single Halo backdrop tint — **starting value, tuned per wallpaper, not immutable** |
| `Surface.CardTint` | black @ ~20% (α≈0x33) | translucent card surface treatment **over the same Halo backdrop** — **starting value, not immutable** |
| `Text.Tertiary` | `#7AFFFFFF` | tertiary/placeholder text |
| `Border.Strong` | `#40FFFFFF` | strong divider or drop-target highlight |
| `State.Info` | accent-tinted | informational (migrate Bluetooth scanning, loading) |
| `Surface.Scrim` | `#F41C1E26`-family | near-opaque overlay surface (source: DropHerePopup gradient) |

Rules:
- **Text contrast must never depend on the wallpaper.** Text brushes stay on the
  opaque-white hierarchy above; content sits on inner surfaces (raised/card tints)
  rather than floating directly on wallpaper.
- Two brush families exist today (`TextPrimaryBrush` etc. in `ThemeResources.xaml`
  and `Semantic.*` in `Tokens.xaml`) with **duplicated values**. The redesign unifies
  on one: keep the short brush names (they are the de-facto production system) and
  retire/alias the parallel Semantic text roles (§13, finding 12).
- No new hex literals in XAML. Any color not in this table is a finding, not a rule.
- Orange/brown is not in the palette (§1.10).

### 3.4.1 Primary accent — Azure (CONFIRMED 2026-08-14)

Azure (`#5B9CFF`) is the authoritative primary Halo Bar accent. The values above
are **design-system starting values** — components consume the semantic tokens
(`Color.Accent.*` / `Semantic.Accent.*`), not these exact hex values directly.

**Visual hierarchy (unchanged intent):**

```
DARK GLASS
    ↓
NEUTRAL CONTENT
    ↓
AZURE INTERACTION SIGNAL
```

Azure communicates interaction and state; it must not overpower the glass material.

**Use Azure for:**
- active controls
- selected states
- Focus active/progress states
- interactive highlights
- progress indicators
- important interactive icons
- focus indicators
- subtle active-state surfaces (`Color.Accent.Subtle`)

**Do NOT use Azure as:**
- the main dashboard or Halo surface background
- the card background
- a universal border color
- decorative color on every component
- a replacement for neutral text
- a replacement for semantic status colors

**Glass + Azure relationship:** Halo Bar's identity is translucent dark glass +
environmental wallpaper bleed + a restrained Azure accent. The wallpaper/background
is not part of the theme accent. Azure must stay readable over dark, bright, and
colorful wallpapers and over the translucent Halo and card surfaces — but never by
adding an opaque Azure panel just to guarantee visibility; readability comes from
the glass tint layer (§3.5), not from an accent-colored backing.

**Semantic status colors stay independent:** `State.Success` (green),
`State.Warning` (amber), `State.Danger` (red), `State.Muted` remain as defined and
are **not** replaced by Azure. Azure means interaction/selection/active state/Halo
identity; it never means success/warning/error.

**Purple migration:** purple `#FF8B5CF6` (and its derived `#FFA78BFA` /
`#FF6D28D9`) is **REPLACED** by Azure as the primary accent and is no longer the
primary Halo Bar theme/accent. Any code or theme resource that still references the
purple value is an **implementation cleanup item** to be migrated to
`Color.Accent.*` / `Semantic.Accent.*` in a later pass — not a competing accent
system.

### 3.5 Material & Elevation — `Elevation.*`

CONFIRMED: no real shadows exist. `Elevation.Flat / Raised / Overlay` are
placeholder border-thickness styles (used by WidgetCard for its layered surface +
focus ring). `Shadows.xaml` is referenced in comments but never existed.

CONFIRMED material stack (V1):
- Window-level `DesktopAcrylicController` — `SystemBackdropTheme.Dark`,
  TintColor (20, 20, 20), TintOpacity 0.45, LuminosityOpacity 0.55. This is the
  production default (PASS 38).
- Shaped in-app acrylic fallback (PASS 37): region-clipped, tint (46, 46, 46),
  opacity 0.55 / luminosity 0.35 — A/B tested, kept as fallback path.

PROPOSED elevation hierarchy (redesign, replaces placeholders in intent):

| Level | Rendering | Use |
|---|---|---|
| 0 — flat | no fill, no border | default content rows |
| 1 — separated | `Surface.Raised` fill + `Border.Default` 1px | cards on the surface, hover states |
| 2 — halo | the acrylic surface itself (region radius 24) | the Halo surface — never shadowed |
| 3 — floating | subtle shadow **or** `Surface.Scrim` + 1px stroke | transient overlays (DropHere popup, settings popup) |

Rules:
- Prefer borders + darker fills over shadows. Shadows only for level-3 floating
  overlays, and only if WinUI `ThemeShadow` renders cleanly against acrylic (verify
  per pass); otherwise use `Surface.Scrim` + `Border.Default`.
- Single backdrop: the Halo has exactly **one acrylic/blur layer**. Cards do not
  have independent blur/acrylic; a card's tint is a translucent surface treatment
  over that same backdrop, not another glass pane. The ≈15% / ≈20% tint figures are
  starting values, tuned per wallpaper, not immutable.
- Wallpaper bleed is intentional; a given layout must remain readable over bright,
  dark, colorful, and detailed wallpapers.

### 3.6 Motion — `Motion.*`

CONFIRMED scale (`Tokens.xaml`): Micro 120 / Standard 180 / Emphasized 250 /
Completion 450 ms.

CONFIRMED production motion (behavior — frozen):

| Motion | Duration | Easing | Where |
|---|---|---|---|
| Surface expand | 280 ms | `easeOutCubic`, v0=1.8, velocity-aware retarget | WindowService size animation |
| Surface collapse | 230 ms | `easeOutCubic`, v0=1.8 | WindowService |
| Micro-interactions | 120 ms | per-state | WidgetCard state transitions |
| DropHere popup | 160 ms | CubicEase EaseOut (opacity 0→1, scale 0.92→1, translateY 10→0) | Drop-here affordance |
| Clipboard reveal | 180 ms | ExponentialEase EaseOut (translate X) | front-card slide |
| Battery pulse | 1000 ms loop | Linear, opacity 1.0↔0.6 | charging indicator |

PROPOSED semantic rules (map any new motion onto the confirmed language):

- **EaseOutCubic is the primary easing.** EaseOutExponential is sanctioned for
  quick-settle reveals only. Linear for continuous/looping indicators only.
- **No bounce, no back, no spring.** "Calm, predictable; never nervous geometry."
- Default duration ladder for new motion: ≤60 ms instant toggle → 120 ms
  micro → 180 ms standard → 250 ms emphasized → 280/230 ms surface expansion.
- Entrance translation ≤ 10 DIP; scale only for popups (0.92→1.0); opacity 120–180 ms.
- No staggering beyond 30 ms between siblings; no motion without purpose.
- Reduced-motion: respect the platform setting once supported (currently unhandled —
  deferred, logged in §12).

### 3.7 Iconography — `Icon.*`

CONFIRMED: single glyph set = `Controls/AppIcon.xaml` (filled Fluent paths,
`Stretch=Uniform`, default 24 DIP, `Fill=TextPrimaryBrush`). Icon geometry is code
(`AppIcons.cs`).

PROPOSED size ladder (the current `Icon.*` values 16/20/24 are unconsumed; redefined
to match real usage):

| Token | Size | Use |
|---|---|---|
| `Icon.Small` | 12 | inline eyebrow icons (Bluetooth header), badges |
| `Icon.Default` | 16 | standard control icons |
| `Icon.Large` | 20 | prominent row icons |
| `Icon.Hero` | 24 | primary/card-level icons (AppIcon default) |

Rules:
- One weight family per card: filled glyphs everywhere, never mix filled + outline.
- Icons without adjacent text need a tooltip or an unambiguous universal meaning.
- Icon↔text gap: `Spacing.S` (8); dense rows may use `Spacing.XS` (4).

### 3.8 Control Sizing — `Control.*`

CONFIRMED scale (`Tokens.xaml`): Small 28 / Default 36 / Large 44 — **unconsumed**.

PROPOSED semantic sizing (redefines the unconsumed tokens to match the app's real
control classes):

| Token | Hit area | Visual | Use |
|---|---|---|---|
| `Control.Micro` | 20–24 | ≤32 | inline icon buttons (focus add), shelf slot buttons |
| `Control.Small` | 28 | 28, radius 14 | compact row buttons (search clear) |
| `Control.Default` | 32–36 | 32, radius 16 | primary pill buttons (clipboard copy, battery) |
| `Control.Large` | 38–48 | 38/48 | primary action (play button), shelf open |

Rules:
- Circular icon buttons: `radius = height/2` (§3.2). A 32 DIP button → radius 16,
  a 38 → 19, a 24 → 12. Never write a non-matching radius.
- Buttons must be ≥ 24 DIP hit area with ≥ 8 DIP separation, or the row must widen.

### 3.9 Notification / Auto-dismiss — `NotificationDuration`

CONFIRMED: Short 2 s, Brief 3 s, Standard 4 s, Extended 6 s, Critical 8 s.
Duration is chosen by content severity, not by content length.

### 3.10 Token governance

- **Tokens.xaml / ThemeResources.xaml are the only token files.** No new token files.
- Adding a token: ≥3 independent uses, name in this doc, entry in the decision log.
- Removing/renaming a token: audit + decision-log entry; no silent renames.
- A component may never bypass a token with a literal (§13).

---

## 4. Layout

Two distinct concerns are deliberately separated:

- **Halo Surface** (§4.1) — the window's geometry (envelope, region, growth) and
  its single material (the acrylic backdrop, §3.5). This is the *vessel*.
- **Dashboard composition** (§4.2) — the grid and content arrangement rendered on
  that surface. This is the *content*.

Changing the surface does not require changing the composition, and vice versa.
Material rules live in §3.5; geometry rules live in §4.1; grid rules live in §4.2.

### 4.1 Halo Surface — geometry & material (CONFIRMED — structural, frozen)

- Window envelope: **800 × 664 DIP** (Expanded); compact strip height = taskbar
  height (~48 DIP, adaptive); compact pill width adaptive (ideal 350, band 340–360).
- The envelope grows **upward** from the pill; bottom edge fixed at the taskbar top.
- Region radius **24** on the Halo surface; the surface carries **one** material:
  the window-level acrylic backdrop (§3.5). No additional blur/acrylic layers.
- Dashboard content: **780 × 640** inside the envelope; dashboard `CornerRadius=24`,
  margin `0,0,0,8` above the strip.
- Popup stage heights: file shelf 340, clipboard 180, drop-here popup
  `compactH + 60 + 4`.
- These numbers are the V1 geometry contract. The redesign tunes visuals, not this
  contract.

### 4.2 Dashboard composition / grid (PROPOSED)

- Outer dashboard padding: `Spacing.L` (16) on all sides (today it is an
  inconsistent 8/40/8/8 — audit finding).
- Card gutters: `Spacing.S`–`Spacing.M` (8–12), consistent both axes.
- Main split: **controlled asymmetric two-column composition; the exact ratio is
  finalized during the dashboard composition pass.** Never let cards invent their
  own margins.
- Card roles (PROPOSED shapes, finalized in the composition pass):
  - Focus — square / near-square utility card (large hero value).
  - Bluetooth — compact utility card.
  - Music — horizontal media card (art + waveform + controls in one row).
  - Clipboard — large, information-dense card.
- Relative scale over pixel scale: define cards by role ("compact utility",
  "standard", "media", "dense") not by absolute width.

---

## 5. Components

### 5.1 Component tree (CONFIRMED)

`Halo surface` → `Section` → `Card` → `Control` → `Content`.

### 5.2 WidgetCard shell (CONFIRMED — owns ALL card chrome)

- Four content slots: `HeaderContent`, `BodyContent`, `FooterContent`, `OverlayContent`.
- Four interaction states (see §6).
- Header row: fixed **40 DIP** (structural constant). Header style = eyebrow icon
  (accent, `Icon.Small`) + `Type.Label` (10 SemiBold, uppercase, `CharacterSpacing=50`,
  `TextSecondaryBrush`).
- Padding: `Spacing.L` (16), materialized in code (WinUI cannot convert
  `x:Double`→`Thickness` at resource time — documented limitation).
- Radius: `Radius.Large` (16) for surface/raised borders; focus ring `Radius.Large`.
- State transitions: 120 ms (`Motion.Micro`).
- `IsTabStop=true` on the card; children that are interactive remain focusable.
- Cards must not add their own outer borders, shadows, or background fills.

### 5.3 Known non-shell chrome (audit findings, V1 frozen)

- Now Playing + clipboard front cards use raw `Border Brush=Gray` (1px) — they are
  **not** WidgetCard shells and visually break "shared chrome". Migrate to the shell
  during the redesign (§13, findings 4, 7, 12).

### 5.4 Controls

- Buttons follow §3.8; all fills/borders from `Semantic.*`.
- Loading/spinner: accent at low opacity (`State.Muted`), never a hardcoded color.

---

## 6. Interaction States

CONFIRMED WidgetCard language (layered borders toggled by opacity):

| State | Rendering |
|---|---|
| Default | surface transparent (`Surface.Default`), border 0 |
| Hover | `Surface.Raised` + `Border.Default` 1px |
| Pressed | flattened (surface 0, border 0) |
| Focused | accent ring 2px (`Elevation.Overlay`) |

PROPOSED universal hierarchy (all interactive components):

| State | Language |
|---|---|
| Hover | raise surface tint + border one level |
| Pressed | flatten (confirmed behavior) |
| Focused | accent ring (confirmed) |
| Selected | accent tint or accent icon fill (filter toggles, session dots, pin) |
| Disabled | opacity ≈ 0.4, no color swap |
| Loading | accent pulse at low opacity (tokenize the battery-pulse pattern) |
| Active | accent fill (playing, charging, connected) |
| Error | `State.Danger` on text/icon only — never a background fill |

Rules:
- Prefer tint/opacity/elevation over borders; borders are a finishing stroke.
- Children's pointer events bubble to the card's Pressed visual (confirmed accepted
  behavior) — keep it; it makes cards feel reactive.

---

## 7. Content Density

CONFIRMED: "Glanceable first, interactive second" (§1.1).

PROPOSED rules:
- **One primary value per card.** Focus → the timer; Music → the track; Clipboard →
  the latest item; Bluetooth → connection count/state.
- Primary value at `Type.Title`/`Type.LargeNumber`; metadata at `Type.Caption`;
  tertiary at `Type.Micro`; never expose implementation state verbatim (no "331
  items", no raw adapter status).
- Progressive disclosure: collapsed shows one line, expanded shows detail (the
  existing collapsed→expanded pattern is the model).
- **Every surface needs an empty state** (Scanned "No devices", Clipboard
  "Nothing copied yet", Music "Nothing playing") — consistency gap today.
- If a row needs more than ~2 metadata lines, it is two rows.

---

## 8. Accessibility

CONFIRMED:
- The app is dark-only (light theme dictionaries exist but are never switched).
- Hover is never the only affordance; states also express via focus.

PROPOSED:
- Target ≥ 4.5:1 for text against the nearest surface fill (not the wallpaper —
  text sits on inner surfaces per §3.4).
- Focus must be visible on every focusable element (WidgetCard ring is the model).
- Respect reduced-motion (platform setting) once implemented — logged in §12.
- Do not introduce sound or light as the sole signal.

---

## 9. Exceptions

An exception is a deliberate, documented level-4 override.

Process:
1. Written justification against the philosophy (§1) in the pass report.
2. Recorded in the Decision Log (§12) — never just in a code comment.
3. Reused ≥3 times → promoted to a token instead.

Current standing exceptions:
- **DropHere popup gradient** (`#F41C1E26`→`#F210131A`) and its 24 radius — a
  floating overlay (§3.5 level 3); intended to become `Surface.Scrim`.
- **Focus ring 4px track in Gray / dots LightGray** — V1 accepted; migrate to tokens.
- **PomodoroTimerWidget** — dead stub with hardcoded Gray; decision pending
  (documented; not part of the redesign unless resurrected).

---

## 10. Anti-Patterns

1. **Inconsistent size scales.** Reusing 24/30/32/38 DIP buttons with mixed radii
   (12/15/16/19) instead of the Control ladder (§3.8). (Current state — V1.)
2. **Hardcoded colors** where a token exists (`#E81123`, `Gray`, `#FFFFD700`,
   `#12FFFFFF`, `#22FFFFFF`, `#33FFFFFF`, AQI hex values in code). (Current state — V1.)
3. **Fabricated font sizes** (8, 8.5, 9, 9.5, 10.5, 11.5, 14, 15) outside the
   `Type.*` roles. (Current state — V1.)
4. **Borders as hierarchy.** Outlining rows/cards with hardcoded gray strokes
   instead of surface tint + the confirmed border tokens.
5. **Redundant glass.** Stacking extra blur/acrylic panes inside the Halo surface.
6. **Spring/bounce/back easing.** "Nervous geometry" is forbidden (§3.6).
7. **Silent token drift.** Same value defined twice (the `TextPrimaryBrush` vs
   `Semantic.Text.*` duplication) — unify, don't extend.
8. **Design rules in prose only.** If it isn't in this file or a token, it's not a rule.
9. **Introducing new features during a redesign pass.** The redesign changes visual
   language only (§11).

---

## 11. V1 Redesign Constraints

CONFIRMED working contract (from the owner):

1. **V1 is frozen.** Functionality and behavior are constraints, not targets. The
   redesign does not add features, remove behavior, or fix behavior bugs.
2. **The redesign is not V2.** No new widgets, no new integration surfaces.
3. **No changes to application code during research.** `DesignRules.md` is the only
   deliverable of the research phase.
4. **Pass-based workflow.** Each redesign change lands as a numbered pass, validated
   against this document, and recorded in `currentstatus.md` and the Decision Log.
5. **Main container before cards.** The Halo surface + layout grid land first; cards
   follow. Never redesign a card in isolation.
6. **Preserve good existing rules.** The confirmed tokens, the confirmed motion
   language, the acrylic default, and the WidgetCard shell are the foundation the
   redesign builds on — not the thing being replaced.
7. **Controlled asymmetry.** A controlled asymmetric two-column composition with
   mixed card roles is intentional; the exact split ratio is finalized during the
   dashboard composition pass, and the redesign disciplines the composition rather
   than flattening it.

---

## 12. Decision Log

| # | Date | Decision | Status |
|---|---|---|---|
| 1 | 2026-08-14 | V1 freeze; commit `8f5251e`. Redesign research begins; only `DesignRules.md` created/modified. | CONFIRMED |
| 2 | 2026-08-14 | Purple `#FF8B5CF6` remains the primary interactive accent. | SUPERSEDED — replaced by #8 (Azure) |
| 3 | 2026-08-14 | Quiet native glass model: one backdrop/acrylic layer; card tints are translucent surface treatments over that backdrop; outer Halo tint ≈15% black, inner ≈20% black — starting values, not immutable; wallpaper bleed intentional; readability independent of wallpaper. | PROPOSED |
| 4 | 2026-08-14 | Orange/brown excluded from the palette (wallpaper artifact, not app color). | CONFIRMED |
| 5 | 2026-08-14 | Main container before cards; pass-based delivery; controlled asymmetric two-column composition (exact ratio finalized in the composition pass). | CONFIRMED |
| 6 | 2026-08-14 | Unify the two color families (`TextPrimaryBrush`-style brushes vs `Semantic.Text.*` aliases) onto one during the redesign; `Icon.*`/`Control.*` mapped to real usage and unconsumed `Type.*` tokens mapped to roles (values unchanged). | PROPOSED |
| 7 | 2026-08-14 | Deferred: reduced-motion handling, ThemeShadow validation against acrylic, PomodoroTimerWidget fate. | OPEN |
| 8 | 2026-08-14 | **Azure `#5B9CFF` becomes the primary interaction accent**, replacing purple `#FF8B5CF6` (marked REPLACED — implementation cleanup item). Hover `#70AAFF`, pressed `#4A8FEF`, subtle = Azure @ ~10–12% opacity. Semantic status colors remain independent. Azure is restrained; glass stays dominant. | CONFIRMED |

---

## 13. Appendix — Current-State Audit Findings

Inventory of what the research found that deviates from the confirmed rules. These
are findings, not new rules. They are the redesign's work list. Each entry cites the
category it violates.

1. **Typography sprawl** — 13 distinct inline `FontSize` values (8, 8.5, 9, 9.5, 10,
   10.5, 11, 11.5, 12, 13, 14, 15, 24) in source XAML vs 7 `Type.*` roles; the roles
   are nearly all unconsumed. Violates §3.3.
2. **Header labels hardcoded** — "BLUETOOTH DEVICES" / "FOCUS SESSION" / "NOW
   PLAYING" / "LOCATION" each re-inline `FontSize=10 Bold CS50 TextSecondary` instead
   of a `Type.Label` style. Violates §3.3/§5.2.
3. **Hardcoded colors** — `#12FFFFFF` (bluetooth rows, clipboard tiles),
   `#22FFFFFF` (shelf divider), `#33FFFFFF` (DropHere popup border),
   `#15FFFFFF` (drag handle, tiles) vs confirmed `Surface.Raised`/`Border.*`;
   `#E81123` delete strip; `#FFFFD700` weather/battery amber; AQI colors
   `#10B981`/`#F59E0B`/`#EF4444` in code-behind; converter-returned `Colors.Gray`.
   Violates §3.4.
4. **Gray strokes** — Now Playing `Border Brush=Gray`, clipboard front-card
   `BorderBrush=Gray`, Focus ring track `Gray` / dots `LightGray`, `PomodoroTimerWidget`
   Gray scheme. Violates §3.4/§5.3.
5. **Radius sprawl** — 0.75 (volume bar), 1.25 (waveform), 4 (filter toggle), 6
   (thumbnails/delete/badge), 13 (bluetooth icon), 15/16/19 (buttons), 24 (popup) on
   top of tokens 8/12/16/24. Violates §3.2.
6. **Button-size sprawl** — 24×24/r12 (music pill), 28 (search clear), 30×30/r15
   (battery), 32×32/r16 (card + clipboard), 38×38/r19 (play), 48×32 (shelf).
   `Control.*` tokens unconsumed. Violates §3.8.
7. **Icon-size sprawl** — 12/13/14/16/20 DIP; 16/20 coincide with token values but
   are not consumed via tokens. Violates §3.7.
8. **Spacing tokens barely used** — only 3 `{StaticResource Spacing.*}` references;
   margins/paddings use 2/4/6/7/8/10/12/14/18/22 freely. Violates §3.1.
9. **Opacity-as-data** — stat dots at accent Opacity 0.75/0.5/0.25, "Copied" tag
   0.6, artist 0.7, open-in-new 0.6; battery pulse 1.0↔0.6; no opacity tokens.
   Violates §3.4/§3.6.
10. **Motion mixed** — micro 120 (uses `Motion.Micro`), popup 160 CubicEase,
    reveal 180 ExponentialEase, expand/collapse 280/230 consts; `Motion.Standard/
    Emphasized/Completion` unconsumed; two easing vocabularies. Violates §3.6.
11. **Duplicate color families** — `TextPrimaryBrush` etc. vs `Semantic.Text.*`
    (same values, two names). Violates §3.4/§10.7.
12. **Raw Border chrome** — Now Playing + clipboard cards draw their own 1px gray
    outline instead of the WidgetCard shell. Violates §1.4/§5.2.
13. **Unconsumed tokens** — `Semantic.Surface.Glass`, `Icon.*`, `Control.*`,
    `Motion.Standard|Emphasized|Completion`, `Type.*` (nearly all),
    `IslandBorderBrush` (0 refs). Violates §3.x governance.
14. **Dual theme dictionaries** — Light + Dark defined, app never switches.
    (Low risk; document intent rather than delete.)
15. **Stale docs** — README claims 800×480 and width tiers 170/250/320 (actual:
    adaptive width, 800×664 envelope); `02-Design-System.md`, `03-Design-Tokens.md`,
    `Shadows.xaml` are referenced in code comments but never existed. Violates §0.
16. **Empty-state inconsistency** — some surfaces show "Scanning…" / "Nothing
    copied yet", others (shelf, now-playing) have no empty state. Violates §7.
17. **Padding inconsistency** — dashboard outer 8/40/8/8, row spacing 8 vs 12,
    mixed 10/12 inside Bluetooth card. Violates §4.2.

---

*End of DesignRules.md. Changes to confirmed rules require a decision-log entry and
a pass that validates against this document.*
