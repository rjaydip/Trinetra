using System.Globalization;

namespace Trinetra.Federation.Adapters.Dahua;

/// <summary>
/// Parses Dahua's flat <c>key.path=value</c> response format.
/// </summary>
/// <remarks>
/// Dahua CGI returns neither XML nor JSON but a line-per-value text format:
/// <code>
/// table.RemoteDevice[0].Enable=true
/// table.RemoteDevice[0].Name=Front Gate
/// </code>
/// This parser turns it into indexed records so the adapter can treat it like any other
/// structured response.
/// </remarks>
internal static class DahuaResponseParser
{
    /// <summary>Flattens a response into key/value pairs, preserving the full dotted key.</summary>
    internal static Dictionary<string, string> ParseFlat(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
        {
            return result;
        }

        foreach (var line in body.Split('\n', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return result;
    }

    /// <summary>
    /// Groups an indexed table into one dictionary per row.
    /// </summary>
    /// <remarks>
    /// Given <c>table.RemoteDevice[3].Name</c> and prefix <c>table.RemoteDevice</c>, yields index
    /// 3 with key <c>Name</c>. Indices are sparse in practice — a device with channels 0, 1 and 7
    /// configured produces exactly those — so the result is keyed by index rather than being a
    /// dense list.
    /// </remarks>
    internal static SortedDictionary<int, Dictionary<string, string>> ParseIndexedTable(
        string body, string prefix)
    {
        var rows = new SortedDictionary<int, Dictionary<string, string>>();

        foreach (var (key, value) in ParseFlat(body))
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = key[prefix.Length..];
            if (remainder.Length == 0 || remainder[0] != '[')
            {
                continue;
            }

            var close = remainder.IndexOf(']', StringComparison.Ordinal);
            if (close < 0
                || !int.TryParse(remainder[1..close], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var field = remainder[(close + 1)..].TrimStart('.');
            if (field.Length == 0)
            {
                continue;
            }

            if (!rows.TryGetValue(index, out var row))
            {
                rows[index] = row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            row[field] = value;
        }

        return rows;
    }

    /// <summary>
    /// Parses the channel list from <c>getEventIndexes</c>, e.g. <c>channels[0]=2</c>.
    /// </summary>
    /// <remarks>
    /// Used for bulk video-loss status: one call returns every channel currently in the given
    /// state, which keeps status polling proportional to devices rather than cameras.
    /// </remarks>
    internal static HashSet<string> ParseChannelIndexes(string body)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in ParseFlat(body))
        {
            if (key.StartsWith("channels", StringComparison.OrdinalIgnoreCase))
            {
                channels.Add(value);
            }
        }

        return channels;
    }

    /// <summary>
    /// Parses a Dahua event-stream payload: <c>Code=VideoMotion;action=Start;index=0</c>,
    /// optionally followed by a <c>data=</c> JSON blob.
    /// </summary>
    internal static Dictionary<string, string> ParseEventPayload(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
        {
            return result;
        }

        // The data= field is JSON that may itself contain semicolons, so it is split off first
        // rather than being shredded by the field separator.
        var dataMarker = body.IndexOf("data=", StringComparison.OrdinalIgnoreCase);
        var head = dataMarker >= 0 ? body[..dataMarker] : body;

        if (dataMarker >= 0)
        {
            result["data"] = body[(dataMarker + "data=".Length)..].Trim();
        }

        foreach (var field in head.Split(';', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = field.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                result[field[..equals].Trim()] = field[(equals + 1)..].Trim();
            }
        }

        return result;
    }
}
