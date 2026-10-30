"""YouTube Data API v3 search wrapper with caching and per-day quota cap.

Uses search.list with eventType-free, type=video, videoEmbeddable=true so the
results we hand back to the player are guaranteed embeddable (avoiding the
101/150 IFrame errors you get when an uploader disabled embedding).

Each search.list call costs 100 quota units. Default Google free tier is
10,000/day → ~95 searches/day before throttling. The skill defaults to a
soft cap of 100 searches/day (configurable) so the user notices before
Google starts returning 403 quotaExceeded.

Cache: in-memory by normalized query, TTL configurable. Quota counter:
persisted to disk (cache_dir/quota.json) keyed by UTC date so it resets at
midnight UTC (matches Google's reset).
"""

from __future__ import annotations

import asyncio
import json
import logging
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import aiohttp

logger = logging.getLogger(__name__)

YOUTUBE_API_URL = "https://www.googleapis.com/youtube/v3/search"


class YouTubeSearch:
    def __init__(
        self,
        api_key: str,
        cache_dir: Path,
        daily_search_limit: int = 100,
        cache_ttl_seconds: int = 3600,
    ) -> None:
        self._api_key = api_key or ""
        self._cache_dir = cache_dir
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        self._daily_search_limit = max(1, int(daily_search_limit))
        self._cache_ttl_seconds = max(0, int(cache_ttl_seconds))
        self._memory_cache: dict[str, tuple[float, list[dict[str, Any]]]] = {}
        self._quota_path = self._cache_dir / "youtube_video_player_quota.json"

    # ------------------------------------------------------------------
    # Quota tracking — persisted by UTC date
    # ------------------------------------------------------------------

    @staticmethod
    def _today_utc() -> str:
        return datetime.now(timezone.utc).strftime("%Y-%m-%d")

    def _read_count(self) -> int:
        try:
            data = json.loads(self._quota_path.read_text(encoding="utf-8"))
            if data.get("date") == self._today_utc():
                return int(data.get("searches", 0))
        except (FileNotFoundError, json.JSONDecodeError, ValueError, OSError):
            pass
        return 0

    def _write_count(self, count: int) -> None:
        try:
            self._quota_path.write_text(
                json.dumps({"date": self._today_utc(), "searches": int(count)}),
                encoding="utf-8",
            )
        except OSError as ex:
            logger.warning("Could not persist quota counter: %s", ex)

    def update_api_key(self, api_key: str) -> None:
        """Replace the API key in place — used by the recovery paths
        (`secret_changed` callback when key arrives via Wingman Settings UI,
        or the `set_youtube_api_key` voice tool)."""
        self._api_key = api_key or ""

    def has_api_key(self) -> bool:
        return bool(self._api_key)

    @property
    def daily_limit(self) -> int:
        return self._daily_search_limit

    def searches_used_today(self) -> int:
        return self._read_count()

    def searches_remaining_today(self) -> int:
        return max(0, self._daily_search_limit - self._read_count())

    # ------------------------------------------------------------------
    # Search
    # ------------------------------------------------------------------

    @staticmethod
    def _normalize_query(q: str) -> str:
        return " ".join(q.lower().strip().split())

    async def search(self, query: str, max_results: int = 5) -> list[dict[str, Any]] | None:
        """Returns a list of search result items (raw API shape: each has `id`
        and `snippet`), or None if the API call failed or quota is exhausted.

        Cache: identical normalized queries within cache_ttl_seconds return
        the cached list without a fresh API call (free).
        """
        normalized = self._normalize_query(query)
        if not normalized:
            return []
        if not self._api_key:
            logger.warning("YouTube search called without an API key configured")
            return None

        # Cache hit?
        now = time.time()
        if normalized in self._memory_cache:
            cached_at, cached_results = self._memory_cache[normalized]
            if (now - cached_at) < self._cache_ttl_seconds:
                logger.info("YouTube search cache hit: %s", normalized)
                return cached_results

        if self.searches_remaining_today() <= 0:
            logger.warning("Daily YouTube search limit (%d) reached", self._daily_search_limit)
            return None

        params = {
            "key": self._api_key,
            "q": query,
            "type": "video",
            "part": "snippet",
            "maxResults": str(max(1, min(int(max_results), 5))),
            "videoEmbeddable": "true",
            "safeSearch": "moderate",
        }

        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(
                    YOUTUBE_API_URL,
                    params=params,
                    timeout=aiohttp.ClientTimeout(total=10),
                ) as resp:
                    body = await resp.text()
                    if resp.status == 403:
                        logger.error(
                            "YouTube API 403 — bad API key, exceeded daily quota, "
                            "or referrer/IP restriction blocked the request. Body: %s",
                            body[:300],
                        )
                        return None
                    if resp.status >= 400:
                        logger.error(
                            "YouTube API %d for q=%s: %s",
                            resp.status, normalized, body[:300],
                        )
                        return None
                    try:
                        data = json.loads(body)
                    except json.JSONDecodeError:
                        logger.error("YouTube API returned non-JSON body")
                        return None
        except (aiohttp.ClientError, asyncio.TimeoutError) as ex:
            logger.error("YouTube API call failed: %s", ex)
            return None

        # Increment counter only after a successful response.
        self._write_count(self._read_count() + 1)

        items = data.get("items", []) or []
        # Defensive: only keep items that have a videoId we can hand to the player.
        items = [
            item for item in items
            if isinstance(item, dict)
            and isinstance(item.get("id"), dict)
            and item["id"].get("videoId")
        ]

        self._memory_cache[normalized] = (now, items)
        return items
