using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>
/// Makes strings safe as file or folder names on Windows, macOS and Linux.
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>Maximum length of one path segment (before extension).</summary>
    public const int MaxLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? value, string fallback = "Untitled")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            char mapped = c switch
            {
                ':' => '-',
                '/' or '\\' or '|' => '-',
                '<' or '>' or '"' or '?' or '*' => '\0',
                _ when char.IsControl(c) => ' ',
                _ => c,
            };

            if (mapped == '\0')
            {
                continue;
            }

            if (char.IsWhiteSpace(mapped))
            {
                if (lastWasSpace)
                {
                    continue;
                }

                mapped = ' ';
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(mapped);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength].TrimEnd('.', ' ');
        }

        // Leading dots would create hidden files; ".." must never appear as a segment.
        result = result.TrimStart('.');

        if (result.Length == 0)
        {
            return fallback;
        }

        if (Reserved.Contains(result))
        {
            result += "_";
        }

        return result;
    }
}
