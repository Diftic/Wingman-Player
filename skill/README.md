# YouTube Video Player

Voice-driven YouTube video playback for Wingman AI. The skill drives [Wingman Player](https://github.com/Diftic/Wingman-Player), a small overlay app installed separately. Once installed, the skill tells you when a new version is out and updates itself when you ask.

Needs a Wingman AI with the v3 skill API (tested on 3.2.12). Older versions of this skill (0.1.0 from Discord) are skipped there as "legacy".

## What it does

| You say... | What happens |
|------------|--------------|
| "Play *Bohemian Rhapsody* by *Queen*" | Searches YouTube, auto-plays the top result if confidence is high |
| "Play that one Queen song you know" | Returns top 5 results — Wingman speaks them, you pick |
| "Find me something by *The Beatles*" | Top-5 list to choose from |
| "Play 2" / "the third one" | Plays the chosen item from the most recent list |
| "Pause" / "Resume" / "Stop" | Transport — straight pass-through to the player |
| "Skip to the next song" / "Previous track" | Playlist navigation (when in a playlist) |
| "Skip ahead 30 seconds" / "Go to 1:30" | Seek (relative or absolute) |
| "Hide the player" / "Show the player" | Tucks the overlay away or brings it back |
| "What's playing?" | Returns current title, state, position, duration |
| "Set the YouTube API key" | Picks up the key, or tells you where to enter it |
| "Download the Wingman Player installer" | Opens the player installer in your browser |
| "Is there an update for the YouTube skill?" | Checks GitHub for a newer version |
| "Update the YouTube skill" | Downloads, verifies and installs it; restart Wingman to load it |

Volume is intentionally **not** controlled by the skill — per-app volume is the OS / Wingman audio router's job.

---

## Requirements

- **Wingman AI** installed
- **Wingman Player** — the overlay that shows the video ([installer](https://github.com/Diftic/Wingman-Player/releases/latest); the skill can open it for you)
- **Free YouTube Data API v3 key** — see setup below

---

## Setup

### 1. Get a YouTube Data API key

1. Go to **https://console.cloud.google.com**
2. Create a project (or pick an existing one) — name it "Wingman" or similar
3. Sidebar → **APIs & Services → Library** → search **"YouTube Data API v3"** → click **Enable**
4. Sidebar → **APIs & Services → Credentials**
5. **+ Create Credentials → API key** → copy the key
6. **Recommended:** click **Restrict Key** → **API restrictions** → select only "YouTube Data API v3". This limits damage if the key ever leaks.

The key never leaves your machine — Wingman's SecretKeeper stores it locally. Default daily quota is 10,000 units (≈ 95 searches/day with the skill's caching).

### 2. Install the skill

1. Open the [latest release](https://github.com/Diftic/Wingman-Player/releases/latest) and download `youtube_video_player-vX.Y.Z.zip`.
2. Extract it into `%AppData%\ShipBit\WingmanAI\custom_skills\`, so you end up with `custom_skills\youtube_video_player\main.py`.
3. Restart Wingman and switch the skill on for your Wingman.

If Wingman Player isn't installed yet, the skill shows a yellow notice. Say *"download the Wingman Player installer"* and run the installer (per-user, no admin rights), then restart Wingman.

### 3. First launch

When Wingman activates the skill it'll prompt for the YouTube API key. Paste it once and Wingman stores it.

If you dismiss the prompt, open Wingman Settings → Secrets and enter the key as `youtube_api_key`. The skill picks it up without a restart. (Wingman shows the prompt once per session, and skills can't reopen it.)

You're ready: say **"play Never Gonna Give You Up by Rick Astley"** and the player launches and starts playing.

---

## Updates

At start, the skill reads the latest release info from this repo (one small request, nothing downloaded). If a newer version exists, Wingman shows a notice. Say *"update the YouTube skill"* and it downloads the release, checks its SHA-256 checksum, and replaces its own files. Restart Wingman to load the new version. Your settings and API key are kept, since Wingman stores them, not the skill folder.

Turn the check off with the **Check for Updates** setting. You can always update by hand: extract a newer release zip over the old folder.

---

## Configuration

All knobs live in `default_config.yaml` under `custom_properties`:

| Property | Default | What it does |
|----------|---------|--------------|
| `daily_search_limit` | 100 | Max searches per UTC day. Each costs 100 units of the 10,000-unit free quota, so 95–100 is the practical ceiling. |
| `cache_ttl_minutes` | 60 | In-memory cache TTL for identical normalized queries. Repeats inside the window are free. |
| `auto_play_threshold` | 0.98 | Composite confidence (LLM + heuristic) above which the top result auto-plays. Lower = more eager auto-play. |
| `player_host` | 127.0.0.1 | Wingman Player command server host |
| `player_port` | 17330 | Wingman Player command server port |
| `player_exe_path` | (empty) | **Optional override.** Leave empty to use the installed player. Set a path only to run a different build (e.g. a local dev build). |
| `auto_launch_player` | true | If true, the skill launches Wingman Player automatically when needed |
| `check_for_updates` | true | Read the latest release info at start and show a notice when a newer version exists. Installing always needs your request. |

---

## How confidence works

Auto-play uses a composite score from two sources:

**LLM confidence (0–1)** — the Wingman LLM's read on how clearly the user named what they want:

| User said | LLM confidence |
|-----------|----------------|
| "Play *X* by *Y*" (specific song + artist) | ~0.85–0.95 |
| "Play *X*" (specific song, no artist) | ~0.70–0.85 |
| "Find something by *Y*" (artist only) | ~0.40–0.60 |
| "Play that *Y* song you know" (vague reference) | ~0.30–0.50 |
| "Find me something good" (no specifics) | ~0.10–0.20 |

**Heuristic boost (0–0.30)** — applied by the skill based on the top YouTube result:

- Channel matches the artist (substring or 60%+ similarity) → +0.15
- Title contains the song name (substring or 50%+ similarity) → +0.10
- Weaker matches → smaller boosts

**Composite = LLM + boost**, capped at 1.0. Auto-plays if `composite ≥ auto_play_threshold` (default 0.98). Otherwise the skill returns a numbered top-5 list and the LLM speaks it.

The default threshold is intentionally strict — the skill prefers showing options to picking wrong. Lower it (e.g. 0.85) if you want more eager auto-play.

---

## Files

This skill lives in the `skill/` folder of the Wingman Player repo.

- `skill/youtube_video_player/` — the skill folder, exactly as installed
  - `main.py` — `YoutubeVideoPlayer(Skill)` with all tools and lifecycle
  - `youtube_search.py` — Data API v3 client + per-day quota tracker + per-query cache
  - `player_client.py` — HTTP client for Wingman Player (with auto-launch)
  - `confidence.py` — composite confidence math (heuristic + LLM)
  - `youtube_skill_updater.py` — update check and verified install from this repo's releases
  - `default_config.yaml` — manifest, prompt, custom properties
- `skill/tests/` — pytest, no network needed
- `skill/docs/` — the skill's dev log, TODO and design notes
- `release-notes/` (repo root) — one file per version; the text becomes the release body Wingman can read out

## Releasing (maintainer)

The player and the skill share one version and one GitHub release, because both update from `releases/latest`.

1. Pick the next version. Bump it in `skill/youtube_video_player/__init__.py`, `main.py` (`VERSION`) and `skill_installer_config.json` (a test checks they agree). The player takes its version from the tag.
2. Write `release-notes/vX.Y.Z.md` (short; Wingman may read it aloud).
3. Commit, then `git tag vX.Y.Z` and `git push origin master vX.Y.Z`.

The Build & Release workflow builds the player exe and MSI, runs the skill tests, checks the skill version matches the tag, builds `youtube_video_player-vX.Y.Z.zip` and its `.sha256`, and publishes all four files in one release. Installed players and skills both see it at their next start.

---

## Troubleshooting

**"Wingman Player isn't running and I couldn't auto-launch it"**
Wingman Player isn't installed, or isn't where the skill looks (`%LocalAppData%\Programs\Wingman Player\Wingman-Player.exe`). Say *"download the Wingman Player installer"*, or set `player_exe_path` to your own build.

**"YouTube search failed (403)"**
Almost always one of:
- Wrong / revoked API key — check `console.cloud.google.com` → Credentials
- Daily 10,000-unit quota exceeded (Google's, not the skill's local cap) — wait until midnight UTC
- API key restriction blocking the request — check the **Restrict Key** settings, ensure only "YouTube Data API v3" is allowed

**"Daily YouTube search limit reached"**
Local cap (`daily_search_limit`) hit. Resets at midnight UTC. Raise it in the skill config if you want more headroom.

**"YouTube API key isn't configured"**
The prompt was dismissed, or the key was deleted from Wingman's secrets. Recovery: open Wingman Settings → Secrets and enter the key as `youtube_api_key`; no restart needed.

**"The update failed and nothing was changed"**
GitHub couldn't be reached, or the download didn't match its checksum. Try again later, or install the release zip by hand.

**Top result auto-plays the wrong video**
Raise `auto_play_threshold` to 0.99+ so you get the list more often. Searching for a generic title (e.g. "Yesterday") will sometimes pick a cover instead of the original.
