# YouTube Video Player — TODO

---

## v0.1.0 scaffold (2026-05-02)

### Done

- [x] **Skill manifest** — `default_config.yaml` with name, module, tags, discovery_keywords, prompt with confidence rubric, examples, 7 custom_properties, hint
- [x] **Installer manifest** — `skill_installer_config.json` listing the 6 distribution files + preserve list
- [x] **`__init__.py`** — version stamp matching SC_Accountant convention
- [x] **`confidence.py`** — composite confidence math (B+C: heuristic + LLM)
- [x] **`youtube_search.py`** — Data API v3 search.list wrapper, in-memory query cache, per-day quota counter persisted to `quota.json`
- [x] **`player_client.py`** — async HTTP client for `127.0.0.1:17330/player/*` with auto-launch via `subprocess.Popen`
- [x] **`main.py`** — `WingmanYoutube(Skill)` with 9 tools and lifecycle hooks
- [x] **`README.md`** — user-facing setup including YouTube API key walkthrough and config knobs
- [x] **Standalone validation** — all 5 .py files `py_compile` clean; YAML/JSON parse clean; confidence math sanity-tested across four cases

---

## Rename + bundling (2026-05-02) — SUPERSEDED 2026-05-04

The bundling-the-player approach was **reversed in Session 14 (2026-05-04)** when the user finalised the new distribution model: player ships independently via GitHub releases, skill ships separately as a small zip and points users at the GitHub link if the player isn't installed. See DEVLOG Session 14 for the rationale.

### Historical (kept for traceability)

- [x] **Skill rename** `wingman_youtube` → `youtube_video_player`. Folder, module path in `default_config.yaml`, class `YoutubeVideoPlayer`, skill_installer_config skill_name + display_name, install.bat DEST, quota counter filename, all docs.
- [x] ~~**Bundle the media player.** `release_version/player/` contains `Wingman-Player.exe` (~174MB self-contained) + `Renderer/` (2MB).~~ **Reverted 2026-05-04** — `release_version/player/` deleted. Player no longer bundled.
- [x] ~~**Bundled-path resolution in `main.py`.**~~ **Replaced 2026-05-04** — `release_version/main.py` now probes `%LocalAppData%\Programs\Wingman Player\Wingman-Player.exe` (canonical MSI install location) when `player_exe_path` is empty.
- [x] ~~**`skill_installer_config.json`** — added `dirs: ["player"]`.~~ **Reverted 2026-05-04** — `dirs` cleared.
- [x] **Live test env updated.** `custom_skills\youtube_video_player\` populated. Re-installed 2026-05-04 with the new no-player-bundle layout via the rebuilt zip.

---

## v0.1.0 release prep (2026-05-04)

### Done

- [x] **`release_version/main.py`** — path resolution rewritten for canonical MSI install location. Failure messages embed the GitHub installer URL so Wingman shows a clickable link.
- [x] **`release_version/install.bat`** — rewritten with `goto`-based control flow + silenced robocopy (`/NJH /NJS /NFL /NDL /NC /NS /NP >nul 2>&1`). Fixes the v0.0 prototype bug where the success branch's escaped parens dropped execution into the ELSE branch and printed both "Install complete!" and "ERROR: Install failed with code 1".
- [x] **`release_version/RELEASE_NOTES_v0.1.0.txt`** — two-step install flow (player MSI → skill zip).
- [x] **`release_version/TESTER_README.md`** — same two-step framing; added "player missing" recovery section.
- [x] **`release_version/default_config.yaml`** — `player_exe_path` reframed as override-only; description and HTML hint updated.
- [x] **`release_version/skill_installer_config.json`** — `dirs` cleared.
- [x] **`release_version/__init__.py`** — drops "bundled" wording.
- [x] **`youtube_video_player_v0.1.0.zip`** packaged (62 KB, wraps contents in a `youtube_video_player/` folder so unzip is clean).

### Open

- [x] **Sync source files at the parent dir** (`skills/youtube_video_player/main.py`, `default_config.yaml`, `__init__.py`, `skill_installer_config.json`) with the `release_version/` versions. Completed 2026-05-13 in Session 15. All four files now match release_version; `py_compile` clean on `main.py`.
- [ ] **Live test result.** User is set up to install the player MSI from GitHub (if not already), restart Wingman, activate the skill, and exercise search/play/transport. Outcomes feed into the post-test polish list.

---

## Open follow-ups

### Bugs (from initial test)

- [x] **API key prompt has no recovery path** — fixed across three sessions:
  - **Session 2:** `secret_changed` override (picks up key entered via Wingman Settings → Secrets) + `set_youtube_api_key` voice tool (clears `prompted_secrets`, re-broadcasts dialog).
  - **Session 5:** `_ensure_api_key_or_prompt()` helper. When the user dismisses the initial prompt and then makes any "play X" request, the dialog **automatically re-broadcasts** without needing a magic phrase.
  - **Session 6:** **In-UI text input** in the skill settings (`youtube_api_key_input` STRING property). User opens the skill's settings panel, pastes the key into the visible field, saves. `update_config` override moves it into SecretKeeper. No dialog round-trip. Wingman's `CustomPropertyType` doesn't include BUTTON/ACTION, so a literal "click to open dialog" button isn't possible — a paste field achieves the same goal more directly.
  - **Session 7:** **Force re-activation after UI key entry.** Real-run revealed: saving the secret + setting `is_validated = False` doesn't actually trigger re-validation for `auto_activate: true` skills that failed initial activation. Tools never appear in the LLM's toolbox until something explicitly calls `ensure_activated()`. Fix: `update_config` now calls `await self.ensure_activated()` itself after a successful save, walking the skill from failed state to ready in one shot.

### Playback / UX

- [x] **Video loaded but not playing.** Two-step fix:
  - **Session 9:** `/player/load` (and `/play`, `/next`, `/previous`, `/seek`) call `EnsureOverlayVisibleAsync` on the bridge, bringing the player on-screen. Necessary but not sufficient — Chromium's autoplay decision happens before the window-show race resolves.
  - **Session 11:** WebView2 launched with `--autoplay-policy=no-user-gesture-required` so `loadVideoById` can actually start playback without requiring a real user click. The voice request *is* the intent; we don't need Chromium gesture-tracking gating it.
  - **Contingency if Session 11 still doesn't autoplay:** launch `Wingman-Player.exe` from the skill's `prepare()` instead of waiting for the first `/player/load`. By the time the user actually says "play X", WebView2 is fully initialised and YT.Player is ready, so there's no cold-start race for the autoplay-policy gate to lose. Cost: player runs in idle for the whole Wingman session (~170MB RSS, low CPU). Implementation: skill's `prepare()` calls `await self._player.ensure_running()` after `_init_search`, ignoring the result — fire-and-forget warm-up. Tracked here pending fireside test result.
- [x] **Splash screen removed.** Session 9. Used to gate the user during the startup update check; now the result lands on `PlayerCommandBridge.LatestUpdate`, surfaces through `/player/state`, and the skill's `get_player_status` mentions update availability so Wingman tells the user (who then uses the existing in-overlay 'Check for updates' button to install).
- [x] **New icon adopted.** Session 10. Multi-res `icon.ico` (16/24/32/48/64/128/256) baked into the player exe + tray. Skill `logo.png` next to `default_config.yaml` so Wingman auto-loads it. Source PNG checked in at `wingman-player/src/Assets/source_icon.png` for reproducible regen.

### Visual rebrand of the bundled player

- [x] **Settings panel colors still PulseNet-cyan** — fixed in Session 4 (full crimson recolor, see DEVLOG). 56 token replacements across `style.css` (38), `banner.css` (10), `SplashWindow.xaml` (7), `TrayIcon.cs` fallback (1). Player rebuilt + rebundled. Commit `011d8e7` in wingman-player.

### Fireside test plan

- [ ] **Strong-confidence path** — "Play *Never Gonna Give You Up* by *Rick Astley*". Expect: skill auto-launches player if needed, hits API, top result is the official Rick Astley video, composite ≥ 0.98, auto-plays. Wingman speaks "Playing *X* by *Y*."
- [ ] **List path** — "Find me something by *The Beatles*". Expect: artist-only, llm_confidence ~0.5, falls below threshold, returns 5-row list. Wingman speaks the list.
- [ ] **Pick from list** — "Play 2". Expect: `play_search_result(2)` loads the second item.
- [ ] **Transport** — "Pause", "Resume", "Stop", "Skip 30 seconds", "Go to 1 minute 30", "What's playing?".
- [ ] **Auto-launch** — kill `Wingman-Player.exe` between commands; next play command should re-launch.
- [ ] **Quota guard** — verify counter increments per real search and skips on cache hit. (Manual: speak the same query twice within a minute, should only consume 1 search.)

### Things likely to surface during the test

- **LLM populating `song`/`artist` fields.** The prompt asks the LLM to extract these explicitly. Different Wingman LLM configs may handle this with varying reliability. If the LLM systematically leaves them blank, the heuristic boost is 0 and the skill stays in list mode even on clear requests. Mitigation: tighten the prompt rubric with more concrete examples, or have the skill regex-parse "X by Y" out of the query as a fallback.
- **Auto-launch latency.** First launch from cold disk is ~2-4s for the WPF/WebView2 stack to come up plus another ~1s for the YT.Player to be ready. The skill waits up to 15s. If real cold-start times exceed that on the user's machine, bump `max_wait_seconds` in `player_client.py:ensure_running`.
- **`get_player_status` returning JSON.** Currently the tool returns a JSON string. Most Wingman LLM configs translate that to natural speech, but some may speak the JSON literally. If so, return prose instead (`f"Playing {title}, {position} of {duration}, currently {state}"`).
- **Mutex collision under auto-launch race.** If the user manually starts the player at the same instant the skill auto-launches, the second instance hits the Mutex and exits cleanly (verified by player code), but the skill's `ensure_running` poll might pick up the first instance's port unpredictably. Low-likelihood, but worth watching the log if behaviour is weird.

### Post-test polish (filled in based on what the test reveals)

- [ ] *(reserved)*
- [ ] *(reserved)*

---

## Install discovery + minimize / show + idle auto-hide (2026-05-14 - Session 18)

### Done

- [x] **Warning badge for missing player** (skill side). `prepare()` calls `self.printr.print(msg, color=LogType.WARNING)` in two cases:
  - `player_exe_path` override is set but file missing
  - No override AND canonical `%LocalAppData%\Programs\Wingman Player\Wingman-Player.exe` missing
  Badge text tells the user how to ask for the download via voice.
- [x] **`download_player_installer` voice tool** (skill side). Opens `https://github.com/Diftic/Wingman-Player/releases/latest/download/Wingman-Player-Setup.msi` via `webbrowser.open(url, new=2)` so the OS default browser handles it (NOT Wingman's embedded WebView).
- [x] **NO auto-download on activation.** Initial iteration auto-opened the browser from `prepare()`; backed out because that pattern is malware-shaped. Consent must be explicit per-request.
- [x] **Honest wording about restart requirement.** Badge text and `download_player_installer` success message both say "restart Wingman or re-activate the skill so I can pick up the new install" instead of pretending auto-pickup works.
- [x] **`hide_player` / `show_player` voice tools** (skill side). Map to `POST /player/hide` and `POST /player/show` on the new player endpoints. Voice triggers: "minimize player", "hide player", "tuck it away" / "show player", "bring it back", "unhide".
- [x] **`PlayerClient.hide()` and `.show()`** in `player_client.py` (both copies).
- [x] **Player v0.5.1 released** via CI tag push. Adds 15s idle auto-hide on stop and natural end-of-video, `/player/hide` and `/player/show` HTTP endpoints, renderer state -> C# message channel for natural-end detection and resume-brings-back. Full detail in player DEVLOG Session 23.
- [x] **Wingman restarted with new skill files.** Live install at `custom_skills/youtube_video_player/` synced from `release_version/`.

### Live fireside verification (Session 18) - the next session's checklist

After the user installs Wingman Player v0.5.1 from the GitHub release URL:

- [ ] **Badge appears.** Activate the skill before installing the player. Confirm yellow warning badge shows in the Wingman chat panel.
- [ ] **`download_player_installer` works.** Say "download the Wingman Player installer". Confirm: (a) default OS browser opens, (b) .msi download starts, (c) badge response is the speak-back about restart-required.
- [ ] **Restart-pickup works.** Run the .msi, restart Wingman, activate skill. Confirm no badge. Say "play X by Y" - confirm player auto-launches and plays.
- [ ] **Pause stays visible.** Confirm `/player/pause` does NOT start the idle timer.
- [ ] **Stop triggers 15s timer.** Say "stop". Confirm player stays visible. Wait 15s. Confirm player auto-hides.
- [ ] **Resume cancels timer.** Repeat stop, but within 15s say "resume" or "play". Confirm player stays visible and timer cancels.
- [ ] **Natural end triggers timer.** Play a short video, let it finish. Confirm player stays visible 15s, then auto-hides.
- [ ] **Resume brings back from idle hide.** After auto-hide kicks in, say "play X". Confirm player shows again.
- [ ] **`hide_player` / `show_player` voice flow.** Say "minimize player" mid-playback. Confirm immediate hide AND audio keeps playing. Say "show player". Confirm immediate show.
- [ ] **Click play inside player UI.** If user manually clicks the YT play button after auto-hide, the renderer state -> PLAYING message should re-show the overlay. Verify this works (it's the "resume from inside the player" path).

### Open (deferred)

- [ ] **Configurable idle timeout.** 15s is hard-coded as `IdleHideSeconds` in `OverlayWindow.xaml.cs`. Expose via `WingmanPlayerSettings.IdleHideSeconds` and the settings panel if users want it tunable. Tracked also in player TODO.
- [ ] **Lazy re-resolution of player path.** Skill currently requires Wingman restart after the user installs the player. Could be fixed by re-probing the canonical install path at tool-call time. User explicitly preferred wording-fix over implementing this in Session 18 - revisit if it becomes a friction point.
- [ ] **Rotate YouTube API key.** Mid-session, the inadvertent dump of secrets.yaml content into the tool output exposed the live key in the conversation transcript. User-side action.

---

## Post-review cleanups (2026-05-13 — Session 16)

### Done

- [x] **`YouTubeSearch.daily_limit` public property** — replaces `_daily_search_limit` access from main.py.
- [x] **`_reprompt_api_key_dialog()` helper** — dedupes the prompted-guard-clear + retrieve_secret sequence shared by `_ensure_api_key_or_prompt` and `set_youtube_api_key`.
- [x] **`tests/test_confidence.py`** — 12 tests, all green. Locks down the four Session 1 hand-validated cases plus 8 edge cases.
- [x] **Bug fix in `confidence.py`** — guard against empty `channelTitle` / `title` to prevent spurious +0.15 boost via `"" in any_string == True`. Caught by `test_missing_snippet_does_not_crash` on first test run.

---

## Frame variants — queued for implementation (2026-05-13 — Session 17)

Design + plan complete; execution paused at the user's request.

### Done

- [x] **Design spec** — `specs/2026-05-13-frame-variants-design.md`. Three variants (Classic / Slim / Borderless), CSS-rendered for slim/borderless, settings-panel toggle, live swap, corner-bracket drag in borderless.
- [x] **Implementation plan** — `specs/2026-05-13-frame-variants-plan.md`. 10 tasks, ~50 bite-sized steps, 7 logical commits, fireside test plan, all work in the `wingman-player` repo.

### Open

- [ ] **Implement frame variants per the plan.** All work is in `D:\PycharmProjects\wingman-player\`, NOT in this skill. Pick up the plan and walk through Task 1 → Task 10 (Tasks 8 + 10 are optional / gated on user). User's preferred execution mode (subagent-driven vs inline) not yet picked.
- [ ] **Optional: cleanup `.superpowers/brainstorm/1954-1778700579/`** in the wingman-ai repo root. These are ephemeral HTML mockups from the brainstorming session. Safe to delete once the design spec is the source of truth.

---

## Considered, deferred

- **Live-stream detection tool.** Add a `find_livestream(channel_query)` tool that calls `search.list?eventType=live&channelId=...`. Useful for "is X streaming right now". Same 100-unit cost; would want a short-cache (60s) since live state changes minute-to-minute. Defer until there's a demand signal.
- **Push state events from player → skill.** Today the skill polls `/player/state` for `get_player_status` calls — single round-trip per question, ~50-100ms. If the fireside test reveals polling latency annoys the user (or we want auto-narration of track changes), add an optional webhook URL the skill registers with the player; player POSTs state-change deltas.
- **Multi-skill source dispatch.** `/player/load` already takes `{source}`. When a Spotify or local-file skill exists, they each implement their own resolver (`spotify_search.py`, etc.) and call `POST /player/load` with their own source string. Player would need to grow a source-aware router (currently only knows `"youtube"`). Defer until there's a second skill to dispatch.
- **Caching search results across sessions.** Currently in-memory only; lost on Wingman restart. Could persist to disk for ~1 hour TTL. Saves quota across short Wingman sessions. Low priority — quota is generous and the use case (asking the same thing twice across sessions) is rare.
- **Quota nag tool.** A `get_youtube_quota` tool returning "X of Y searches remaining today" so the user can voice-check before a heavy session. Trivial to add, but only useful if the user actually approaches the cap.

---

## Distribution + maintenance

### Planned: v1.0.0 + MSI installer + new GitHub repo

> **Superseded 2026-10-30:** the skill now lives in the Wingman Player repo (`skill/`), ships in the same GitHub releases as the player with one shared version, and updates itself from them on the user's yes. See the root README and `.claude/HANDOVER.md`.

Once the fireside test confirms the playback path is solid:

- [ ] **Bump version 0.1.0 → 1.0.0** across `__init__.py`, `skill_installer_config.json`, `default_config.yaml` (if it carries a version), `RELEASE_NOTES_v0.1.0.txt` → `RELEASE_NOTES_v1.0.0.txt`. v1 marks the first publicly distributable release.
- [ ] **Create a new GitHub repo** for the skill (separate from `Diftic/Wingman-Player`). Likely `Diftic/Wingman-YouTube-Skill` or similar — confirm name with user. The skill's source tree (everything in `D:\PycharmProjects\wingman-ai\skills\youtube_video_player\` minus `__pycache__` and the player binary) lives there. The bundled `player/Wingman-Player.exe` stays a build-time pull from `Diftic/Wingman-Player` releases, not source-checked-in.
- [ ] **Build a WiX-based MSI installer** that installs to `%AppData%\Roaming\ShipBit\WingmanAI\custom_skills\youtube_video_player\` by default. Pattern mirror of the player's existing `installer.wxs` with `Scope="perUser"`. Need new UpgradeCode (don't reuse the player's). Installer ships:
  - The 7 distribution files + `logo.png`
  - The `player/` subfolder (Wingman-Player.exe + Renderer/) — pulled from a tagged Diftic/Wingman-Player release at MSI build time, so the skill can pin a specific player version per release
  - Start-menu shortcut? Probably not — the skill is invoked through Wingman, not directly.
- [ ] **CI workflow** to publish the MSI on tag push. Mirrors the player's `.github/workflows/build.yml` pattern. Pulls the player .exe + Renderer/ from a known Wingman-Player release tag at build time.
- [ ] **README rewrite for v1** — drop the "drop the folder into custom_skills" install path in favour of "download MSI, double-click". Keep the API-key setup section.

Open architectural questions to resolve at v1.0.0 cut time:
- How tightly does the skill version pin the player version? Either (a) the skill bundles a specific player at MSI build time and only that version is supported, or (b) skill bundles a minimum version and the player's own auto-updater can advance it. (b) is more flexible but couples the skill more loosely to the player release cycle.
- Pre/post-install actions (e.g. detect if Wingman is running and offer to relaunch)? Probably not for v1 — keep simple.

---

### Dev → live → release flow

| Stage | Location | When updated |
|-------|----------|-------------|
| Dev source | `D:\PycharmProjects\wingman-ai\skills\youtube_video_player\` | Every code change |
| Live test | `%AppData%\Roaming\ShipBit\WingmanAI\custom_skills\youtube_video_player\` | Via `install.bat` from `release_version/`, or robocopy mirror during iteration |
| Release artifact | `D:\...\skills\youtube_video_player\release_version\` | When cutting a versioned release for testers / Discord |
| Distributable zip | `release_version\youtube_video_player_v<x.y.z>.zip` | Rebuilt per release; wraps `release_version/` contents in a top-level `youtube_video_player/` folder |

### Done

- [x] **`release_version/` directory** with 11 files: 8 distribution files (`__init__.py`, `confidence.py`, `default_config.yaml`, `logo.png`, `main.py`, `player_client.py`, `skill_installer_config.json`, `youtube_search.py`) + `install.bat` + `RELEASE_NOTES_v0.1.0.txt` + `TESTER_README.md`. Matches SC_Accountant convention.
- [x] **`install.bat`** robocopies into `%AppData%\ShipBit\WingmanAI\custom_skills\youtube_video_player\`, excluding metadata files.
- [x] **Zip packaging** — `youtube_video_player_v0.1.0.zip` produced 2026-05-04 (62 KB) via PowerShell `Compress-Archive` from a staging folder so the zip wraps a `youtube_video_player/` parent folder. No script automation yet — done by hand each release.

### Open

- [ ] **Automate zip packaging** — a small Python or PowerShell script alongside SC_Accountant's `update_release.py` that stages `release_version/` (excluding any prior `*.zip`) into `youtube_video_player/` and runs Compress-Archive. Five lines of code; just hasn't been done yet.
- [ ] **MCP server distribution path** — when Wingman's MCP-based skill distribution lands, repackage as an MCP server. Defer until that path exists.
- [ ] **Versioning convention** — `__init__.py` tracks `__version__`. Bump on user-visible changes (new tool, new property, behaviour change). Match `skill_installer_config.json` `version` field. Cut a fresh `RELEASE_NOTES_v<x.y.z>.txt` per version. Current: v0.1.0.

---

## Known sharp edges to revisit

- **`videoEmbeddable=true` filter** — search.list filters out videos the uploader has disabled embedding for. Reduces the "100/101/150 embed disabled" error class but isn't perfect; YouTube occasionally serves videos that the IFrame still rejects at load time. If we see consistent load failures despite the filter, fall back to `videos.list` validation (1 quota unit) on the chosen videoId before calling the player.
- **Quota counter race** — counter is read-modify-write to a single JSON file. If two Wingman instances are running simultaneously they could miscount. Single-user scenario, not currently a concern, but worth a flock if it ever becomes one.
- **API key in `secrets.yaml`** — Wingman's SecretKeeper writes secrets to a local YAML file. Plain text on disk. Standard for the Wingman ecosystem; not a regression. User should treat their AppData folder as sensitive.
- **Search result staleness** — cache TTL is 60min by default; if YouTube updates a video's title in that window, the cached list is stale. Acceptable for music search; would matter more for breaking news / livestream contexts which we're not optimising for.
