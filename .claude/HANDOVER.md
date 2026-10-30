# Handover — wingman-player (player + YouTube skill)

## Current state (2026-10-30)

- **Summary (latest, 18:40): v1.0.0 is RELEASED** (https://github.com/Diftic/Wingman-Player/releases/tag/v1.0.0).
  - The user tested live ("works flawlessly") and asked to update git and make the installer.
  - The v3 port is commit 46ca105, tagged `v1.0.0`. Build & Release run 41099838020 succeeded in 2m45s.
  - Assets: `Wingman-Player-Setup.msi` (63.6 MB), `Wingman-Player.exe`, `youtube_video_player-v1.0.0.zip` (65 KB) and its
    `.sha256`. `releases/latest` now reports v1.0.0.
  - The downloaded zip passes the skill's own `unpack` (checksum, names, version stamps; 9 files, api_version 3).
  - Installed v0.5.1 players will now offer 1.0.0. Earlier skill 0.1.0 users must install 1.0.0 by hand once.
  - CI annotation only: actions on Node 20 are deprecated (checkout@v4, setup-dotnet@v4, setup-python@v5,
    action-gh-release@v2); bump them some day.
- Earlier the same evening: master 9d5ef9a was pushed; the v3 port and missing-key fix were then installed in `custom_skills`.
  - Boot at 18:22 (log `wingman-core.2026-10-30_18-22-16.log`): the skill is eligible, and with no key it was
    `validated and prepared` (l.81) instead of removed, so the missing-key fix works.
  - The how-to-get-a-key text was posted (l.79) as a log line, invisible behind the secret dialog. Briefly a toast;
    then **the user decided (18:40): no toast**. The steps and link go in the skill's own page: the `hint` in
    `default_config.yaml` is now a numbered list with bold links that open in the user's browser. Bold is
    `<strong><a class="anchor" target="_blank">`, ShipBit's Spotify-skill pattern; plain `<a href>` had opened inside
    the Wingman frame. The links go straight to the YouTube Data API v3 library page and the Credentials page.
    `_GET_KEY_STEPS` and the toast are removed; `_MISSING_KEY_REPLY` points to the skill page and Settings → Secrets.
    Installed; 50 tests green. **Not yet seen in the app**: does the link open in the browser?
  - Next: the user checks the skill page, enters a key, then the live test.
- **One repo for the player and the YouTube skill**, as the user asked. Branch `feat/youtube-skill` (from `master` 8efb3eb),
  committed as f8910ec, **merged to `master` as a13f7b1 and pushed (2026-10-30)**. No tag yet, so nothing is released.
  - `skill/youtube_video_player/`: the skill, as installed in `custom_skills`. Moved from the scratch repo
    `D:\PycharmProjects\Wingman-YouTube-Skill`, created this session. Its leftovers (empty `.git`, the replaced
    `release.yml` and handover) still need deleting by the user, since the safety hook blocked `rm -rf`. The older copy in
    `D:\PycharmProjects\wingman-skills\youtube_video_player` is untouched and now stale.
  - `skill/tests/` (50 pytest, green), `skill/docs/` (skill DEVLOG/TODO/specs), `skill/README.md`, `skill/build_zip.py`.
  - `release-notes/v1.0.0.md`: the release body. The skill reads its first 600 chars and Wingman may speak them.
- **Installed in the live test env (2026-10-30)**, from master a13f7b1:
  - skill → `%APPDATA%\ShipBit\WingmanAI\custom_skills\youtube_video_player` (it's a custom skill, so custom_skills is its live location)
  - player → `%LOCALAPPDATA%\Programs\Wingman Player\` (where the MSI installs and where the skill looks), built with the CI
    publish flags and `-p:Version=1.0.0`. Smoke test: `/player/state` answers, and no false update is offered.
  - Wingman had not been restarted, so the skill isn't loaded yet. It also has to be enabled on a wingman in the UI.
- **Ported to the v3 skill API (2026-10-30, uncommitted).** Wingman 3.2.12 had skipped the skill as `legacy`
  (log `3_2_12\logs\wingman-core.2026-10-30_17-59-31.log` line 55). Guide: `wingman-ai\skills\MIGRATING-TO-V3.md`.
  - Changes in `main.py`: `api_version: 3`; `printr`/`LogType` → `self.log`; `threaded_execution` →
    `self.wingman.run_in_thread`; `retrieve_secret` → `self.wingman.secrets.retrieve`.
  - API-key flow: v3 skills can't write or re-prompt secrets, so the `youtube_api_key_input` property and the reprompt
    dialog are gone. Wingman prompts once per session; after that the skill points to Settings → Secrets, and a key
    saved there is picked up by `secret_changed` without a restart.
  - Also updated: skill README (key steps, "needs v3 skill API, tested on 3.2.12"), the v1.0.0 release notes, and
    DEVLOG Session 19.
  - Verified: 50 tests green; Core's `SkillCatalog._evaluate` gives `ok` on the installed copy; a stub build runs
    validate and lists all 15 tools.
  - Installed into custom_skills (it matches the repo). **Not yet seen loading in a running Wingman: needs a restart.**
- **First live boot after the port (2026-10-30 18:10, log `wingman-core.2026-10-30_18-10-23.log`):**
  `SkillCatalog: 27 eligible, 0 skipped`, so the v3 port loads. Enabling the skill on ORIN then failed:
  `failed during prepare/activate and was removed: ... Missing secret 'youtube_api_key'`. In v3 Core
  (`services/wingman_skill_manager.py` ~l.270) a validation error removes an auto-activated skill, so a key entered
  afterwards never reached it. The user also asked for instructions in the secret dialog (the screenshot shows only
  "Please enter your 'youtube_api_key' secret/API key").
  - Fix (uncommitted): `validate()` still asks for the key but keeps the missing-secret error out of `errors`, so the
    skill stays loaded and `secret_changed` wires the key in on submit. Tools answer `_MISSING_KEY_REPLY` until then.
  - When the key is missing, `validate()` also posts `_GET_KEY_STEPS` (how to get a key from Google Cloud, plain
    ASCII) once as a warning notice next to the dialog.
  - Core can't put text into the dialog (`PromptSecretCommand` has only `requester` + `secret_name`). The request
    for ShipBit is logged in `skill/docs/TODO.md`.
  - Verified with a stub: 3 validations with no key give no errors and 1 notice. 50 tests green. Copied to custom_skills.
- **Version bug found and fixed in 9d5ef9a:** `src/wingman_player.csproj` hard-coded `AssemblyVersion 0.5.0.0`, and
  `UpdateChecker` reads AssemblyVersion. `-p:Version` from CI did not override it, so every build thought it was 0.5.0.
  Installed v0.5.1 players therefore keep offering 0.5.1, and a v1.0.0 built that way would keep offering 1.0.0. Fix:
  only `<Version>1.0.0</Version>`, so AssemblyVersion/FileVersion follow it. Verified: the rebuilt 1.0.0 reports no update.
  Committed and pushed as 9d5ef9a (2026-10-30).
- **One shared version, one release.** The player's `UpdateChecker.cs` and the skill's updater both read
  `api.github.com/repos/Diftic/Wingman-Player/releases/latest`. Every release must therefore carry the exe, the MSI
  and the skill zip + `.sha256`. Prefixed skill tags would make a skill-only release "latest" and break installed
  players (v0.5.1 is out there). The first combined release is planned as **v1.0.0** (player jumps 0.5.1 → 1.0.0).
- `.github/workflows/build.yml` extended. On a `v*` tag it:
  - fails unless the tagged commit is on `origin/master`, skill `__version__` == tag, and `release-notes/<tag>.md` exists
  - runs the skill tests
  - builds the skill zip with `skill/build_zip.py`, which checks every name against the updater's own rules
  - publishes the exe, MSI, zip and sha256, with `body_path` from the release notes (generated notes are dropped).
  The rest of the workflow is unchanged. **Not yet run on GitHub.**
- **Skill self-update** (`skill/youtube_video_player/youtube_skill_updater.py`):
  - At start (setting `check_for_updates`, default on) it shows an INFO badge if a newer release exists. Read-only.
  - Tools: `check_for_skill_update`, plus `install_skill_update(version)`, which runs only on the user's explicit
    request and only for the version the user was told about.
  - Fetch: redirects are followed by hand, each hop must be HTTPS to a GitHub host (allowlist), and bodies are capped
    while reading (JSON 256 KB, zip 20 MB).
  - Install: asset URLs must equal `https://github.com/Diftic/Wingman-Player/releases/download/v<ver>/<name>`.
    The sha256 must match, file names are allowlisted (plain names, `.py/.yaml/.json/.png/.md/.txt`, no reserved
    names, `~`, trailing dots or case collisions), and both `__version__` and `main.VERSION` must equal the tag.
  - Files are staged in `<skill>/.update_staging`. Each existing file is backed up, `__init__.py` is moved last, and
    everything rolls back on failure. Unpack/install run in a worker thread under an `asyncio.Lock`.
- Security review (ecc:security-reviewer, 2026-10-30): no CRITICAL. H1–H3 and M1–M5 fixed, plus L1, L2, L5 (master
  check), L7 and L8. Not done: pinning actions by SHA (L5), and checking the zip name in the .sha256 file (L6, not
  exploitable).
- Verified locally: the zip that CI will build (66 KB) passes `unpack` and `install`; `main.py` imports against the
  wingman-ai checkout. **Not installed live, not tested in Wingman.**

## Decisions (2026-10-30, the user)

- Skills are shared as GitHub links in Discord; updates are pushed from the repo (memory `skills-distributed-via-github`).
- Updates: notify, install on yes. No silent auto-update (skill DEVLOG Session 18).
- Player and skill live in one repo. Repo = `Diftic/Wingman-Player`, because installed players' updaters point there.

## Open (owner: the user)

- Post the release link in Discord (the user's call).
- ShipBit request: help text in the secret dialog (`skill/docs/TODO.md`).
- Tag `v1.0.0` to publish the first combined release (best after one live test of the skill).
- `master` also carries `instructions.json` (5c59882 "update test", 2026-09-20, pushed from GitHub before this merge). It looks unrelated to the player; keep or remove.
- Whether to rename the repo later (GitHub redirects the API, but that hasn't been checked with the player's HttpClient).
- No LICENSE file.
- Users on the old 0.1.0 Discord zip must install 1.0.0 once by hand (0.1.0 has no updater).
- Whether to delete `youtube_video_player/` from the wingman-skills repo.
- A release that changes only the player still makes skills offer a "new" version with identical code (accepted cost of one shared version).

## Next step

Nothing pending in the code. The user posts the release link in Discord. Next work, when wanted: a LICENSE file,
bump the Node-20 actions, the ShipBit secret-dialog request, and deleting the stale wingman-skills copy.

## Evidence

- Action log: `~/.claude/logs/<project>/2026-10-30.jsonl`.
- Tests: `D:\PycharmProjects\wingman-ai\.venv\Scripts\python.exe -m pytest -q -p no:cacheprovider skill/tests`.
- Skill history: `skill/docs/DEVLOG.md` (Sessions 1–18).
