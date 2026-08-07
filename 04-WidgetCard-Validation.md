# 04 — WidgetCard Validation Checklist

Live-validation log for the shared WidgetCard shell (`Controls/WidgetCard.xaml` + `.cs`).
The public contract is four content slots — `HeaderContent` / `BodyContent` / `FooterContent` /
`OverlayContent` — plus the Default / Hover / Pressed / Focused interaction states, all visuals
built from `Resources/Tokens.xaml`. A widget earns a ✅ here only after real-click runtime
verification, not just a clean build.

## Widgets Validated Against

| Widget | Status | Validated | Scope / Notes |
|--------|--------|-----------|---------------|
| Focus (ExpandedDashboard) | ✅ | 2026-08-07 | PR 1 shell-only migration. Header/body/footer moved into the slots; in-place main↔settings swap preserved as a three-slot-pair Visibility toggle (not the overlay slot); ring drag math, pill-angle assignment, H:M:S NumberBoxes, and the OneTime `ItemsSource` rebind all untouched and re-verified live with real clicks. |
| Clipboard (ExpandedDashboard) | ✅ | 2026-08-07 | PR 2 shell-only migration. Header/filter row → `HeaderContent`; empty state + list → `BodyContent` (item template moved verbatim). `GetRevealTargets` hardened with an explicit `Tag="ClipboardFrontCard"` before the template moved into the shell. Single-step delete, pin/unpin, All/Pinned filter, re-copy — all re-verified live with real clicks. |
| (next widget — Media, Battery, ...) | ⬜ | — | |

## Discoveries Log

Real-world lessons from validating against live widgets, recorded here before `WidgetCard.md` is written for real.

### 2026-08-07 — RowSpacing doesn't take Spacing.* tokens directly

- The `Spacing.*` tokens in `Resources/Tokens.xaml` are `x:Double`. `RowSpacing` / `ColumnSpacing`
  consume them directly, but `Margin` (a `Thickness`) does **not** — WinUI has no resource-time
  Double→Thickness conversion (`Margin="{StaticResource Spacing.S}"` throws). `WidgetCard.xaml.cs`
  already works around this by materializing `Thickness`/`GridLength` from the token values in code
  (see the `Token()` helper, `WidgetCard.xaml.cs:90-98`).
- When a pre-shell card with `RowSpacing="8"` between its internal rows is split into WidgetCard's
  three slots, the gaps are lost (the shell's slot rows have no spacing). Restore them with explicit
  margins on the two **Auto** cells — a bottom margin on the header section and a top margin on the
  footer section. Do **not** put the margin on the star-row body: a margin on a vertically-centered
  element is half-absorbed into the centering offset (an 8px margin renders as ~4px of gap below).
- Example in `ExpandedDashboard.xaml`: settings header `Margin="0,0,0,8"`, settings footer
  `Margin="0,8,0,0"` (matches the pre-migration `RowSpacing="8"` on `FocusSettingsView`).

### 2026-08-07 — Structure-matched code-behind needs an explicit marker before moving into the shell

- `GetRevealTargets` in `ExpandedDashboard.xaml.cs` walked up the tree matching "any `Border`
  with a `TranslateTransform` whose parent `Grid` has a `Button` child" — a structural match with
  no explicit marker. That was safe while the template sat in a standalone column, but once nested
  inside WidgetCard's slots the same walk could match the shell's own chrome if WidgetCard ever
  gained a `TranslateTransform` (e.g. hover/press feedback).
- Fix: `Tag="ClipboardFrontCard"` on the front-card `Border`, and the walk now matches on that Tag
  (the `TranslateTransform` check stays as an invariant guard). Applies to any code-behind that
  reaches into template internals by shape rather than by name — mark the anchor explicitly before
  nesting the template inside the shared shell.
