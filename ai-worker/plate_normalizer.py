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
_OCR_DIGIT_TO_LETTER = str.maketrans({"0": "O", "1": "I", "5": "S", "8": "B"})
_OCR_LETTER_TO_DIGIT = str.maketrans({"O": "0", "I": "1", "S": "5", "B": "8"})


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
