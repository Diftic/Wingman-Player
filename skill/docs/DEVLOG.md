# YouTube Video Player — Dev Log

---

## 2026-05-14 - Session 18 - Install discovery UX + minimize / show / idle auto-hide + companion player v0.5.1 release

### What landed today

Three feature areas, plus a security audit and a malware-shaped iteration that we backed out of.

1. **Install-discovery UX.** A yellow warning badge now fires in the Wingman chat panel the moment the skill activates and Wingman Player isn't installed. A new `download_player_installer` voice tool opens the MSI in the user's default OS browser (NOT the embedded WebView).
2. **Minimize / show + 15s idle auto-hide.** Player now parks itself off-screen 15s after a stop or a natural end-of-video, and resume / new playback brings it back. Voice tools `hide_player` and `show_player` for explicit control. All player-side state machinery lives in the companion repo.
3. **Companion player release v0.5.1.** Pushed tag `v0.5.1` to `Diftic/Wingman-Player`. CI built clean (0 warnings, 0 errors) and published `Wingman-Player.exe` + `Wingman-Player-Setup.msi` to https://github.com/Diftic/Wingman-Player/releases/tag/v0.5.1. The skill's `latest/download/Wingman-Player-Setup.msi` URL now resolves to this build.

### Why the warning badge (and what NOT to do)

Pre-Session-18, the only signal that the player was missing was a `logger.warning(...)` line in the Wingman server log. The user never opens that log, so the skill silently failed at first use. New approach: `self.printr.print(msg, color=LogType.WARNING)` in `prepare()` whenever the canonical install path is missing OR the user's `player_exe_path` override points at a nonexistent file. Yellow chat-panel badge appears at skill activation, includes recovery instructions.

Saved as a feedback memory ([[feedback-missing-dep-warning-badge]]) so future skills that depend on external programs use the same pattern: badge first, not just a log line.

### Why we backed out of the auto-browser-launch

Mid-session iteration: I had the warning badge auto-call `webbrowser.open(msi_url)` so the download would start immediately after activation. User correctly flagged this as **malware-shaped behaviour**: an app spawning a download dialog on startup without user consent looks exactly like a drive-by. Reverted the side effect. The badge now tells the user how to ask for the download via the new `download_player_installer` voice tool. Consent is an explicit user act, every time.

Saved this as a hard lesson: never trigger network or file actions on skill activation without explicit user consent.

### Why the wording walked back

The original badge text said *"After running the installer, just ask me to play something and the player will auto-launch."* That's wrong - path resolution happens in `prepare()` which only runs once per skill activation. If the player wasn't installed when `prepare()` ran, the skill's `PlayerClient._exe_path` is empty for the lifetime of that activation. The user has to restart Wingman (or re-activate the skill) for the new install to be picked up. Wording corrected to reflect reality. The user explicitly preferred the wording fix over implementing lazy re-resolution; everything else "works flawlessly".

### Minimize / show + idle auto-hide - design decisions

From the user's clarification:
- Hide, not close. The window object stays alive so the next play request is a warm relaunch (matches Session 13's warm-launch logic). Reuses the existing `OverlayWindow.HideOverlay(restoreFocus: false)` path.
- 15s idle timer applies only on `/player/stop` and on natural end-of-video (renderer state -> ENDED). Pause does NOT start the timer (paused is intentional, user is coming back).
- Resume always brings the player back: renderer state -> PLAYING cancels the timer AND re-shows the overlay if hidden. Skill-side `/player/play /load /next /previous /seek` also cancel the timer.
- New voice tools `hide_player` and `show_player` for explicit control independent of playback state.

### Files changed - skill side (this repo)

- `main.py` (dev + release_version): added `PLAYER_MSI_URL` class constant, the warning-badge logic in `prepare()` for both override-broken and default-missing cases, `download_player_installer` voice tool, `hide_player` voice tool, `show_player` voice tool. Imports: `webbrowser`, `LogType`.
- `player_client.py` (dev + release_version): added `async def hide(self)` and `async def show(self)` HTTP wrappers for `POST /player/hide` / `POST /player/show`.
- `default_config.yaml` (dev + release_version): prompt rubric extended with `download_player_installer`, `hide_player`, `show_player`. Updated `stop_player` description to mention 15s auto-hide.
- `release_version/youtube_video_player_v0.1.0.zip` rebuilt twice this session as wording iterated.

### Files changed - player side (Diftic/Wingman-Player, committed to v0.5.1)

- `src/UI/OverlayWindow.xaml.cs` (+87 lines): `DispatcherTimer`-driven `StartIdleHideCountdown` / `CancelIdleHideCountdown`, `HandlePlayerState(int)` switch reacting to YT.PlayerState ints (1 = PLAYING cancel + show, 0 = ENDED start), new `playerState` case in `OnWebMessageReceived`.
- `src/Services/PlayerCommandBridge.cs` (+37 lines): `EnsureOverlayHiddenAsync` mirror of `EnsureOverlayVisibleAsync`, idle-timer dispatcher wrappers.
- `src/Services/WingmanPlayerHttpServer.cs` (+72 lines): new `PostScriptAction` enum replaces the `bool showOverlay` arg on `SimpleCommandAsync`, new `/player/hide` and `/player/show` routes, all playback-starting routes cancel any in-flight idle timer.
- `src/Renderer/player.js` (+10 lines): `onPlayerStateChange` now posts `{type:'playerState', state:event.data}` via `chrome.webview.postMessage`.
- `DEVLOG.md` (Session 23 entry) + `TODO.md` (new Auto-hide / minimize section).

### Security audit (mid-session, user-requested)

Grep'd for `AIza[0-9A-Za-z_-]{20,}` across the entire skill (dev + release_version + live install) and the entire player repo. Zero matches anywhere. The only place the actual key lives is `%AppData%\ShipBit\WingmanAI\<version>\configs\secrets.yaml` - Wingman's SecretKeeper writes it there in plain text, which is Wingman's storage choice not the skill's. Player codebase has zero YouTube knowledge by design (no `api_key` / `youtube_api_key` references).

**Caveat surfaced to user**: during the grep verification, the secrets.yaml content (including the live YouTube API key and a wingman_pro JWT) was inadvertently dumped into the tool output and is now part of the conversation transcript. Recommended rotating the YouTube key on console.cloud.google.com as a precaution.

### Released

- Skill v0.1.0 (still): deployed to `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\` (8 files), Wingman restarted, processes confirmed running. No version bump - feature additions are still pre-1.0 polish.
- Wingman Player v0.5.1: tag pushed, CI build green in 2m05s, release published with both .exe and .msi assets.

### Validation done

- `python -m py_compile` clean on all skill .py files (dev + release_version).
- `dotnet build -c Release` clean on player (0 warnings, 0 errors).
- `diff -q` clean between dev tree and release_version on `main.py`, `player_client.py`, `default_config.yaml`.
- CI pipeline green; release artifacts confirmed via `gh release view v0.5.1`.

### Pending verification (fireside, next session)

End-to-end voice flow against the live Wingman + v0.5.1 player. See TODO "Live fireside verification, Session 18" for the full checklist.

---

## 2026-05-13 — Session 17 — Frame variants: design + plan complete, execution paused

### What's done

User asked for an option to remove the player backdrop. Backdrop is the only thing that's draggable today — so a straight removal would break window movement. We brainstormed and landed on **three selectable frame variants** instead, all switchable live from the in-overlay settings panel:

- **Classic** (default) — existing chunky sci-fi frame, unchanged
- **Slim** — 8px crimson border + 36×36 corner accents (CSS-rendered)
- **Borderless** — four 48×48 corner brackets only; corners double as drag handles

Window stays 1247×726 in every variant; the WebView2 grows inward to fill the freed space, so slim/borderless deliver more video. Setting is owned by the player (the skill is unaffected); persists to the existing `settings.json` via a new `FrameVariant` enum on the `WingmanPlayerSettings` record.

### Architecture decisions log (Q1–Q5)

| # | Question | Decision |
|---|----------|----------|
| 1 | Where does the user pick? | Player settings panel only (gear menu) |
| 2 | What happens to freed space when frame shrinks? | Window stays same size, video grows inward |
| 3 | Drag region in borderless? | Corner brackets only (aesthetic purity) |
| 4 | Default for existing users? | Classic (zero-surprise migration) |
| 5 | Asset strategy? | CSS-rendered for slim/borderless (no new PNGs) |

### Where things live

- **Design spec:** `specs/2026-05-13-frame-variants-design.md` — full architecture, data model, CSS, drag implementation, settings panel UI, live-switch flow, fireside test plan, rejected alternatives
- **Implementation plan:** `specs/2026-05-13-frame-variants-plan.md` — 10 tasks (~50 bite-sized steps), 7 logical commits, no new test framework (matches spec decision), all work in the `wingman-player` repo
- **Brainstorming visuals** (ephemeral, can be deleted): `.superpowers/brainstorm/1954-1778700579/` — landing, sizing, drag, visual-specs HTML mockups

### Why this is logged but not implemented yet

User paused development at the execution-choice handoff (subagent-driven vs inline). Resume by picking one or just walking through the plan task-by-task by hand. Plan is self-contained — engineer can pick it up cold.

### Out of scope reminder

This feature touches the `wingman-player` repo, not the skill code. The skill (this folder) doesn't change as part of frame variants. Skill-side voice tools for variant switching (e.g. `set_frame_variant("borderless")`) were considered and deferred — only adds value if users actually ask for voice control over a 3-option preference.

---

## 2026-05-13 — Session 16 — Post-review cleanups + first test file + bug found by the test

### What changed

Working through the three post-review items I'd queued up from Session 15. Pulled in dev source + release_version together so the two stay locked.

**1. `YouTubeSearch.daily_limit` public property.** `main.py:391` was reaching for `self._search._daily_search_limit` — single-underscore protected attribute. Exposed it as a `@property` instead. Pure ergonomics, no behaviour change.

**2. Extracted `_reprompt_api_key_dialog()` helper.** The "clear prompted_secrets guard → drop cached empty value → retrieve_secret(...)" sequence was duplicated between `_ensure_api_key_or_prompt` (auto-recovery from a missed prompt) and `set_youtube_api_key` (voice tool). One helper, both callers shrunk. `_ensure_api_key_or_prompt` is now 3 lines, `set_youtube_api_key` is 8.

**3. First tests for this skill (`tests/test_confidence.py`).** Locks down the four hand-validated cases from Session 1's DEVLOG + 8 edge cases (LLM clamping, composite capping, boost capping, empty inputs, weak similarity bands, missing snippet defence). 12 tests, all green.

### Real bug surfaced by the new test

`test_missing_snippet_does_not_crash` failed on first run with `boost == 0.15` instead of `0.0`. Investigation:

- `_normalize("")` returns `""`.
- In `heuristic_boost`, the check `channel_n in artist_n` evaluates `"" in artist_n` which is **always True** (every string contains the empty string).
- So a search result with no `channelTitle` (deleted channel, malformed payload) would silently fire the +0.15 artist-channel-match boost.

Dormant in practice — YouTube reliably returns `channelTitle` for `type=video` queries — but a real correctness hole. With LLM confidence ≥ 0.83, a spurious +0.15 pushes composite past the 0.98 auto-play threshold, meaning the skill auto-plays an unrelated video on a malformed result instead of returning a list.

Fix: guard the artist block on `if channel_n:` and the song block on `if title_n:`. Same fix in both `skills/youtube_video_player/confidence.py` and `release_version/confidence.py`.

### Files changed (both dev and release_version unless noted)

- `main.py` — `_search._daily_search_limit` → `.daily_limit`; helper extracted; two recovery methods slimmed down.
- `youtube_search.py` — `@property daily_limit`.
- `confidence.py` — empty-channel / empty-title guards on the two boost blocks.
- `tests/test_confidence.py` (dev only — tests don't ship in the release artifact) — new, 12 tests.

### Verified

- `python -m py_compile` clean on all four modified .py files (both copies).
- `pytest tests/test_confidence.py -v` → 12/12 passed.
- `diff -q` across all six file pairs (4 .py + .yaml + .json) → all clean (dev == release_version).

---

## 2026-05-13 — Session 15 — Dev source ↔ release_version sync (closing Session 14 deferral)

### What changed

Code review surfaced that the four files Session 14 only updated under `release_version/` had drifted from dev source for 9 days. Re-cutting the zip from dev source on the next release would have silently re-introduced the bundled-player path resolution and the no-GitHub-URL failure messages.

Synced `release_version/` → dev source for the four drifted files:

- `main.py` — bundled-path probe → `%LOCALAPPDATA%\Programs\Wingman Player\Wingman-Player.exe` MSI probe; failure messages embed the GitHub installer URL; module docstring rewritten for the independent-player distribution model.
- `default_config.yaml` — `description`, `player_exe_path` hint, `auto_launch_player` hint, and HTML `hint` all reframed away from "bundles a player" toward "drives the separately-installed Wingman Player".
- `__init__.py` — module docstring drops "bundled".
- `skill_installer_config.json` — `"dirs": ["player"]` → `"dirs": []` (no `player/` subfolder ships any more).

### Verified

- `diff -q` on all four file pairs returns clean (dev source == release_version).
- `python -m py_compile skills/youtube_video_player/main.py` exits 0.

### Why this matters for the next release cut

The dev source is now the authoritative copy for v0.1.0+. A future `release_version/` rebuild can be a straight copy from dev source without re-applying the Session 14 hand-edits.

---

## 2026-05-04 — Session 14 — Distribution model reversed + release_version rebuilt + install.bat bug fix

### What changed

Player and skill now ship through separate channels — the previous "skill bundles the ~174 MB player exe" approach is dead. Triggered by the user's distribution-model decision after v0.5.0 of the player went live on GitHub.

**New model:**
- **Player** distributes from `https://github.com/Diftic/Wingman-Player/releases/latest` as `Wingman-Player-Setup.msi`. Per-user install to `%LocalAppData%\Programs\Wingman Player\`. Has its own update channel (in-app "Check for updates" → MSI swap).
- **Skill** distributes via the Wingman Discord forum as `youtube_video_player_v0.1.0.zip` (62 KB, was ~170 MB) containing `install.bat`. Skill probes the player's canonical install path at runtime; on miss, surfaces a clickable GitHub link to the user via Wingman.

Reasons:
- Player gets its own update lane independent of skill releases (codec/code updates without re-distributing the skill).
- Skill stays simple — no msiexec orchestration code, no `urllib`/`aiohttp` MSI download logic.
- Clearer ownership separation — player is an app, skill is a skill, neither installs the other.

### Files changed (release_version/ only — source files at the parent untouched per scope constraint)

- **`main.py`** — Path resolution rewritten. When `player_exe_path` is empty, probe `%LocalAppData%\Programs\Wingman Player\Wingman-Player.exe` (canonical MSI install). On miss, log a warning that includes the GitHub installer URL.
- **`main.py`** — `search_and_play_youtube` and `_play_result_at` failure messages now embed the GitHub URL: *"I can't reach Wingman Player. If it isn't installed yet, download the installer from https://github.com/Diftic/Wingman-Player/releases/latest …"*. Wingman renders URLs in tool returns as clickable links.
- **`main.py`** — Module docstring rewritten to describe the new model.
- **`install.bat`** — Rewritten (see install.bat bug fix below).
- **`RELEASE_NOTES_v0.1.0.txt`** — Two-step install flow: 1) install Wingman Player MSI from GitHub, 2) run `install.bat` for the skill. Player section reframed: "ships independently from this skill" + URL.
- **`TESTER_README.md`** — Two-part install: player MSI first, then skill .zip. Recovery section split into "API key missing" and "player missing".
- **`default_config.yaml`** — `player_exe_path` hint reframed as override-only over the canonical install. `auto_launch_player` hint matches. Description text updated. `hint` HTML now links to the GitHub release.
- **`skill_installer_config.json`** — `dirs: ["player"]` cleared.
- **`__init__.py`** — Docstring drops "bundled" wording.
- **Deleted `release_version/player/`** — Renderer/ + Wingman-Player.exe (~174 MB total).

### install.bat bug fix

User test run printed BOTH "Skill install complete!" AND "ERROR: Install failed with code 1" despite a fully successful 8-file copy to `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\` (verified separately). Two compounding causes:

1. **Robocopy exit code 1 means "files copied successfully"** — not failure. Codes 0–7 are success flavours; 8+ is real error. `if %errorlevel% leq 7` was correct, but the bat structure was broken (see #2).
2. **Parenthesised IF block escape bug.** The success branch had `echo installed at the default location ^(per-user MSI install^).` — escaped parens for echo. cmd's IF block parser counts `(` and `)` for block matching *before* handling `^` escapes in some versions, so the `^)` was treated as the closing of the parenthesised IF block. cmd then ran the rest of the success-branch text as orphan commands, fell into the ELSE, and printed the error message too.

Fix:
- Replaced parenthesised `IF (…) ELSE (…)` with `goto :success / goto :failure / labels`. No parens to misalign.
- Silenced robocopy entirely: `/NJH /NJS /NFL /NDL /NC /NS /NP >nul 2>&1`. The summary table robocopy normally prints has a literal "FAILED" column header (with 0 in it on success) that panics non-technical testers regardless. Now invisible.
- Reframed the failure copy: *"Robocopy reported a real error (exit code N, anything 8 or above)"* — explicitly distinguishes real errors from success-but-busy codes.
- Output on success is now a clean *"Installing → Destination → Done → Next steps → Press any key"* sequence with no robocopy noise wedged in.

New `install.bat` is 1524 bytes; rezipped to `release_version/youtube_video_player_v0.1.0.zip` (62 KB total).

### Verified

- Robocopy in user's first test actually copied all 8 expected skill files to the destination — only the bat's wrapper output was wrong. User does NOT need to reinstall.
- New bat dry-run printed the clean output sequence end-to-end (PowerShell sandbox prevented the source-file copy in the test harness, so files-at-dest wasn't directly verified, but the output structure proves the GOTO control flow + silencing flags work).
- `release_version/main.py` `py_compile` clean after edits.

### Out of scope this session

- Source files in `skills/youtube_video_player/*.py` (the parent dir, not `release_version/`) still have the bundled-player path resolution. Not edited because user's instruction was *"only update the skill release folder"*. Sync the parent on the next non-test session.
- `DEVLOG.md`, `README.md`, `TODO.md` at the parent dir — unchanged. The release artifact doesn't need them.

---

## 2026-05-03 — Session 13 — Warm-launch player + chain playVideo() after load

### Test result confirming the gap

User retested after the Session 11 + 12 fixes. Tool *did* fire this time (Tool Execution: 2.2s), Alfred said *"Starting us off right with Muse's 'Supermassive Black Hole'..."* — but no audio. Same shape as before: video loaded, playback held.

So `--autoplay-policy=no-user-gesture-required` alone isn't enough on this Edge/WebView2 build. Or there's a timing issue where YT.Player isn't fully ready when `loadVideoById` is called on a freshly-spawned player process.

### Two-part fix

**Part 1 — Skill-side warm-launch.** `prepare()` now fires `threaded_execution(self._warm_launch_player)` which calls `_player.ensure_running()` in the background. By the time the user issues their first voice request, the player has been alive for however many seconds since skill activation — WebView2 fully initialised, YT.Player past `onReady`, surface "warm". No more cold-start race.

```python
if bool(auto_launch_player):
    self.threaded_execution(self._warm_launch_player)

async def _warm_launch_player(self) -> None:
    if self._player is None:
        return
    try:
        ok = await self._player.ensure_running()
        ...
```

This is the contingency we discussed earlier — gated on `auto_launch_player` so users who want manual control still get it.

**Part 2 — Renderer-side belt.** `__wingmanLoad` chains `setTimeout(playVideo, 250)` after `loadVideoById` / `loadPlaylist`. If autoplay-policy *did* take effect, this is a no-op (already playing). If it didn't, the explicit `playVideo()` un-stalls the held state.

```js
player.loadVideoById(args);
setTimeout(function () {
  try { player.playVideo(); } catch (_) {}
}, 250);
```

250ms is enough for `loadVideoById` to register and YT.Player to settle into a buffering/cued state where `playVideo()` is meaningful.

### Files changed

Skill (`youtube_video_player`):
- `main.py` — `prepare()` fires warm-launch, new `_warm_launch_player()` method.

Player (`wingman-player`, commit `a8fe56c`):
- `src/Renderer/player.js` — chained `setTimeout(playVideo, 250)` in `__wingmanLoad`.

Re-published, re-bundled. Live env wiped + reinstalled. Stale player killed.

### Why I bet this lands

The single most-cited fix for "WebView2 autoplay won't start" across the wild is exactly this combo: pass the policy flag *and* explicitly call play after load. Belt-and-suspenders means any one of three reasons could fail and we'd still get audio:

1. autoplay-policy flag was honored → `loadVideoById` plays directly
2. flag wasn't honored but warm-launch put YT.Player past onReady → `loadVideoById` plays anyway
3. neither — but `setTimeout(playVideo, 250)` kicks the cued video into playing

If audio *still* doesn't play after this, the issue is upstream of YT.Player itself (WebView2 audio routing, system mute, Wingman's audio bridge stealing the stream, etc.) and we'd need to verify with renderer-side console logs.

---

## 2026-05-02 — Session 12 — Re-activate on any config save, not just key entry

### Bug from real run

Tested the autoplay-policy fix — but the LLM didn't even call the tool. Tool Execution: 0ms. Alfred said *"my YouTube player module is missing or down"*.

Log trace:
```
23:54:06 - Skill 'YoutubeVideoPlayer' validated and prepared
23:54:20 - Skill 'YouTube Video Player' config updated, will revalidate on next use
23:54:36 - [User] play Supermassive Black Hole by Muse
23:54:39 - [Alfred] my YouTube player module is missing or down (Tool Execution: 0ms)
```

### Root cause

The Session 7 `update_config` override has an early-return that I didn't properly close on:

```python
if not new_input:
    return  # ← bug: skips ensure_activated() below
```

When the user saved settings without re-entering the API key (e.g. they just clicked Save, or reset some other field, or Wingman fired a config-changed event for a different reason), the input field was empty, my override early-returned, and `ensure_activated()` never ran. The skill stayed in `is_validated=False` with the tools registered but the wingman runtime treating the skill as in a "config-changed, awaiting revalidation" state — which apparently surfaces to the LLM as something like *"this skill is unavailable right now"*, leading to refusal.

### Fix

Restructured `update_config` so the re-activation check runs **unconditionally** at the end, regardless of whether the input field had a new key. The key-save path is still gated on `if new_input` (we don't want to save an empty key), but `ensure_activated()` runs whenever `is_validated` or `is_prepared` is False. Idempotent when already valid+prepared.

### Files changed

- `main.py` — `update_config` restructured. ~10 lines moved.

Mirrored to `release_version/main.py` and live `custom_skills/youtube_video_player/main.py`. No player rebuild needed.

### Outstanding question

The autoplay-policy fix from Session 11 still hasn't been validated end-to-end. The LLM never reached `search_and_play_youtube` because of this bug. After Session 12 lands, next test should:
1. Hit the tool (Tool Execution > 0ms)
2. Show "Playing X by Y" reply
3. Audio actually starts within 1-2s without a follow-up

If audio still doesn't autoplay after a successful tool call, the contingency in TODO is: warm-launch the player from `prepare()` so WebView2 is fully primed before the first `/load`.

---

## 2026-05-02 — Session 11 — Bypass Chromium autoplay policy

### Bug

Same shape as the Session 9 issue. User asks for a song, Alfred says *"Playing X"*, then the user has to say *"start that song"* — only then does playback actually begin. Session 9's auto-show fix wasn't enough.

### Why Session 9 didn't solve it

Order of operations matters:

1. HTTP `/player/load` arrives
2. `bridge.ExecuteAsync` dispatches `__wingmanLoad(...)` → `player.loadVideoById()` runs *immediately* in the WebView2
3. Chromium evaluates the autoplay policy at this point — surface has no prior user gesture (the WebMessage from C# isn't counted as one), so playback is held
4. *Then* we call `EnsureOverlayVisibleAsync` to bring the window on-screen
5. Window becomes visible — but YouTube already decided "no autoplay"

The auto-show fix is correct for visibility, but it loses the race against Chromium's autoplay decision. Could have reordered (show first, then dispatch), but that just reduces the probability — Chromium's gesture-tracking is still likely to mark our load as untrusted.

### Real fix

Override the policy at the WebView2 environment level:

```csharp
var envOptions = new CoreWebView2EnvironmentOptions
{
    AdditionalBrowserArguments = "--disk-cache-size=0 --autoplay-policy=no-user-gesture-required"
};
```

`--autoplay-policy=no-user-gesture-required` is a Chromium command-line flag that disables the autoplay restriction entirely for this WebView2 instance. It's safe in our context because:

- This WebView2 is embedded in our own player exe, not browsing arbitrary user-supplied URLs
- The only content loaded is `https://wingman.local/index.html` (our renderer) which embeds the YouTube IFrame
- The user's voice request *is* the intent — we don't need Chromium gesture-tracking to gate it

### Files changed (player repo, commit `9447fb2`)

- `src/UI/OverlayWindow.xaml.cs` — added the autoplay flag to `AdditionalBrowserArguments`

### Mirror

Re-published. Both `release_version/player/` and live `custom_skills/youtube_video_player/player/` got the new exe. Stale running `Wingman-Player.exe` killed so the next `/player/load` from the skill spawns a fresh process with the new flag.

### Verification approach

User says "play X by Y". Log should show:
- `search_and_play_youtube` returns success
- Alfred relays "Playing X"
- Audio actually starts within ~1-2s of the tool returning
- No follow-up "start that song" needed

If audio still doesn't start: investigate whether the WebView2 flag actually applied (it could be silently ignored on some Edge channel versions). Diagnostic: open Edge dev tools on the WebView2, check `chrome://flags` or run `navigator.userActivation.isActive` in the console.

---

## 2026-05-02 — Session 10 — Icon + skill logo adoption

User supplied a 200×200 RGBA source — red V on dark hex. Two artifacts generated:

**Player exe / tray icon** (`wingman-player` repo):
- `src/Assets/source_icon.png` — checked in as the canonical source for future regen
- `src/Assets/icon.ico` — multi-resolution: 16, 24, 32, 48, 64, 128, 256 (largest slot upscaled via PIL LANCZOS since source is 200×200)
- Player rebuilt — `<ApplicationIcon>` in csproj picks up the new icon at compile time, embedded as RT_ICON resource. Tray icon also picks it up via the existing `wingman_player.Assets.icon.ico` embedded-resource lookup in `TrayIcon.cs`.
- Player repo commit `6c47662`.

**Skill logo** (this repo):
- `logo.png` next to `default_config.yaml` — Wingman's `module_manager.py` auto-loads any `logo.png` next to a skill's config and exposes it via the skill API. No config field needed.
- Mirrored to `release_version/logo.png` and live `custom_skills/youtube_video_player/logo.png`.
- `skill_installer_config.json` `files` list extended with `"logo.png"` so packaged distributions include it.

### Verification

- exe mtime > ico mtime — rebuild captured the icon. Quick check via filesystem timestamps.
- Live skill folder contains the logo.png — Wingman should display it in the skill UI on next reload.

---

## 2026-05-02 — Session 9 — Auto-show on /load + splash removal + frame retighten

### "Video loaded but not playing" — root cause

User asked for a video, search returned the right result, `/player/load` succeeded — but the video sat at frame 0 until the user said "play that video" 20s later. Two contributing factors:

1. **Off-screen WebView2 surface.** The overlay launches `Visibility="Collapsed"` and parks itself at `Left = -(FrameDisplayWidth + 100)`. The WebView2 child HWND is alive (so YT.Player initialises), but the surface is hidden until the user presses F8.
2. **Browser autoplay policy.** YouTube's IFrame uses Chromium's media-autoplay heuristics. On a hidden / never-interacted surface, `loadVideoById` triggers a network load but holds back actual playback. A subsequent `playVideo()` (which is what `/player/play` does) bypasses the policy because it's a programmatic play after surface init.

The user's hypothesis ("the splash screen on video startup might be to blame") was directionally right — anything that keeps the user from seeing/interacting with the WebView2 surface compounds the autoplay block.

### Fix part 1 — auto-show on playback-starting commands

`PlayerCommandBridge` got a new `EnsureOverlayVisibleAsync()` method that dispatches `OverlayWindow.EnsureVisible()` on the UI thread. Wired into the HTTP server:

| Route | showOverlay | Reasoning |
|-------|-------------|-----------|
| `/player/load` | ✓ | User just asked for a video — show it |
| `/player/play` | ✓ | Resume → user wants to see |
| `/player/next` | ✓ | Different content — show |
| `/player/previous` | ✓ | Different content — show |
| `/player/seek` | ✓ | User is watching — keep visible |
| `/player/pause` | ✗ | User is stopping — don't pop |
| `/player/stop` | ✗ | User is unloading — don't pop |
| `/player/state` (GET) | ✗ | Status query, no UI side-effect |

Net result: voice "play X" produces a visible window with playback running. The autoplay block disappears because the surface is on-screen at the moment `loadVideoById` fires.

### Fix part 2 — remove SplashWindow entirely

Per user direction. The splash existed solely to gate the user on the startup update check. That gate was a leftover from the original PulseNet-Player UX and adds friction in the voice-driven flow. Removed:

- `src/UI/SplashWindow.xaml`
- `src/UI/SplashWindow.xaml.cs`
- Splash creation + `ShowUpdateBanner` call in `App.OnStartup`
- `OnFirstHotkey` splash-close handler in `App.OnStartup`

The startup `UpdateChecker.CheckAsync` still runs. Its result now lands on `PlayerCommandBridge.LatestUpdate` (new property). The HTTP server augments `/player/state` responses with `updateAvailable: true, latestVersion: "v0.6.0"` whenever the bridge has a non-null update result.

### Skill side: surface update info to the user

`get_player_status` now includes `updateAvailable` and `latestVersion` in its response when the player reports them. Default config prompt updated with a one-line rubric:

> *When `get_player_status` returns `updateAvailable: true`, mention it once: "Heads up: Wingman Player v{latestVersion} is available. Open the player's Settings menu and click 'Check for updates' to install." Don't keep nagging.*

Existing in-overlay **Check for updates** button in the settings panel still handles the actual install — no change there.

### Frame retighten

Frame canvas: `1252×731` → `1247×726` (5px on each axis). User feedback was that the video rect looked a bit narrow inside the frame bezel. Three places kept in sync: `Constants.cs` (drives WPF window), `style.css#app` (canvas), `style.css#frame-base` (frame element).

### Files changed (player repo, commit `6f22afe`)

- `src/App.xaml.cs` — splash removal, update info → bridge
- `src/Services/PlayerCommandBridge.cs` — `LatestUpdate` property, `EnsureOverlayVisibleAsync()`
- `src/Services/WingmanPlayerHttpServer.cs` — auto-show on load/play/next/prev/seek, augment `/state` with update info
- `src/Constants.cs` — `FrameDisplayWidth/Height` 1247/726
- `src/Renderer/style.css` — `#app` and `#frame-base` resized 1247×726
- `src/UI/SplashWindow.xaml` + `.xaml.cs` — deleted

### Skill side

- `main.py` — `get_player_status` augments with update info
- `default_config.yaml` — prompt rubric updated

Re-published, re-bundled, mirrored to live (`%AppData%\...\custom_skills\youtube_video_player\`).

---

## 2026-05-02 — Session 8 — next/previous_track walks search-result cache

User reported: pause / resume / stop all worked from voice, but `next_track` was a silent no-op. Hypothesis (correct): the YouTube IFrame API's `nextVideo()` only advances within a loaded *playlist*. When the user picks an item via `play_search_result(index)`, the skill calls `loadVideoById` for a single video — no playlist context, no queue to advance through, `nextVideo()` is a no-op.

User's preference (from the same exchange): *"It just plays right now, don't need to put it in the queue."* So loading the 5 search results as an actual YouTube playlist via `loadPlaylist` was the wrong direction — they want single-video playback with "next" meaning "another song from those options I just heard about".

### Fix

- New `_last_played_index: int` instance state (default `-1`) tracking which slot in `_last_results` is currently playing.
- New `_play_result_at(zero_based_index)` helper — handles the load + index-update for both `play_search_result` and the next/previous walkers. Single source of truth.
- `search_and_play_youtube` auto-play branch now sets `_last_played_index = 0` so a follow-up "next" works after the auto-play case too.
- `next_track`: if `_last_results` has more entries past the current index, plays the next one. At the end of the list returns *"That was the last result from the previous search. Ask me to find more if you want another."* — leaves room for a future quota-aware "find more" path.
- `previous_track`: walks backward symmetrically. Returns *"Already on the first result from the last search."* at index 0.
- Both fall back to the player's native `next()` / `previous()` when no search-result cache exists — preserves real-playlist behaviour for any future flow where the skill loads a YouTube playlist via the source-agnostic `/player/load` schema.

### Updated tool descriptions

The @tool descriptions now name the cache-walk behaviour explicitly so the LLM doesn't guess. *"Walks forward through the most recent YouTube search results"* is the headline.

### Files changed

`main.py` only. Synced to `release_version/main.py` and live `custom_skills/youtube_video_player/main.py`. No player rebuild needed (the player's HTTP `/player/next` route is unchanged — it's just used less now, only when a real playlist context exists).

---

## 2026-05-02 — Session 7 — Force re-activation after UI key entry

### Bug from real run

User saved the API key via the new UI text field. Voice request: *"play me something on YouTube please."* Alfred replied: *"Seems I can't access YouTube right now, boss."* — Tool Execution: 0ms. Log evidence:

```
19:48:02 - Auto-activated skill 'YouTube Video Player' failed to activate: Missing secret 'youtube_api_key'
19:48:11 - Skill 'YouTube Video Player' config updated, will revalidate on next use.
19:48:11 - [Alfred] Secret saved
...
19:48:49 - [Alfred] Seems I can't access YouTube right now... Tool Execution: 0ms
```

The LLM never called any tool. Why? **The skill was stuck in failed-validation state.** When initial activation failed (key missing), `ensure_activated()` returned False. When the user later pasted the key via the UI field and our `update_config` saved it, `super().update_config()` set `is_validated = False` — but **nothing ever calls `ensure_activated()` again** for an auto-activated skill that already failed. The "lazy revalidation on next use" comment is misleading: revalidation only re-fires through the `activate_capability` LLM tool path (`open_ai_wingman.py:1969, 2005`), which auto-activated skills bypass entirely.

So the skill's tools were never registered with the LLM's toolbox. Alfred had no `search_and_play_youtube` to call, fell back to dialog.

### Fix

`update_config` override now calls `await self.ensure_activated()` after a successful secret save, but only if the skill is in a not-validated/not-prepared state. Walks the skill from failed state through `validate()` → `prepare()` → ready, which:

1. Re-validates (now passes — key is in SecretKeeper)
2. Runs `prepare()` (which subscribes `secret_changed`, builds `YouTubeSearch` with the new key, builds `PlayerClient`)
3. Sets `is_validated = True`, `is_prepared = True`
4. Skill's tools become visible to the LLM on the next user request

Idempotent: if the skill is already validated and prepared (e.g. user is rotating an existing key), the early-return in `ensure_activated` makes this a no-op.

### Why super().update_config alone wasn't enough

The standard `Skill.update_config(new_config)` just sets `is_validated = False` and assumes "something will revalidate later." For most skills that's fine — the `activate_capability` tool path eventually triggers it. But for `auto_activate: true` skills where the initial activation failed, there's no later trigger. The skill stays dormant forever (or until Wingman restart). My override closes that gap.

### Files changed

- `main.py` — added the `ensure_activated()` call at the end of `update_config`. ~10 new lines.

Mirror: `release_version/main.py` and live `custom_skills/youtube_video_player/main.py` re-synced.

### Verification approach

The user should now: paste the key in the field, save, immediately ask Alfred to play something. Without restart, the request should hit `search_and_play_youtube` (Tool Execution > 0ms in the log). If logs still show 0ms, there's a deeper Wingman-side caching of the LLM's toolbox we haven't accounted for, in which case the user may need to deactivate-then-reactivate the skill to refresh.

---

## 2026-05-02 — Session 6 — In-UI API key input field

User requested a button in the skill settings menu that triggers the secret prompt. Wingman's `CustomPropertyType` enum has no BUTTON or ACTION variant (`STRING / TEXTAREA / NUMBER / BOOLEAN / SINGLE_SELECT / VOICE_SELECTION / SLIDER / AUDIO_FILES / AUDIO_DEVICE / COLOR`), so a literal button isn't feasible without changing Wingman itself. User pivoted: a text field inside the skill settings is fine.

### Implementation

**`default_config.yaml`** — added `youtube_api_key_input` (STRING property) at the top of `custom_properties`. Hint walks the user through pasting the key plus the link to `console.cloud.google.com` for first-time setup.

**`main.py`** — overrode `update_config(new_config)`. Skill-base's default `update_config` invalidates the validation cache so the next request re-reads config. We extend that:

1. Look for `youtube_api_key_input` in the new config's properties
2. If non-empty: save it as `youtube_api_key` to SecretKeeper (which triggers `secrets_saved` → our existing `secret_changed` callback — that's the canonical path, so no duplicate work)
3. Best-effort clear the input field's `prop.value` on the live config object so the key doesn't sit in plaintext on screen. Whether this persists to disk depends on Wingman's write-back behaviour; if not, the user can clear the field manually.

### UX flow now

The skill has three converging recovery paths for a missing/dismissed API key:

- **(Auto) Tool-triggered re-prompt** — User says "play X", skill detects missing key, re-broadcasts the secret-prompt dialog. *No user knowledge required.* (Session 5)
- **(Explicit) Voice tool** — User says "set the YouTube API key", skill re-broadcasts the dialog. (Session 2)
- **(UI)** **Paste field in skill settings** — User opens skill settings, pastes key into the visible text field, saves. *No dialog at all.* (Session 6)

Three paths because each suits different user contexts: the auto path catches users who don't know about the others, the voice path is a power-user shortcut, the UI path is for users who prefer typing into a settings UI over talking to a dialog.

### Files changed

- `default_config.yaml` — new property at top of `custom_properties`
- `main.py` — `update_config` override (~30 lines added)

Mirror: `release_version/` synced; live test env reinstalled at `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\` (user had swept the folder again before this session).

---

## 2026-05-02 — Session 5 — Auto-reprompt for missing API key

### The Session-2 fix wasn't enough

User dismissed the API key prompt during a fresh activation. After dismiss, "no way to input an API anymore". My Session-2 fix added two recovery paths (the `secret_changed` callback for users who navigate to Wingman Settings → Secrets, and the `set_youtube_api_key` voice tool for users who know to ask). But neither is *discoverable* — the user has to know either Wingman has a Secrets UI or that the magic phrase exists. In practice they don't, and the skill feels broken.

### Fix: automatic recovery on tool invocation

New `_ensure_api_key_or_prompt()` helper. When called:
- If key present → return True, proceed normally.
- If missing → clear `secret_keeper.prompted_secrets["youtube_api_key"]` (the per-session "already prompted" guard), drop any cached empty value, then call `retrieve_secret(...)` which broadcasts a fresh `PromptSecretCommand` over the websocket. Returns False with a friendly message asking the user to fill in the dialog and try again.

`search_and_play_youtube` now calls this *first*. The flow becomes:

1. User dismisses initial prompt → key missing.
2. User says "play *X*" expecting it to work.
3. Skill notices missing key → re-broadcasts the prompt → dialog re-appears.
4. Tool returns: *"I need a YouTube Data API key first. I've reopened the key prompt — paste your key in the dialog, then say your request again."*
5. User pastes key → SecretKeeper saves → publishes `secrets_saved` → `secret_changed` callback wires the key into the running search instance.
6. User repeats "play *X*" → works normally.

No magic phrase. No Settings UI hunting. The skill self-heals on the next natural request.

### Set-the-key voice tool stays

`set_youtube_api_key` is still useful for explicit recovery (e.g. user wants to rotate their key, or wants the prompt to appear without making any other request). Kept as a power-user / explicit path. Most users will never invoke it because the auto-recovery handles their case.

### Files changed

- `main.py` — added `_ensure_api_key_or_prompt()` helper; `search_and_play_youtube` calls it first instead of just returning a "key isn't configured" error.

### Mirror

Re-synced `release_version/main.py` from dev source. Reinstalled live test env at `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\` with the full bundle (including the rebuilt crimson player from Session 4).

---

## 2026-05-02 — Session 4 — Crimson recolor of the bundled player

User flagged: settings panel still PulseNet-cyan, clashes with the Wingman frame. Decision: match the existing crimson toolkit button at (1133, 615), full scope (every cyan accent in the player).

### Palette swap

| Old (PulseNet cyan) | New (Wingman crimson) |
|---------------------|------------------------|
| `#22d3ee` (cyan-400) | `#f87171` (red-400) |
| `#67e8f9` (cyan-300) | `#fca5a5` (red-300) |
| `#cfeefa` (cyan-tinted text) | `#fee2e2` (red-tinted text) |
| `rgba(34, 211, 238, X)` | `rgba(248, 113, 113, X)` (alpha preserved) |

Same alpha values across all `rgba()` forms — only the hue changes. Glow intensities, fade levels, and box-shadow visibility all stay identical.

### Files changed (in `wingman-player` repo)

- `src/Renderer/style.css` — 38 instances. Settings panels (main / miniplayer / streamer info), version banner, search results, scrollbar, etc.
- `src/Renderer/banner.css` — 10 instances. Mini "now playing" banner.
- `src/UI/SplashWindow.xaml` — 7 instances. Top accent line, update banner border + background, hotkey label, prompt text colors.
- `src/UI/TrayIcon.cs` — 1 instance. Fallback icon color (only used if the embedded .ico fails to load).

Commit `011d8e7` in `Diftic/Wingman-Player`. Build green, 0/0.

### Re-bundle

Re-published the player with `dotnet publish -c Release ...` and copied the new exe + Renderer/ into:
- `release_version/player/` (skill artifact for distribution)
- `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\player\` (live test env)

Both 169MB. Both contain the crimson-themed player.

### Verification approach

CSS/XAML changes are visually verified through use, not unit tests. Build catches XAML syntax errors (clean). Runtime check: the user reloads the skill in Wingman, `<skill_dir>/player/Wingman-Player.exe` launches, and the settings panel should now show red accents instead of cyan.

---

## 2026-05-02 — Session 3 — Skill rename + bundle the player

### Two bundled changes

**(1) Rename:** `wingman_youtube` → `youtube_video_player`. Touches every layer:
- Folder: `skills/wingman_youtube/` → `skills/youtube_video_player/`
- Module path in `default_config.yaml`: `skills.wingman_youtube.main` → `skills.youtube_video_player.main`
- Class: `WingmanYoutube` → `YoutubeVideoPlayer`
- Skill name + display name in `skill_installer_config.json`: `youtube_video_player` / "YouTube Video Player"
- `install.bat` DEST: `custom_skills\youtube_video_player`
- Quota counter filename: `wingman_youtube_quota.json` → `youtube_video_player_quota.json`
- All docs (`README.md`, `RELEASE_NOTES_*.txt`, `TESTER_README.md`, `DEVLOG.md`, `TODO.md`) reworded

**(2) Bundle the media player.** Goal: users get one download, not two separate ones.

The skill now ships `Wingman-Player.exe` and its `Renderer/` folder inside `release_version/player/`. At runtime, `prepare()` resolves the player path:

1. If user set `player_exe_path` (custom property) → use that
2. Else compute `<skill_dir>/player/Wingman-Player.exe` and use it if present
3. Else log a warning; auto-launch will fail gracefully

Bundle size: ~170MB. The .NET 9 runtime + WebView2 native deps are baked into the single-file exe so users have zero prerequisites — but the skill zip is no longer Discord-free-tier-friendly. Distribution shifts toward Discord Nitro forums, GitHub releases, or whatever channels Wingman's eventual MCP-server distribution uses.

### Build pipeline for the bundle

```bash
cd D:\PycharmProjects\wingman-player
dotnet publish src/wingman_player.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts
```

Output: `artifacts/Wingman-Player.exe` (174MB) + `artifacts/Renderer/` (2MB) + some .pdb/.xml files we don't need.

Then copy into `skills/youtube_video_player/release_version/player/`. Future automation: a small `update_release.py` script that runs the publish + copy in one step (matching SC_Accountant's pattern).

### Auto-launch + auto-update interaction

- Skill auto-launches `<skill_dir>/player/Wingman-Player.exe` via `subprocess.Popen` (existing `PlayerClient.ensure_running` logic, unchanged).
- The player has its own `UpdateChecker` that polls `Diftic/Wingman-Player` releases and self-updates in place via `SelfUpdateService`. That keeps working — it overwrites the bundled exe with whatever's newer on GitHub. The skill folder keeps the originally-bundled snapshot until next install.
- If the user reinstalls the skill (running `install.bat` again), the auto-updated exe is overwritten with the bundled version. Acceptable behaviour: reinstall is a deliberate "back to clean state" action.

### Files changed

Skill source:
- `default_config.yaml` — module path, name, display_name, description, `player_exe_path` reframed as override-only
- `__init__.py`, `main.py` — class rename + bundled-path resolution logic in `prepare()`
- `youtube_search.py` — quota filename namespaced to skill
- `skill_installer_config.json` — skill_name, display_name, added `dirs: ["player"]`
- `README.md` — full rewrite for the bundled-player story
- `release_version/install.bat`, `release_version/RELEASE_NOTES_v0.1.0.txt`, `release_version/TESTER_README.md` — rewritten
- New: `release_version/player/Wingman-Player.exe`, `release_version/player/Renderer/`

Live test env (`%AppData%\ShipBit\WingmanAI\custom_skills\`):
- Old `wingman_youtube/` removed
- New `youtube_video_player/` installed with full bundle

### Validation

- `py_compile` clean on all 5 .py files after the rename + bundled-path logic
- Live folder structure: `youtube_video_player/{python files}` + `youtube_video_player/player/{Wingman-Player.exe, Renderer/}` = 169MB
- Only `.exe` + `Renderer/` from publish output are bundled — `.pdb` (debug symbols) and `.xml` (IntelliSense docs) stripped, not needed at runtime

### Open follow-up surfaced this session

Settings panels in `player/Renderer/style.css` still use PulseNet-era cyan (`#22d3ee` / `#67e8f9`), which clashes with the Wingman frame artwork (red/dark sci-fi tones, crimson toolkit button). Tracked in TODO. Awaiting palette decision from user before implementation.

---

## 2026-05-02 — Session 2 — API key recovery + release packaging

### Bug surfaced from initial test

User activated the skill in Wingman, the SecretKeeper popped its prompt for `youtube_api_key`, and a misclick dismissed the dialog. After dismissal, no way to re-enter the key short of restarting Wingman.

**Root cause** (in `services/secret_keeper.py:93`):
```python
if not secret and prompt_if_missing and not key in self.prompted_secrets:
    await self._connection_manager.broadcast(PromptSecretCommand(...))
    self.prompted_secrets.append(key)
```
`prompted_secrets` is a session-scoped list — once `youtube_api_key` is in it, subsequent `retrieve()` calls won't re-prompt. The list is meant to prevent annoying repeat prompts, but the side effect is no recovery path from an accidental dismissal.

**Important context** discovered during the dig (`services/tower.py:183-195`): `MISSING_SECRET` errors are **non-fatal** — `wingman.prepare()` runs even when a secret is missing, so the skill's `prepare()` runs, and the `secret_keeper.secret_events.subscribe("secrets_saved", self.secret_changed)` line in `skill_base.py:503` is in effect even in the missing-key state. So a `secret_changed` callback fires correctly when the user later enters the key via Wingman's Settings → Secrets UI.

### Fix — two recovery paths

**Path 1 (passive, the bulletproof one):** Override `secret_changed` in the skill. When SecretKeeper publishes `secrets_saved` (which happens any time the user saves through the Settings UI), the override picks up `youtube_api_key`, stores it on the skill, and updates the existing `YouTubeSearch` instance in place via a new `update_api_key()` method. No restart required.

**Path 2 (active, voice-driven):** New `set_youtube_api_key` tool. When invoked, it:
- Removes `"youtube_api_key"` from `secret_keeper.prompted_secrets` (clearing the per-session "already asked" guard)
- Drops any cached empty value from `secret_keeper.secrets`
- Calls `retrieve_secret(...)` — which now broadcasts a fresh `PromptSecretCommand` and reopens the dialog

The user can now say *"set the YouTube API key"* / *"my YouTube key is missing"* to reopen the prompt. The actual save path is still Path 1 (`secret_changed` callback) because `retrieve_secret` returns immediately with empty string while the dialog awaits user input.

### Refactor for shared init

Extracted `_init_search(api_key)` helper that both `prepare()` and the recovery paths use. Caches `_cache_dir`, `_daily_search_limit`, `_cache_ttl_seconds` as instance state in `prepare()` so the recovery paths don't need to re-read config. Avoids repeating the lookup work across three call sites.

### Search-side hardening

- `YouTubeSearch.__init__` accepts empty key without crashing (just stores `""`).
- New `has_api_key()` boolean and `update_api_key(key)` setter.
- `search()` returns `None` cleanly with a log warning if called without an API key.

### LLM prompt update

Added the new tool to the prompt in `default_config.yaml`:
- *"User says 'set my YouTube key', 'the YouTube key is missing', or searches are returning 'API key isn't configured' → `set_youtube_api_key`"*

`search_and_play_youtube` now returns a clearer message when the key is missing: *"YouTube API key isn't configured. Open Wingman Settings → Secrets to enter your YouTube Data API key, or say 'set the YouTube API key' to reopen the prompt."*

### Release packaging — `release_version/` directory

Per user's reminder of the SC_Accountant convention. Skill now ships a `release_version/` subdirectory containing:

- 6 distribution files (`__init__.py`, `confidence.py`, `default_config.yaml`, `main.py`, `player_client.py`, `youtube_search.py`)
- `install.bat` — robocopies all distribution files into `%AppData%\ShipBit\WingmanAI\custom_skills\wingman_youtube\` (excludes `install.bat`, `RELEASE_NOTES_*.txt`, `TESTER_README.md` from the copy via `/XF`)
- `RELEASE_NOTES_v0.1.0.txt` — what's in this version, requirements, quota notes, known limitations
- `TESTER_README.md` — install steps + what to test + how to report findings

This is the artifact that gets zipped and dropped in the Discord forum, or run locally via `install.bat` to push into the live `custom_skills/` folder. The dev source in `D:\PycharmProjects\wingman-ai\skills\wingman_youtube\` is the authoritative copy; `release_version/` is built from it.

### Dev → live → release flow

| Stage | Path | When updated |
|-------|------|-------------|
| Dev source | `D:\PycharmProjects\wingman-ai\skills\wingman_youtube\` | Every code change |
| Live test | `%AppData%\ShipBit\WingmanAI\custom_skills\wingman_youtube\` | Mirrored when iterating during a test session, OR via `install.bat` from `release_version/` |
| Release artifact | `D:\...\skills\wingman_youtube\release_version\` (and zipped) | When cutting a versioned release for testers / Discord |

### Files changed

- `youtube_search.py` — `update_api_key`, `has_api_key`, empty-key tolerance
- `main.py` — `_init_search` helper, `secret_changed` override, `set_youtube_api_key` tool, clearer missing-key error from `search_and_play_youtube`
- `default_config.yaml` — new tool documented in prompt + intent rubric
- `release_version/` — new directory with 9 files (6 distribution + 3 metadata)

Live `custom_skills/wingman_youtube/` updated with `main.py`, `youtube_search.py`, `default_config.yaml` so the user can re-test immediately without an installer round-trip.

### Validation

- `py_compile` clean on all 5 .py files
- The `secret_changed` path is testable end-to-end only through Wingman; pending fireside re-test
- The `set_youtube_api_key` voice path requires the LLM to pick the right tool — added explicit triggers to the prompt rubric

---

## 2026-05-02 — Session 1 — Initial scaffold

### Goal

Build the YouTube skill counterpart to the Wingman Player overlay's HTTP command surface. Voice-driven entry point: user says "play X by Y" → skill searches YouTube → either auto-plays the top result (high confidence) or speaks a top-5 list and waits for the user to pick. Plus a full transport surface (pause/resume/stop/next/previous/seek/state) routed through to the player.

### Requirements (from user)

1. "Play *song* by *artist*" → top 5 results, OR auto-play if confidence ≥ 98%
2. "Songs by *artist*" → top 5 list, user picks → plays
3. "Play *song*" → top 5 list, user picks → plays
4. Transport: pause / stop / next / previous / play / seek to timestamp. **No volume control** (Wingman / OS handles audio routing).
5. Player must be controllable by *other* skills in the future — generic "media player for Wingman as a whole".

### Design decisions

**Layered architecture (driven by req 5).** The player is a media-source-agnostic transport. YouTube-specific concerns (search, API key, quota, confidence) live in this skill. Player's `POST /player/load` takes `{source: "youtube", videoId, ...}`. Future Spotify/local-file skills will use the same `/player/load` with a different `source` discriminator — no player-side changes needed.

**Confidence as B + C** (per user choice from earlier conversation):
- **(B)** LLM provides `llm_confidence` ∈ [0, 1] based on how clearly the user named what they want
- **(C)** Skill applies a heuristic boost ∈ [0, 0.30] from the top YouTube result:
  - Channel matches the artist (substring or 60%+ similarity) → +0.15
  - Title contains the song name (substring or 50%+ similarity) → +0.10
  - Weaker similarity matches give smaller boosts
- Composite = LLM + boost, capped at 1.0
- Auto-play if composite ≥ `auto_play_threshold` (default 0.98 — strict on purpose; cheaper to show a list than to play wrong)

**API key stays in the skill, never the player.** Player codebase has zero YouTube knowledge. Skill pulls the key via `await self.retrieve_secret("youtube_api_key", ...)` — Wingman's SecretKeeper prompts on first run and stores locally.

**Quota: soft cap at 100 searches/day.** Each search.list call costs 100 quota units; Google's free tier is 10,000/day → ~95 searches as the practical ceiling. Skill caps at 100 (configurable) and persists the counter to `quota.json` keyed by UTC date so it resets at midnight UTC (matches Google's reset). In-memory query cache (default 60 min TTL) makes repeat searches free.

**Player auto-launch.** Skill polls `127.0.0.1:17330/player/state` on every command. If the port doesn't respond, falls back to `subprocess.Popen(player_exe_path)` then polls until the port comes up (15s default). User configures `player_exe_path` once. The player's existing `Mutex` in `Program.cs` correctly prevents double-launch on race conditions.

**Audio responses.** Tool functions return strings; Wingman's TTS speaks them. For the top-5 list case, the return string is a numbered list designed to be read aloud naturally.

### File layout

| File | Lines | Purpose |
|------|-------|---------|
| `main.py` | ~370 | `WingmanYoutube(Skill)` with all 9 tools and `validate`/`prepare` lifecycle |
| `youtube_search.py` | ~165 | YouTube Data API v3 client; in-memory query cache; per-day quota counter persisted to disk |
| `player_client.py` | ~165 | Async HTTP client for `127.0.0.1:17330/player/*`; auto-launch via `subprocess.Popen` |
| `confidence.py` | ~75 | Composite confidence math (LLM + heuristic boost) |
| `default_config.yaml` | ~150 | Wingman skill manifest; LLM prompt with confidence rubric; 7 custom_properties |
| `skill_installer_config.json` | 13 | File manifest for the Wingman installer |
| `__init__.py` | 9 | Version stamp |
| `README.md` | ~140 | User-facing setup guide including YouTube API key walkthrough |

### Tools exposed

| Tool | When the LLM picks it |
|------|------------------------|
| `search_and_play_youtube(query, song?, artist?, llm_confidence)` | Any "play X" / "find X by Y" / "play something by Y" |
| `play_search_result(index)` | User picked from a previous top-5 list ("play 2", "the third one") |
| `pause_player` | "Pause" |
| `resume_player` | "Play" / "Resume" / "Continue" with no new content |
| `stop_player` | "Stop" |
| `next_track` | "Next" / "Skip" within a playlist |
| `previous_track` | "Previous" / "Back" |
| `seek_player(seconds)` | "Go to 1:30" / "Skip ahead 30 seconds" (LLM converts time to seconds) |
| `get_player_status` | "What's playing?" / lookup before relative seek |

### Custom properties

All optional with defaults; live in `default_config.yaml` and exposed in the Wingman UI:

- `daily_search_limit` (100) — soft cap on searches per UTC day
- `cache_ttl_minutes` (60) — repeat-query cache window
- `auto_play_threshold` (0.98) — composite confidence above which top result auto-plays
- `player_host` (127.0.0.1) — Wingman Player command server host
- `player_port` (17330) — command server port
- `player_exe_path` (empty) — path to `Wingman-Player.exe` for auto-launch
- `auto_launch_player` (true) — toggle auto-launch behaviour

### Validation

Standalone testing without Wingman runtime:

- `python -m py_compile` clean on all 5 .py files (no syntax errors)
- `yaml.safe_load(default_config.yaml)` parses cleanly; all expected keys present; all custom_property ids match what `main.py` retrieves
- `json.load(skill_installer_config.json)` parses; file list matches what's actually on disk
- Confidence math, four cases:
  - Strong match (clear song + artist + matching top result, llm=0.90) → composite **1.000** → auto-plays ✓
  - Weak match (clear request, unrelated top result, llm=0.90) → composite **0.950** → stays in list mode ✓
  - Artist-only request (llm=0.50, channel matches artist) → composite **0.650** → list mode ✓
  - Heuristic-only artist match → boost **0.150** → matches the +0.15 spec ✓

End-to-end testing through Wingman is the fireside test (see TODO).

### Out of scope this pass

- **Live-stream detection** — the search wrapper supports `eventType=live` via parameter but the tool doesn't surface it yet. If a user asks "is X streaming right now" we'd add a `find_livestream` tool that calls `search.list?eventType=live&channelId=...` (100 quota units, would want short-cache).
- **Push state events from player → skill.** Today the skill polls `/player/state` when it needs to know what's playing. If the fireside test reveals polling latency is annoying, add an optional webhook URL the skill registers with the player.
- **Skill ↔ Wingman log-source integration.** Other Wingman skills (SC_LogReader, etc.) emit events into Wingman's log bus. Wingman YouTube currently only receives tool calls and returns strings — no event subscriptions. Could add later if e.g. we want to auto-pause on game-state changes.
- **Multi-source schemas.** Player accepts `{source: "youtube", ...}` already; Spotify/local-file sources will need their own resolver classes mirroring `youtube_search.py` + `player_client.load_*`. Out of scope until those skills exist.

### Distribution

Per user: distributed as a `.zip` dropped in a Discord forum until Wingman gets MCP-server-based skill distribution. No git repo of its own — lives inside `wingman-ai/skills/wingman_youtube/`.

To package a release, zip the directory minus `__pycache__` and DEVLOG/TODO/README. The `skill_installer_config.json`'s `files` list defines what the Wingman installer expects.

### Why each module exists

- `confidence.py` — pure functions, no IO, no Wingman dependency. Easy to unit-test (and was, standalone). Decouples the policy decision (auto-play vs list) from how we got the data.
- `youtube_search.py` — owns API key lifetime (only used inside this module), quota state, cache state. Single responsibility: "given a query, give me up to 5 embeddable video items, or None if I can't right now." Network failures, 403s, quota exhaustion all return None — caller decides how to surface.
- `player_client.py` — single responsibility: talk to a localhost HTTP player. Auto-launch logic lives here so `main.py` doesn't deal with subprocesses. All methods return bool (success/fail) or None (server not reachable) — caller composes higher-level UX.
- `main.py` — orchestration only. Pulls config, instantiates the two service classes, exposes tool methods, formats responses for TTS.
