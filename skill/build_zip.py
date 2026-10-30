"""Build the skill's release zip and its .sha256 (run by .github/workflows/build.yml).

Usage: python skill/build_zip.py youtube_video_player-vX.Y.Z.zip

Every file goes through the updater's own name rules, so a release that
installed skills would refuse fails here instead of in users' hands.
"""

from __future__ import annotations

import hashlib
import sys
import zipfile
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent / "youtube_video_player"
sys.path.insert(0, str(SKILL_DIR))

import youtube_skill_updater as updater  # noqa: E402


def main(out_name: str) -> None:
    out = Path(out_name)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(SKILL_DIR.rglob("*")):
            if not path.is_file() or "__pycache__" in path.parts:
                continue
            name = f"{updater.SKILL_FOLDER}/{path.relative_to(SKILL_DIR).as_posix()}"
            if updater._member_path(name) is None:
                continue
            archive.write(path, name)
    digest = hashlib.sha256(out.read_bytes()).hexdigest()
    Path(f"{out}.sha256").write_text(f"{digest}  {out.name}\n", encoding="utf-8")
    print(f"{out} {out.stat().st_size} bytes sha256 {digest}")


if __name__ == "__main__":
    main(sys.argv[1])
