# Focus Migration — PR 1: Shell Only (WidgetCard)

Single rule, stated before anything else: no feature additions, no visual polish, no "while I'm in here" improvements. Focus must look and behave identically after this PR. If you notice something that seems worth fixing along the way, name it in your report — do not fix it in this PR.

## Scope

Move Focus's existing card chrome — currently `Border Style="SectionBorderStyle"` + `AppIcon Kind="Target"` + "FOCUS SESSION" label (`FontSize=10`, `CharacterSpacing=50`) — into WidgetCard's slots (`HeaderContent`/`BodyContent`/`FooterContent`/`OverlayContent`). Everything else — the decrement-based DispatcherTimer, ring geometry, drag math, settings input, session storage — stays exactly as it is today, bugs and all.

## Explicit Preservation Requirements (verified against the actual code — do not rediscover these the hard way)

1. **OneTime rebind trick** (`ExpandedDashboard.xaml.cs:1105-1106`, inside `FocusSettingsSave_Click`). `ItemsSource = null; ItemsSource = _focusSessions;` — this is a workaround for `x:Bind FocusSessions` (xaml:138) defaulting to OneTime. Must survive verbatim. If dropped, new sessions won't appear until app restart.

2. **FocusCardHost's main/settings swap stays an in-place Visibility swap, not `OverlayContent`.** `FocusMainView`/`FocusSettingsView` visibility toggle at `:1076-1077`/`:1082-1083`, both inside `FocusCardHost` (xaml:109). This was a deliberate design decision — do not convert to WidgetCard's overlay slot even though it's now available.

3. **Leave `FocusPillRotate.Angle = FocusProgressFraction * 360;` exactly where it is** (set at `:448`, also `:910`/`:936`), direct code-behind assignment. Don't refactor toward binding just because it's now visible during the move.

4. **Ring geometry + drag math moves as-is**: `FocusRing_PointerMoved` (`:916`), `AngleFromFocusRingPoint` (`:895`), `DurationToFraction`/`FractionToDurationSeconds` (`:871`/`:874`). Hard-won, already-debugged — do not touch the math, only its container.

5. **Settings input stays the three H:M:S NumberBox controls** (`FocusSettingsHoursBox`/`MinutesBox`/`SecondsBox`, `:197-208`) — confirmed as the real, current implementation. Do not change to a slider or anything else.

6. **WidgetCard's Pressed visual state will now fire during ring drags, dot taps, and NumberBox interaction** — this is new, expected behavior from event bubbling (pointer events on `FocusRingDragSurface`, dots, and NumberBoxes bubble up to WidgetCard's root pointer handlers), not a migration defect. Do not attempt to suppress or work around it in this PR — it's out of scope (would require a WidgetCard opt-out API, which is a shell change, not a shell-only migration). Just confirm in your report that you observed it and that it doesn't visually break anything (e.g. the ring itself still renders/drags correctly even while the card flattens underneath it). Whether this is the desired long-term behavior is a decision for later, not part of this PR.

## What Actually Changes

- Focus's card chrome is replaced by WidgetCard's slots.
- Any raw spacing/radius/color/typography values currently hardcoded in Focus's XAML should migrate to the `Spacing.*`/`Radius.*`/`Semantic.*`/`Type.*` tokens in `Resources/Tokens.xaml` — structural only, not a visual change. If a token value doesn't closely match the current visual (e.g. the header's `FontSize=10`, `CharacterSpacing=50` vs. `Type.Section`), flag it rather than silently changing the look.

## Verification (mandatory — build success is not proof)

- Build clean.
- Live runtime test, real clicks (synthetic input unreliable against WinUI controls, per prior sessions):
  - Start a focus session, confirm ring animates and countdown ticks correctly
  - Pause/resume, confirm state correct
  - Open settings, change H:M:S values, save — confirm the OneTime rebind trick still works: add a new session, confirm it appears in the dot switcher without restarting the app
  - Switch between sessions via the dots
  - Let a session complete, confirm completion behavior matches current (don't change it)
- Screenshot before/after side by side — confirm no visible difference beyond token migration (report any mismatches found, e.g. the header font/spacing noted above).
- There is no `WidgetCard.Validation.md` checklist file in this repo — skip that reference. Instead, confirm against WidgetCard's actual public contract: only `HeaderContent`/`BodyContent`/`FooterContent`/`OverlayContent` are consumed, no widget-specific logic (timer math, session state) leaked into WidgetCard itself.

## Report

### What was preserved (verified against the live app, real clicks, human observation)

1. **OneTime rebind trick** — `ItemsSource = null; ItemsSource = _focusSessions;` in `FocusSettingsSave_Click` (`:1105-1106`) survived verbatim. **Critical live check passed**: user opened settings, renamed "Deep Focus" → "coding", changed 50:00 → 5:00, saved, and the changed session appeared in the dot switcher **without restarting the app**.
2. **Main/settings swap stays an in-place Visibility swap, not the overlay slot.** Because the card now lives inside WidgetCard's `HeaderContent`/`BodyContent`/`FooterContent`, the old single-pair toggles no longer resolved correctly (compile break); the toggle was rewritten as three slot-pair swaps — `FocusMainHeader|Body|Footer` ↔ `FocusSettingsHeader|Body|Footer` — in `OpenFocusSettings()`/`CloseFocusSettings()` (`:1069-1087`). No stale references; verified live: gear icon swaps card to the edit view, close swaps back to the ring view.
3. **`FocusPillRotate.Angle = FocusProgressFraction * 360;`** untouched (`:448`, also `:910`/`:936`).
4. **Ring geometry + drag math** untouched: `FocusRing_PointerMoved` (`:916`), `AngleFromFocusRingPoint` (`:895`), `DurationToFraction`/`FractionToDurationSeconds` (`:871`/`:874`).
5. **H:M:S NumberBox settings input** untouched (`:197-208`).

### What changed / what to look at after token migration

- Focus card chrome replaced: `Border Style="SectionBorderStyle"` (was xaml:108), `AppIcon Kind="Target"` + "FOCUS SESSION" label moved into WidgetCard's `HeaderContent`; body and footer moved into `BodyContent`/`FooterContent`. **No visible difference in any state** per human verification.
- Spacing migrated to tokens: `Spacing.XS` (dot panel, `:156`), `Spacing.M` (footers, `:203`/`:218`).
- Timer readout migrated to `Type.LargeNumber` (26/Bold) — exact token match.
- Settings `RowSpacing="8"` (was on `FocusSettingsView`) was lost in the mechanical split into three slots — **restored, not left flagged.** Implemented as `Margin="0,0,0,8"` on the settings header and `Margin="0,8,0,0"` on the settings footer (`xaml:116`/`xaml:218`). The margins sit on the two Auto slot cells (not the star body row) to reproduce the original geometry exactly: the old 8px gaps lived *outside* the centered body row, and a margin on the centered body would contribute only 4px (half absorbed by centering). The `Spacing.S` token could not be used here — `Spacing.*` are `x:Double` while `Margin` is `Thickness`, and WinUI has no resource-time Double→Thickness conversion; introducing a Thickness-typed spacing token would be a token-set change (out of PR 1 scope). Value hardcoded to `8` to match the original `RowSpacing="8"`. **Verified visually: settings row rhythm matches pre-migration. Closed.**
- **Flagged, not changed** (per scope rule — no exact token match, changing would alter the look):
  - Header "FOCUS SESSION" keeps raw `FontSize="10"` + `CharacterSpacing="50"` (`:114`). Nearest tokens don't match: `Type.Micro` is 10/Medium with no CharacterSpacing; `Type.Section` is 12/SemiBold.
  - Session-name TextBlock in the body keeps raw `FontSize="10"` (`:129`).

### Verification results (manual, real clicks — per the mandatory live-test instruction)

| # | Check | Result |
|---|-------|--------|
| 1 | Expand dashboard | ✅ |
| 2 | Play → countdown ticks down | ✅ |
| 3 | Pause, then resume | ✅ |
| 4 | Reset → returns to **31:00** (maths, 1860s) | ✅ |
| 5 | Gear icon → card swaps to settings edit view | ✅ |
| 6 | Change H:M:S to 5:00 + rename session to "coding" | ✅ |
| 7 | **Changed session visible in dot switcher without restart** (OneTime rebind trick) | ✅ |
| 8 | Close settings → back to ring view | ✅ |

### Notes observed and left alone (named per scope rule, not fixed)

- `IsTabStop="False"` on the Focus `WidgetCard` instance (`:108`) — pre-existing, left as-is.
- Orphaned `SectionBorderStyle` resource (`:22-27`) — no longer referenced after the chrome move; left in place.
- Landmine 6 (bubbling → WidgetCard Pressed visual during drags/dots/NumberBox): observed as expected; no visual breakage — ring rendered and dragged correctly throughout.
- Screenshot side-by-side skipped in favor of direct human verification (the app proved hostile to synthetic capture tooling in this session); the user confirmed no visible difference in every state.
- Test-data note: the hand-crafted `maths ` session originally had `DurationSeconds=0`, making the initial timer read 00:00 and reset-to-0 a false alarm. This was a data artifact, not a code bug — the reset handler was correct. Data fixed to 1860; live reset verified at 31:00.

Do not start on FocusEngine in this PR — that's explicitly Phase 3, separate and later.

---

### Record of pre-flight verification (what was checked against the code before this prompt was finalized)

- §1d rebind: `FocusDotsControl`/`FocusSettingsSave_Click`, `ItemsSource = null; ItemsSource = _focusSessions;` at `ExpandedDashboard.xaml.cs:1105-1106`; `ItemsControl ItemsSource="{x:Bind FocusSessions}"` (xaml:138) is `OneTime` (x:Bind default). Confirmed.
- §1b Visibility swap: `FocusMainView`/`FocusSettingsView` at `:1076-1077`/`:1082-1083` inside `FocusCardHost` (xaml:109). Confirmed.
- §1g pill angle: `FocusPillRotate.Angle = FocusProgressFraction * 360;` at `:448` (also `:910`/`:936`). Confirmed.
- Drag math: `FocusRing_PointerMoved` (`:916`), `AngleFromFocusRingPoint` (`:895`), `DurationToFraction`/`FractionToDurationSeconds` (`:871`/`:874`). Confirmed.
- Settings: `FocusSettingsHoursBox`/`MinutesBox`/`SecondsBox` (`:197-208`). Confirmed.
- Chrome being replaced: `Border Style="SectionBorderStyle"` (xaml:108), `AppIcon Kind="Target"` (xaml:112), "FOCUS SESSION" `FontSize=10` `CharacterSpacing=50` (xaml:113).
- Landmine discovered during pre-flight (now item 6): pointer events from `FocusRingDragSurface` (xaml:130-135), dot buttons, and NumberBoxes bubble to WidgetCard's root handlers (`OnPointerPressed`/`OnPointerReleased`, WidgetCard.xaml:8-14), so WidgetCard's Pressed visual fires during interaction — expected, not a defect; out of PR 1 scope.
- `WidgetCard.Validation.md` does not exist in the repo; the Verification section references the actual WidgetCard public contract instead.
