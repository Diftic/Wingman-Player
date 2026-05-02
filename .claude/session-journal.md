# Session Journal

A living journal that persists across compactions. Captures decisions, progress, and context.

## Current State
- **Focus:** Full PulseNet → Wingman Player rebrand landed on 2026-05-02. Code identifiers (`pulsenet` → `wingman_player`, `PulsenetSettings` → `WingmanPlayerSettings`, `__pulsenet*` JS hooks → `__wingman*`), file names (`pulsenet.csproj` → `wingman_player.csproj`, slnx, `PulsenetSettings.cs` → `WingmanPlayerSettings.cs`), assembly name (`PulseNet-Player` → `Wingman-Player`), MSI manufacturer/name/UpgradeCode, GitHub URLs (now point at `Diftic/Wingman-Player`), AppData folder (`pulsenet-radio` → `wingman_player`), virtual host (`pulsenet.local` → `wingman.local`), Mutex GUID, all UI display strings, README, docs/index.html. Build green (0/0).
- **Blocked:** Wingman YouTube skill (TBD) not yet built — no host-side bridge that calls `__wingmanLoad`. `Assets/icon.ico` is still the old PulseNet icon (works, but a placeholder — needs new artwork before regen). GitHub repo `Diftic/Wingman-Player` doesn't exist yet; UpdateChecker URL is set up for it but auto-update will 404 until the repo is created and a release published.

## Log

### 2026-05-02 — Completed: Full PulseNet → Wingman Player rebrand
- **Scope.** Closed out the rebrand the v1.8.x Wingman conversion deferred. User picked snake-case `wingman_player` for code/namespace/file convention, `Wingman Player` (space) for UI display strings, `Wingman-Player` (hyphen) for binary/repo/installer artifact naming. Treated as a fresh product (no published repo): hard-switched AppData folder without migration, regenerated MSI UpgradeCode + Mutex GUID.
- **Project files.** `src/pulsenet.csproj` → `src/wingman_player.csproj`; `pulsenet.slnx` → `wingman_player.slnx`. csproj `<StartupObject>` and `<AssemblyName>` updated; slnx `<Project Path>` updated.
- **Code identifiers.** Bulk-flipped `namespace pulsenet[.X]` → `namespace wingman_player[.X]` across every .cs file. `PulsenetSettings` record + .cs file → `WingmanPlayerSettings`; SettingsManager + OverlayWindow.OnSettingsChanged retyped. App.xaml + 3 Window XAMLs (`Overlay`, `MiniBanner`, `Splash`) flipped `x:Class` to `wingman_player.UI.*`. TrayIcon embedded resource lookup: `pulsenet.Assets.icon.ico` → `wingman_player.Assets.icon.ico`.
- **Constants identity.** `ApplicationName` = "Wingman Player". `MutexId` regenerated (`wingman_player-64459292-292A-417A-9E12-E6E00A3040B5`). `AppDataFolderName` = `wingman_player` (hard switch — no migration; user said no repo / no users yet). `PlayerVirtualHost` = `wingman.local`.
- **JS hooks.** `__pulsenetForwardKey`, `__pulsenetNowPlaying`, `__pulsenetVersion`, `__pulsenetRefreshVersionLabel`, `__pulsenetUpdateCheckDone` all → `__wingman*`. Both player.js and the C# emitters in OverlayWindow updated. `window.__wingmanLoad` was already correct from session 17.
- **Update path.** `UpdateChecker` API URL → `https://api.github.com/repos/Diftic/Wingman-Player/releases/latest`. UA → `Wingman-Player/{ver}`. Asset names: `PulseNet-Player.exe` → `Wingman-Player.exe`, `PulseNet-Setup.msi` → `Wingman-Player-Setup.msi`. `SelfUpdateService` temp dir + tempMsi/Exe paths aligned.
- **Workflow + installer.** `.github/workflows/build.yml`: csproj path, copy/upload exe names, MSI output name. `installer/installer.wxs`: Name, Manufacturer, INSTALLFOLDER, AppMenuFolder, Shortcut Name, Target exe, RegistryValue Key all rebranded; **UpgradeCode regenerated** to `65E7C967-BC4C-4DDF-9BDE-BD8B9765ED28` so MSI sees this as a new product. `installer/license.rtf` copyright line updated.
- **OBS Streamer Info.** "Set Window to *PulseNet Player*" → "Set Window to *Wingman Player*" — now matches the actual window title after the rebrand.
- **Docs.** README rewritten (the prior version still described 19 stations / station selector / `stations.js` config that no longer exist). `docs/index.html` (GitHub Pages landing) replaced — old version was a 1300-line sales page for PulseNet stations. TODO title + open follow-ups updated; "Full rebrand" item moved to checked.
- **Out of scope.** Historical DEVLOG sessions 1–17, `audits/2026-04-30-red-team.md`, `PulseNetPlan.md`, `Codex analysis.md` (untracked), `tools/audio-probe/` (untracked). All historical / dated content — names of past PulseNet artifacts in those entries reflect the codebase at the time.
- **Build.** `dotnet build src/wingman_player.csproj -a x64` after wiping stale `bin/`/`obj/`/`artifacts/`: green, 0 warnings, 0 errors.

### 2026-05-02 — Completed: Wingman conversion (visual sign-off + close-down)
- **Iterative hex/red toolkit button.** Started as a flat-top hexagon at (1062, 595) inside the cutout. User asked for it on the frame, not the video; moved to (1178, 670). User asked for nudges of "15 up + 15 left", "another 15 up + 15 left", "5 left + 10 up", "again", "5 up + 5 left, make it round, make it red". Final: round 40×40 at **(1133, 615)** with a red palette (`rgba(40,0,8,0.70)` background, `rgba(248,113,113,0.45)` border, hover `rgba(248,113,113,0.20)` + crimson glow), `border-radius: 50%`. The crossed hammer + screwdriver SVG remained throughout.
- **Panels lowered.** After the button settled, user asked to lower the menus by 25px. Final panel anchor: **`right: 75px; bottom: 105px`** for all three sub-panels (main settings, miniplayer settings, streamer info).
- **F8 default hotkey.** `PulsenetSettings.ToggleHotkey`, `MiniBannerWindow._pendingHotkey` + fallback, `banner.html` label, `banner.js` fallback, and the Streamer Info "Press F8 to hide" tip all flipped from F9 → F8. Existing user settings.json migrated in-place via PowerShell during testing (System.Text.Json doesn't auto-migrate; deployed installs will keep their bound key until rebound).
- **Discord URL swap.** `player.js` Discord button now opens `discord.com/invite/shipbit-1173573578604687360` (ShipBit's Discord, replacing the old Vxn7kzzWGJ invite).
- **Splash full-bleed.** `SplashWindow.xaml` restructured as a `Grid` with `<Image Stretch="UniformToFill">` covering the whole window, a bottom-band gradient (`#000d1b2a` → `#CC0d1b2a`) for legibility, and the F-key prompt + tray-exit hint floating at the bottom. The earlier 225×225 inset image + divider StackPanel layout is gone. User confirmed: "splash screen is perfect !!"
- **Build / runtime fixes during the session.** `Assets/icon.ico` was missing in the working tree (deleted pre-session) and broke the build via `<ApplicationIcon>` + `<EmbeddedResource>` in `pulsenet.csproj` and `TrayIcon.cs`. Restored from HEAD via `git checkout`. `src/Assets/main_logo.png` (referenced by `SplashWindow.xaml` + csproj `<Resource>`) was also deleted pre-session — replaced with a copy of `passive background.png` so the splash background and the in-app idle layer share the artwork.
- **Documentation closed out.** This entry plus a full session-17 entry in `DEVLOG.md` and a Wingman-conversion section in `TODO.md` (with open follow-ups: Wingman skill control mechanism, full rebrand, new icon, streamer-info heading copy). User's instruction: "log everything to .md and close down for the day."

### 2026-05-02 — Started: Wingman conversion
- **Goal.** Strip PulseNet-specific YouTube channel lock-in and station UI. Player becomes a thin overlay driven by a Wingman YouTube skill (TBD); native YouTube IFrame controls remain visible inside the cutout.
- **UI.** Removed left/right station columns (`#stations-left`/`#stations-right`), `#pulsenet-home-btn`, `#about-btn`, `#station-preview` hover layer. Replaced the wide horizontal Settings button with a hex-clipped tool button (hammer + screwdriver crossbones SVG) anchored bottom-right of the cutout. Settings panel re-anchored to `right: 130 / bottom: 100`. Settings menu contents kept as-is for now.
- **Frame & background.** New `Frame.png` (1537×1023, transparent cutout 167,131 → 1203×771, 3:2 aspect). Frame is stretched non-uniformly into a 1252×731 canvas so the cutout lands on a 16:9 video rect at (136, 94) 980×551. `Constants.FrameDisplayWidth/Height` updated. New passive background (`src/Assets/passive background.png`) replaces `main_logo.png` in both renderer (idle layer) and `src/Assets/main_logo.png` (referenced by `SplashWindow.xaml` and csproj `<Resource>`). `idle-logo` switched to `object-fit: cover`.
- **Code rip-out.** `Renderer/stations.js` deleted; `Renderer/assets/stations/` deleted; `Renderer/assets/{Info,pulsenet_icon,radio_background,Background graphics,PulseNet Player}.png` deleted. `player.js` rewritten: removed `STATIONS`, `buildButtons`, `activateStation`, `loadStationIntoPlayer`, `loadLiveStream`, `liveStreamActive`, `PULSENET_LIVE_CHANNEL`, station preview, channelId URL-param parsing, `uploadsPlaylistId`. Added `window.__wingmanLoad(videoId, playlistId)` as the new external entry point — host calls via `CoreWebView2.ExecuteScriptAsync`.
- **C# rip-out.** `Constants.DefaultChannelId` deleted, `PulsenetSettings.YoutubeChannelId` deleted, `OverlayWindow._loadedChannelId` deleted, channel-change reload path in `OnSettingsChanged` deleted, `BuildPlayerUrl` simplified to no-arg. `RestartNavigation` deleted (only the channel-change path used it).
- **Build.** Green (0/0). `Assets/icon.ico` was deleted from the working tree pre-session and broke the build; restored from HEAD via `git checkout HEAD -- src/Assets/icon.ico`. Replacing with a Wingman-branded icon is a follow-up.
- **Open decisions.** Window title (`Title="PulseNet Player"` in OverlayWindow.xaml) and assembly name `PulseNet-Player` left untouched until full rebrand is scoped. OBS Streamer Info instructions still reference `PulseNet Player` window title to match.

### 2026-05-02 - Validated: v1.8.x architecture received well by testers
- v1.8.0 (localhost audio stream) and v1.8.1 (Streamer Info polish + em-dash sweep) both shipped 2026-05-01. Tester feedback the next day was positive - the OBS Media Source workflow Just Worked for them, no Sonar / Voicemeeter / Wavelink required, no doubled local audio.
- Implication for future work: the architectural hypothesis (passive audio listener over loopback HTTP, instead of dual-render with app-router workflow) is the right path. Future audio-routing work (e.g. Codex's endpoint-selection proposal) should layer on top of this rather than replace it.
- Next natural follow-ups (none currently active): block-public-release security tier from `audits/2026-04-30-red-team.md` (sign MSI in CI, pin GitHub Actions by SHA, pin WiX version, replace wildcard `<Files Include="stage\**">` with an explicit list); real playlist IDs for the 18 stations; bump to v1.0.0 on/after 2026-06-17 if no major issues emerge during the rest of the public beta.

### 2026-05-01 — Completed: v1.8.0 — localhost audio stream architecture
- **Architecture replacement.** v1.6.3's bridge re-emitted captured WebView2 audio to the system default endpoint, doubling local audio for any streamer not running an app router and requiring a `StreamerModeEnabled` opt-out toggle. New design: bridge captures only, forwards PCM to a `LocalAudioStreamServer` (TcpListener on 127.0.0.1:17329) that serves an endless 16-bit stereo WAV. OBS pulls the URL via Media Source. Listener hears WebView2 directly through Windows as normal. Single audio path locally.
- **Why this works where v1.4.x's BrowserSourceServer didn't.** The earlier "audio over localhost" attempt hosted a YT player iframe in OBS Browser Source — two YouTube players with no control channel between them, sync hell. The v1.8.0 design has only one YT player (PulseNet's WebView2); OBS is a passive consumer of whatever PCM the bridge captures from it. Pause/skip in PulseNet propagates because there's nothing in OBS making playback decisions to desync.
- **Validation.** Smoke test: TCP bound, 503 before bridge active, 200 + WAV header after. Probe: ~5s = 2.07 MB at 96 kHz / 2 ch / 16 bit, 99.93 % non-zero samples. OBS in real use: control retention confirmed, latency ~1 s (acceptable for radio because the visible "video" is essentially static).
- **Deletions.** `StreamerModeEnabled` (PulsenetSettings), `streamerMode` web-message case (OverlayWindow), `streamerModeToggle` wiring (player.js), checkbox row (index.html), `streamer-mode-*` CSS, Sonar/Voicemeeter/Wavelink walkthrough paragraph (Streamer Info), entire render path in AudioBridge (Initialize / Start / GetService / GetBufferSize / pre-fill / pump-side render write / render cleanup in finally).
- **Streamer Info rewrite.** Two-section walkthrough: 1. Video — Window Capture (steps 1-6, explicitly leave Capture Audio BETA unticked); 2. Audio — Media Source (steps 7-10, paste localhost URL, Copy URL button). Line-heights tightened ~25 % cumulative across the panel after the rewrite added enough content to clip at the bottom.
- **Settings migration.** Existing v1.6.x `settings.json` keeps `StreamerModeEnabled` until next save; System.Text.Json ignores unknown properties on deserialize, then drops the field on next serialize. No migration code needed.
- **Branch:** experiment/local-audio-stream → master via squash to a single feat: v1.8.0 commit.

### 2026-05-01 — Completed: v1.6.3 — capture-client AudioCategory.Media + Streamer Info specifics
- **Capture-client fix.** v1.4.2 set `AudioStreamCategory.Media` on the bridge's render client only. The capture (process-loopback) client inherited the default `Other` category, which caused intermittent AUX-channel placement on Sonar at startup. Added matching `IAudioClient2.SetClientProperties` call before `Initialize` on the capture client in `src/Services/AudioBridge.cs`.
- **Streamer Info copy.** `src/Renderer/index.html` rewritten: names the two MEDIA-channel sessions explicitly (`MSEDGEWEBVIEW2` is the one to mute, `PULSENET-PLAYER` is the broadcast-clean re-emit), warns against dragging entries between channels (creates phantom locked duplicates Sonar can't clean up), and points at the channel-level volume slider as the first thing to check if audio sounds quiet.
- **Diagnostic harness.** `tools/audio-probe/` (new, untracked + `.gitignore`'d): standalone .NET probe with `--pid` mode (verified loopback capture is post-mute, so `SetMute` stealth-bridge isn't viable) and `--list` mode (enumerates all render endpoints + their sessions, surfaced Sonar/Wave Link virtual outputs). Lives in working tree only.
- **Audit.** `audits/2026-04-30-red-team.md` committed: 2 CRITICAL (MSI signing, supply-chain tier), several HIGH around the auto-update path, with the same-user local attacks accepted as out-of-scope per the OSS-desktop threat model. New "Code health" section in TODO breaks the actionable items into a block-public-release tier and a defense-in-depth tier.
- **Version labelling.** Working tree was labelled v1.6.2 mid-session (csproj, DEVLOG, TODO, audit header). At release time the user asked for a `+0.0.1` bump, so all references were relabelled to v1.6.3 in one pass before commit. Audit's snapshot reference updated alongside so the file stays internally consistent.

### 2026-04-28 — Completed: v1.6.0 — manual update check + version banner
- **Gap.** Auto-update-check on startup existed since v1.4.x, but no in-app way to retrigger between launches and no in-app surface for the current version number. PyCharm crashed mid-session; recovered the work-in-progress diff (5 files modified) and finished it.
- **Fix.** New `version-btn` row at the top of main settings panel. JS posts `{type:'checkForUpdates'}`; new `case "checkForUpdates"` in `OverlayWindow.OnWebMessageReceived` fires `HandleCheckForUpdatesAsync` which calls `UpdateChecker.CheckAsync` off-thread, marshals back via `Dispatcher.InvokeAsync` for a Yes/No `MessageBox`, and on Yes reuses `SelfUpdateService.ApplyAsync` (same path the auto-check uses). C# calls `window.__pulsenetUpdateCheckDone()` after the modal closes to re-enable the button.
- **Version-injection timing fix.** First implementation injected `window.__pulsenetVersion` only via `BuildSyncScript` on `ShowOverlay`, so `player.js`'s initial `versionLabel()` call hit `undefined` and rendered "v0.0.0 — Check for updates" until the first overlay open. Moved injection to `WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` during `InitializeWebViewAsync` — runs before any page script. `BuildSyncScript` still re-injects on every overlay show so a future in-place self-update would surface the new version without restart.
- **Cleanup.** Removed dead `frame_glow.png` references from `index.html`, `style.css` (rule + `glow-pulse` keyframes), and TODO. Asset was aspirational, never produced.
- **Files touched.** `src/UI/OverlayWindow.xaml.cs` (case + handler + injection), `src/Renderer/player.js` (button wiring + version refresh hook), `src/Renderer/index.html` (button row, removed glow img), `src/Renderer/style.css` (removed glow rule), `src/pulsenet.csproj` (1.5.0 → 1.6.0), `TODO.md`, `DEVLOG.md`.
- **Verified.** Clean build (0 warnings, 0 errors). Player launches, WebView2 ready, on-startup auto-check logged "current: 1.6.0, latest: 1.5.0" as expected (latest is still 1.5.0 until the v1.6.0 release publishes via the workflow).
- Tagged `v1.6.0`, pushed. Build & Release workflow producing the release.

### 2026-04-27 21:10 — Completed: v1.5.0 — Streamer Mode toggle + sub-panel UX fixes
- **The bug we shipped in v1.4.2.** v1.4.2 declared the OBS streaming feature complete after testing on a Sonar-equipped streaming setup. What we missed: AudioBridge ran unconditionally on every launch, so the ~90% of users who are *just listening* to music — and don't have an app router — heard doubled audio from both paths hitting their default device with no available fix on their end. Tester surfaced this as soon as we had the discussion about the broader user base. Genuine regression vs pre-AudioBridge.
- **The fix.** New `StreamerModeEnabled` field on `PulsenetSettings`, default `false`. `AudioBridge.RunPump` checks `_settings.Current.StreamerModeEnabled` in its outer scheduling loop *and* in `RunOnce`'s inner sample-pump loop. When false: pump sleeps, no audio sessions created, non-streamers hear single-path audio. When toggled true: pump wakes within ~500ms, WebView2 PID lookup + WASAPI activate proceed as before. When toggled false mid-playback: inner loop notices within ~200ms (next capture-event wait timeout), `RunOnce` returns cleanly, WASAPI clients dispose, back to single-path. No `ManualResetEvent` plumbing needed — the pump's natural sleep cadence covers reaction time.
- **UI.** Checkbox row at the top of the Streamer Info sub-panel (deliberately not in the main settings panel — listeners shouldn't see it; streamers will see it as the natural first step before the OBS setup walkthrough). New web-message case `streamerMode` in `OverlayWindow.OnWebMessageReceived` persists the setting; `BuildSyncScript` reads current value and ticks the checkbox on every overlay show.
- **Sub-panel UX fixes (v1.4.2 missed these).** Two related issues with Streamer Info: (a) clicking outside the panel didn't close it because the document-level click-outside handler in `player.js` was watching only `#settings-panel`; (b) clicking the Settings Menu button while Streamer Info was open opened the main panel *behind* Streamer Info because the button handler folded only `#miniplayer-settings-panel` before toggling. Fix: extended the click-outside handler to also close `#streamer-settings-panel`, and extended the settings-button handler to also fold it before toggling. The miniplayer panel had already been getting both treatments correctly; we just hadn't replicated them when Streamer Info was added.
- Locally verified: streamer mode off → no second `PULSENET-PL` entry in Sonar, no doubling. Streamer mode on → bridge spins up, OBS captures, AUX-mute workflow works. Toggle off mid-playback → bridge tears down within ~200ms. Click-outside and settings-button transitions both work cleanly.
- Tagged `v1.5.0`, pushed. Build & Release workflow producing the release.

### 2026-04-27 20:20 — Completed: v1.4.2 — Sonar workflow + AudioSessionRenamer rip
- v1.4.1's AudioBridge worked for the broadcast but Sonar's per-session UI surfaced two new problems on the streamer's machine:
  1. Sonar parked our session in GAME channel (which applies game-oriented DSP that mangles music — "FAR lesser audio quality") and locked the controls because the classification couldn't be retroactively applied — `PulseNet Player didn't allow Sonar to change the audio settings`.
  2. WebView2's direct path was on a separate Sonar channel, so the streamer heard both paths simultaneously → echo on headphones.
- **Fix 1 — declare AudioCategory_Media before Initialize.** Added `IAudioClient2`, `AudioStreamCategory`, `AudioStreamOptions`, `AudioClientProperties` to `src/PInvoke/AudioBridgeInterop.cs`; in `AudioBridge.RunOnce` we QI the activated IAudioClient to IAudioClient2 and `SetClientProperties` with `eCategory = Media` *before* the `Initialize` call. Sonar now routes us to MEDIA channel with clean DSP. Tester confirmed: lock disappeared, audio quality matched WebView2 direct.
- **Fix 2 — workflow, not architecture.** Streamer mutes Sonar's AUX channel (where WebView2's direct path lives) — only MEDIA reaches their headphones, no echo, no quality degradation. Sonar's per-channel mute is local; it doesn't reach OBS's WASAPI process loopback, so the broadcast continues unaffected. Verified: muting any Sonar channel does not affect OBS Audio Mixer levels for our Window Capture source. Streamer Info panel got both this conditional step and the earlier OBS Monitor Off step.
- **AudioSessionRenamer killed.** Diagnostic (renamer disabled in App.xaml.cs) confirmed Sonar groups WebView2 helpers via process tree, not via display name or icon. Renamer was innocent of the binding; with AudioBridge present it's actively harmful in Volume Mixer (two same-named "PulseNet Player" entries side by side). `src/Services/AudioSessionRenamer.cs` deleted, `src/PInvoke/AudioSessionInterop.cs` trimmed from ~190 → ~95 lines (kept only MMDevice + toolhelp32 bits AudioBridge still uses), `_audioRenamer` field + Dispose call removed from `App.xaml.cs`.
- **Buffer durations dropped to 0** on capture+render Initialize calls (auto = engine period, ~10 ms each side). Tester confirmed this didn't perceptibly change the echo, which validates that the dominant cause was duplicate playback paths through Sonar, not latency. Kept the change anyway — slightly tighter sync is free.
- Tagged `v1.4.2`, pushed. Build & Release workflow producing the release.

### 2026-04-27 17:50 — Completed: AudioBridge (WASAPI process-loopback re-emit)
- Problem: OBS Window Capture's "Capture Audio (BETA)" binds to the captured window's process. PulseNet's window is `PulseNet-Player.exe` but its audio is generated entirely by `msedgewebview2.exe` helper PIDs (descendants), so OBS captured silence. Application Audio Capture didn't help either — `msedgewebview2.exe` doesn't appear in its picker on this machine.
- Solution: new `Services/AudioBridge.cs` (`IHostedService`) plus `PInvoke/AudioBridgeInterop.cs`. Pipeline:
  1. Polls for the WebView2 root browser child of our PID (image == `msedgewebview2.exe`, parent == self)
  2. Activates WASAPI process-loopback on that PID with `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` via `ActivateAudioInterfaceAsync`(`VAD\Process_Loopback`, IAudioClient, PROPVARIANT(VT_BLOB→AUDIOCLIENT_ACTIVATION_PARAMS))
  3. Opens a render `IAudioClient` on the default render endpoint, takes its mix format and uses the *same* format for capture init so no resampling is needed in the pump path
  4. Pump thread (Pro Audio MMCSS priority): block on capture event, drain `IAudioCaptureClient.GetBuffer` packets, copy into `IAudioRenderClient.GetBuffer` slots; capture release happens unconditionally to keep latency low under transient render stalls
- Verified working: tester's machine in 8-channel 96 kHz 32-bit shared-mode rendering. AudioBridge logged "running" within seconds of launch; OBS Window Capture's Capture Audio (BETA) immediately registered audio levels from `PulseNet-Player.exe`.
- Trade-off accepted by user: streamer hears doubled audio locally (WebView2 direct + our re-emit). Streamer manages this via OBS per-source audio monitoring controls; viewers receive the clean re-emit only.
- Streamer Info panel left as-is — Capture Audio (BETA) instruction remains correct now that we actually emit on `PulseNet-Player.exe`.

### 2026-04-27 17:30 — Decision: pivot to Window Capture (solution 1.5) for OBS
- After the Browser Source approach hit progressively harder sync issues (live mode pause not propagating without YT.Player attach, position sync needing a polling channel, audio doubling between re-emit and direct path), realised the simpler answer: just remove `WS_EX_TOOLWINDOW` from the overlay so OBS lists it, and change F9 to *park off-screen* instead of `Visibility.Collapsed` so DWM keeps rendering it for WGC capture.
- Implementation in `src/UI/OverlayWindow.xaml.cs`: clear `WS_EX_TOOLWINDOW` in `OnSourceInitialized` (was previously *adding* it); `HideOverlay` now persists current Left/Top then sets `Left = -(FrameDisplayWidth + 100)` instead of `Visibility = Collapsed`. The hidden owner WPF auto-creates from `ShowInTaskbar="False"` keeps it out of Alt+Tab and the taskbar regardless.
- Streamer Info panel reworked from the old Browser Source URL config into setup steps: Window Capture, capture method = Windows 10 (1903+), Capture Audio (BETA) on, Capture Cursor off, Client Area on, F9 hint.
- Click-blocker fixes that landed in this pass:
  - New `#click-blocker-br` (80×50 at right=0 bottom=83px) over the YouTube fullscreen icon in the bottom-right
  - `#click-blocker` height 60→62 to fully clear hover affordances
  - `.station-col` got `pointer-events: none` — column boxes were swallowing clicks in the leftmost/rightmost ~37/39px of the video rect because they overlap the video horizontally; buttons inside still set `pointer-events: auto` so they remain clickable
- Browser Source server (`BrowserSourceServer`, `NowPlayingState`, `Renderer/obs/`) remains in the tree but is dead code — flagged for removal in a follow-up cleanup commit once Window Capture is proven over a few real streaming sessions.

### 2026-04-27 15:06 — Plan + Started: OBS Browser Source feature
- Tester reported PulseNet Player invisible to OBS Window/Game Capture (only Display Capture works).
- Root cause confirmed in `src/UI/OverlayWindow.xaml.cs:171-175` — explicit `WS_EX_TOOLWINDOW` set in `OnSourceInitialized` "to hide from Alt+Tab". Combined with XAML `ShowInTaskbar="False"` (which already auto-applies the same flag) and the layered/transparent style, OBS's window enumerator filters it out. Banner has the same pattern.
- Decision: don't strip `WS_EX_TOOLWINDOW` (would break Alt+Tab hide UX). Instead, expose `/banner` and `/player` over an embedded localhost HTTP server so streamers paste a URL into OBS Browser Source. Sidesteps the window-capture problem entirely; also gives transparent compositing over game capture for free (Browser Source supports alpha).
- Architecture:
  - `Services/NowPlayingState.cs` — singleton title+station holder, `Changed` event. Replaces direct overlay→banner event chain.
  - `Services/BrowserSourceServer.cs` — `IHostedService` using `TcpListener` (chosen over `HttpListener` to avoid URL ACL elevation), bound to `127.0.0.1:<port>`. Routes: `/`, `/banner`, `/player`, `/events` (SSE), `/assets/*`. Rebinds when port changes.
  - `Renderer/obs/{banner,player}.{html,css,js}` — streamer-tailored variants, no chrome/settings/drag, transparent background. Banner consumes `/events` via `EventSource`. Player embeds the configured channel's `live_stream` iframe behind `frame_base.png` overlay.
  - `Renderer/index.html` + `style.css` + `player.js` — new "Streamer Options" sub-panel mirrors the Miniplayer Settings pattern (`#streamer-settings-panel`, Back button). Contents: port number input, banner URL + Copy, player URL + Copy, status indicator.
- Defaults: port 17328 (configurable), server on by default, bind 127.0.0.1 only.
- Known risk: YouTube `live_stream` embed may behave differently when loaded from `127.0.0.1` referrer vs `pulsenet.local`. Will start with plain HTTP and adapt if a real channel rejects.
- Out of scope this pass: TLS, on/off toggle, network exposure, current-track mirroring (player just shows the configured live channel; banner mirrors title via SSE).

### 2026-04-22 22:00 — Completed: Outer frame edge-clipping fix
- Session 6 (v0.3.1) had stretched the frame PNG (1252×670) at offset (-25, -12) so its inner cutout matched the widened video, with `overflow: hidden` on a 1202×646 `#app` clipping the overhang. The overhang turned out to contain visible bezel detail — corners/bolts/edges were being chopped on all 4 sides.
- Fix (option 1 — code-only, user chose this to preserve build integrity):
  - `src/Renderer/style.css`: `#app` 1202×646 → 1252×670. `#frame-base`/`#frame-glow` offsets `(-25,-12)` → `(0,0)`. All other absolute-positioned elements shifted `+25x, +12y`: `#video-wrap`, `#station-preview` (193,88)→(218,100); `.station-col` top 80→92; `#stations-left` (37,129)→(62,141); `#stations-right` left 966→991; `#pulsenet-home-btn` (118,80)→(143,92); `#about-btn` (1049,521)→(1074,533); `#settings-btn` (507,581)→(532,593); both settings panels left 348→373, bottom 62→74.
  - `src/Constants.cs`: `FrameDisplayWidth/Height` 1202/646 → 1252/670 so WPF window + WebView viewport grow to match (feeds `App.xaml.cs` off-screen parking and `OverlayWindow.ApplyZoom`).
- Testing gotcha: `dotnet run --no-build` hit the Mutex because a stale v0.3.1 binary from Apr 18 was running from `bin/x64/Debug/net9.0-windows/PulseNet-Player.exe` (no `win-x64/` subpath) — that's why the splash was showing "v0.4.0 available". Killed PID and rebuilt with `dotnet build -a x64`; fresh bits land in `bin/x64/Debug/net9.0-windows/win-x64/`. Launched that exe directly; user confirmed fix looks good.

### 2026-04-18 — Completed: v0.3.1 video + frame refit
- Removed `transform: scale(1.055)` from YouTube iframe → video plays uncropped
- `#video-wrap` reshaped to 812×457 (true 16:9), top=88, centered at y=316.5 so station buttons don't move
- Frame scaled to 1252×670, offset (-25, -12) — cutout aligns with new video rect (user confirmed "PERFECT")
- `#click-blocker` height 50px → 60px to fully cover YouTube end-card controls
- Bumped csproj Version 0.3.0 → 0.3.1 (Version / FileVersion / AssemblyVersion)
- Pushed `4768eb3` to master, tagged `v0.3.1`, workflow run id `24588476912` queued
- README install section rewritten (MSI + standalone exe); roadmap trimmed (auto-update / CI / WiX installer ticked off)
- DEVLOG.md got Session 6 entry; TODO.md updated to reflect completed distribution items

### 2026-04-17 — Completed: Rebrand Radio to Player
- Directive from PulseNet owner: never use the word "Radio" in any circumstance. Player = "PulseNet Player", service = "PulseNet Broadcasting", full corp = "Pulse Broadcasting Network", ticker = PLSN.
- Renamed binary `PulseNet-Broadcaster.exe` → `PulseNet-Player.exe` (csproj AssemblyName, workflow copy+upload, installer target, UpdateChecker asset lookup, SelfUpdateService paths)
- Renamed GitHub repo `Diftic/pulsenet-radio` → `Diftic/PulseNet-Player` (via `gh repo rename`); local git remote updated; UpdateChecker API URL + UA updated
- Scrubbed "Pulsenet Radio" → "PulseNet Player" across Constants, WPF titles, error strings, renderer title/comments, installer product/shortcut/dir/regkey/description, license copyright, README/DEVLOG/TODO titles and prose
- Scrubbed "Cargo Deck Radio" → "The Cargo Deck" in RP lore
- Renamed `RadioPlan.md` → `PulseNetPlan.md` and `src/Assets/radio_background.png` → `src/Assets/pulsenet_background.png`; scrubbed content references
- `%APPDATA%\pulsenet-radio\` folder intentionally preserved (exempted per user decision to avoid migrating beta testers' saved settings)
- Build green, pushed as commit `0a0ab81`; GitHub Pages will redeploy the sales page

### 2026-04-14 17:00 — Completed: Session 2 UI polish
- Drag rebuilt: JS-initiated `startDrag` → C# hook handles MOUSEMOVE/LBUTTONUP. Whole frame draggable.
- Zoom resizes WPF window + WebView.ZoomFactor in same call — no blink, correct hit areas at all zoom levels
- Zoom slider replaced with -10%/input/+10% buttons
- Settings panel doubled in size (font, padding, button heights)
- Window position persists between open/close cycles
- Scroll wheel fix: only captured when cursor is inside overlay window rect
- Frame width trimmed iteratively: 1258 → 1238 → 1218 → 1202px (28px each side)
- Video iframe scaled via transform: scale(1.055) to fill 16:9 pillarbox bars
- Station videoId support added; top-left station wired to test video b-YcZMSKqeo
- Real channel wired: UCIMaIJsfJEMi5yJIe5nAb0g (all 18 station playlists → UUIMaIJsfJEMi5yJIe5nAb0g)
- icon.ico replaced with Pulsenet branding (16/32/48/256px from PulseNetIcon 1024x1024.png)
- main_logo.png replaced with resized Pulsenet logo for idle state and splash
- DEVLOG.md and TODO.md updated

### 2026-04-14 15:00 — Completed: Button layout locked
- Pixel-tuned station buttons to final spec: 38×38px, scale(1.23), gap 17px, top 96px
- Center-anchored (justify-content: center) — button 5 of 9 is the prime anchor
- Left column x=45, right column x=987 (both inset from frame edges toward center)
- WebView2 disk cache disabled (--disk-cache-size=0) — no more manual cache wipe needed

### 2026-04-14 14:00 — Completed: Modules 0-2 sci-fi frame overlay rebuild
- Rebuilt renderer from scratch: frame_base.png overlay, YouTube iframe at video rect, 18 station buttons
- Window changed from fullscreen to fixed 1258×646 (frame canvas at 50% scale)
- All station buttons wired to @Mr_Xul test channel (UCDemStdcwUHbqhD2ePbKH6A) with placeholder icons
- Build: green — 0 errors, 0 warnings

### 2026-04-14 13:00 — Decision: stay WPF, discard Python rewrite
- PulseNetPlan.md (formerly RadioPlan.md) proposed PyQt6 rewrite, no technical justification found
- WPF prototype already has working WebView2, hotkeys, transparency, tray, settings
- All visual layers live in HTML/CSS/JS renderer to avoid WPF airspace problem
