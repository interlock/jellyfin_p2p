using System;
using System.Globalization;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>Result of parsing a Range header.</summary>
public enum RangeParseResult
{
    /// <summary>No (usable) Range header; serve the whole entity with 200.</summary>
    None,

    /// <summary>A satisfiable single range; serve 206.</summary>
    Satisfiable,

    /// <summary>Syntactically valid but outside the entity; serve 416.</summary>
    NotSatisfiable,
}

/// <summary>
/// An inclusive byte range, as used by HTTP Range requests (single range only).
/// </summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    public string ToContentRange(long totalLength) => $"bytes {Start}-{End}/{totalLength}";

    /// <summary>
    /// Parses a Range header value against an entity of <paramref name="totalLength"/> bytes.
    /// Multi-range and non-byte units are treated as "no range" (allowed by RFC 9110).
    /// </summary>
    public static RangeParseResult TryParse(string? header, long totalLength, out ByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return RangeParseResult.None;
        }

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParseResult.None;
        }

        var spec = value[6..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return RangeParseResult.None;
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return RangeParseResult.None;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        if (totalLength <= 0)
        {
            return RangeParseResult.NotSatisfiable;
        }

        if (startText.Length == 0)
        {
            // Suffix range: last N bytes.
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
            {
                return RangeParseResult.None;
            }

            if (suffix == 0)
            {
                return RangeParseResult.NotSatisfiable;
            }

            var start = Math.Max(0, totalLength - suffix);
            range = new ByteRange(start, totalLength - 1);
            return RangeParseResult.Satisfiable;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return RangeParseResult.None;
        }

        long last;
        if (endText.Length == 0)
        {
            last = totalLength - 1;
        }
        else if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out last))
        {
            return RangeParseResult.None;
        }
        else if (last < first)
        {
            return RangeParseResult.None;
        }

        if (first >= totalLength)
        {
            return RangeParseResult.NotSatisfiable;
        }

        range = new ByteRange(first, Math.Min(last, totalLength - 1));
        return RangeParseResult.Satisfiable;
    }
}
