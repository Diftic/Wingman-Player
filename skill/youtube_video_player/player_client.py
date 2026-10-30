"""Async HTTP client for the Wingman Player command surface.

The player is a separate Windows app exposing a tiny localhost HTTP API:

  GET  /player/state    → {state, videoId, title, currentTime, duration}
  POST /player/load     body: {source, videoId?, playlistId?, ...}
  POST /player/play     resume
  POST /player/pause
  POST /player/stop
  POST /player/next
  POST /player/previous
  POST /player/seek     body: {seconds}

This client wraps those routes, plus an `ensure_running` that auto-launches
Wingman-Player.exe via subprocess when configured.
"""

from __future__ import annotations

import asyncio
import logging
import os
import subprocess
from pathlib import Path
from typing import Any

import aiohttp

logger = logging.getLogger(__name__)


class PlayerClient:
    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 17330,
        exe_path: str = "",
        auto_launch: bool = True,
    ) -> None:
        self._host = host
        self._port = port
        self._exe_path = exe_path
        self._auto_launch = auto_launch
        self._base_url = f"http://{host}:{port}"

    @property
    def base_url(self) -> str:
        return self._base_url

    async def is_running(self) -> bool:
        """Cheap probe — single GET /player/state with a short timeout."""
        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(
                    f"{self._base_url}/player/state",
                    timeout=aiohttp.ClientTimeout(total=1),
                ) as resp:
                    return resp.status == 200
        except (aiohttp.ClientError, asyncio.TimeoutError):
            return False

    async def ensure_running(self, max_wait_seconds: int = 15) -> bool:
        """Returns True if the player is already up, or if auto-launch
        succeeds and the HTTP port comes up within max_wait_seconds.
        Returns False if we can't or won't auto-launch."""
        if await self.is_running():
            return True

        if not self._auto_launch:
            logger.info("Player not running and auto_launch disabled")
            return False

        if not self._exe_path:
            logger.warning("Player not running and player_exe_path is empty")
            return False

        if not Path(self._exe_path).exists():
            logger.warning("player_exe_path does not exist: %s", self._exe_path)
            return False

        try:
            # DETACHED_PROCESS so the player survives Wingman exiting.
            flags = subprocess.DETACHED_PROCESS if os.name == "nt" else 0
            subprocess.Popen(
                [self._exe_path],
                creationflags=flags,
                close_fds=True,
            )
            logger.info("Launched Wingman-Player.exe from %s", self._exe_path)
        except OSError as ex:
            logger.error("Failed to launch player: %s", ex)
            return False

        # Poll the HTTP port until it answers, up to max_wait_seconds.
        for _ in range(max_wait_seconds * 2):
            await asyncio.sleep(0.5)
            if await self.is_running():
                return True

        logger.warning(
            "Launched player but HTTP port %d did not come up within %ds",
            self._port, max_wait_seconds,
        )
        return False

    # ------------------------------------------------------------------
    # Internal request helpers
    # ------------------------------------------------------------------

    async def _post(self, path: str, body: dict[str, Any] | None = None) -> bool:
        """Returns True on 2xx, False otherwise. Body is JSON-serialised."""
        try:
            async with aiohttp.ClientSession() as session:
                kwargs: dict[str, Any] = {"timeout": aiohttp.ClientTimeout(total=5)}
                if body is not None:
                    kwargs["json"] = body
                async with session.post(f"{self._base_url}{path}", **kwargs) as resp:
                    if 200 <= resp.status < 300:
                        return True
                    text = await resp.text()
                    logger.warning(
                        "Player %s returned %d: %s", path, resp.status, text,
                    )
                    return False
        except (aiohttp.ClientError, asyncio.TimeoutError) as ex:
            logger.error("Player POST %s failed: %s", path, ex)
            return False

    async def _get_json(self, path: str) -> dict[str, Any] | None:
        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(
                    f"{self._base_url}{path}",
                    timeout=aiohttp.ClientTimeout(total=5),
                ) as resp:
                    if resp.status >= 400:
                        return None
                    return await resp.json()
        except (aiohttp.ClientError, asyncio.TimeoutError) as ex:
            logger.error("Player GET %s failed: %s", path, ex)
            return None

    # ------------------------------------------------------------------
    # Public command surface (mirrors the player's HTTP routes)
    # ------------------------------------------------------------------

    async def load_youtube(self, video_id: str | None = None, playlist_id: str | None = None) -> bool:
        body: dict[str, Any] = {"source": "youtube"}
        if video_id:
            body["videoId"] = video_id
        if playlist_id:
            body["playlistId"] = playlist_id
        return await self._post("/player/load", body)

    async def play(self) -> bool:
        return await self._post("/player/play")

    async def pause(self) -> bool:
        return await self._post("/player/pause")

    async def stop(self) -> bool:
        return await self._post("/player/stop")

    async def next(self) -> bool:
        return await self._post("/player/next")

    async def previous(self) -> bool:
        return await self._post("/player/previous")

    async def seek(self, seconds: float) -> bool:
        return await self._post("/player/seek", {"seconds": float(seconds)})

    async def hide(self) -> bool:
        return await self._post("/player/hide")

    async def show(self) -> bool:
        return await self._post("/player/show")

    async def state(self) -> dict[str, Any] | None:
        return await self._get_json("/player/state")
