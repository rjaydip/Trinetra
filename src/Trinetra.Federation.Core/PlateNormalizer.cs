using System.Text.RegularExpressions;

namespace Trinetra.Federation.Core;

/// <summary>
/// Canonicalizes license-plate OCR output so the same physical plate always matches itself.
/// </summary>
/// <remarks>
/// Mirrors <c>ai-worker/plate_normalizer.py</c> exactly — kept as a small pure function ported
/// per-language rather than called cross-language, per that file's own comment. "MH-12-AB-1234"
/// / "mh12ab1234" / "MH 12 AB 1234" all normalize to "MH12AB1234".
/// </remarks>
public static partial class PlateNormalizer
{
    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NonAlnum();

    private static readonly Dictionary<char, char> DigitToLetter = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['5'] = 'S', ['8'] = 'B',
    };

    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['O'] = '0', ['I'] = '1', ['S'] = '5', ['B'] = '8',
    };

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        return NonAlnum().Replace(raw.ToUpperInvariant(), "");
    }

    /// <summary>
    /// The normalized form plus a small set of OCR-confusable alternatives, for matching against
    /// a watchlist. Not fuzzy matching — only the handful of digit/letter pairs OCR actually
    /// confuses (0/O, 1/I, 5/S, 8/B).
    /// </summary>
    public static IReadOnlySet<string> NormalizedVariants(string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized.Length == 0)
        {
            return new HashSet<string>();
        }

        return new HashSet<string>(StringComparer.Ordinal)
        {
            normalized,
            Translate(normalized, DigitToLetter),
            Translate(normalized, LetterToDigit),
        };
    }

    private static string Translate(string value, Dictionary<char, char> map)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (map.TryGetValue(chars[i], out var replacement))
            {
                chars[i] = replacement;
            }
        }

        return new string(chars);
    }
}
