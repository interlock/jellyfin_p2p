using System;

namespace Jellyfin.Plugin.P2PShare.Core;

/// <summary>
/// URL helpers.
/// </summary>
public static class UrlHelper
{
    /// <summary>
    /// Validates an absolute http(s) URL and removes any trailing slash, query and fragment.
    /// </summary>
    public static bool TryNormalizeServerUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>Returns true if the URL uses HTTPS.</summary>
    public static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Joins a base URL and a relative path with exactly one slash.</summary>
    public static string Combine(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
