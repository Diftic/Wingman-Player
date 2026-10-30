"""Build a release zip and its .sha256 (run by .github/workflows/build.yml).

Usage:
  python skill/build_zip.py Wingman-Player-vX.Y.Z.zip --player <publish dir>
      install.bat + the skill folder with the player in youtube_video_player/player/.
      What users download, and what the skill's updater installs.
  python skill/build_zip.py youtube_video_player-vX.Y.Z.zip
      The skill folder only, for skills on 1.0.0, whose updater looks for this name.

Every file goes through the updater's own name rules, so a release that
installed skills would refuse fails here instead of in users' hands.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SKILL_DIR = HERE / "youtube_video_player"
INSTALL_BAT = HERE / "install.bat"
sys.path.insert(0, str(SKILL_DIR))

import youtube_skill_updater as updater  # noqa: E402


def _skill_entries() -> list[tuple[Path, str]]:
    entries = []
    for path in sorted(SKILL_DIR.rglob("*")):
        if not path.is_file() or "__pycache__" in path.parts:
            continue
        entries.append((path, f"{updater.SKILL_FOLDER}/{path.relative_to(SKILL_DIR).as_posix()}"))
    return entries


def _player_entries(player_dir: Path) -> list[tuple[Path, str]]:
    """The published player, minus debug symbols and API docs (.pdb, .xml)."""
    prefix = f"{updater.SKILL_FOLDER}/{updater.PLAYER_DIR}"
    return [
        (path, f"{prefix}/{path.relative_to(player_dir).as_posix()}")
        for path in sorted(player_dir.rglob("*"))
        if path.is_file() and path.suffix.lower() in updater.PLAYER_SUFFIXES
    ]


def main(out_name: str, player_dir: Path | None) -> None:
    entries = _skill_entries()
    if player_dir is not None:
        entries += _player_entries(player_dir) + [(INSTALL_BAT, INSTALL_BAT.name)]
    out = Path(out_name)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
        for path, name in entries:
            if updater._member_path(name) is None and name not in updater.INSTALLER_FILES:
                continue
            archive.write(path, name)
    if player_dir is not None:
        # Run the updater's own checks on the result: fail the build, not the users' updates.
        version = updater._INIT_VERSION.search((SKILL_DIR / "__init__.py").read_text())[1]
        raw = out.read_bytes()
        updater.unpack(raw, hashlib.sha256(raw).hexdigest(), version)
    digest = hashlib.sha256(out.read_bytes()).hexdigest()
    Path(f"{out}.sha256").write_text(f"{digest}  {out.name}\n", encoding="utf-8")
    print(f"{out} {out.stat().st_size} bytes sha256 {digest}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("--player", type=Path, help="dotnet publish output folder")
    args = parser.parse_args()
    main(args.out, args.player)
