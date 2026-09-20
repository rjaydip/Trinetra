"""Canonicalizes license-plate OCR output so the same physical plate always matches itself.

Pure function, no I/O — deliberately kept dependency-free so it is trivial to port into the
.NET Core layer in Phase 2 rather than calling across languages for something this small
(brief §15: "MH-12-AB-1234" / "mh12ab1234" / "MH 12 AB 1234" must normalize to one identifier).
"""

from __future__ import annotations

import re

_NON_ALNUM = re.compile(r"[^A-Z0-9]")

# OCR commonly confuses these characters in the letter/digit positions of a plate. This is a
# best-effort correction, not a guarantee — matching is still done on the raw normalized form
# too, so an unresolved ambiguity fails to match rather than silently matching the wrong plate.
#
# G/6 added after a real observed misread this session: a genuine plate "GJ11S7924" was OCR'd as
# "6J1157924" — note both G->6 (position 0) and S->5 (position 4) fired on the same real read,
# confirming these are live confusions on this camera's footage, not theoretical.
_OCR_DIGIT_TO_LETTER = str.maketrans({"0": "O", "1": "I", "5": "S", "8": "B", "6": "G"})
_OCR_LETTER_TO_DIGIT = str.maketrans({"O": "0", "I": "1", "S": "5", "B": "8", "G": "6"})


def normalize_plate(raw: str) -> str:
    """Uppercases and strips everything but letters/digits.

    "MH-12-AB-1234", "mh12ab1234", "MH 12 AB 1234" all normalize to "MH12AB1234".
    """
    if not raw:
        return ""

    return _NON_ALNUM.sub("", raw.upper())


def normalized_variants(raw: str) -> set[str]:
    """The normalized form plus OCR-confusable alternatives, for matching against a watchlist.

    A single best-guess normalization is fragile: OCR reading "MH12A81234" instead of
    "MH12AB1234" (8 for B) is common enough that the matching engine should try a small set of
    plausible corrections rather than only the literal OCR output. Kept intentionally small —
    this is not fuzzy matching, just the handful of digit/letter pairs OCR actually confuses.
    """
    base = normalize_plate(raw)
    if not base:
        return set()

    return {base, base.translate(_OCR_DIGIT_TO_LETTER), base.translate(_OCR_LETTER_TO_DIGIT)}


# A real Indian plate, normalized, is always 8-10 characters (2-letter state + 1-2 digit RTO
# code + 0-3 letter series + 4-digit number) and always contains both letters and digits — no
# real plate is all-letters or all-digits. Deliberately NOT a strict per-position format regex
# (state-code letters, then digits, then optional series letters, then exactly 4 digits): tested
# against a real observed misread this session, a strict regex rejects "GJ1157924" (the
# head-corrected form of a genuine plate "GJ11S7924" that also has an uncorrected S->5 confusion
# in its RTO/series segment) purely because that one remaining error breaks the expected
# character-class-per-position shape. The point of this check is filtering out obviously-wrong
# short fragments ("4", "CQ4", "HHE", "Q4" — all real misreads this session, all 1-3 characters
# with no such structure at all), not rejecting a real plate over a single remaining OCR error
# elsewhere in it.
_MIN_PLATE_LENGTH = 8
_MAX_PLATE_LENGTH = 11  # +1 over the usual 10 for tolerance on an unusual/BH-series plate


def is_plausible_plate(normalized: str) -> bool:
    """Whether `normalized` (already through `normalize_plate`) is even shaped like a real
    Indian plate — length in range, and containing both a letter and a digit (a plate is never
    all one character class). Confirmed real misreads this session — "4", "CQ4", "HHE", "Q4" —
    are all rejected by this; a real plate with one remaining OCR error elsewhere is not."""
    return (
        _MIN_PLATE_LENGTH <= len(normalized) <= _MAX_PLATE_LENGTH
        and any(c.isalpha() for c in normalized)
        and any(c.isdigit() for c in normalized)
    )


def correct_plate_format(normalized: str) -> str:
    """Position-aware OCR-confusion correction, applied to what actually gets stored/displayed —
    unlike `normalized_variants` (which only ever produced *candidates* for watchlist fuzzy-
    matching and was never used to correct the stored plate text itself).

    The first two characters of a normalized Indian plate are always the state code (letters)
    and the last four are always the vehicle number (digits), regardless of the variable-length
    RTO-code + series segment in between. A digit found where a letter is structurally required
    (or vice versa) is almost certainly an OCR confusion, not a real ambiguity, so it's corrected
    outright. Confirmed against a real misread this session: OCR read plate "GJ11S7924" as
    "6J1157924" — the leading "6" is exactly this class of error and this function corrects it
    back to "G".

    The middle segment (RTO digits + optional series letters) is deliberately left untouched: its
    length varies, so there is no reliable positional rule to apply there, and a wrong guess is
    worse than no guess (the same real misread above also has "S" misread as "5" mid-string,
    which this does *not* catch — `normalized_variants`'s multi-candidate matching is what
    watchlist lookups still rely on to catch that class)."""
    if len(normalized) < 6:
        return normalized

    head = normalized[:2].translate(_OCR_DIGIT_TO_LETTER)
    tail = normalized[-4:].translate(_OCR_LETTER_TO_DIGIT)
    middle = normalized[2:-4]
    return head + middle + tail
