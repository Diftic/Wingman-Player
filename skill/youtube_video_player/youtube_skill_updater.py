"""Self-update from the skill's GitHub releases.

The skill lives in the Wingman Player repo, https://github.com/Diftic/Wingman-Player,
and ships in the same releases as the player (one shared version).
Each release (built by .github/workflows/build.yml on a version tag) carries,
next to the player exe and MSI, youtube_video_player-v<x.y.z>.zip and its .sha256.

check() reads the latest release and says whether it is newer than the running
version. It never downloads code. download_and_install() runs only when the
user asks for it (the install_skill_update voice tool). It verifies the
checksum, accepts only plain file names inside the skill folder, checks the
zip's own version stamps match the release tag, then swaps the files in place,
rolling back if any move fails. Wingman loads the new code on its next restart.

Pure logic, no Wingman imports. fetch() is injectable so tests run without a
network; the caller's fetch must refuse any URL (including redirect targets)
for which allowed_url() is False.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Awaitable, Callable
from urllib.parse import urlsplit

REPO = "Diftic/Wingman-Player"
LATEST_RELEASE_URL = f"https://api.github.com/repos/{REPO}/releases/latest"
RELEASES_PAGE_URL = f"https://github.com/{REPO}/releases/latest"
SKILL_FOLDER = "youtube_video_player"
STAGING_DIR = ".update_staging"
REQUIRED_FILES = ("__init__.py", "main.py", "default_config.yaml")
# __init__.py carries the version, so it is moved last: a half-done install
# that somehow survives rollback still reports the old version.
COMMIT_FILE = "__init__.py"
ALLOWED_HOSTS = frozenset({
    "api.github.com",
    "github.com",
    "objects.githubusercontent.com",
    "release-assets.githubusercontent.com",
    "github-releases.githubusercontent.com",
})
ALLOWED_SUFFIXES = frozenset({".py", ".yaml", ".json", ".png", ".md", ".txt"})
MAX_JSON_BYTES = 256 * 1024
MAX_SHA_BYTES = 1024
MAX_ZIP_BYTES = 20 * 1024 * 1024
MAX_UNPACKED_BYTES = 50 * 1024 * 1024
MAX_NOTES_CHARS = 600

_VERSION = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)$")
_INIT_VERSION = re.compile(r"""^__version__\s*=\s*["']([^"']+)["']""", re.MULTILINE)
_MAIN_VERSION = re.compile(r"""^\s*VERSION\s*=\s*["']([^"']+)["']""", re.MULTILINE)
_NAME_PART = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.-]*$")
_RESERVED = frozenset(
    {"con", "prn", "aux", "nul"}
    | {f"com{i}" for i in range(1, 10)}
    | {f"lpt{i}" for i in range(1, 10)}
)
_CONTROL = re.compile(r"[\x00-\x08\x0b-\x1f\x7f]")

# fetch(url, max_bytes) -> (HTTP status, body). Network errors raise.
Fetch = Callable[[str, int], Awaitable[tuple[int, bytes]]]


class UpdateError(ValueError):
    """A release, download or zip that must not be used."""


@dataclass(frozen=True)
class Release:
    version: str
    zip_url: str
    sha256_url: str
    notes: str


def allowed_url(url: str) -> bool:
    """HTTPS to GitHub or its release-asset hosts, no credentials or port."""
    parts = urlsplit(url)
    return (parts.scheme == "https" and parts.hostname in ALLOWED_HOSTS
            and not parts.username and not parts.password and parts.port is None)


def parse_version(text: str) -> tuple[int, int, int]:
    match = _VERSION.match(str(text).strip())
    if not match:
        raise UpdateError(f"not a version: {str(text)[:40]!r}")
    return int(match[1]), int(match[2]), int(match[3])


def _asset_url(assets: list, name: str, version: str) -> str:
    expected = f"https://github.com/{REPO}/releases/download/v{version}/{name}"
    for asset in assets:
        if isinstance(asset, dict) and asset.get("name") == name:
            if asset.get("browser_download_url") == expected:
                return expected
            raise UpdateError(f"asset {name} has an unexpected download URL")
    raise UpdateError(f"release has no asset {name}")


def parse_release(raw: bytes) -> Release:
    """Read GitHub's releases/latest JSON into a Release. Raises UpdateError."""
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as error:
        raise UpdateError(f"release info is not JSON: {error}") from None
    if not isinstance(data, dict):
        raise UpdateError("release info is not a JSON object")
    major, minor, patch = parse_version(data.get("tag_name", ""))
    version = f"{major}.{minor}.{patch}"
    assets = data.get("assets")
    if not isinstance(assets, list):
        raise UpdateError("release has no assets")
    zip_name = f"{SKILL_FOLDER}-v{version}.zip"
    notes = data.get("body") if isinstance(data.get("body"), str) else ""
    return Release(
        version=version,
        zip_url=_asset_url(assets, zip_name, version),
        sha256_url=_asset_url(assets, zip_name + ".sha256", version),
        notes=_CONTROL.sub("", notes).strip()[:MAX_NOTES_CHARS],
    )


async def check(fetch: Fetch, current_version: str) -> Release | None:
    """The latest release if it is newer than current_version, else None."""
    status, body = await fetch(LATEST_RELEASE_URL, MAX_JSON_BYTES)
    if status == 404:  # repo has no published release yet
        return None
    if status != 200:
        raise UpdateError(f"GitHub answered {status}")
    release = parse_release(body)
    if parse_version(release.version) <= parse_version(current_version):
        return None
    return release


def _member_path(name: str) -> str | None:
    """Path inside the skill folder for a zip entry, None to skip, or raise.

    An allowlist, not a blacklist: Windows aliases names with trailing dots,
    reserved device names and short names, so only plain names get through.
    """
    prefix = SKILL_FOLDER + "/"
    if not name.startswith(prefix):
        raise UpdateError(f"zip entry outside {SKILL_FOLDER}/: {name[:80]!r}")
    rel = name[len(prefix):]
    if not rel or rel.endswith("/"):
        return None
    parts = rel.split("/")
    if "__pycache__" in parts or parts[0].casefold() == STAGING_DIR:
        return None
    for part in parts:
        if (not _NAME_PART.match(part) or part.endswith(".") or "~" in part
                or part.split(".")[0].lower() in _RESERVED):
            raise UpdateError(f"unsafe zip entry: {name[:80]!r}")
    if Path(parts[-1]).suffix.lower() not in ALLOWED_SUFFIXES:
        raise UpdateError(f"unexpected file type in zip: {name[:80]!r}")
    return rel


def _stamp(files: dict[str, bytes], name: str, pattern: re.Pattern) -> str | None:
    match = pattern.search(files[name].decode("utf-8", "replace"))
    return match[1] if match else None


def unpack(zip_bytes: bytes, sha256_text: str, version: str) -> dict[str, bytes]:
    """Verify a release zip and return {relative path: content}. Raises UpdateError."""
    if len(zip_bytes) > MAX_ZIP_BYTES:
        raise UpdateError("release zip is too large")
    expected = sha256_text.strip().split()[0].lower() if sha256_text.strip() else ""
    if hashlib.sha256(zip_bytes).hexdigest() != expected:
        raise UpdateError("release zip does not match its checksum")
    files: dict[str, bytes] = {}
    seen: set[str] = set()
    try:
        with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
            infos = archive.infolist()
            if sum(info.file_size for info in infos) > MAX_UNPACKED_BYTES:
                raise UpdateError("release zip unpacks too large")
            for info in infos:
                rel = _member_path(info.filename)
                if rel is None:
                    continue
                if rel.casefold() in seen:
                    raise UpdateError(f"duplicate zip entry: {rel[:80]!r}")
                seen.add(rel.casefold())
                files[rel] = archive.read(info)
    except (zipfile.BadZipFile, NotImplementedError, RuntimeError, EOFError) as error:
        raise UpdateError(f"release zip is broken: {error}") from None
    missing = [f for f in REQUIRED_FILES if f not in files]
    if missing:
        raise UpdateError(f"release zip lacks {', '.join(missing)}")
    if (_stamp(files, "__init__.py", _INIT_VERSION) != version
            or _stamp(files, "main.py", _MAIN_VERSION) != version):
        raise UpdateError("release zip's version stamps do not match the release")
    return files


def _restore(moved: list[tuple[Path, Path | None]]) -> None:
    """Undo moves newest first: put backups back, delete files that were new."""
    for dest, backup in reversed(moved):
        if backup is not None:
            os.replace(backup, dest)
        else:
            dest.unlink(missing_ok=True)


def install(files: dict[str, bytes], skill_dir: Path) -> None:
    """Swap the skill's files for `files`, all or nothing. Raises UpdateError.

    Every new file is written to a staging folder inside the skill folder
    first (Wingman never sees it as a skill, and os.replace stays on one
    volume). Each existing file is moved to a backup before its replacement
    goes in; any failure puts the backups back. Files the new release no
    longer ships are left alone; they are never imported.
    """
    root = skill_dir.resolve()
    stage = root / STAGING_DIR
    backups = stage / "_backup"
    order = sorted(files, key=lambda rel: rel == COMMIT_FILE)
    for rel in order:
        dest = (root / rel).resolve()
        if not dest.is_relative_to(root) or dest.is_relative_to(stage):
            raise UpdateError(f"refusing to write outside the skill folder: {rel[:80]!r}")
        if dest.is_dir():
            raise UpdateError(f"{rel} is a folder in the installed skill")
    if stage.exists():
        shutil.rmtree(stage)
    moved: list[tuple[Path, Path | None]] = []
    try:
        for rel in order:
            target = stage / "new" / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(files[rel])
        for rel in order:
            dest = root / rel
            backup = None
            if dest.exists():
                backup = backups / rel
                backup.parent.mkdir(parents=True, exist_ok=True)
                os.replace(dest, backup)
            moved.append((dest, backup))
            dest.parent.mkdir(parents=True, exist_ok=True)
            os.replace(stage / "new" / rel, dest)
    except OSError as error:
        try:
            _restore(moved)
        except OSError as restore_error:
            raise UpdateError(
                f"update failed ({error}) and the old files could not all be put "
                f"back ({restore_error}); reinstall from {RELEASES_PAGE_URL}"
            ) from None
        raise UpdateError(f"update failed, old version kept ({error})") from None
    shutil.rmtree(stage, ignore_errors=True)


async def download_and_install(fetch: Fetch, release: Release, skill_dir: Path) -> None:
    """Download, verify and install a release. Raises UpdateError.

    unpack and install are synchronous; callers on an event loop should run
    this through verify_and_install in a worker thread (see main.py).
    """
    zip_body, sha_text = await download(fetch, release)
    verify_and_install(zip_body, sha_text, release.version, skill_dir)


async def download(fetch: Fetch, release: Release) -> tuple[bytes, str]:
    """Fetch the zip and its checksum. Raises UpdateError."""
    status, sha_body = await fetch(release.sha256_url, MAX_SHA_BYTES)
    if status != 200:
        raise UpdateError(f"checksum download failed ({status})")
    status, zip_body = await fetch(release.zip_url, MAX_ZIP_BYTES)
    if status != 200:
        raise UpdateError(f"zip download failed ({status})")
    return zip_body, sha_body.decode("utf-8", "replace")


def verify_and_install(zip_body: bytes, sha_text: str, version: str, skill_dir: Path) -> None:
    install(unpack(zip_body, sha_text, version), skill_dir)
