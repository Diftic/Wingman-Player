"""Composite confidence math for auto-play decisions.

Combines two signals:
  1. LLM-provided confidence (0..1) reflecting how clearly the user named what
     they want. The Wingman LLM passes this when it calls the tool.
  2. Heuristic boost (0..0.30) based on how well the top YouTube search result
     aligns with the user-stated song and/or artist (channel match + title match).

Auto-play fires when (llm + boost) >= auto_play_threshold (configurable, default 0.98).
"""

from __future__ import annotations

import re
from difflib import SequenceMatcher
from typing import Any


def _normalize(s: str) -> str:
    """Lowercase, strip punctuation, collapse whitespace — for fuzzy matching."""
    if not s:
        return ""
    return re.sub(r"\s+", " ", re.sub(r"[^\w\s]", " ", s.lower())).strip()


def _similarity(a: str, b: str) -> float:
    """0..1 ratio via difflib's SequenceMatcher on normalized strings."""
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, _normalize(a), _normalize(b)).ratio()


def heuristic_boost(top_result: dict[str, Any], song: str, artist: str) -> float:
    """Boost in 0..0.30 reflecting how well the top result matches the
    parsed song/artist hints from the user's request.

    Channel match is the strongest signal (artists usually upload through their
    own / VEVO channel). Title match is weaker — VEVOs and lyric-channels often
    embed the song name verbatim, but covers and reactions also do.
    """
    boost = 0.0
    snippet = top_result.get("snippet", {}) or {}
    title = snippet.get("title", "") or ""
    channel = snippet.get("channelTitle", "") or ""

    if artist:
        artist_n = _normalize(artist)
        channel_n = _normalize(channel)
        # Guard against empty channel: "" is a substring of every string, so
        # `channel_n in artist_n` would spuriously match and fire +0.15 on
        # any malformed search result with no channelTitle.
        if channel_n:
            sim = _similarity(channel, artist)
            if sim >= 0.6 or artist_n in channel_n or channel_n in artist_n:
                boost += 0.15
            elif sim >= 0.4:
                boost += 0.07

    if song:
        song_n = _normalize(song)
        title_n = _normalize(title)
        if title_n:
            sim = _similarity(title, song)
            if sim >= 0.5 or song_n in title_n:
                boost += 0.10
            elif sim >= 0.3:
                boost += 0.05

    return min(0.30, boost)


def composite_confidence(
    llm_confidence: float,
    top_result: dict[str, Any],
    song: str,
    artist: str,
) -> float:
    """Combined confidence in 0..1, capped at both ends."""
    llm = max(0.0, min(1.0, float(llm_confidence)))
    boost = heuristic_boost(top_result, song, artist)
    return min(1.0, llm + boost)
