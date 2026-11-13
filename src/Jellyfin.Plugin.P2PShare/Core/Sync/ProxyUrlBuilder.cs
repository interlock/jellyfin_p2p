using System;
using System.Globalization;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>
/// Builds the local proxy URLs written into .strm files on the receiving server.
/// </summary>
public sealed class ProxyUrlBuilder
{
    public const string RouteTemplate = "P2PShare/Proxy/{0:N}/{1:N}";

    private readonly string _baseUrl;
    private readonly Guid _connectionId;
    private readonly string _proxyKey;
    private readonly bool _transcode;
    private readonly long _maxBitrate;

    /// <param name="localBaseUrl">e.g. http://127.0.0.1:8096</param>
    /// <param name="connectionId">Connection id.</param>
    /// <param name="proxyKey">Per-connection key.</param>
    /// <param name="transcode">Ask the peer to transcode.</param>
    /// <param name="maxBitrate">Target bitrate in bits/s when transcoding (0 = peer default).</param>
    public ProxyUrlBuilder(string localBaseUrl, Guid connectionId, string proxyKey, bool transcode, long maxBitrate)
    {
        if (!Core.UrlHelper.TryNormalizeServerUrl(localBaseUrl, out _baseUrl))
        {
            throw new ArgumentException("Local proxy base URL must be an absolute http(s) URL.", nameof(localBaseUrl));
        }

        ArgumentException.ThrowIfNullOrEmpty(proxyKey);
        _connectionId = connectionId;
        _proxyKey = proxyKey;
        _transcode = transcode;
        _maxBitrate = maxBitrate;
    }

    public string Build(Guid itemId)
    {
        var url = Core.UrlHelper.Combine(_baseUrl, string.Format(CultureInfo.InvariantCulture, RouteTemplate, _connectionId, itemId))
            + "?key=" + Uri.EscapeDataString(_proxyKey);
        if (_transcode)
        {
            url += "&transcode=true";
            if (_maxBitrate > 0)
            {
                url += "&maxBitrate=" + _maxBitrate.ToString(CultureInfo.InvariantCulture);
            }
        }

        return url;
    }
}
