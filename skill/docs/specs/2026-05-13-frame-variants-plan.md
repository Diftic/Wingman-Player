# Frame Variants Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three selectable frame styles (Classic / Slim / Borderless) to Wingman Player, with the chosen variant persisted in `settings.json` and switchable live from the in-overlay settings panel.

**Architecture:** Player-owned setting (the youtube_video_player skill is unchanged). A new `FrameVariant` enum on `WingmanPlayerSettings` drives a class on `#app` (`frame-classic` / `frame-slim` / `frame-borderless`). Slim and Borderless are rendered entirely in CSS (no new image assets). Click-blockers and the settings gear button move inside `#video-wrap` so they track the video rect across all variants.

**Tech Stack:** .NET 9 / WPF / C# 12 (player host); WebView2 / vanilla JS / CSS (renderer). No new dependencies.

**Spec:** `skills/youtube_video_player/specs/2026-05-13-frame-variants-design.md`. Read first if any task is unclear.

**Repo:** All edits in `D:\PycharmProjects\wingman-player\`. No changes to the wingman-ai repo. Plan file lives in wingman-ai per the user's skill-folder-only rule.

**Test strategy:** This project has no unit-test harness — setting one up just for this feature is out of scope (decided in the spec). Validation per task is `dotnet build` succeeds; full validation is the fireside test plan in §Testing of the spec, run after Task 7.

---

## Pre-flight

- [ ] **Step 1: Confirm repo and branch**

Run:
```bash
git -C D:/PycharmProjects/wingman-player status
git -C D:/PycharmProjects/wingman-player log --oneline -5
```

Expected: clean working tree (or at least no unrelated WIP); recent commits visible. If there's WIP that isn't yours, stop and ask the user.

- [ ] **Step 2: Create a feature branch**

Run:
```bash
git -C D:/PycharmProjects/wingman-player checkout -b feat/frame-variants
```

Expected: switched to a new branch.

- [ ] **Step 3: Baseline build to confirm a clean starting state**

Run:
```bash
dotnet build D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` If errors exist before any edits, stop and fix or escalate before continuing.

---

## Task 1: Add `FrameVariant` enum and field on `WingmanPlayerSettings`

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/Models/WingmanPlayerSettings.cs`

- [ ] **Step 1: Add the enum and field**

Replace the entire file with:

```csharp
namespace wingman_player.Models;

using Keyboard;

public enum MinimizeMode
{
    Banner,
    Tray,
}

public enum FrameVariant
{
    Classic,     // existing chunky sci-fi frame (frame_base.png)
    Slim,        // 8px crimson border + 36×36 corner accents (CSS-rendered)
    Borderless,  // 48×48 corner brackets only (CSS-rendered)
}

public record WingmanPlayerSettings
{
    public KeyboardShortcut ToggleHotkey    { get; set; } = new([KeyboardKey.F8]);
    public int WebViewZoomPct               { get; set; } = 100;
    public Guid InstallationId              { get; init; } = Guid.NewGuid();
    public double? WindowLeft               { get; set; } = null;
    public double? WindowTop                { get; set; } = null;
    public MinimizeMode MinimizeMode        { get; set; } = MinimizeMode.Banner;
    public bool BannerLocked                { get; set; } = true;
    public double BannerOpacity             { get; set; } = 1.0;
    public int BannerScalePct               { get; set; } = 100;
    public double? BannerLeft               { get; set; } = null;
    public double? BannerTop                { get; set; } = null;
    public FrameVariant FrameVariant        { get; set; } = FrameVariant.Classic;
}
```

- [ ] **Step 2: Build**

Run:
```bash
dotnet build D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).` Warnings about unused enum values are fine — they get used in Task 2 / 3.

- [ ] **Step 3: Verify existing settings.json deserializes cleanly**

Manual: open the existing `%LocalAppData%\Programs\Wingman Player\settings.json` (or `%AppData%\Roaming\wingman_player\settings.json` — wherever `SettingsManager` writes; check `src/Settings/SettingsManager.cs` if unsure). It will be missing `FrameVariant`. Run the player once:

```bash
dotnet run --project D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: player launches without exception. Open `settings.json` again — should now contain `"FrameVariant": "Classic"` (or be unchanged depending on whether SettingsManager writes immediately; either is acceptable as long as no exception).

Close the player.

- [ ] **Step 4: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/Models/WingmanPlayerSettings.cs
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): add FrameVariant enum and settings field"
```

---

## Task 2: Handle `frameVariant` WebMessage in `OverlayWindow`

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/UI/OverlayWindow.xaml.cs` (inside `OnWebMessageReceived`, switch statement)

- [ ] **Step 1: Add the case**

In `OnWebMessageReceived`, find the switch on `t.GetString()` (currently includes cases for `lock`, `startDrag`, `opacity`, `zoom`, `hotkey-focus`, `hoverVideo`, ... `minimizeMode`, `openUrl`, `hotkey`, `checkForUpdates`).

Add a new case after `minimizeMode`:

```csharp
case "frameVariant":
    var variantStr = root.GetProperty("value").GetString();
    if (Enum.TryParse<FrameVariant>(variantStr, ignoreCase: true, out var variant))
        _settings.Save(_settings.Current with { FrameVariant = variant });
    break;
```

You will also need a `using` for the enum — add `using Models;` at the top of the file if not already there (it is — already imports `Models` and `Models.Keyboard`).

- [ ] **Step 2: Build**

Run:
```bash
dotnet build D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/UI/OverlayWindow.xaml.cs
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): handle frameVariant WebMessage and persist"
```

---

## Task 3: Push variant state into the renderer via `BuildSyncScript`

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/UI/OverlayWindow.xaml.cs` (`BuildSyncScript` method)

- [ ] **Step 1: Extend the sync script**

Find the `BuildSyncScript(double opacity, int zoomPct)` method. At the top of the method body, after `int opacityDisplay = ...`, add:

```csharp
var frameVariant = _settings.Current.FrameVariant.ToString().ToLowerInvariant();
```

Then in the concatenated JS string, before the closing `}})();`, add two more lines:

```csharp
$"var fvs=document.getElementById('frame-variant-select');" +
$"var app=document.getElementById('app');" +
$"if(fvs)fvs.value='{frameVariant}';" +
$"if(app)app.className='frame-{frameVariant}';" +
```

Place these immediately before the existing reset-to-main-settings-page block:

```csharp
// Reset to main settings page when overlay re-opens.
$"if(sp)sp.classList.add('hidden');" +
```

The final method should still end with `$"}})();";`.

- [ ] **Step 2: Build**

Run:
```bash
dotnet build D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/UI/OverlayWindow.xaml.cs
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): sync FrameVariant into renderer on overlay show"
```

---

## Task 4: Update `index.html` — DOM additions, reparenting, settings row

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/Renderer/index.html`

- [ ] **Step 1: Add the variant class to `#app` and the `drag-zone` class to `#frame-base`**

Find:
```html
<div id="app">
```
Replace with:
```html
<div id="app" class="frame-classic">
```

Find:
```html
<img id="frame-base" src="assets/frame_base.png" draggable="false" alt="">
```
Replace with:
```html
<img id="frame-base" class="drag-zone" src="assets/frame_base.png" draggable="false" alt="">
```

- [ ] **Step 2: Move click-blockers inside `#video-wrap`**

Currently `#click-blocker`, `#click-blocker-tl`, `#click-blocker-br` are siblings inside `#video-wrap` ALREADY — re-read `index.html` lines 13–25 to confirm. They are already inside `<div id="video-wrap">`. **No reparenting needed** for click-blockers.

Verify by reading lines 13–25 of `index.html`. If all three click-blockers are already inside `<div id="video-wrap"> ... </div>`, skip the rest of this step.

If for some reason they aren't, move them inside `#video-wrap` so the structure is:

```html
<div id="video-wrap">
  <div id="player"></div>
  <img id="idle-logo" ...>
  <div id="click-blocker" aria-hidden="true"></div>
  <div id="click-blocker-tl" aria-hidden="true"></div>
  <div id="click-blocker-br" aria-hidden="true"></div>
</div>
```

- [ ] **Step 3: Move `#settings-btn` inside `#video-wrap`**

Find the settings button (currently a sibling of `#video-wrap`, after `#frame-base`):

```html
<button id="settings-btn" aria-label="Settings">
  <svg ...>...</svg>
</button>
```

Cut the entire `<button id="settings-btn">...</button>` element. Paste it as the LAST child inside `<div id="video-wrap">`, after the three click-blockers:

```html
<div id="video-wrap">
  <div id="player"></div>
  <img id="idle-logo" ...>
  <div id="click-blocker" aria-hidden="true"></div>
  <div id="click-blocker-tl" aria-hidden="true"></div>
  <div id="click-blocker-br" aria-hidden="true"></div>
  <button id="settings-btn" aria-label="Settings">
    <svg ...>...</svg>
  </button>
</div>
```

(Note: settings PANELS `#settings-panel`, `#miniplayer-settings-panel`, `#streamer-settings-panel` stay at the `#app` level — they overlay the whole player, not the video rect.)

- [ ] **Step 4: Add the `#frame-css` container**

Immediately AFTER the existing `<img id="frame-base">` line and BEFORE the settings panels, add:

```html
    <!-- CSS-rendered chrome for slim / borderless variants -->
    <div id="frame-css">
      <div class="frame-edge drag-zone"></div>
      <div class="frame-corner top-left drag-zone"></div>
      <div class="frame-corner top-right drag-zone"></div>
      <div class="frame-corner bottom-left drag-zone"></div>
      <div class="frame-corner bottom-right drag-zone"></div>
    </div>
```

- [ ] **Step 5: Add the frame-style dropdown row in the settings panel**

In `<div id="settings-panel">`, find the row containing `<select id="minimize-mode-select">`. Immediately AFTER that closing `</div>` (the row div containing minimize-mode-select), add:

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

- [ ] **Step 6: Visual check (no build needed for HTML)**

Open `D:/PycharmProjects/wingman-player/src/Renderer/index.html` in any browser or VS Code preview. Confirm structure is well-formed (no stray closing tags). If using VS Code with HTML language server: ensure no red squiggles.

- [ ] **Step 7: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/Renderer/index.html
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): add #app variant class, #frame-css container, settings row, reparent gear"
```

---

## Task 5: Add CSS for all three variants

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/Renderer/style.css`

- [ ] **Step 1: Append the frame-variant rules at the end of the file**

Open `D:/PycharmProjects/wingman-player/src/Renderer/style.css`. At the end of the file, append:

```css
/* ============================================================================ */
/* Frame variants (Classic / Slim / Borderless)                                 */
/* ============================================================================ */

/* Container — transparent areas must pass clicks through to the video below.
   Otherwise a full-bleed #frame-css would block all YouTube interaction in
   borderless mode. */
#frame-css {
  position: absolute;
  inset: 0;
  pointer-events: none;
}

/* Classic — existing PNG frame, CSS chrome hidden, video in original cutout */
.frame-classic #frame-base { display: block; }
.frame-classic #frame-css  { display: none; }
/* #video-wrap position is unchanged in classic — leave existing rule alone */

/* Slim — 8px crimson border, 36×36 lighter corner accents, video inset 8px */
.frame-slim #frame-base { display: none; }
.frame-slim #frame-css  { display: block; }
.frame-slim .frame-edge {
  position: absolute;
  inset: 0;
  border: 8px solid rgba(248, 113, 113, 0.9);
  pointer-events: auto;
  box-sizing: border-box;
}
.frame-slim .frame-corner {
  position: absolute;
  width: 36px;
  height: 36px;
  pointer-events: auto;
  box-sizing: border-box;
}
.frame-slim .frame-corner.top-left     { top: 0;    left: 0;    border-top:    3px solid #fca5a5; border-left:   3px solid #fca5a5; }
.frame-slim .frame-corner.top-right    { top: 0;    right: 0;   border-top:    3px solid #fca5a5; border-right:  3px solid #fca5a5; }
.frame-slim .frame-corner.bottom-left  { bottom: 0; left: 0;    border-bottom: 3px solid #fca5a5; border-left:   3px solid #fca5a5; }
.frame-slim .frame-corner.bottom-right { bottom: 0; right: 0;   border-bottom: 3px solid #fca5a5; border-right:  3px solid #fca5a5; }
.frame-slim #video-wrap { inset: 8px; }

/* Borderless — no border, 48×48 crimson corner brackets only, video fills window */
.frame-borderless #frame-base { display: none; }
.frame-borderless #frame-css  { display: block; }
.frame-borderless .frame-edge { display: none; }
.frame-borderless .frame-corner {
  position: absolute;
  width: 48px;
  height: 48px;
  pointer-events: auto;
  box-sizing: border-box;
  background: rgba(248, 113, 113, 0.05);
}
.frame-borderless .frame-corner.top-left     { top: 0;    left: 0;    border-top:    4px solid #f87171; border-left:   4px solid #f87171; }
.frame-borderless .frame-corner.top-right    { top: 0;    right: 0;   border-top:    4px solid #f87171; border-right:  4px solid #f87171; }
.frame-borderless .frame-corner.bottom-left  { bottom: 0; left: 0;    border-bottom: 4px solid #f87171; border-left:   4px solid #f87171; }
.frame-borderless .frame-corner.bottom-right { bottom: 0; right: 0;   border-bottom: 4px solid #f87171; border-right:  4px solid #f87171; }
.frame-borderless #video-wrap { inset: 0; }

/* Gear button — in borderless, clear the 48×48 bottom-right corner bracket's
   drag hit-area so clicking the gear doesn't fall inside the bracket. */
.frame-borderless #settings-btn { bottom: 60px; right: 60px; }
```

- [ ] **Step 2: Verify the existing #video-wrap rule remains**

Open `style.css`, search for `#video-wrap`. There should be an existing rule with positioning for the cutout. The slim and borderless `inset:` rules above override `inset`/positioning when their variant class is on `#app`. If the existing `#video-wrap` rule uses different properties (e.g. `top: 75px; left: 75px; width: ...; height: ...`), the override above might not fully replace it. In that case adjust:

```css
.frame-slim #video-wrap { top: 8px; left: 8px; right: 8px; bottom: 8px; width: auto; height: auto; }
.frame-borderless #video-wrap { top: 0; left: 0; right: 0; bottom: 0; width: auto; height: auto; }
```

Decision rule: if `#video-wrap` uses `inset:` → keep the `inset:` form above. If it uses individual properties → use the long form. Read the existing rule first, then pick.

- [ ] **Step 3: No build needed for CSS — verify by visual scan**

Open `style.css`, scroll to your appended block. No syntax errors (matched braces, semicolons on every line). Save.

- [ ] **Step 4: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/Renderer/style.css
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): CSS for classic/slim/borderless variants + gear repositioning"
```

---

## Task 6: Add JS handlers — drag binding + variant dropdown

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/src/Renderer/player.js`

- [ ] **Step 1: Find the existing drag binding**

Open `player.js`. Search for the existing mousedown handler on `#frame-base` or whichever element fires `{type:'startDrag'}`. The handler currently looks roughly like:

```js
document.getElementById('frame-base').addEventListener('mousedown', (e) => {
  if (e.button !== 0) return;
  window.chrome.webview.postMessage({ type: 'startDrag' });
});
```

(Exact form may differ slightly — find the actual code that posts `{type:'startDrag'}`.)

- [ ] **Step 2: Replace with a `.drag-zone` query selector**

Replace the existing binding with:

```js
// Bind drag to every .drag-zone element — covers the PNG frame (classic),
// slim border, slim corner accents, and borderless corner brackets. Single
// JS code path regardless of which variant is active; CSS display:none on
// non-active variant elements suppresses their hit-testing automatically.
document.querySelectorAll('.drag-zone').forEach(el => {
  el.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    window.chrome.webview.postMessage({ type: 'startDrag' });
  });
});
```

- [ ] **Step 3: Add the variant dropdown handler**

Find where the existing `#minimize-mode-select` handler lives (search for `'minimizeMode'` in player.js). Immediately after that handler, add:

```js
// Frame-variant dropdown — applies the new class locally for instant feedback,
// then posts to C# for persistence into settings.json.
const frameVariantSelect = document.getElementById('frame-variant-select');
if (frameVariantSelect) {
  frameVariantSelect.addEventListener('change', (e) => {
    const variant = e.target.value;
    document.getElementById('app').className = `frame-${variant}`;
    window.chrome.webview.postMessage({ type: 'frameVariant', value: variant });
  });
}
```

- [ ] **Step 4: Lint check (no build for JS — visual scan)**

No build step for renderer JS. Open `player.js`, scroll to your additions, confirm balanced braces and matching template-literal backticks. Save.

- [ ] **Step 5: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add src/Renderer/player.js
git -C D:/PycharmProjects/wingman-player commit -m "feat(frame): generalize drag to .drag-zone + dropdown change handler"
```

---

## Task 7: End-to-end build and fireside test

**Files:** none modified — verification only

- [ ] **Step 1: Stop any running player instances**

Run (PowerShell):
```powershell
Get-Process -Name "Wingman-Player" -ErrorAction SilentlyContinue | Stop-Process
```

Or via Task Manager. Confirm no `Wingman-Player.exe` is running.

- [ ] **Step 2: Full build**

Run:
```bash
dotnet build D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 3: Run from Debug bin**

Run:
```bash
D:/PycharmProjects/wingman-player/src/bin/x64/Debug/net9.0-windows/win-x64/Wingman-Player.exe
```

Player launches. Press F8 if overlay is hidden.

- [ ] **Step 4: Walk through the spec's fireside test plan**

Open `skills/youtube_video_player/specs/2026-05-13-frame-variants-design.md` and run steps 1–15 in the *Testing strategy* table. For each step:

- Perform the action
- Record the outcome (match expected? ✓ / ✗ / note)
- If anything fails, drop a comment in the plan or open a new commit fixing the specific issue and re-run

If a regression risk fires (click-blockers misaligned in slim/borderless, gear button under bracket, first-frame flash noticeable on cold start), follow up in the relevant CSS file with a fix commit before claiming success.

- [ ] **Step 5: Stop the player**

Press F8 + close from tray, or end the process.

- [ ] **Step 6: If all fireside steps pass — commit the success marker**

```bash
git -C D:/PycharmProjects/wingman-player commit --allow-empty -m "test(frame): fireside test plan passed for v0.1 of frame variants"
```

(Empty commit purely for the audit trail. Skip if you'd rather note it in a PR description.)

---

## Task 8: Publish a Release build for the live test env

Optional — only do this if you want to test from the installed MSI path rather than the Debug bin folder.

**Files:** none modified — packaging only

- [ ] **Step 1: Publish**

Run:
```bash
dotnet publish D:/PycharmProjects/wingman-player/src/wingman_player.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o D:/PycharmProjects/wingman-player/artifacts
```

Expected: produces `D:/PycharmProjects/wingman-player/artifacts/Wingman-Player.exe` (~170 MB) + `artifacts/Renderer/` folder.

- [ ] **Step 2: Copy over the installed player**

The MSI installs to `%LocalAppData%\Programs\Wingman Player\`. Replace those files:

Run (PowerShell):
```powershell
$dest = "$env:LocalAppData\Programs\Wingman Player"
Copy-Item -Force D:/PycharmProjects/wingman-player/artifacts/Wingman-Player.exe "$dest/"
Copy-Item -Force -Recurse D:/PycharmProjects/wingman-player/artifacts/Renderer/* "$dest/Renderer/"
```

- [ ] **Step 3: Launch the installed player and re-run fireside steps 3 + 6**

Just confirm slim and borderless render correctly when launched from the canonical install location (which is what the skill resolves to). No commit needed.

---

## Task 9: Document in DEVLOG (player side)

**Files:**
- Modify: `D:/PycharmProjects/wingman-player/DEVLOG.md` (if it exists; otherwise skip)

- [ ] **Step 1: Check if there's a player-side DEVLOG**

Run:
```bash
ls D:/PycharmProjects/wingman-player/DEVLOG.md 2>/dev/null || echo "no devlog"
```

If "no devlog", skip this task.

- [ ] **Step 2: Add a session entry**

Prepend (use the date from `git log` of your work):

```markdown
## 2026-05-13 — Frame variants (Classic / Slim / Borderless)

### What changed

Three selectable frame styles in the player settings panel. Classic is the existing chunky sci-fi frame (unchanged). Slim is an 8px crimson border + 36×36 corner accents, all CSS-rendered (no new image assets). Borderless is four 48×48 corner brackets only — they double as drag handles since there's no frame to grab.

Window dimensions stay 1247×726 in every variant; the WebView2 (video) grows to fill the freed space, so picking slim or borderless gives the user more video.

### Files changed

- `src/Models/WingmanPlayerSettings.cs` — new `FrameVariant` enum + field on the record
- `src/UI/OverlayWindow.xaml.cs` — `frameVariant` WebMessage case + `BuildSyncScript` extension
- `src/Renderer/index.html` — `#app` class, `#frame-css` container, frame-style dropdown row, `#settings-btn` reparented inside `#video-wrap`
- `src/Renderer/style.css` — three `.frame-*` rule sets + gear repositioning in borderless
- `src/Renderer/player.js` — drag binding generalised to `.drag-zone`; dropdown change handler

### Design + plan

`wingman-ai/skills/youtube_video_player/specs/2026-05-13-frame-variants-design.md` and `.../2026-05-13-frame-variants-plan.md` in the wingman-ai repo.
```

- [ ] **Step 3: Commit**

```bash
git -C D:/PycharmProjects/wingman-player add DEVLOG.md
git -C D:/PycharmProjects/wingman-player commit -m "docs: log frame-variants session"
```

---

## Task 10: Merge / push (gated on user confirmation)

**Files:** none modified — branch handling

- [ ] **Step 1: Push the feature branch**

```bash
git -C D:/PycharmProjects/wingman-player push -u origin feat/frame-variants
```

- [ ] **Step 2: Open a PR / merge**

User decides whether to merge to main directly or via PR. Don't auto-merge — confirm with the user first.

---

## Notes & gotchas

- **First-frame flash on returning users:** A user whose last session was Slim or Borderless sees ~50ms of Classic before `BuildSyncScript` swaps `#app.className`. This is documented in the spec as acceptable. If the fireside test reveals it's worse than ~50ms or causes a visible flicker, fix-forward by writing the variant into a cookie that `index.html` reads on initial parse (out of scope for this plan; revisit only if step 12 of the fireside plan flags it).

- **`#video-wrap` positioning style:** Task 5 Step 2 has a decision branch. The existing player CSS uses one of `inset:` or individual properties (`top:/left:/width:/height:`). Read the existing rule first; pick the matching form for the overrides.

- **Gear button z-index:** The settings gear was previously a sibling of `#video-wrap`. After reparenting inside, verify it still paints above the YouTube iframe and the three click-blockers in `#video-wrap`. If it disappears under one of them, add `z-index: 10` (or appropriate value above the click-blocker stack) to `#settings-btn`. Step 6 of the fireside plan catches this.

- **The skill side is untouched:** Don't edit anything in `D:/PycharmProjects/wingman-ai/skills/youtube_video_player/`. The plan's HTTP API to the player is unchanged. If the user later asks for a voice tool (`set_frame_variant`), that's a separate skill-side feature.

- **No PowerShell prompts:** `Get-Process -ErrorAction SilentlyContinue | Stop-Process` will not prompt for confirmation. If your shell is bash (WSL), use the equivalent: `pkill -f "Wingman-Player.exe"` or skip — Windows shell access is required for the actual run anyway.

---

## Self-review checklist

Verified before handoff:

- ✓ Every step in the spec has a corresponding task or step here.
- ✓ No "TBD", "TODO", "fill in", or "similar to" placeholders.
- ✓ All file paths absolute and exact.
- ✓ All code blocks complete (no `...` truncations except for omitted unchanged surrounding code, which is always clearly demarcated).
- ✓ `FrameVariant` enum signature consistent across Tasks 1, 2, 3 (PascalCase enum members; lower-case wire values `"classic"`/`"slim"`/`"borderless"`).
- ✓ `.drag-zone` selector used identically in Task 4 (HTML), Task 5 (CSS — implicit via `.frame-*`), Task 6 (JS).
- ✓ `frame-${variant}` template literal in JS matches CSS class names `.frame-classic` / `.frame-slim` / `.frame-borderless`.
- ✓ Commit boundaries are clean (one logical concern per commit).
- ✓ Validation strategy matches the spec (fireside, no new unit tests).
