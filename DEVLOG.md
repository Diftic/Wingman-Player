# Wingman Player — Dev Log

---

## 2026-05-02 — Session 21 — Skill scaffold complete (external)

### Goal

Build the Wingman YouTube skill end-to-end as a counterpart to the player's HTTP command surface, so a true fireside voice test is possible. Skill lives in a different codebase (`D:\PycharmProjects\wingman-ai\skills\wingman_youtube\`) — this entry captures the player-relevant aspects only; full skill DEVLOG lives in that folder.

### What's wired

- Skill calls the player's HTTP routes (`/player/load`, `/player/play`, `/player/pause`, `/player/stop`, `/player/next`, `/player/previous`, `/player/seek`, `/player/state`) via `aiohttp`. The player codebase is now the *driven* side; the skill is the *driver*.
- Skill auto-launches `Wingman-Player.exe` via `subprocess.Popen` if the HTTP port isn't responding, then polls until the port comes up. `player_exe_path` is a configurable property in the skill.
- The `Constants.MutexId` regenerated during the rebrand correctly prevents the auto-launch from spawning a second player instance — the second `Mutex` open returns `isNewInstance = false` and `Program.cs` exits cleanly.
- Source-agnostic schema verified: skill always sends `{source: "youtube", ...}` to `/player/load`, leaving room for the future Spotify / local-file skills to use the same transport without player-side changes.

### Validation

- Skill's confidence math tested standalone: strong match (clear song + artist + matching top result) → composite 1.00 → auto-plays; weak match → 0.95 → falls below 0.98 threshold → list mode; artist-only → 0.65 → list mode. Sane behaviour out of the box.
- Skill's YAML manifest + JSON installer config parse cleanly.
- All 5 Python modules `py_compile` clean.
- Player + skill integration only verifiable end-to-end through Wingman; pending fireside test.

### Out of scope this pass

- Renderer-side now-playing push to the skill (skill polls `/player/state` instead). If post-test feedback shows polling latency hurts, we'd add a webhook callback URL the skill registers with the player.
- Multi-result `/player/load` with index parameter — skill chooses the videoId before calling the player, so the player itself only ever sees one ID. Keeps player surface minimal.

---

## 2026-05-02 — Session 20 — Transport endpoints (Step 2 of skill bridge)

### Goal

Add the playback transport HTTP routes (`/player/play|pause|stop|next|previous|seek`) plus a real `/player/state` round-trip, so the skill can drive everything except volume (deliberately excluded per user's req #4).

### PlayerCommandBridge

New `src/Services/PlayerCommandBridge.cs` (DI singleton, ~80 lines). Late-bound shim between the HTTP accept thread (background) and the WebView2 player (UI thread). Pattern:

- HTTP server is constructed during host startup.
- `OverlayWindow` is constructed afterwards (needs UI thread). On construction, it calls `commandBridge.AttachOverlay(this)`.
- HTTP server resolves the bridge from DI and calls `bridge.ExecuteAsync(script)` / `ExecuteWithResultAsync(script)`.
- If the overlay isn't attached yet (first ~1s of startup before WPF is up), bridge returns `false`/`null`; HTTP server surfaces that as `503 Service Unavailable`.

### OverlayWindow.ExecutePlayerScriptAsync

Public method, thread-aware:

```csharp
public async Task<string> ExecutePlayerScriptAsync(string script)
{
    if (!Dispatcher.CheckAccess())
        return await Dispatcher.InvokeAsync(() => ExecutePlayerScriptAsync(script)).Task.Unwrap();
    if (!_webViewReady) return string.Empty;
    return await WebView.CoreWebView2.ExecuteScriptAsync(script);
}
```

Recursive single dispatch hop. Returns the JSON-encoded result from `ExecuteScriptAsync` verbatim — for `/player/state` the server passes that JSON straight through to the HTTP body, avoiding double-serialization.

### Renderer additions (`player.js`)

New named hooks for transport (the previous `__wingmanForwardKey` will continue to be used by the C# global keyboard hook for in-overlay hover keys; HTTP-driven control uses these new hooks):

- `__wingmanPlay()`, `__wingmanPause()`, `__wingmanStop()`, `__wingmanNext()`, `__wingmanPrevious()`
- `__wingmanSeek(seconds)`
- `__wingmanGetState()` — returns `{state, videoId, title, currentTime, duration}`. State strings (`'idle'`, `'playing'`, `'paused'`, `'cued'`, etc.) so the skill doesn't need to map YT.PlayerState integers.

`__wingmanLoad` extended with optional opts argument: `__wingmanLoad(videoId, playlistId, {startSeconds, endSeconds, index})`. Pending-load path also remembers opts so a load issued before WebView2 is ready replays correctly.

### `/load` vs `/play` split (caught mid-test)

Initially had `POST /player/play` taking a body and reloading. That conflicted with "play = resume" in the user's req #4. The IFrame API itself distinguishes `loadVideoById` (new content) from `playVideo` (resume), so split the routes:

- `POST /player/load` — body `{source, videoId?, playlistId?, ...}` — start new content (used by skill reqs 1/2/3)
- `POST /player/play` — no body — resume (used by skill req 4)

Skills pick based on intent.

### Security: JS-injection safety

Every value spliced into the script that goes through `ExecuteScriptAsync` is JSON-encoded via `JsonSerializer.Serialize` so a malicious videoId can't break out of its string literal. Tested: `videoId: "abc\");alert(1);//"` returns 204 (the literal string passes through to YouTube as a videoId, which obviously fails to load — but no script execution).

### Validation

End-to-end with `curl` against the standard Rick Astley video:

| Step | Result |
|------|--------|
| `POST /player/load` | 204; state `playing`, real title/duration/currentTime |
| `POST /player/pause` | 204; state `paused` at currentTime ~4 |
| `POST /player/play` (no body) | 204; resumes from same currentTime |
| `POST /player/seek {seconds:60}` | 204; currentTime jumps to 61.9 |
| `POST /player/stop` | 204; state `cued` |
| `POST /player/load` bad source | 400 `unsupported_source` |
| `POST /player/load` no ids | 400 `missing_videoId_or_playlistId` |
| `POST /player/seek` no `seconds` | 400 `missing_seconds` |
| Malformed JSON | 400 `invalid_json: ...` |
| Injection in videoId | 204, JS-escaped |

### Files

- `src/Services/PlayerCommandBridge.cs` (new, +84)
- `src/Services/WingmanPlayerHttpServer.cs` (extended, +210)
- `src/Renderer/player.js` (extended, +50)
- `src/UI/OverlayWindow.xaml.cs` (constructor + ExecutePlayerScriptAsync, +25)
- `src/App.xaml.cs` (DI + ctor wiring, +10)

Build green, 0/0. Commit `980b180`.

---

## 2026-05-02 — Session 19 — Skill control surface (Step 1 — HTTP skeleton) + v0.5.0

### Goal

Two pieces in one work session: (a) finalize project as a fresh v0.5.0 product line and push to GitHub; (b) start the skill bridge by adding the HTTP server skeleton + `/player/state` stub, so the wire is provable from `curl` before any skill code exists.

### v0.5.0 + repo creation

- `Version` / `FileVersion` / `AssemblyVersion` in `wingman_player.csproj` flipped from `1.8.1` → `0.5.0`. New project, new versioning baseline. The post-rebrand product is conceptually a fresh line — the v1.8.x history reflects the PulseNet era.
- `PulseNetPlan.md` deleted. Stale planning artifact (proposed a PyQt6 rewrite that was rejected back in session 1). Already covered in DEVLOG.
- New GitHub repo `Diftic/Wingman-Player` created via `gh repo create`. Public, default branch `master`, description set, homepage points at `diftic.github.io/Wingman-Player/` (Pages not yet enabled).
- `git remote set-url origin` to repoint to the new repo. Old `Diftic/PulseNet-Player` repo untouched — still has its v1.8.1 release intact.
- `git push -u origin master` — full history (including PulseNet-era commits) preserved on the new repo. Repointing was a remote swap, not a history rewrite.

Commits: `f266dd3` (v0.5.0 + plan deletion), then push.

### HTTP skeleton (Step 1)

Two new files plus DI wiring:

**`src/Settings/PlayerConfig.cs`** — `PlayerConfig` record (just `CommandServerPort = 17330` for now) loaded once on startup from `%APPDATA%\wingman_player\config.json`. Defaults silently if the file is absent. Deliberately separate from `WingmanPlayerSettings` so the renderer's settings panel can never round-trip system-side knobs through user-facing JSON. As the system surface grows (auth tokens, future flags) it stays out of the user-tweakable settings file.

**`src/Services/WingmanPlayerHttpServer.cs`** — `IHostedService` modeled on `LocalAudioStreamServer`. `TcpListener` bound to `127.0.0.1:port`, single accept thread, minimal HTTP/1.1 parser (request line + headers + Content-Length body), JSON response writer, `(method, path)` switch routing.

For Step 1 only `GET /player/state` is implemented (returns `{"state":"idle"}`); unknown paths return 404. All other routes added in Step 2.

**Why TcpListener, not HttpListener:** matches the existing audio stream server's pattern, no URL ACL elevation needed (HttpListener requires `netsh http add urlacl` on Windows for non-admin processes), no extra dependency. Localhost-only so we don't need TLS. ~150 lines vs Kestrel's overhead. Adequate for skill use.

**Why one connection at a time:** consistent with `LocalAudioStreamServer`, and skills are low-concurrency (one user, one voice channel). If a future scenario needs concurrent requests it's a small refactor to spawn a Task per connection.

### Validation

`curl` round-trip after build:
- `GET /player/state` → 200 `{"state":"idle"}`
- Unknown path → 404 `{"error":"not_found"}`
- `POST /player/play` (not yet implemented) → 404 (correct)
- Player exits cleanly, port releases.

### Files

- `src/Settings/PlayerConfig.cs` (new, +44)
- `src/Services/WingmanPlayerHttpServer.cs` (new, +210)
- `src/App.xaml.cs` (+10 for DI registration of `PlayerConfig` + `WingmanPlayerHttpServer`)

Build green. Commits: `c8c6a36` (HTTP skeleton).

### Decision: layered architecture for the skill bridge

The user's req #5 ("player must be possible to be controlled by other skills in the future, as the player will be a local media player for wingman as a whole") drove a key call: the player exposes a **media-source-agnostic transport** (`/player/load` takes `{source: "youtube", ...}`), and **the YouTube-specific concerns live in the skill**. So:

- Player codebase: HTTP transport only. Knows nothing about YouTube search, API keys, or quotas.
- YouTube skill (separate codebase): owns the YouTube Data API key, search calls, caching, quota tracking, confidence math.

Future Spotify / local-file skills slot into the same player surface without player-side changes. This is a meaningfully cleaner boundary than the original plan, which had the player owning a `/search` endpoint and the API key.

---

## 2026-05-02 — Session 18 — Full PulseNet → Wingman Player rebrand

### Goal

Complete the rebrand the v1.8.x Wingman conversion deferred. Strip every PulseNet-named identifier from code and surfaces, renaming to `wingman_player` (snake_case namespace + filenames, matching the prior `pulsenet` convention) for code, `WingmanPlayer` for class names, `Wingman Player` for UI display strings, and `Wingman-Player` for the binary / repo / installer artifact form. Treat this as a fresh product (no repo created yet), so MSI gets a new UpgradeCode and AppData folder name flips without a migration.

### Project files

- `src/pulsenet.csproj` → `src/wingman_player.csproj`. `<StartupObject>` and `<AssemblyName>` flipped to `wingman_player.Program` / `Wingman-Player`.
- `pulsenet.slnx` → `wingman_player.slnx`. `<Project Path>` updated.

### Code identifiers

- `namespace pulsenet[.X]` → `namespace wingman_player[.X]` across every .cs file (App.xaml.cs, Constants.cs, Program.cs, Models/, PInvoke/, Services/, Settings/, UI/, Models/Keyboard/).
- `src/Models/PulsenetSettings.cs` → `WingmanPlayerSettings.cs`; record `PulsenetSettings` → `WingmanPlayerSettings`. `SettingsManager.Current`, `Save`, `Load`, the `SettingsChanged` event arg, and `OverlayWindow.OnSettingsChanged` all retyped.
- `App.xaml` `x:Class="pulsenet.App"` → `wingman_player.App`. Three Window XAMLs (`OverlayWindow`, `MiniBannerWindow`, `SplashWindow`) flipped to `wingman_player.UI.*` + `Title=` strings rebranded.
- `TrayIcon.cs` embedded resource lookup `pulsenet.Assets.icon.ico` → `wingman_player.Assets.icon.ico` (resource ID derives from the new namespace).

### Constants + identity

- `ApplicationName` → `Wingman Player`.
- `MutexId` regenerated (`wingman_player-64459292-292A-417A-9E12-E6E00A3040B5`) so a v1.8.1 instance and a rebranded build don't collide on the named mutex during testing.
- `AppDataFolderName` → `wingman_player`. Hard switch — no migration code, since per the user there's no published Wingman-Player repo yet so no users to keep settings for.
- `PlayerVirtualHost` → `wingman.local` (was `pulsenet.local`). Comment in `OverlayWindow.InitializeWebViewAsync` updated.

### JS hooks

`__pulsenetForwardKey`, `__pulsenetNowPlaying`, `__pulsenetVersion`, `__pulsenetRefreshVersionLabel`, `__pulsenetUpdateCheckDone` all flipped to `__wingman*`. Touched both `player.js` and the C# emitters in `OverlayWindow.xaml.cs` (`BuildSyncScript`, `AddScriptToExecuteOnDocumentCreatedAsync`, the `__wingmanForwardKey` injection, and the `__wingmanUpdateCheckDone` re-enable hook). The previously-shipped `window.__wingmanLoad` already used the new prefix from the v1.8.x conversion, so it stays.

### Renderer + display strings

- `banner.html` title + default station name → "Wingman Banner" / "Wingman Player".
- `banner.js` header comment + station fallback string updated.
- `MiniBannerWindow._pendingStation` default + `SetStation` fallback → "Wingman Player".
- OBS Streamer Info instruction: "Set Window to *PulseNet Player*" → "Set Window to *Wingman Player*" (now matches the actual window title).
- Update-check MessageBox title and body, "Could not load …" navigation-failure HTML, "WebView2 not installed" balloon body all rebranded.

### Update path

- `UpdateChecker` API URL → `https://api.github.com/repos/Diftic/Wingman-Player/releases/latest`. User-Agent → `Wingman-Player/{ver}`. Asset name lookups: `PulseNet-Player.exe` → `Wingman-Player.exe`, `PulseNet-Setup.msi` → `Wingman-Player-Setup.msi`.
- `SelfUpdateService` exe-name fallback `Path.Combine(AppContext.BaseDirectory, ...)`, temp dir name (`pulsenet_update` → `wingman_player_update`), and the temp MSI/EXE file names all aligned.

### Workflow + installer

- `.github/workflows/build.yml`: csproj path, `Copy-Item` exe name, MSI output name, and the release upload `files:` list all flipped to the Wingman naming.
- `installer/installer.wxs`: `Name`, `Manufacturer`, `MajorUpgrade DowngradeErrorMessage`, `Feature.Title`, `INSTALLFOLDER` directory name, `AppMenuFolder` name, `Shortcut.Name`, `Target` exe, and `RegistryValue.Key` (`Software\Wingman Player`) all updated. `UpgradeCode` regenerated to `65E7C967-BC4C-4DDF-9BDE-BD8B9765ED28` so MSI treats Wingman Player as a new product instead of an upgrade of a not-shipped PulseNet build.
- `installer/license.rtf`: copyright line updated.

### Documentation

- `README.md` rewritten for the actual current product. The previous version still described 19 stations, a station selector UI, station icon files in `Renderer/assets/stations/`, and a `stations.js` config — none of which exist post-Wingman-conversion. New README documents the `__wingmanLoad` entry point, the OBS streaming pipeline, settings / hotkey behaviour, and the file layout actually present in the tree.
- `docs/index.html` (GitHub Pages landing) replaced. Old version was a 1300-line sales page for the station-based PulseNet product — entirely stale. New version is a focused single-page intro to Wingman Player.
- `TODO.md` title + open follow-ups updated; "Full rebrand" item moved to checked.

### Out of scope this pass

- `Assets/icon.ico` — still the old PulseNet icon. Tracked in the open-follow-ups; needs new artwork before the .ico can be regenerated (16/32/48/256).
- Historical DEVLOG entries (sessions 1–17) — left as-is; they're history, not current state. Names of session-specific PulseNet artifacts in those entries (channel IDs, station-icon filenames, etc.) reflect the codebase at the time and shouldn't be retroactively edited.
- `audits/2026-04-30-red-team.md` — references PulseNet by name in its findings; left alone because the audit is dated documentation, not living spec.
- `Codex analysis.md` — working-tree note, intentionally untracked.

### Build

`dotnet build src/wingman_player.csproj -a x64` after the rename: green (0 warnings, 0 errors).

---

## 2026-05-02 — Session 17 — Wingman conversion (prep for the Wingman YouTube skill)

### Goal

Repurpose PulseNet Player as a generic media-player overlay to be driven by a future Wingman YouTube skill. The PulseNet station system, channel lock-in, and the home/about utility buttons go; the YouTube IFrame API surface, native YouTube controls, settings panel, mini banner, and OBS streaming path stay. Window title and assembly name remain `PulseNet Player` until full rebrand is scoped explicitly — only the visible artwork (frame, passive background) and outbound URLs (Discord) flip to ShipBit / Wingman branding.

### Frame artwork

User supplied `src/Assets/Frame.png` (1537×1023, transparent cutout at 167,131 → 1203×771; aspect 3:2) and `src/Assets/passive background.png` (Wingman / Media Player branding). Cutout was measured by scanning rows from the centre outward for fully-transparent pixels.

Decision: stretch the source frame non-uniformly into a **1252×731** canvas so the 3:2 art cutout lands on a **16:9 video rect at (136, 94) 980×551**. Approach trades ~14% non-uniform stretch on the frame artwork for a clean 16:9 video without letterboxing inside the cutout. Constants `FrameDisplayWidth/Height` updated; `#frame-base` keeps its `width/height` matching `#app` so the stretch happens via CSS.

### What got removed

- **Side station buttons** — `#stations-left`, `#stations-right` columns, `#pulsenet-home-btn`, `#about-btn`, the `#station-preview` hover layer, and all matching CSS.
- **Station system** — `Renderer/stations.js`, `Renderer/assets/stations/` (entire directory), `Renderer/assets/{Info,pulsenet_icon,radio_background,Background graphics,PulseNet Player}.png`. `STATIONS` global, `buildButtons`, `activateStation`, `loadStationIntoPlayer`, station preview JS, the live-stream iframe path (`loadLiveStream`, `liveStreamActive`, `PULSENET_LIVE_CHANNEL`).
- **Channel lock-in** — `Constants.DefaultChannelId`, `PulsenetSettings.YoutubeChannelId`, `OverlayWindow._loadedChannelId`, `RestartNavigation`, the channel-change reload path in `OnSettingsChanged`, and the `?channelId=…` URL plumbing in `BuildPlayerUrl` + `player.js`. Player URL is now a no-arg `https://pulsenet.local/index.html`.

### What got added

- **`window.__wingmanLoad(videoId, playlistId)`** in `player.js` — the entry point Wingman's YouTube skill will drive via `CoreWebView2.ExecuteScriptAsync`. Either-or: pass a videoId for a single video, a playlistId for a playlist, or both null to return to the idle/passive screen. If the YT.Player isn't ready yet the call queues into `pendingVideoId`/`pendingPlaylist` and fires on `onReady`.
- **Hex / round red toolkit button** — replaces the wide horizontal Settings button. Started as a hexagonal flat-top clip-path with crossed hammer + screwdriver SVG glyph; user iterated through several positions (1062,595 → 1178,670 → 1163,655 → 1148,640 → 1143,630 → 1138,620 → 1133,615) before landing on **`(1133, 615)`, 40×40, `border-radius: 50%`, red palette** (background `rgba(40,0,8,0.70)`, border `rgba(248,113,113,0.45)`, hover glow `rgba(248,113,113,0.40)`). Moved off the video onto the frame's bottom-right cluster so it never obscures the playback area.
- **Default hotkey: F8** (was F9) — `PulsenetSettings.ToggleHotkey`, `MiniBannerWindow._pendingHotkey` and its fallback, the banner.html static label, banner.js fallback, and the Streamer Info `Press F8 to hide` tip all swapped. Existing user `settings.json` migrated in-place via PowerShell during testing — System.Text.Json doesn't auto-migrate, so deployed installs will keep their bound key until rebound.
- **Discord URL** swapped from the old `Vxn7kzzWGJ` invite to `discord.com/invite/shipbit-1173573578604687360` (ShipBit's Discord).
- **Splash window full-bleed background** — `SplashWindow.xaml` restructured as a single `Grid` with `<Image Stretch="UniformToFill">` covering the whole window, a bottom gradient (`#000d1b2a` → `#CC0d1b2a`) for legibility, and the F-key prompt + "right-click tray" hint floating at the bottom. The earlier 225×225 logo + divider StackPanel layout is gone. Previously the splash had a small inset image with empty padding around it.

### Settings panel anchoring

The three sub-panels (`#settings-panel`, `#miniplayer-settings-panel`, `#streamer-settings-panel`) all share the same anchor. As the hex button moved up-and-left through iterations, the panel anchor moved by the same delta to stay aligned. Final anchor: **`right: 75px; bottom: 105px`** — the user requested an additional 25px lower at the end of the iteration to give the panel breathing room from the cutout, so `bottom` settled below the position that mirrored the button.

### Build / runtime

- `Assets/icon.ico` was deleted in the working tree pre-session as part of the asset purge but is still referenced by `pulsenet.csproj` (`<ApplicationIcon>` + `<EmbeddedResource>`) and `TrayIcon.cs` (loaded via embedded resource). Build broke on `CS7064: Error opening icon file`; restored via `git checkout HEAD -- src/Assets/icon.ico`. Still PulseNet branding — Wingman icon swap is a follow-up.
- `src/Assets/main_logo.png` was also deleted but is referenced by `SplashWindow.xaml` and the csproj `<Resource>`. Restored as a copy of `passive background.png` so the splash background and the in-app idle layer share the same artwork.

### Open / deferred

- **Wingman skill control mechanism** — `__wingmanLoad` is the JS-side surface, but the C# host doesn't yet expose any way to drive it. When the Wingman YouTube skill exists, decide whether the host pulls commands (HTTP / named-pipe listener) or the skill pushes (out-of-process bridge invoking `CoreWebView2.ExecuteScriptAsync`).
- **Full rebrand** — window title (`Title="PulseNet Player"` in `OverlayWindow.xaml`), assembly name `PulseNet-Player`, OBS Streamer Info instructions (still "Set Window to PulseNet Player" because that matches reality), README, DEVLOG, TODO, repo name. Out of scope for today.
- **Icon** — `Assets/icon.ico` is still the old PulseNet icon. Generate a multi-resolution Wingman .ico when artwork is provided.

---

## 2026-05-01 - v1.8.1 - Streamer Info polish

Two small follow-ups after looking at v1.8.0 in real use:

- **Header-to-list gap.** The `1. Video` and `2. Audio` section headers were sitting too far above their respective `<ol>` lists because `.streamer-info-content`'s flex `gap: 12px` applies uniformly between every child. Added `margin-bottom: -8px` on `.streamer-info-section` so a header pulls its following OL up against itself, preserving the 12px gap between unrelated siblings.
- **Em-dashes out of user-facing text.** Section headers, the "audio comes from the next source" step, the F9 tip, the version-banner button label, the Update available MessageBox title, the Splash window's "Update failed" label, and every em-dash in README all swapped for colons / periods / hyphens / a centred dot in the version label. Per-context choice rather than a uniform substitute (colons for definition pairs, periods to split run-on sentences, hyphens for inline separators).

---

## 2026-05-01 — Session 16 — v1.8.0 — Localhost audio stream replaces dual-render bridge

### Background
v1.6.3 shipped the dual-render audio bridge: WebView2 audio captured via process-loopback and re-emitted from `PulseNet-Player.exe`'s own session so OBS Window Capture's Capture Audio (BETA) could see it. The cost was doubled local audio for any streamer not running an app router (Sonar / Voicemeeter / Wavelink), gated behind a default-off `StreamerModeEnabled` toggle. This session replaces the whole architecture.

### Architecture change
WebView2 still plays through the system default endpoint. The bridge no longer renders anywhere — it captures and forwards the PCM to a new `LocalAudioStreamServer` that exposes `http://127.0.0.1:17329/stream.wav` to OBS Media Source. OBS gets a dedicated audio channel; the listener hears WebView2 directly through Windows as normal. Single audio path locally. No doubling. No app-router workflow.

### Why this works where v1.4.x's BrowserSourceServer didn't
The previous "audio for OBS over localhost" attempt hosted a YouTube player iframe in OBS Browser Source — two YouTube players (one in PulseNet, one in OBS) had to stay in sync with no control channel between them. Pause/skip in PulseNet didn't propagate. Killed.

The new design has only one YouTube player. OBS is a passive listener consuming whatever PCM the bridge captures from PulseNet's WebView2. Pause in PulseNet → loopback packets go silent → stream goes silent → viewers hear silence. Skip station → WebView2 swaps videos → capture picks up the new audio → stream carries it through. Nothing in OBS could desync because nothing in OBS is making playback decisions. Confirmed end-to-end during testing: control retention works.

### Components
- `LocalAudioStreamServer` (new) — `IHostedService`, `TcpListener` on `127.0.0.1:17329`. Accepts a single HTTP GET, writes a WAV header with a fake-huge data chunk (`0x7FFFFFFF`), then drains 16-bit stereo PCM forever from the bridge.
- `AudioBridge` (rewritten) — capture-only. Render-device `IAudioClient` Activate stays just to read `GetMixFormat` (we need a format to pass into the capture client's `Initialize`); `Initialize`/`Start` of a render session are gone. Pump loop pushes captured packets to the stream server with on-the-fly conversion (32-bit float → 16-bit, downmix to stereo).

### What got deleted
- `StreamerModeEnabled` field from `PulsenetSettings`.
- `streamerMode` web-message case in `OverlayWindow.OnWebMessageReceived` plus the toggle sync in `BuildSyncScript`.
- Streamer Mode checkbox row in `index.html`, `streamerModeToggle` wiring in `player.js`.
- `streamer-mode-row` / `streamer-mode-label` CSS rules.
- The Sonar / Voicemeeter / Wavelink walkthrough paragraph in Streamer Info — irrelevant once we're not asking the user to deal with two render sessions.

Existing v1.6.x `settings.json` files keep `StreamerModeEnabled` until the next save (System.Text.Json ignores unknown properties on deserialize), at which point the field disappears naturally.

### Streamer Info panel rewrite
Two-section walkthrough. **1. Video — Window Capture** keeps steps 1-6 but explicitly says to leave Capture Audio (BETA) *unticked* since audio comes from the next source. **2. Audio — Media Source** is the new section: add a Media Source, untick Local File, paste `http://127.0.0.1:17329/stream.wav` into Input. Inline Copy URL button next to the URL display flashes "Copied!" via `navigator.clipboard.writeText`. F9 hide hint preserved. Line-heights tightened ~25% across the panel after the rewrite added enough content that the original spacing was clipping at the bottom.

### Latency
~1 s buffer end-to-end, dominated by OBS Media Source's default Network buffering. Acceptable here because the visible "video" is essentially static — player UI, station thumbnails, occasional title text. Nothing the viewer can spot the audio lagging behind. The streamer-side Network buffering / Render Delay options remain in OBS for anyone who needs tighter sync, but we don't document them by default.

### Validation
1. Smoke test: TCP listener bound on 127.0.0.1:17329; HTTP GET returns 503 while bridge isn't yet capturing, 200 + valid WAV header once it is.
2. Probe: ~5 s capture, 2 069 804 bytes, RIFF/WAVE/fmt/data parsed cleanly, 96 kHz / 2 ch / 16 bit, 99.93 % of sampled positions non-zero — real audio, not silence.
3. OBS in real use: audio reached Media Source, levels moved, control retention confirmed (pause/skip propagated, station change carried through).
4. After dropping the render path: bridge runs without `StreamerModeEnabled` being on, single-path local audio, no doubling.

### Release
Tagged `v1.8.0`. Skipped v1.7.0 — the conceptual delta from v1.6.3 (architecture replacement, setting deletion, panel rewrite) is bigger than a typical minor warrants. v1.6.3 clients see the update banner on next launch (auto-check) or via the manual button.

---

## 2026-05-01 — Session 15 — v1.6.3 — Capture-client AudioCategory + Streamer Info specifics

### Background
v1.5.0 shipped the StreamerMode toggle so non-streamers can opt out of the doubled-audio cost. v1.6.0 added the manual update path. With the workflow stable, today's session went deep on the actual streamer experience in SteelSeries Sonar. Three things came out of it: a real fix for the bridge's capture session, a specifics rewrite of the Streamer Info copy, and a confirmed architectural map of what's actually happening on the audio side.

### What we mapped (the architecture side)
With StreamerMode on, two render sessions exist on the default endpoint: `msedgewebview2.exe` (Chromium's direct audio path) and `PulseNet-Player.exe` (the AudioBridge re-emit). Both are correctly classified as Media via `IAudioClient2.SetClientProperties` (the v1.4.2 fix on the render client, plus a new matching call on the capture client this session). On systems with Sonar / Voicemeeter / Wavelink, both sessions land in the MEDIA channel cleanly.

The "AUX duplicate" we hunted for half the session turned out to be a Sonar UI quirk: dragging an app entry between channels and back creates a phantom locked session that Sonar can't clean up. Don't drag, no duplicate.

The doubled-audio cost when StreamerMode is on becomes a single-app-router decision: in Sonar / Voicemeeter / Wavelink, click the speaker-icon mute on `MSEDGEWEBVIEW2` in the MEDIA channel. Sonar's per-app mute is post-mix, so it silences the Chromium direct path without affecting the AudioBridge's process-loopback capture upstream. Bridge keeps capturing, re-emits via `PULSENET-PLAYER` (also in MEDIA), and the user hears one clean audio path attributed to `PulseNet-Player.exe`. OBS Window Capture's Capture Audio (BETA) sees the same clean session.

A side note worth banking: process-loopback's mute is post-tap. If you call `ISimpleAudioVolume::SetMute` on the WebView2 session directly (the obvious "stealth bridge" instinct), the loopback capture goes silent at the same time. Tested via a standalone audio-probe tool (untracked, lives in `tools/audio-probe/`). Don't go down that path.

### Capture-client AudioCategory.Media
v1.4.2 only set `AudioStreamCategory.Media` on the bridge's render client. The capture client (process-loopback) inherited the default Other category, which caused it to land in Sonar's AUX channel under certain edge cases. Now matched: `SetClientProperties` is called on the capture client too before its `Initialize`. AUX leak no longer occurs at startup.

### Streamer Info copy rewrite
Old text said "PulseNet's clean audio is routed to the Media channel; the underlying browser engine appears as a duplicate (often in Aux) with degraded processing. Mute the duplicate's channel." That was vague and partly wrong (the AUX behaviour was a Sonar UI quirk, not a category miscall). New text names the specific entries (`PULSENET-PLAYER` vs `MSEDGEWEBVIEW2`), tells the user which one to mute and which one is the broadcast-clean re-emit, warns against dragging entries between channels, and points at the channel-level volume slider as the first place to check if audio sounds quiet.

### Diagnostic infrastructure (kept untracked)
`tools/audio-probe/` contains a standalone .NET test harness that:
- `--pid <id>` mode mutes a target tree's audio sessions to verify whether process-loopback captures pre-mute or post-mute. Answer: post-mute. Stealth bridge via `SetMute` is not viable.
- `--list` mode enumerates every render endpoint on the system, including disabled and unplugged ones, and dumps every session's PID, state, exe name, and display name. Surfaced the existence of Sonar's virtual endpoints (Game / Chat / Media / Aux / Stream / Microphone) plus a couple of Elgato Wave Link virtual outputs that exist on the dev box.

Kept untracked so it doesn't bloat the shipped repo or distract future readers. Lives only in the working tree.

### Release
Tagged `v1.6.3`. Build & Release workflow publishes; v1.6.1 clients see the update banner on next launch (auto-check) or via the manual button. No user-visible behaviour change unless they're on Sonar with StreamerMode on, in which case the AUX leak is gone at startup and the Streamer Info text now actually tells them which entry to mute.

---

## 2026-04-28 — Session 14 — v1.6.0 — Manual update check + version banner

### What was missing
We had `UpdateChecker` and `SelfUpdateService` running an automatic check on startup since v1.4.x — but the only way to *retrigger* a check between launches was to restart the app. If a user dismissed the update prompt and changed their mind, or if a release shipped while the app was running, they had no in-app path to re-check. They also had no in-app surface for the **current** version number — that lived only in the assembly metadata.

### The fix — version banner with click-to-recheck
New row at the top of the main settings panel: `v1.6.0 — Check for updates`. Click triggers `UpdateChecker.CheckAsync` on the C# side. If a new release is available, a Yes/No `MessageBox` offers to apply it via `SelfUpdateService.ApplyAsync` (same code path the auto-check on startup uses). If up to date, an info box confirms.

JS↔C# wire-up:
- `player.js` posts `{type:'checkForUpdates'}` and disables the button + shows "Checking for updates…" until it hears back.
- `OverlayWindow.OnWebMessageReceived` adds a `case "checkForUpdates"` that fires `HandleCheckForUpdatesAsync` (off-thread `CheckAsync`, `Dispatcher.InvokeAsync` for the modal + restart marshalling).
- C# calls `window.__pulsenetUpdateCheckDone()` after the modal closes to re-enable the button.

### Version injection — first-render correctness
The button label reads from `window.__pulsenetVersion`. First implementation injected this only via `BuildSyncScript` on `ShowOverlay`, which meant `player.js`'s initial render saw `undefined` and fell back to "0.0.0". Fix: `WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` is now called once during `InitializeWebViewAsync` to define `window.__pulsenetVersion` *before* any page script runs. `BuildSyncScript` still re-injects on every overlay show so a future in-place self-update would surface the new version without restart (the executable's assembly version has changed by then).

### Cleanup — frame_glow removed
`frame_glow.png` was an aspirational asset referenced in `index.html` and `style.css` since the early frame layering pass. Never produced; the `<img onerror>` fallback hid it gracefully but the dead reference, the CSS rule, and the `glow-pulse` keyframes were just clutter. Removed all three plus the matching TODO entries.

### Release
Tagged `v1.6.0`, pushed. Build & Release workflow publishes; v1.5.0 clients see the update banner on next launch (auto-check) or via the new manual button.

---

## 2026-04-27 — Session 13 — v1.5.0 — Streamer Mode toggle (default off)

### The bug we shipped in v1.4.2
v1.4.2 declared the AudioBridge release "complete". The day's testing was on a streaming setup with SteelSeries Sonar; the workflow there was solid. What we missed: **AudioBridge was running unconditionally for everyone**, not just streamers. For the ~90% of users who are *just listening to music* and don't have an app router like Sonar, both audio paths (WebView2 direct + AudioBridge re-emit) hit their default device simultaneously. That's a permanent doubled-audio regression with no available fix on their end — they have no AUX channel to mute.

### The fix — make the bridge opt-in
New `StreamerModeEnabled` setting on `PulsenetSettings`, default **false**. AudioBridge's pump checks the setting in both its outer scheduling loop *and* its inner sample-pump loop, so:
- Toggle off (default): bridge sleeps; no second audio session created; non-streamers hear single-path audio just like pre-AudioBridge.
- Toggle on: bridge spins up within ~500ms (outer loop wakes from its sleep); WebView2 PID lookup, capture+render activation, sample pump all proceed as in v1.4.2.
- Toggle off mid-stream: inner pump loop notices on its next iteration (~200ms tail) and `RunOnce` returns; bridge tears its WASAPI clients down cleanly; back to single-path.

`SettingsManager.SettingsChanged` already fires synchronously on `Save`, but the pump doesn't subscribe — instead it polls the setting on each loop iteration. The thread is sleeping on either a 500ms `Thread.Sleep` (idle path) or a 200ms `WaitForSingleObject` on the capture event (active path), so the polling cost is zero and the reaction time is bounded by those waits. Cleaner than wiring a `ManualResetEvent` plus all the threading care that needs.

### UI — toggle inside Streamer Info, not in main settings
Added a checkbox row at the top of the Streamer Info sub-panel:
> **Enable streamer mode** &mdash; creates an audio bridge OBS can capture. Leave off if you're just listening to music.

Two reasons for putting it there rather than the main settings panel:
- **Discoverability inversion.** The 90% who don't stream never need to find this toggle. Hiding it inside Streamer Info means it doesn't clutter their settings.
- **Streamers always go to Streamer Info anyway** to read the OBS setup walkthrough. The toggle is the natural first step in that flow — it lives at the top of the panel, before the OBS setup steps.

The checkbox's state syncs from settings on every overlay show via `BuildSyncScript`, so it correctly reflects the persisted value across launches and across panel close/reopen.

### Sub-panel UX fixes (v1.4.2 missed these too)
Tester noticed two related glitches with Streamer Info:
1. Clicking outside the panel didn't close it (the main settings panel's click-outside handler was watching only `#settings-panel`, not `#streamer-settings-panel`).
2. Clicking the **Settings Menu** button while Streamer Info was open opened the main settings panel *behind* it, leaving both visible at once.

Fix: extended the main settings button handler to also fold `#streamer-settings-panel` (it already folds `#miniplayer-settings-panel` for the same reason), and extended the document-level click-outside handler to also close `#streamer-settings-panel` when the click lands outside it.

### Release
Tagged `v1.5.0`, pushed. Build & Release workflow publishes; v1.4.2 clients see the update banner on next launch and get a clean single-path listening experience by default.

---

## 2026-04-27 — Session 12 — v1.4.2 — AudioCategory, Streamer Info polish, AudioSessionRenamer rip

### What v1.4.1 missed
Session 11 shipped `AudioBridge` correctly emitting captured WebView2 audio from `PulseNet-Player.exe` so OBS Window Capture's Capture Audio (BETA) picks it up. Tester verified broadcast worked. What we discovered when running the bridge alongside SteelSeries Sonar:

1. **Sonar dumped our session into the GAME channel** because `IAudioClient` defaults to `AudioCategory_Other` and Sonar's heuristic for unclassified streams is GAME. The GAME channel applies game-oriented DSP (positional/spatial processing tuned for footsteps) which audibly mangles music — tester reported "FAR lesser audio quality".
2. **The session controls were locked** with the warning `PulseNet Player didn't allow Sonar to change the audio settings` — Sonar's UI grays the slider when classification can't be retroactively applied.
3. **Echo on the streamer's headphones**, because Sonar routes both WebView2's direct path and AudioBridge's re-emit to the listening output simultaneously with ~20–40 ms latency offset.

### Fix — declare the audio category explicitly
`IAudioClient2.SetClientProperties` with `eCategory = AudioStreamCategory.Media`, called *before* `Initialize`. Sonar then routes our session into the MEDIA channel with clean DSP, and the controls unlock.

Implementation:
- New types in `src/PInvoke/AudioBridgeInterop.cs`: `AudioStreamCategory` enum (mirrors Windows' eleven values, with `Media = 11` being the relevant one), `AudioStreamOptions` enum, `AudioClientProperties` struct, and `IAudioClient2` COM interface declared with the full IAudioClient vtable inheritance order followed by the three IAudioClient2 additions.
- `AudioBridge.RunOnce` now QIs the activated render IAudioClient to `IAudioClient2` and sets properties before `Initialize`. Capture client is left as plain IAudioClient — process-loopback capture doesn't need a category and `InitializeSharedAudioStream` doesn't accept the LOOPBACK flag anyway.
- Buffer durations dropped to `0` (auto = engine period, ~10ms) on both capture and render init for slightly tighter sync — though latency was a red herring; the dominant audible echo cause was the duplicate playback paths, not the time offset.

### The Sonar workflow
With AudioCategory in place, the architecture sorts cleanly into two visible Sonar entries:
- **MEDIA**: AudioBridge re-emit (clean processing — the audio we want streamer + viewers to hear)
- **AUX**: WebView2's helper sessions (Sonar can't classify them, defaults to AUX with degrading DSP)

Streamer mutes AUX in Sonar → only MEDIA reaches their headphones → no echo, clean quality. Sonar mutes are local-only — they don't reach OBS's WASAPI process loopback, so the broadcast continues unaffected. Streamer Info panel now documents this conditional step for users on Sonar/Voicemeeter/Wavelink.

### AudioSessionRenamer permanently removed
The pre-AudioBridge service that renamed `msedgewebview2.exe` audio sessions to "PulseNet Player" in Volume Mixer. Diagnostic test confirmed it was *not* the cause of Sonar grouping the WebView2 sessions with each other (Sonar groups by process tree, not by display name or icon). With AudioBridge providing a real `PulseNet-Player.exe` audio session, the renamer is now actively *harmful* in Volume Mixer — would create two same-named "PulseNet Player" entries side-by-side. Killed: `src/Services/AudioSessionRenamer.cs` deleted; trimmed `src/PInvoke/AudioSessionInterop.cs` from ~190 lines to ~95 lines (kept only the MMDevice + toolhelp32 interop AudioBridge needs); dropped the field + Dispose call from `App.xaml.cs`.

### Streamer Info refinements
- Added the OBS "Audio Monitoring → Monitor Off" step earlier in the session (stops OBS from also playing the captured audio back through OBS's monitor device on top of Windows' direct path).
- Added the conditional Sonar/Voicemeeter/Wavelink note about muting the AUX duplicate.

### Release
Tagged `v1.4.2`, pushed. Build & Release workflow publishes the artifacts; v1.4.1 clients see the update banner on next launch.

---

## 2026-04-27 — Session 11 — v1.4.1 — OBS streaming via Window Capture + WASAPI audio bridge

### The problem
Tester report: streamers can't add PulseNet to OBS unless they capture their entire desktop. Not viable — desktop capture exposes everything else on the streamer's screen (chat windows, browser tabs, etc.). They want PulseNet as a *targeted* OBS source.

Two distinct sub-problems surfaced:

1. **Window-list invisibility.** OBS's Window Capture and Game Capture pickers didn't list `PulseNet Player` at all. Investigation via `EnumWindows` + `GetWindowLongPtr(GWL_EXSTYLE)` confirmed the cause: the overlay window had `WS_EX_TOOLWINDOW` set, both implicitly via XAML `ShowInTaskbar="False"` (WPF auto-applies it) and explicitly in `OnSourceInitialized` "to hide from Alt+Tab". OBS filters tool windows out of its source list by design — they're meant for floating utility palettes, not main app windows. The Alt+Tab hiding goal is independently achieved by the hidden owner window WPF auto-creates from `ShowInTaskbar="False"`, so `WS_EX_TOOLWINDOW` was redundant for that purpose and was the single thing locking streamers out.

2. **Audio invisibility.** Even after fixing #1, OBS Window Capture's "Capture Audio (BETA)" produced silence. The captured window is owned by `PulseNet-Player.exe` but the audio sessions live entirely in `msedgewebview2.exe` helper PIDs — descendants spawned by the WebView2 runtime. Application Audio Capture didn't help either: `msedgewebview2.exe` audio sessions aren't enumerated for it on tester's machine. The `AudioSessionRenamer` already in the codebase paints over the same architectural mismatch in Volume Mixer (renames the display name to "PulseNet Player"); it doesn't move the audio's process ownership.

### The fix — pixel-mirror via Window Capture + audio re-emit

**Window discoverability** (`src/UI/OverlayWindow.xaml.cs`):
- `OnSourceInitialized` now *clears* `WS_EX_TOOLWINDOW` instead of setting it. Owner-window pattern still keeps the overlay out of Alt+Tab/taskbar without the flag.
- `HideOverlay` (F9 toggle) used to set `Visibility = Visibility.Collapsed`; now persists current Left/Top to settings then moves the window to `(-(FrameDisplayWidth + 100), 0)`. Window stays `Visibility.Visible`, DWM keeps compositing it, OBS WGC keeps mirroring it pixel-perfect to the broadcast — even though the streamer can no longer see it on their own monitor. F9 to show snaps it back to the saved on-screen position. This is what makes the "hide on streamer's screen / visible on stream" workflow possible: viewers never lose the player when the streamer hides it to clear their game view.

**Audio** (`src/Services/AudioBridge.cs` + `src/PInvoke/AudioBridgeInterop.cs`):
- New `IHostedService` that captures the WebView2 process tree via WASAPI process-loopback (Windows 10 v2004+ API) and re-emits the audio from `PulseNet-Player.exe` itself, so OBS's process-bound Capture Audio (BETA) sees us as the audio-producing process.
- Activation path: `ActivateAudioInterfaceAsync(VAD\Process_Loopback, IAudioClient, PROPVARIANT(VT_BLOB → AUDIOCLIENT_ACTIVATION_PARAMS{PROCESS_LOOPBACK, target=webView2RootPid, INCLUDE_TARGET_PROCESS_TREE}))`. The `INCLUDE_TARGET_PROCESS_TREE` flag follows WebView2's helper renderer/audio children dynamically so we don't need to chase individual PIDs as they spawn and die.
- Capture init reuses the render endpoint's mix format (`IAudioClient.GetMixFormat`), so no resampling happens in the pump path. Tester's machine is in 8-channel 96 kHz 32-bit shared-mode rendering and the bridge handles it without conversion.
- Pump runs on a background thread bumped to MMCSS "Pro Audio" priority via `AvSetMmThreadCharacteristicsW`. Event-driven via `IAudioClient.SetEventHandle` + `WaitForSingleObject`; drains all available capture packets per wake and copies straight into render buffer slots. Capture release happens unconditionally after every read so transient render stalls drop frames rather than blocking the pipeline.
- Trade-off accepted: the streamer hears doubled audio locally (WebView2's direct path + our re-emit). They manage that via OBS per-source audio monitoring controls — viewers receive the clean re-emit only.

### Click-blocker tightening
While the player was now stream-capturable, a few YouTube-chrome leaks became more visible since the broadcast preserves them pixel-for-pixel:
- New `#click-blocker-br` (80×50 at `right:0; bottom:83px`) over the fullscreen-expand icon YouTube overlays in the bottom-right of embedded videos.
- `#click-blocker` height bumped 60→62 to fully clear hover affordances.
- `.station-col` got `pointer-events: none` — the column container boxes were swallowing clicks in the leftmost ~37px and rightmost ~39px of the video rect because they overlap the video horizontally. The buttons inside still set `pointer-events: auto` so they remain clickable.

### Settings panel
"Streamer Options" (which configured the now-removed Browser Source server) became "Streamer Info": a static instructional sub-panel walking through OBS setup — Window Capture, capture method = Windows 10 (1903 and up), Capture Audio (BETA) on, Capture Cursor off, Client Area on, F9 hint. The panel ID stayed `streamer-settings-panel` for backward compatibility with `BuildSyncScript`'s panel-reset logic.

### Dead code removed
The Browser Source approach (built in an earlier draft of this session before the Window Capture pivot) is gone:
- Deleted `src/Services/BrowserSourceServer.cs` (HTTP listener + SSE infrastructure for `/banner`, `/player`, `/events`)
- Deleted `src/Services/NowPlayingState.cs` (was only an SSE broker between overlay and the server; reverted to direct `overlay → banner` event wiring in `App.xaml.cs`)
- Deleted `src/Renderer/obs/{player.html,player.css,player.js}` (the OBS-tailored variants)
- Removed `BrowserSourcePort` from `PulsenetSettings`, `BrowserSourceDefaultPort`/`BrowserSourceLoopback`/`BrowserSourceObsFolder`/`PulsenetBroadcastChannelId` from `Constants`, the `browserSourcePort` and `playerState` web-message handlers from `OverlayWindow`, the `__pulsenetSetStreamerState` JS hook + Streamer Options handlers from `Renderer/player.js`, and the `.streamer-url-input`/`.streamer-status`/`.streamer-copy-btn` CSS rules
- Net delta from the Browser Source experiment: ~600 lines removed across 10 files

### Version bump
`v0.4.1 → v1.4.1`. Major bump because the streamer-facing capability is fundamentally new: PulseNet Player is now usable as an OBS source out of the box, both visually (Window Capture) and audibly (process-loopback bridge). Same workflow that previously required Display Capture (with all its privacy trade-offs) now works with a targeted Window Capture source. This is the feature that makes PulseNet viable for its second, non-music use case too — visual cover for stream-sniper-sensitive game info on the broadcast.

### Release
Tagged `v1.4.1`, pushed. Build & Release workflow publishes `PulseNet-Player.exe` + `PulseNet-Setup.msi` to the GitHub release page; v0.4.x clients see the update banner on next launch.

---

## 2026-04-22 — Session 10 — v0.4.1 — Full outer frame bezel no longer clipped

### The bug
Session 6 (v0.3.1) had resized the frame PNG to 1252×670 and placed it at offset `(-25, -12)` inside a 1202×646 `#app` container with `overflow: hidden`, accepting the overhang clip as a visual tradeoff. Turned out the overhang wasn't empty bezel — it contained the frame's rounded corners, outer bolts, and glow tips. All four outer edges were being shaved 25px horizontally and 12px vertically, visible in tester screenshots.

### The fix (option 1 — code-only, asset untouched)
Grew the canvas to match the art instead of trimming the art to match the canvas:
- `src/Renderer/style.css`: `#app` 1202×646 → 1252×670. `#frame-base` and `#frame-glow` offsets `(-25,-12)` → `(0,0)` — no more overhang, no more clipping. Every other absolutely-positioned element shifted `+25x, +12y` to sit at the same spot relative to the frame: `#video-wrap` and `#station-preview` `(193,88)` → `(218,100)`; `.station-col` base top `80` → `92`; `#stations-left` `(37,129)` → `(62,141)`; `#stations-right` left `966` → `991`; `#pulsenet-home-btn` `(118,80)` → `(143,92)`; `#about-btn` `(1049,521)` → `(1074,533)`; `#settings-btn` `(507,581)` → `(532,593)` (still centred — 626 = 1252/2); both settings panels left `348` → `373`, bottom `62` → `74`.
- `src/Constants.cs`: `FrameDisplayWidth/Height` `1202/646` → `1252/670` so the WPF window and WebView2 viewport grow to match. Consumed by `App.xaml.cs` (off-screen banner-parking offset) and `OverlayWindow.ApplyZoom` (window resize + `WebView.ZoomFactor` scaling) — no other call sites, so the bump propagates automatically. Saved window positions in `settings.json` remain valid (position is x/y, not size).

### Dev-loop gotcha
`<Platform>x64</Platform>` in the csproj makes `dotnet build` write to `bin/x64/Debug/net9.0-windows/` by default, but an older v0.3.1 artefact from Apr 18 was still living at that path and auto-starting at login. `dotnet run --no-build` hit the mutex (`Constants.MutexId`) and silently exited, so the on-screen window kept being the stale v0.3.1 build (which is why its splash correctly reported "v0.4.0 available"). Resolution: `taskkill` the zombie, `dotnet build -a x64` (lands in the RID-qualified `bin/x64/Debug/net9.0-windows/win-x64/` subdir), launch that exe directly. Not a code issue — worth noting for the next time someone wonders why changes don't take.

### Release
Tagged `v0.4.1`, pushed. Build & Release workflow publishes `PulseNet-Player.exe` + `PulseNet-Setup.msi` to the GitHub release page; clients at v0.4.0 will see the banner on next launch.

---

## 2026-04-18 — Session 9 — v0.4.0 — Mini banner + Miniplayer Settings

### Minimise to banner
New tri-state window lifecycle for overlay + banner: `Hidden ↔ Banner ↔ Full`. Added `MinimizeMode` setting (`Banner` default, `Tray` opt-in) selectable in the main settings panel. Hotkey cycles Full ↔ Banner in banner mode, Full ↔ Hidden in tray mode. `MiniBannerWindow` is a separate WPF + WebView2 window, `WS_EX_TRANSPARENT` + `WS_EX_TOOLWINDOW`, alpha=1/255 background (so OS hit-testing still functions when WS_EX_TRANSPARENT is cleared in edit mode), sharing the Renderer virtual host but with its own WebView2 cache folder (`WebView2BannerCache`) because the overlay env uses `--disk-cache-size=0` and mismatched options on the same userDataFolder silently fails `CoreWebView2Environment.CreateAsync`.

### Banner content
Three rows served from `banner.html` via `PostWebMessageAsJson`: station label (pushed on `activateStation` / home-button click), track title (polled by player.js and pushed on change, hidden when empty), and hotkey hint (seeded from `ToggleHotkey`, re-synced on settings change). Home-button label is `Pulse Broadcasting Network - LIVE Music`.

### Miniplayer Settings sub-panel
New button in the main settings menu (between Lock and Minimize-mode rows) opens a sub-panel that takes the same anchor as the main panel. While open the banner enters edit mode: `WS_EX_TRANSPARENT` is cleared (and `SWP_FRAMECHANGED` forced so the style change actually takes effect), so clicks reach the banner. Sub-panel exposes: banner lock toggle (independent of main overlay lock), banner opacity slider (20–100% applied via body opacity), scale control (20–120% default 100, drives both window Width/Height and `WebView.ZoomFactor` so content scales with the frame), and a `Reset to centre` button that re-centres on the working area.

### Drag implementation
The obvious `WndProc` → `WM_NCHITTEST` → `HTCAPTION` trick doesn't work because the WebView2 child HWND intercepts mouse events before the parent's WndProc sees the hit-test. Instead, `banner.js` posts `bannerDragStart` on left-mousedown, and the host calls `ReleaseCapture` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)` to kick off Windows' native modal drag loop. Drop position persists through `LocationChanged` (with a suppression flag around programmatic moves like off-screen parking and `ShowBanner`'s snap-to-anchor).

### Settings shape
New fields on `PulsenetSettings`: `MinimizeMode`, `BannerLocked`, `BannerOpacity`, `BannerScalePct`, `BannerLeft`, `BannerTop`. Tray reset (session 7) already lands the overlay back to 100% zoom; banner `Reset to centre` does the equivalent for the banner independently.

---

## 2026-04-18 — Session 8 — Hover-to-control hotkey forwarder (pinned)

### What's wired
Global low-level keyboard hook (`WH_KEYBOARD_LL`) installed on overlay show. Allow-list: Space, K (play/pause), M (mute), ←/→ (seek ±5s), ↑/↓ (volume ±5). When intercepted, key is swallowed at OS level and `window.__pulsenetForwardKey(action)` is invoked via `ExecuteScriptAsync`; the renderer drives the YouTube IFrame API directly (`pauseVideo`, `seekTo`, `setVolume`, `mute`, etc.). Auto-repeat suppressed via a `_hoverKeysDown` HashSet so holding a key fires once per press.

### Two gates
1. `_cursorOverVideo` — JS-pushed via `mouseenter`/`mouseleave` on `#video-wrap` (kept in JS to avoid C#-side DPI/zoom math; an earlier physical-px rect computation broke at 125% DPI).
2. `_canForwardKeys` — JS-pushed whenever the player mode changes (`onReady`, `teardownPlayer`, `loadLiveStream`). True only when the API player is ready and live-stream mode is off. Without this gate the hook would swallow keys it can't actually forward, which previously broke the original click-then-keys flow.

### Pinned: live-stream limitation
The `PulseNet LIVE` button uses a raw `<iframe>` pointed at `youtube.com/embed/live_stream?channel=…` (the IFrame API can't pin to a live stream because the `videoId` rotates per restart). With no API surface, `_canForwardKeys` stays false on live mode and the hook falls through, preserving YouTube's native click-then-keys behaviour. Hover-to-control on live streams would need either a `YT.Player` wrapping of the live iframe (with periodic re-resolution of the current `videoId`) or a SendInput-based focus/click simulation. Both are intrusive enough to defer until the first playlist-backed station goes live and the API path can actually be exercised.

---

## 2026-04-18 — Session 7 — Tray reset, min zoom 30%, button rows shifted, top-left click-blocker

### Min zoom raised 20% → 30%
Below 30% the frame graphics break down visually. Clamp updated in three places: `OverlayWindow.ApplyZoom` (authoritative; also rescues stale `settings.json` values <30), `index.html` `#zoom-input` `min`, and `player.js` `sendZoom` floor.

### Button columns shifted 10px outward
After the background-graphics widening, both station columns sat too close to the video. Shifted `#stations-left` 47→37, `#stations-right` 956→966; companion home/about buttons updated to keep their column-center alignment (128→118, 1039→1049). Comments updated with new center math.

### Top-left click-blocker (`#click-blocker-tl`)
New 645×60 transparent absorber added inside `#video-wrap`, anchored at (0,0), z-index 3 — same pattern as the existing bottom blocker. Neutralises YouTube's channel-name / title-link in the upper-left of the player chrome, which was spawning external browser windows that kept streaming after close (tester feedback #4). Width tuned iteratively (150→220→600→630→645) to reach the right edge of the YouTube title text without covering the playback toggle / volume / settings controls on the bottom-right.

---

## 2026-04-18 — Session 7 — Tray "Reset window position"

### Recovery from broken window state
Beta tester reported resizing the overlay to 20% pushed it off the visible area, with the only recovery path being hand-editing `settings.json`. Added a `ResetWindowRequested` event + "Reset Player Window" menu item to `TrayIcon`. `OverlayWindow.ResetWindow()` reapplies 100% zoom (which also resizes Width/Height to the full frame canvas), recenters on the primary monitor via the existing `CenterOnScreen()` helper, persists the new position + zoom through `SettingsManager`, and ensures the overlay is visible. Wired in `App.xaml.cs` directly to the singleton overlay reference. Menu order is now: Retry update (conditional), Reset Player Window, Exit.

`ApplyZoom` only updates C# state — the renderer's settings-panel `#zoom-input` field is normally synced on `ShowOverlay` via `BuildSyncScript`. A reset triggered while the overlay is already visible left the displayed value stale, so `ResetWindow()` explicitly re-runs `BuildSyncScript` after the zoom change.

---

## 2026-04-18 — Session 6 — v0.3.1 — Natural video resolution + frame refit

### Video at natural 16:9, no crop
Prior versions used `transform: scale(1.055)` on the YouTube iframe to fill the 812×433 video container's pillarbox bars (container was 1.875:1, not true 16:9). The fill was cosmetic but chopped ~12px off the top and bottom of actual video content. Removed the scale transform entirely. Reshaped `#video-wrap` and `#station-preview` to 812×457 (true 16:9), vertically recentered at y=316.5 so all station button positions remain valid without recalculation.

### Frame refit: 1252×670
With the video rect now taller, the previous frame size (1222×656, offset -10/-5) was too small — the cutout clipped the expanded video. Scaled the frame PNG display to 1252×670 with offset (-25, -12) so the cutout aligns with the new 812×457 video area. Frame art is slightly non-uniformly stretched (1.041× wide, 1.037× tall relative to its native 1202×646 canvas ratio); not visually noticeable.

### Click-blocker — 50px → 60px
YouTube's end-card / share overlay extends slightly higher than the previous 50px absorber reached. Bumped `#click-blocker` height to 60px so the full bottom control strip is neutralised.

### Docs
README Installation section rewritten to describe both `PulseNet-Setup.msi` (per-user installer, recommended) and `PulseNet-Player.exe` (standalone launcher). Roadmap updated — auto-update, CI, and WiX installer now ticked off; remaining roadmap is volume control, Now Playing tooltip, Info dialog, and real playlist IDs.

### Release
Tagged `v0.3.1`, pushed. Build & Release workflow publishes both artifacts to the GitHub release page.

---

## 2026-04-17 — Session 5 — v0.3.0 — Discord CTA, click-blocker, tray icon cleanup

### Discord button in settings panel
Earlier iterations placed a "Join us on Discord" button above and then overlaying the video. Neither position felt right — centered above fought with the video centerline, and bottom-right overlayed YouTube's own controls. Solution: embedded the Discord wordmark button inside the settings panel, above the frame-lock row. Uses the existing `.settings-action-btn` class for identical dimensions (452×40), with the logo filling the button edge-to-edge via `object-fit: contain`. Clicks route through the existing `openUrl` WebView message → `OpenInDefaultBrowser`, opening <https://discord.com/invite/Vxn7kzzWGJ> in the user's default browser. Logo source (2260×200) resized to 904×80 (2x HiDPI) for crisp display.

### Click-blocker — YouTube overlay neutralised
Added a transparent `#click-blocker` div inside `#video-wrap`, positioned to cover the bottom 50px of the video rect (full width × 50px). No listeners, no visual feedback — just absorbs clicks so the user can't reach YouTube's "More videos" / "Share" / fullscreen overlay at the bottom. `z-index: 3` puts it above the iframe but below frame/stations/settings.

### Tray icon — dropped active-state green tint
`TrayIcon.cs` previously applied a 140-alpha green overlay (`Color.FromArgb(80, 200, 120)`) to signal "player open". Clashed with the brand cyan. Removed `_iconActive` + `TintIcon` helper + `SetActive` method entirely; removed the `OverlayShown`/`OverlayHidden` event subscriptions from `App.xaml.cs` that fed it. Tray icon now stays blue regardless of state.

### 1px video-frame gap fixed
`#video-wrap` and `#station-preview` had `top: 101px` (off by one from the file's header spec of `top=100`), producing a visible gap between the top of the video and the sci-fi frame overlay graphic. Reset to `top: 100px` to match the frame's video cutout.

---

## 2026-04-17 — Session 4 — README, sales page, station count correction

### README.md
Comprehensive project README written covering: project overview and lore, features list, requirements, installation, usage table, full 19-station lineup (both columns), architecture overview, adding stations / going live guide, roadmap, and build-from-source instructions.

### Sales page (docs/index.html)
Landing page built matching the SC-HUD design language — dark space theme, cyan accent, star field hero, animated "Coming Soon" badge. Sections: hero with screenshot, stats bar, features grid, full station lineup table, how-it-works steps, settings reference, notes, lore banner, CTA. GitHub references intentionally excluded (no download links, no repo links).

### Promotional screenshot
`src/Assets/PulseNet Player.png` added — used as the hero image in both the README (`docs/preview.png`) and the sales page.

### Station count corrected to 19
**PulseNet LIVE** (the home button, top-left) is the 19th station — plays all channels indiscriminately. Each column has 10 buttons (left: PulseNet LIVE + 9 stations; right: 9 stations + Info). All references updated across README, sales page, and station descriptions.

### SteelSeries Sonar audio session rename — investigated, not achievable
Attempted to rename the WebView2 audio session via `IAudioSessionControl::SetDisplayName` (CoreAudio COM interop) so Sonar would show "PulseNet Player" instead of "MSEDGEWEBVIEW2". Implementation was correct and works for standard Windows audio mixers (EarTrumpet, Windows Volume Mixer), but SteelSeries Sonar reads the process executable name directly and ignores the session display name. Root cause: audio is produced by `msedgewebview2.exe` child processes spawned by WebView2 — this is true regardless of how the host executable is named. No programmatic fix is possible. Code reverted cleanly.

---

## 2026-04-16 — Session 3 — Special buttons, offline states, mouse hook fix

### Special utility buttons
Two new buttons added outside the station columns:
- **PulseNet home button** (top-left): uses `pulsenet_icon.png`, plays the live stream `videoId: 'b-YcZMSKqeo'` directly in the player. Link moved here from l1 (Alternative Routes), which reverts to a standard playlist station.
- **Info button** (bottom-right): text-only ("Info", 8px Consolas), shows `info.png` on hover. Sends `{type:'about'}` to C# on click for future wiring.

### Button sizing and column alignment
- Station buttons reduced from 38×38px to 32×32px DOM (renders ~39×39px with scale 1.23).
- Columns re-centred: mid-gap between items 5 and 6 (counting special buttons as part of the column) aligned to video vertical centre (y=316.5px).
  - Left col: `top: 129px` (home button counts as item 1, stations as 2–10)
  - Right col: `top: 80px` (stations 1–9, info button as item 10)
  - Special buttons spaced one gap (17px) from the nearest station button.

### Station hover preview masked by frame
`#station-preview` z-index lowered from 4 → 1, placing it behind the frame overlay. Previously it rendered above the frame and bled outside the cutout. Now it's masked by the frame the same way the idle logo and video are.

### Offline station images
- `live` flag added to every station entry in `stations.js` (all currently `false`).
- Hover preview auto-derives `Offline_<filename>` path when `live: false`; uses regular icon when `live: true`.
- All 18 `Offline_` images created, deployed to `Renderer/assets/stations/`.
- To bring a station live: set `live: true` and replace the placeholder `playlistId`.

### Mouse hook — no longer blocks system scroll when overlay hidden
`WH_MOUSE_LL` was installed at app startup and ran for the entire process lifetime, adding latency to all mouse events globally even when the overlay was hidden. Refactored: hook is now installed in `ShowOverlay()` and uninstalled in `HideOverlay()`. Zero system-wide impact when the overlay is off.

### Minor layout tweaks
- Video window and preview rect shifted down 1px (`top: 101px`) for better frame alignment.
- Settings Menu button moved up 5px (`top: 581px`).

---

## 2026-04-14 — Session 2 — UI polish, drag overhaul, zoom, channel wired

### Drag system rebuilt (WH_MOUSE_LL → JS-initiated)
Previous drag implementation used a geometric exclusion zone in the C# hook to decide what was draggable. This broke at non-100% zoom because CSS zoom scaled visuals but not the WPF window, causing coordinate mismatches. Full rewrite:

- **JS is authoritative:** `mousedown` listener walks the DOM — if the click is not on a button, input, video area, or settings panel, it sends `{type:'startDrag'}` to C#.
- **C# hook tracks last cursor position** (volatile fields) so it can reconstruct the drag origin when the JS message arrives.
- **WH_MOUSE_LL handles MOUSEMOVE and LBUTTONUP** — moves window via `SetWindowPos(SWP_ASYNCWINDOWPOS)`, syncs WPF `Left`/`Top` on release.
- Result: entire frame is draggable except buttons, video area, and settings panel. Works correctly at all zoom levels.

### Zoom: window resizes with content
`ApplyZoom` now resizes the WPF window proportionally (`Width = FrameDisplayWidth * factor`, `Height = FrameDisplayHeight * factor`) and sets `WebView.ZoomFactor` in the same call. Previously CSS `zoom` was used, which caused blink and coordinate mismatches. Zoom range: 20–100%.

### Zoom controls replaced
Zoom slider removed. Replaced with: `-10% size` button · number input (20–100) · `+10% size` button. Eliminates blink issues the slider had.

### Settings panel doubled
Font sizes doubled (8→16px), panel width 250→500px, button heights 20→40px, border-radius 6→12px.

### Window position persistence
`HideOverlay` saves `WindowLeft`/`WindowTop` to settings. `ShowOverlay` restores them, falls back to center on first run.

### Idle logo
`main_logo.png` shown in video area when no station is active. Hidden when a station button is clicked. Restored on playlist end with no active station.

### Splash screen
Logo updated to `main_logo.png` (Pulsenet branding), resized to 225×225px. Layout anchored top with reduced margins so all content is visible.

### Scroll wheel fix
WH_MOUSE_LL scroll hook now checks if cursor is inside the overlay window rect before consuming the event. Previously captured scroll globally whenever the overlay was visible.

### Frame width trim
Frame width reduced from 1258px to 1202px (28px each side trimmed) to better fit the frame_base.png visual. All CSS x-coordinates and `Constants.FrameDisplayWidth` updated accordingly. Video area (812×433) unchanged.

### Video fill
YouTube iframe scaled via `transform: scale(1.055)` to fill pillarbox bars caused by the 812×433 container being wider than 16:9.

### Station playback
- `stations.js` updated: `videoId` property supported alongside `playlistId` for single-video stations.
- Top-left station (Alternative Routes) wired to test video `b-YcZMSKqeo`.
- All 18 station playlist IDs updated to real Pulsenet channel (`UCIMaIJsfJEMi5yJIe5nAb0g` → uploads playlist `UUIMaIJsfJEMi5yJIe5nAb0g`).
- Default `YoutubeChannelId` in settings updated to `UCIMaIJsfJEMi5yJIe5nAb0g`.

### Assets
- `icon.ico` replaced with Pulsenet branding — multi-resolution (16, 32, 48, 256px) generated from `PulseNetIcon 1024x1024.png`.
- `main_logo.png` replaced with resized (812×433) Pulsenet logo for idle state.
- Stale `logo.png` resource entry removed from `pulsenet.csproj`.

---

## 2026-04-14 — Modules 0-2 — Sci-fi frame overlay rebuild

**Architecture decision: stay WPF, not Python rewrite.**
The PulseNetPlan.md proposal to rewrite in PyQt6 was discarded, no technical justification, WebView2 is more reliable than QWebEngineView on Windows for YouTube, and the existing WPF infrastructure was already proven.

### Frame asset
- Source image: `src/Assets/pulsenet_background.png`, 2515×1292, transparent outside frame
- Video area cut to fully transparent by user
- Displayed at 50% → **1258×646** window (since trimmed to 1202×646)
- Video rect within display: `left=193 top=100 width=812 height=433`

### Renderer rebuild (everything visual lives in HTML/CSS/JS)
WPF airspace problem (WebView2 HWND always renders above WPF visuals) means the frame and buttons cannot be WPF elements. Solution: put everything in the renderer.

Layer stack (z-index):
1. YouTube iframe — `video-wrap` at video rect
2. `frame_base.png` — structural frame, `pointer-events:none`
3. `frame_glow.png` — glow overlay, CSS pulse animation, hidden until asset exists
4. Station buttons — `pointer-events:auto`, inside frame panel areas

### Station buttons
- 18 buttons: 9 left column + 9 right column
- Icon-based; hover shows full station icon preview over video area
- Defined in `Renderer/stations.js`
- Active button gets cyan glow border

### Window
- Fixed size: `Constants.FrameDisplayWidth × FrameDisplayHeight` (currently 1202×646)
- Centered on primary screen on first show; position persisted after that
- Drag handled via JS `startDrag` → C# WH_MOUSE_LL hook

---

## 2026-03-30 — v0.1.0 — Initial project setup

**Cloned from SC-HUD** and transitioned into a standalone YouTube overlay player.

### Architecture decisions

**Virtual host instead of `file://`**
YouTube IFrame API requires the host page to be served over HTTPS. WebView2's `SetVirtualHostNameToFolderMapping` maps `https://pulsenet.local/` to the local `Renderer/` folder, satisfying this requirement without a real server.

**Channel ID via URL query param**
The C# side navigates to `https://pulsenet.local/index.html?channelId=UCxxxxxx`. The JS reads `URLSearchParams` on load. When the user changes the channel ID in Settings, the app re-navigates with the new param.

**Uploads playlist auto-derivation**
Every YouTube channel has an auto-generated uploads playlist with ID `UU` + `channelId[2:]` (swap the `UC` prefix).

**OAuth/sign-in via existing popup mechanism**
Any `window.open()` from within the iframe goes through `OnNewWindowRequested`, which opens a shared-environment WebView2 popup. Auth cookies persist in `%APPDATA%\pulsenet-radio\WebView2Cache`.

### What was stripped from SC-HUD

| Removed | Reason |
|---------|--------|
| `ScAnchorService.cs` | SC-specific — no game to anchor to |
| `OverlayUrl` setting | Replaced by `YoutubeChannelId` |
| Environment toggle (Ctrl+Shift+D) | Was a prod/staging scbridge.app switcher |
| SC-specific error messages | Irrelevant |

---

## In-Universe Lore

**Pulse Broadcasting Network (PulseNet)**
Entertainment Division of The Exelus Corporation

- Founded: 2905 (51+ years of operation as of story time)
- Scope: One of the UEE's largest entertainment broadcast networks
- Tagline: *"The 'Verse always has a soundtrack."*
