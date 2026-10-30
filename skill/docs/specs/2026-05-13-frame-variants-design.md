# Frame Variants — Design Spec

**Status:** Approved
**Date:** 2026-05-13
**Scope:** `wingman-player` repo (player overlay); no changes to `youtube_video_player` skill code.
**Related:** Brainstormed in session with user via the brainstorming skill; visual mockups at `.superpowers/brainstorm/1954-1778700579/`.

---

## Problem

Some users want the option to remove the chunky sci-fi backdrop frame from Wingman Player to maximise video real estate. The backdrop is currently the only way to drag the window — removing it without a replacement breaks window movement.

## Solution

Add a `FrameVariant` setting with three options. The player owns the setting; the skill side is unchanged. CSS-rendered chrome for the new variants (no new image assets).

| Variant | Visual | Drag region |
|---------|--------|-------------|
| **Classic** (default) | Existing `frame_base.png` chunky frame, unchanged | The frame artwork (existing behaviour) |
| **Slim** | 8px crimson border around the window + 36×36 corner accents (lighter shade) | The 8px border (plus corner accents) |
| **Borderless** | No border; four 48×48 crimson corner brackets only | The four corner brackets only |

Window dimensions stay at **1247×726** across all variants. The WebView2 (video) grows inward as the chrome shrinks, so picking slim or borderless gives users more video.

## Decisions log

| # | Question | Decision | Why |
|---|----------|----------|-----|
| Q1 | Where does the user pick? | Player settings panel only | The frame is a player concern; the player already has a built settings UI for opacity / zoom / hotkey / minimize-mode. A skill-side property would duplicate state. |
| Q2 | What happens to freed space when frame shrinks? | Window stays same size; WebView2 grows inward | Matches user intent (remove backdrop → see more video). Same drag-region size in the underlying window. Same on-screen position. |
| Q3 | Drag region in borderless? | Corner brackets only | Purest aesthetic — the only visible chrome is also the drag affordance. Each corner is 48×48px (≥ 28×28 minimum click target). |
| Q4 | Default for existing users? | Classic | Zero surprise. Existing `settings.json` files without `FrameVariant` deserialize to Classic. |
| Q5 | Visual specs and asset strategy | CSS-rendered (no new PNGs); specs as below | Avoids creating new artwork; thickness/colour tweakable via CSS; classic continues to use the existing PNG. |

**Rejected alternatives** (documented for context):

- Skill-driven config (skill stores variant in `default_config.yaml`, pushes to player on activation) — duplicates state, fights with player settings UI.
- Three separate WPF windows (one per variant) — heavier, no upside.
- Top drag rail (visible 6px crimson strip) in borderless — adds chrome that contradicts the "borderless" aesthetic.
- Modifier-key drag (Alt+drag from anywhere) — undiscoverable without a hint.
- Theme system / per-variant PNG assets — overkill for three variants.

---

## Architecture

Single feature, two layers:

```
┌─────────────────────────────────────────────────────────────┐
│  C# (OverlayWindow + SettingsManager)                       │
│  • New FrameVariant enum                                    │
│  • New field on WingmanPlayerSettings record                │
│  • Handle "frameVariant" WebMessage                         │
│  • Push variant into renderer via BuildSyncScript           │
└──────────────────────┬──────────────────────────────────────┘
                       │ WebMessage (postMessage / sync script)
┌──────────────────────┴──────────────────────────────────────┐
│  Renderer (index.html + style.css + player.js)              │
│  • Class on #app: frame-classic / frame-slim / frame-borderless │
│  • Three CSS rule sets — show PNG (classic) or CSS chrome   │
│  • `.drag-zone` class binds startDrag                       │
│  • Settings panel dropdown to switch                        │
└─────────────────────────────────────────────────────────────┘
```

The skill is unchanged. The skill's HTTP API to the player is unchanged.

---

## Data model & persistence

### New enum

File: `src/Models/WingmanPlayerSettings.cs`

```csharp
public enum FrameVariant
{
    Classic,     // existing chunky sci-fi frame (frame_base.png)
    Slim,        // 8px crimson border + 36×36 corner accents (CSS-rendered)
    Borderless,  // 48×48 corner brackets only (CSS-rendered)
}
```

### New field on the existing settings record

```csharp
public record WingmanPlayerSettings
{
    // ... existing fields ...
    public FrameVariant FrameVariant { get; set; } = FrameVariant.Classic;
}
```

### Migration

The field has a default value. Existing `settings.json` files (no `FrameVariant` key) deserialize with `FrameVariant = Classic` automatically. **No migration code required.**

### Save path

Uses the existing `_settings.Save(_settings.Current with { FrameVariant = variant })` flow — same mechanism as every other persisted field (`WebViewZoomPct`, `MinimizeMode`, `BannerOpacity`, etc.).

---

## Renderer-side: CSS + drag

### DOM addition

File: `src/Renderer/index.html`

```html
<div id="app" class="frame-classic">  <!-- class swapped at runtime -->
    <!-- existing #video-wrap, #player stay -->
    <!-- click-blockers move inside #video-wrap (see below) -->
    <img id="frame-base" class="drag-zone" src="assets/frame_base.png" ...>  <!-- classic only -->

    <!-- NEW: CSS-rendered chrome for slim / borderless -->
    <div id="frame-css">
      <div class="frame-edge drag-zone"></div>
      <div class="frame-corner top-left drag-zone"></div>
      <div class="frame-corner top-right drag-zone"></div>
      <div class="frame-corner bottom-left drag-zone"></div>
      <div class="frame-corner bottom-right drag-zone"></div>
    </div>

    <!-- existing #settings-panel, #miniplayer-settings-panel, etc. stay -->
</div>
```

Every draggable element wears the `.drag-zone` class — the PNG frame (classic), the slim border, the slim corners, and the borderless corners — so the JS binding is a single selector.

The settings gear button (`#settings-btn`) and the three click-blockers (`#click-blocker`, `#click-blocker-tl`, `#click-blocker-br`) **move inside `#video-wrap`** so they automatically track the video rect across all variants (see Reparenting below).

### CSS

File: `src/Renderer/style.css`

```css
/* Frame container — transparent areas must pass clicks through to the video below.
   Otherwise a full-bleed #frame-css would block all YouTube interaction in borderless. */
#frame-css {
  position: absolute;
  inset: 0;
  pointer-events: none;   /* children opt back in individually */
}

/* Classic — PNG visible, CSS chrome hidden, WebView2 in original cutout position */
.frame-classic #frame-base { display: block; }
.frame-classic #frame-css  { display: none; }
.frame-classic #video-wrap { /* existing cutout-aligned position, unchanged */ }

/* Slim — 8px border, 36×36 corners (lighter), video inset by 8px */
.frame-slim #frame-base { display: none; }
.frame-slim #frame-css  { display: block; }
.frame-slim .frame-edge {
  position: absolute;
  inset: 0;
  border: 8px solid rgba(248, 113, 113, 0.9);
  pointer-events: auto;             /* opt back in: this region is draggable */
  box-sizing: border-box;
}
.frame-slim .frame-corner {
  position: absolute;
  width: 36px;
  height: 36px;
  pointer-events: auto;
  box-sizing: border-box;
  /* 3px L-shape via two borders per corner. Lighter shade #fca5a5. */
}
.frame-slim .frame-corner.top-left     { top: 0;    left: 0;    border-top:    3px solid #fca5a5; border-left:   3px solid #fca5a5; }
.frame-slim .frame-corner.top-right    { top: 0;    right: 0;   border-top:    3px solid #fca5a5; border-right:  3px solid #fca5a5; }
.frame-slim .frame-corner.bottom-left  { bottom: 0; left: 0;    border-bottom: 3px solid #fca5a5; border-left:   3px solid #fca5a5; }
.frame-slim .frame-corner.bottom-right { bottom: 0; right: 0;   border-bottom: 3px solid #fca5a5; border-right:  3px solid #fca5a5; }
.frame-slim #video-wrap { inset: 8px; }

/* Borderless — corners only, video fills the window */
.frame-borderless #frame-base    { display: none; }
.frame-borderless #frame-css     { display: block; }
.frame-borderless .frame-edge    { display: none; }
.frame-borderless .frame-corner {
  position: absolute;
  width: 48px;
  height: 48px;
  pointer-events: auto;             /* opt back in: corners are draggable */
  box-sizing: border-box;
  background: rgba(248, 113, 113, 0.05);  /* faint hover-able fill */
  /* 4px L-shape via two borders per corner. Crimson #f87171. */
}
.frame-borderless .frame-corner.top-left     { top: 0;    left: 0;    border-top:    4px solid #f87171; border-left:   4px solid #f87171; }
.frame-borderless .frame-corner.top-right    { top: 0;    right: 0;   border-top:    4px solid #f87171; border-right:  4px solid #f87171; }
.frame-borderless .frame-corner.bottom-left  { bottom: 0; left: 0;    border-bottom: 4px solid #f87171; border-left:   4px solid #f87171; }
.frame-borderless .frame-corner.bottom-right { bottom: 0; right: 0;   border-bottom: 4px solid #f87171; border-right:  4px solid #f87171; }
.frame-borderless #video-wrap { inset: 0; }

/* Settings gear — must clear the 48×48 corner bracket in borderless so a click
   on the gear doesn't fall inside the bracket's drag hit-area. 60px clearance
   leaves ~12px breathing room on the corner-facing sides. */
.frame-borderless #settings-btn { bottom: 60px; right: 60px; }
```

### Drag handler

File: `src/Renderer/player.js`

The current mousedown handler on `#frame-base` posts `{type:'startDrag'}` to C#. Generalise:

```js
// Single selector covers the PNG frame (classic), slim border, slim corners,
// and borderless corners — every draggable element wears .drag-zone.
document.querySelectorAll('.drag-zone').forEach(el => {
  el.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;  // left-click only
    window.chrome.webview.postMessage({ type: 'startDrag' });
  });
});
```

C# side is unchanged — the `startDrag` message handler in `OverlayWindow.xaml.cs` already does the work.

### Reparenting (click-blockers + gear)

The three click-blockers (`#click-blocker`, `-tl`, `-br`) currently sit as direct children of `#app`, positioned absolutely. They cover YouTube's overlay UI (end-screen buttons, channel-avatar link, fullscreen-icon). In slim/borderless, the video rect grows, so the blocker positions need to follow.

**Fix:** move all three click-blockers (and `#settings-btn`) **inside `#video-wrap`**. They become children of the video container and their `inset` / corner positions track `#video-wrap` automatically across variants.

Verify `z-index` ordering after the move so the gear button isn't covered by `#click-blocker-br`.

---

## Live-switch flow + settings panel UI

### New row in the settings panel

File: `src/Renderer/index.html`, inside `#settings-panel`, after the minimize-mode row:

```html
<div class="settings-row">
  <span class="settings-label">Frame style</span>
  <select id="frame-variant-select" class="settings-select">
    <option value="classic">Classic</option>
    <option value="slim">Slim border</option>
    <option value="borderless">Borderless</option>
  </select>
</div>
```

Reuses `.settings-select` styling — visually identical to the minimize-mode dropdown.

### Renderer-side event handler

File: `src/Renderer/player.js`

```js
document.getElementById('frame-variant-select').addEventListener('change', (e) => {
  const variant = e.target.value;
  // Instant local feedback — no round-trip wait.
  document.getElementById('app').className = `frame-${variant}`;
  // Post to C# for persistence.
  window.chrome.webview.postMessage({ type: 'frameVariant', value: variant });
});
```

### C# message handler

File: `src/UI/OverlayWindow.xaml.cs`, new case in the `OnWebMessageReceived` switch:

```csharp
case "frameVariant":
    var variantStr = root.GetProperty("value").GetString();
    if (Enum.TryParse<FrameVariant>(variantStr, ignoreCase: true, out var variant))
        _settings.Save(_settings.Current with { FrameVariant = variant });
    break;
```

### BuildSyncScript extension

Appended to the existing concatenated JS in `BuildSyncScript`:

```csharp
var frameVariant = _settings.Current.FrameVariant.ToString().ToLowerInvariant();
// ... existing $"var os=..." etc. lines, then:
$"var fvs=document.getElementById('frame-variant-select');" +
$"var app=document.getElementById('app');" +
$"if(fvs)fvs.value='{frameVariant}';" +
$"if(app)app.className='frame-{frameVariant}';" +
```

### End-to-end data path

```
User picks variant in dropdown
  └─→ JS change handler
        ├─→ Update #app.className locally (CSS variants re-paint instantly)
        └─→ postMessage { type: 'frameVariant', value }
              └─→ C# OnWebMessageReceived
                    └─→ _settings.Save(_settings.Current with { FrameVariant = v })
                          └─→ settings.json on disk
                                └─→ (Next launch / next overlay show)
                                      └─→ BuildSyncScript pushes variant back
                                            └─→ Dropdown value + #app class restored
```

### First-frame consideration

`index.html` ships with `class="frame-classic"` baked in. A returning user on Slim/Borderless sees ~50ms of Classic before `BuildSyncScript` corrects the class on overlay show. Acceptable; if it turns out to be visible in practice, follow up by writing the variant into a cookie or URL hash that loads earlier in the boot sequence. Not in scope for this design.

---

## Testing strategy

No new pure-functional surface area, so no new automated tests fall out. Existing skill-side tests (`skills/youtube_video_player/tests/test_confidence.py`) are unaffected.

### Fireside test plan (run in order)

| # | Action | Expected |
|---|--------|----------|
| 1 | Fresh install / delete settings.json. Launch. | Classic frame. Drag works from frame. Gear button visible bottom-right of video. |
| 2 | Existing settings.json missing `FrameVariant` key. Launch. | Still Classic. No crash, no exception in log. |
| 3 | Gear → Frame style → Slim. | Frame swaps live to slim. Video grows inward 8px on each side. Border + 36×36 corner accents visible. |
| 4 | Drag from the 8px slim border. | Window moves. |
| 5 | Drag from a slim corner accent. | Window also moves. |
| 6 | Gear → Frame style → Borderless. | Live swap. Video fills window. Only four 48×48 corner brackets visible. |
| 7 | Drag from a corner bracket. | Window moves. |
| 8 | Click middle of the video (borderless). | YouTube interaction (pause toggle) — no drag. |
| 9a | Lock toggle → drag from slim border / corner. | Drag rejected — `_dragLocked` path works. |
| 9b | Lock toggle → drag from a borderless corner bracket. | Drag rejected. |
| 10 | Opacity slider in each variant. | All three variants fade together (border, corners, classic PNG). |
| 11 | Zoom −10% then +10% in each variant. | Window resizes; border / corners scale proportionally. |
| 12 | Close player, reopen. | Last-selected variant restored from settings.json. |
| 13 | Tray → Reset Window. | Position + zoom reset. **Frame variant unchanged** (not part of reset). |
| 14 | Drag to a second monitor in each variant. | DPI transition clean; corner hit-targets feel correct. |
| 15 | Video end-screen overlay (let a video finish in each variant). | YouTube's "More videos" / share buttons stay blocked — `#click-blocker` still covers them inside the new `#video-wrap` parent. |

### Regression risks to eyeball during the run

- **Click-blockers reparented to `#video-wrap`** — biggest risk. If positioning math drifts, blocker rects could miss YouTube's overlay buttons by a few pixels. Step 15 catches this.
- **Settings gear button reparented to `#video-wrap`** — could end up under `#click-blocker-br` if z-index is off, or inside the bottom-right corner bracket's drag hit-area in borderless if the 60px clearance is wrong. Steps 3 / 6 confirm the gear is clickable across variants.
- **High-DPI monitor** — corners sized in CSS pixels, already account for Windows system DPI. Step 14 partially covers; a 5-second check on a 4K monitor if available.
- **First-frame flash on startup** — see *First-frame consideration* above. Step 12 reveals whether it's noticeable.

### Not in scope for automation

WebView2 + WPF UI testing has no existing harness in this project; setting one up just for this feature is not justified. Fireside is the bar.

---

## Out of scope / future considerations

- **Skill-side voice tool** (e.g. `set_frame_variant("borderless")` callable from Wingman voice). Deferred until/unless a demand signal arrives. Easy bolt-on later: skill calls a new `POST /player/frame-variant` endpoint, player applies + persists.
- **Animated transitions** between variants. Live swap is instant today; animation would be a nice-to-have, not a need.
- **Per-monitor variants** (e.g. "use slim on this monitor, borderless on that one"). No demand; the existing single-window model doesn't justify the complexity.
- **Reset Window resetting frame variant.** Currently `ResetWindow` resets zoom + position only. Frame variant is treated as a user preference, not a recovery target.
- **Modifier-key drag** (Alt+drag from anywhere) as a parallel drag mechanism. Rejected as undiscoverable; revisit only if users hit drag-target issues with the 48×48 corners.

---

## Files changed

### `wingman-player` repo (the only repo touched)

- `src/Models/WingmanPlayerSettings.cs` — new `FrameVariant` enum + new field on the record
- `src/UI/OverlayWindow.xaml.cs` — new `case "frameVariant"` in `OnWebMessageReceived`; extend `BuildSyncScript` to push variant to renderer
- `src/Renderer/index.html` — `#app` class, new `#frame-css` container, new `#frame-variant-select` row, reparent click-blockers + `#settings-btn` inside `#video-wrap`
- `src/Renderer/style.css` — three `.frame-*` rule sets, edge / corner styles
- `src/Renderer/player.js` — generalise mousedown drag binding to `.drag-zone`; new change handler on `#frame-variant-select`

### `wingman-ai` repo

No changes. The skill is unaffected.
