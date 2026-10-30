"""Self-update: reading the latest release, verifying the zip, swapping files (no network)."""

from __future__ import annotations

import asyncio
import hashlib
import io
import json
import os
import re
import sys
import zipfile
from pathlib import Path

import pytest

SKILL_DIR = Path(__file__).resolve().parent.parent / "youtube_video_player"
sys.path.insert(0, str(SKILL_DIR))

import youtube_skill_updater as up  # noqa: E402

BASE = f"https://github.com/{up.REPO}/releases/download/"


def make_zip(version: str, extra: dict[str, bytes] | None = None,
             main_version: str | None = None) -> bytes:
    files = {
        "youtube_video_player/__init__.py": f'__version__ = "{version}"\n'.encode(),
        "youtube_video_player/main.py": f'class X:\n    VERSION = "{main_version or version}"\n'.encode(),
        "youtube_video_player/default_config.yaml": b"name: x\n",
        **(extra or {}),
    }
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return buf.getvalue()


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def release_json(tag: str, zip_url: str | None = None) -> bytes:
    version = tag.lstrip("v")
    name = f"youtube_video_player-v{version}.zip"
    return json.dumps({
        "tag_name": tag,
        "body": "Fixes things.\x07",
        "assets": [
            {"name": name, "browser_download_url": zip_url or f"{BASE}{tag}/{name}"},
            {"name": name + ".sha256", "browser_download_url": f"{BASE}{tag}/{name}.sha256"},
        ],
    }).encode()


class FakeGitHub:
    def __init__(self, responses: dict[str, tuple[int, bytes]]) -> None:
        self.responses = responses
        self.requests: list[tuple[str, int]] = []

    async def __call__(self, url: str, max_bytes: int) -> tuple[int, bytes]:
        self.requests.append((url, max_bytes))
        return self.responses.get(url, (404, b""))


def run(coro):
    return asyncio.run(coro)


def installed_release(tmp_path: Path, raw: bytes) -> tuple[up.Release, FakeGitHub]:
    release = up.parse_release(release_json("v1.1.0"))
    gh = FakeGitHub({release.zip_url: (200, raw), release.sha256_url: (200, sha(raw).encode())})
    return release, gh


def test_versions_compare_as_numbers():
    assert up.parse_version("v1.10.0") > up.parse_version("1.9.9")
    with pytest.raises(up.UpdateError):
        up.parse_version("1.0")


def test_newer_release_is_reported_with_its_assets():
    gh = FakeGitHub({up.LATEST_RELEASE_URL: (200, release_json("v1.1.0"))})
    release = run(up.check(gh, "1.0.0"))
    assert release is not None
    assert release.version == "1.1.0"
    assert release.zip_url == f"{BASE}v1.1.0/youtube_video_player-v1.1.0.zip"
    assert release.notes == "Fixes things."  # control characters stripped
    assert gh.requests == [(up.LATEST_RELEASE_URL, up.MAX_JSON_BYTES)]  # no code downloaded


@pytest.mark.parametrize("current", ["1.1.0", "1.2.0"])
def test_same_or_older_release_is_not_an_update(current):
    gh = FakeGitHub({up.LATEST_RELEASE_URL: (200, release_json("v1.1.0"))})
    assert run(up.check(gh, current)) is None


def test_repo_without_releases_is_not_an_error():
    assert run(up.check(FakeGitHub({}), "1.0.0")) is None


def test_github_error_raises():
    gh = FakeGitHub({up.LATEST_RELEASE_URL: (403, b"rate limited")})
    with pytest.raises(up.UpdateError, match="403"):
        run(up.check(gh, "1.0.0"))


@pytest.mark.parametrize("url", [
    "https://evil.example/youtube_video_player-v1.1.0.zip",
    f"{BASE}v1.1.0/../../../../evil/repo/releases/download/v1.1.0/youtube_video_player-v1.1.0.zip",
    f"{BASE}v1.1.0/youtube_video_player-v1.1.0.zip?x=1",
])
def test_asset_url_must_be_exactly_this_repos_release_asset(url):
    with pytest.raises(up.UpdateError, match="unexpected download URL"):
        up.parse_release(release_json("v1.1.0", zip_url=url))


@pytest.mark.parametrize("url,ok", [
    ("https://github.com/x", True),
    ("https://release-assets.githubusercontent.com/x", True),
    ("http://github.com/x", False),
    ("https://github.com.evil.example/x", False),
    ("https://user:pw@github.com/x", False),
    ("https://github.com:8443/x", False),
])
def test_only_https_github_hosts_are_allowed(url, ok):
    assert up.allowed_url(url) is ok


def test_unpack_returns_skill_files_and_skips_pycache():
    raw = make_zip("1.1.0", {"youtube_video_player/__pycache__/main.cpython-312.pyc": b"x"})
    files = up.unpack(raw, sha(raw) + "  x.zip\n", "1.1.0")
    assert sorted(files) == ["__init__.py", "default_config.yaml", "main.py"]


def test_unpack_refuses_wrong_checksum():
    raw = make_zip("1.1.0")
    with pytest.raises(up.UpdateError, match="checksum"):
        up.unpack(raw, "0" * 64, "1.1.0")


@pytest.mark.parametrize("name", [
    "../evil.py", "youtube_video_player/../evil.py", "other_skill/main.py",
    "youtube_video_player/C:x.py", "youtube_video_player/main.py:stream",
    "youtube_video_player/aux.py", "youtube_video_player/nul",
    "youtube_video_player/main.py.", "youtube_video_player/MAIN~1.PY",
    "youtube_video_player/evil.exe", "youtube_video_player/evil.pyd",
])
def test_unpack_refuses_unsafe_entries(name):
    raw = make_zip("1.1.0", {name: b"x"})
    with pytest.raises(up.UpdateError):
        up.unpack(raw, sha(raw), "1.1.0")


def test_unpack_refuses_names_that_collide_on_windows():
    raw = make_zip("1.1.0", {"youtube_video_player/Main.py": b"x"})
    with pytest.raises(up.UpdateError, match="duplicate"):
        up.unpack(raw, sha(raw), "1.1.0")


@pytest.mark.parametrize("init,main", [("1.0.9", "1.1.0"), ("1.1.0", "1.0.9")])
def test_unpack_refuses_zip_whose_version_stamps_differ_from_the_tag(init, main):
    raw = make_zip(init, main_version=main)
    with pytest.raises(up.UpdateError, match="version stamps"):
        up.unpack(raw, sha(raw), "1.1.0")


def test_unpack_refuses_oversized_zip(monkeypatch):
    raw = make_zip("1.1.0")
    monkeypatch.setattr(up, "MAX_ZIP_BYTES", len(raw) - 1)
    with pytest.raises(up.UpdateError, match="too large"):
        up.unpack(raw, sha(raw), "1.1.0")


def test_download_and_install_replaces_files_and_leaves_no_staging(tmp_path):
    (tmp_path / "main.py").write_bytes(b"# old main\n")
    (tmp_path / "logo.png").write_bytes(b"keep")
    raw = make_zip("1.1.0", {"youtube_video_player/sub/data.json": b"{}"})
    release, gh = installed_release(tmp_path, raw)
    run(up.download_and_install(gh, release, tmp_path))
    assert b"VERSION" in (tmp_path / "main.py").read_bytes()
    assert (tmp_path / "sub" / "data.json").read_bytes() == b"{}"
    assert (tmp_path / "logo.png").read_bytes() == b"keep"
    assert not (tmp_path / up.STAGING_DIR).exists()
    assert [m for _, m in gh.requests] == [up.MAX_SHA_BYTES, up.MAX_ZIP_BYTES]


def test_failed_move_rolls_everything_back(tmp_path, monkeypatch):
    old = {"__init__.py": b'__version__ = "1.0.0"\n', "main.py": b"# old main\n",
           "default_config.yaml": b"old\n"}
    for name, content in old.items():
        (tmp_path / name).write_bytes(content)
    files = up.unpack(raw := make_zip("1.1.0", {"youtube_video_player/new.py": b"x"}), sha(raw), "1.1.0")
    real_replace = os.replace
    calls = {"n": 0}

    def flaky_replace(src, dst):
        calls["n"] += 1
        if calls["n"] == 5:  # fail midway through the moves
            raise PermissionError("locked by antivirus")
        return real_replace(src, dst)

    monkeypatch.setattr(up.os, "replace", flaky_replace)
    with pytest.raises(up.UpdateError, match="old version kept"):
        up.install(files, tmp_path)
    for name, content in old.items():
        assert (tmp_path / name).read_bytes() == content
    assert not (tmp_path / "new.py").exists()


def test_init_is_moved_last():
    files = {"__init__.py": b"", "main.py": b"", "a.py": b""}
    order = sorted(files, key=lambda rel: rel == up.COMMIT_FILE)
    assert order[-1] == "__init__.py"


def test_failed_download_changes_nothing(tmp_path):
    (tmp_path / "main.py").write_bytes(b"# old main\n")
    release = up.parse_release(release_json("v1.1.0"))
    gh = FakeGitHub({release.sha256_url: (200, b"0" * 64), release.zip_url: (200, make_zip("1.1.0"))})
    with pytest.raises(up.UpdateError):
        run(up.download_and_install(gh, release, tmp_path))
    assert (tmp_path / "main.py").read_bytes() == b"# old main\n"


def test_shipped_versions_agree():
    """__init__, main.VERSION and the installer manifest must name the same version."""
    init = re.search(r'__version__ = "([^"]+)"', (SKILL_DIR / "__init__.py").read_text())[1]
    main = re.search(r'VERSION = "([^"]+)"', (SKILL_DIR / "main.py").read_text(encoding="utf-8"))[1]
    manifest = json.loads((SKILL_DIR / "skill_installer_config.json").read_text())["version"]
    assert init == main == manifest


def test_every_shipped_file_passes_the_updaters_name_rules():
    for path in SKILL_DIR.rglob("*"):
        if path.is_file() and "__pycache__" not in path.parts:
            rel = path.relative_to(SKILL_DIR).as_posix()
            assert up._member_path(f"youtube_video_player/{rel}") == rel
