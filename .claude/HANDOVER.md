# Handover — wingman-player (player + YouTube skill)

## Current state (2026-10-30)

- **One repo for the player and the YouTube skill**, as the user asked. Branch `feat/youtube-skill` (from `master` 8efb3eb),
  **committed as f8910ec (2026-10-30), not pushed, not merged.**
  - `skill/youtube_video_player/`: the skill, as installed in `custom_skills`. Moved from the scratch repo
    `D:\PycharmProjects\Wingman-YouTube-Skill`, created this session. Its leftovers (empty `.git`, the replaced
    `release.yml` and handover) still need deleting by the user, since the safety hook blocked `rm -rf`. The older copy in
    `D:\PycharmProjects\wingman-skills\youtube_video_player` is untouched and now stale.
  - `skill/tests/` (50 pytest, green), `skill/docs/` (skill DEVLOG/TODO/specs), `skill/README.md`, `skill/build_zip.py`.
  - `release-notes/v1.0.0.md`: the release body. The skill reads its first 600 chars and Wingman may speak them.
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

- Merge `feat/youtube-skill` (f8910ec) to `master` and push. Then tag `v1.0.0` to publish the first combined release.
- Whether to rename the repo later (GitHub redirects the API, but that hasn't been checked with the player's HttpClient).
- No LICENSE file.
- Users on the old 0.1.0 Discord zip must install 1.0.0 once by hand (0.1.0 has no updater).
- Whether to delete `youtube_video_player/` from the wingman-skills repo.
- A release that changes only the player still makes skills offer a "new" version with identical code (accepted cost of one shared version).

## Next step

Merge f8910ec to `master` and push (the user’s call). Then: tag `v1.0.0`, watch the Actions run,
install the skill from the release into `custom_skills` and test it live.

## Evidence

- Action log: `~/.claude/logs/<project>/2026-10-30.jsonl`.
- Tests: `D:\PycharmProjects\wingman-ai\.venv\Scripts\python.exe -m pytest -q -p no:cacheprovider skill/tests`.
- Skill history: `skill/docs/DEVLOG.md` (Sessions 1–18).
