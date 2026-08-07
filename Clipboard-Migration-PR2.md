# Clipboard Migration — PR 2: Shell Only (WidgetCard) + GetRevealTargets Hardening

Single rule, same as PR 1: no feature additions, no visual polish, no "while I'm in here" changes. Clipboard looks and behaves identically after this PR; anything worth fixing is named here, not fixed here.

## Part A — GetRevealTargets Hardening (done first, verified standalone)

- Added `Tag="ClipboardFrontCard"` to the front-card `Border` in the clipboard item `DataTemplate` (`ExpandedDashboard.xaml:265`).
- `GetRevealTargets` (`ExpandedDashboard.xaml.cs:755-774`) now matches on that Tag instead of purely the `Border` + `TranslateTransform` type pattern; the `TranslateTransform` check is kept as an invariant guard alongside the Tag.
- **Gate passed before any shell work**: user verified reveal/close/delete work exactly as before on this change alone.

Why: the old walk ("any Border with a TranslateTransform whose parent Grid has a Button child") was a structural match with no explicit marker. With the template now inside WidgetCard's slots, that walk could match the shell's own chrome if WidgetCard ever gained a TranslateTransform for hover/press feedback. The Tag makes the anchor explicit and immune to nearby restructuring.

## Part B — Clipboard shell into WidgetCard

- **`HeaderContent`**: "Clipboard" title + All/Pinned filter toggle row (moved from the column's row 0).
- **`BodyContent`**: `ClipboardEmptyText` + the `ScrollViewer`/`ItemsControl`, wrapped in a `Grid RowDefinitions="Auto,*" RowSpacing="6"` so the empty-state row keeps its place above the list.
- **`FooterContent`**: unused (clipboard has no footer).
- **Item template**: moved verbatim — the only change inside it is the `Tag` from Part A.
- `IsTabStop="False"` on the clipboard `WidgetCard` instance, consistent with the Focus card.

## Explicit Preservation Requirements (verified live)

1. **Single-step delete, no confirmation** — preserved exactly as it works today. Note for the parked delete-UX conversation: the Audit flagged a discrepancy with an earlier "two-step delete" plan description; this PR does not assume which is correct and does not change delete behavior.
2. **`UpdateFilterVisual()` manual `Foreground`/`FontWeight` toggling** — kept as-is (no XAML visual states). Known future candidate for WidgetCard's Interaction States vocabulary; untouched in this PR.
3. **Item template structure** (`Grid ColumnDefinitions="Auto,*,Auto"` — thumbnail/text/actions) — moved largely as-is; only the Part A Tag changed inside it.
4. **Bubbling consequence (Focus landmine 6 analog)** — pointer events from "…"/pin/delete taps bubble to WidgetCard's root handlers, so the card's Pressed visual fires during interaction. User confirmed it does **not** interfere with the interactions (card briefly flattens, controls still work). Expected; not suppressed.

## What Actually Changed

- Clipboard's bare column is now the shared WidgetCard shell (bordered, rounded, padded from `Spacing.L`, hover/pressed states) — an intended consequence of the migration, matching the Focus card. All content renders identically inside it.
- Token migration (exact match only): filter-row `Spacing="4"` → `Spacing.XS`.
- **Flagged, not changed** (no close token match — changing would alter the look):
  - "Clipboard" title `FontSize="14"` Bold (closest: `Type.Title` 16/SemiBold, `Type.Section` 12/SemiBold).
  - `ClipboardEmptyText` `FontSize="11"` Regular (closest: `Type.Caption` 11/Medium — weight differs).
  - `RowSpacing="6"` on the body grid — no Spacing token for 6 (`XS`=4, `S`=8); kept raw `6` like the original. Restored inside the body slot rather than lost across the slot split (per the Discoveries Log from PR 1).
  - Item-template values left untouched: `FontSize="11"` SemiBold, `FontSize="9.5"`, `ColumnSpacing="10"`, `CornerRadius="6"`, `Spacing="2"`, `#E81123` delete red, `#12FFFFFF` thumbnail fill.
- Header note: title/filters now sit in the shell's fixed 40px header row (previously an Auto row) — a little extra vertical padding, inherent to the shared chrome.

## Verification Results (manual, real clicks — no synthetic automation)

| # | Check | Result |
|---|-------|--------|
| 1 | Existing history displays (thumbnails, titles, timestamps) | ✅ |
| 2 | Tap "…" → delete strip reveals (Part A hardening intact) | ✅ |
| 3 | Delete an item (single-step, no confirmation) | ✅ |
| 4 | All/Pinned toggle updates `UpdateFilterVisual` state | ✅ |
| 5 | Pin/unpin moves item between All and Pinned views | ✅ |
| 6 | Tap an item to re-copy it to clipboard | ✅ |
| 7 | Empty state ("Nothing copied yet") | ✅ |

Build: clean, 0 warnings / 0 errors. Screenshot side-by-side skipped in favor of direct human verification (per the PR 1 lesson); user reported no visible difference beyond the expected shell chrome.

## Contract Check

- Only `HeaderContent`/`BodyContent` consumed (FooterContent unused); no clipboard-specific logic (item state, delete, reveal) leaked into WidgetCard itself — all stays in the dashboard code-behind.

## Do Not Touch (separate, later conversations)

- Delete-confirmation UX (single-step vs two-step discrepancy is parked for its own design discussion).
- `UpdateFilterVisual()` → XAML visual states / WidgetCard Interaction States vocabulary.
