"""Tests for confidence math.

Locks down the four cases hand-validated in DEVLOG Session 1 (2026-05-02)
plus a few edge cases. The auto-play threshold (default 0.98) is part of
the skill's UX contract — a refactor that quietly changes these numbers
would change when users get auto-play vs a list, so the values matter.

Run from the repo root:
    pytest tests
"""

from __future__ import annotations

import sys
from pathlib import Path

# Make the skill modules importable regardless of pytest cwd.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "youtube_video_player"))

from confidence import composite_confidence, heuristic_boost  # noqa: E402


def _result(title: str, channel: str) -> dict:
    """Helper: build a YouTube-shaped result dict for the tests."""
    return {"snippet": {"title": title, "channelTitle": channel}}


# ---------------------------------------------------------------------------
# DEVLOG Session 1 — the four hand-validated cases
# ---------------------------------------------------------------------------


def test_strong_match_auto_plays() -> None:
    """Clear song + artist + matching top result, llm=0.90 → composite 1.000.

    Both boosts apply at full strength (0.15 + 0.10 = 0.25), total 1.15
    capped to 1.0. Auto-plays at any reasonable threshold (<=0.98).
    """
    top = _result(
        title="Queen - Bohemian Rhapsody (Official Video Remastered)",
        channel="Queen Official",
    )
    composite = composite_confidence(
        llm_confidence=0.90,
        top_result=top,
        song="Bohemian Rhapsody",
        artist="Queen",
    )
    assert composite == 1.0


def test_weak_match_stays_in_list_mode() -> None:
    """Clear request, weak title overlap, llm=0.90 → composite ~ 0.95.

    Boost is the weak song tier (+0.05); no artist match (channel doesn't
    contain the artist string). Stays below the default 0.98 threshold so
    the user gets a list, not auto-play.
    """
    top = _result(
        title="Something Completely Different Cover",
        channel="Random Channel",
    )
    composite = composite_confidence(
        llm_confidence=0.90,
        top_result=top,
        song="Bohemian",
        artist="Queen",
    )
    # Below the auto-play threshold; user picks from list.
    assert composite < 0.98
    assert composite >= 0.90  # at minimum, the llm score


def test_artist_only_request() -> None:
    """Artist-only, llm=0.50, channel matches artist → composite 0.65.

    +0.15 artist boost; no song to match. Below threshold → list mode.
    """
    top = _result(title="Some Beatles Song", channel="The Beatles")
    composite = composite_confidence(
        llm_confidence=0.50,
        top_result=top,
        song="",
        artist="The Beatles",
    )
    assert composite == 0.65


def test_heuristic_only_artist_match_yields_15() -> None:
    """Boost-only check: artist match alone yields +0.15."""
    top = _result(title="Anything", channel="Queen Official")
    boost = heuristic_boost(top, song="", artist="Queen")
    assert boost == 0.15


# ---------------------------------------------------------------------------
# Edge cases — clamping, capping, empty input
# ---------------------------------------------------------------------------


def test_llm_confidence_clamped_below_zero() -> None:
    composite = composite_confidence(
        llm_confidence=-0.5,
        top_result=_result("x", "y"),
        song="",
        artist="",
    )
    assert composite == 0.0


def test_llm_confidence_clamped_above_one() -> None:
    composite = composite_confidence(
        llm_confidence=2.0,
        top_result=_result("x", "y"),
        song="",
        artist="",
    )
    assert composite == 1.0


def test_composite_capped_at_one() -> None:
    """llm=0.95 + max boost 0.25 → 1.20 capped to 1.0."""
    top = _result(title="Bohemian Rhapsody", channel="Queen")
    composite = composite_confidence(
        llm_confidence=0.95,
        top_result=top,
        song="Bohemian Rhapsody",
        artist="Queen",
    )
    assert composite == 1.0


def test_empty_song_and_artist_returns_pure_llm() -> None:
    composite = composite_confidence(
        llm_confidence=0.70,
        top_result=_result("anything", "anyone"),
        song="",
        artist="",
    )
    assert composite == 0.70


def test_boost_capped_at_thirty() -> None:
    """Even with both song and artist matching, boost never exceeds 0.30."""
    top = _result(
        title="Bohemian Rhapsody (Remastered 2011)",
        channel="Queen Official",
    )
    boost = heuristic_boost(top, song="Bohemian Rhapsody", artist="Queen")
    assert boost <= 0.30


def test_missing_snippet_does_not_crash() -> None:
    """Defensive: malformed search results shouldn't blow up boost math."""
    boost = heuristic_boost({}, song="x", artist="y")
    assert boost == 0.0


def test_partial_artist_match_via_substring() -> None:
    """Channel 'Queen Official' contains 'Queen' → artist boost fires."""
    top = _result(title="Random", channel="Queen Official")
    boost = heuristic_boost(top, song="", artist="Queen")
    assert boost == 0.15


def test_weak_artist_match_via_similarity() -> None:
    """Channel similar (0.4 <= sim < 0.6) but no substring → +0.07 weak boost."""
    # 'Quueen' vs 'Queen' — typo, no substring, but similar.
    top = _result(title="x", channel="Quueens Vibe Channel")
    boost = heuristic_boost(top, song="", artist="Queen")
    # The exact value depends on SequenceMatcher; assert it landed in the
    # weak-boost band, not the strong band, and not zero.
    assert 0.0 < boost < 0.15
