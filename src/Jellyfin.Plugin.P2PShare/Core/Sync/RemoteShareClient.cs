using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Tokens;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>An error returned by (or while reaching) a peer.</summary>
public sealed class RemoteShareException : Exception
{
    public RemoteShareException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets a value indicating whether the peer rejected our credentials (revoked/expired/disabled).</summary>
    public bool IsAccessDenied => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>Catalog operations against a peer.</summary>
public interface IRemoteShareClient
{
    Task<RemoteServerInfo> GetInfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteLibrary>> GetLibrariesAsync(CancellationToken cancellationToken);

    Task<RemoteItemPage> GetItemsAsync(Guid libraryId, int startIndex, int limit, CancellationToken cancellationToken);

    Task<byte[]?> GetImageAsync(Guid itemId, string imageType, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP client for the peer API. The <see cref="HttpClient"/> is owned by the caller.
/// </summary>
public sealed class RemoteShareClient : IRemoteShareClient
{
    /// <summary>Items requested per page during sync.</summary>
    public const int PageSize = 500;

    private readonly HttpClient _http;
    private readonly ShareInvite _invite;

    public RemoteShareClient(HttpClient http, ShareInvite invite)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _invite = invite ?? throw new ArgumentNullException(nameof(invite));
    }

    public ShareInvite Invite => _invite;

    public Task<RemoteServerInfo> GetInfoAsync(CancellationToken cancellationToken)
        => GetJsonAsync<RemoteServerInfo>(RemoteApiRoutes.Info, cancellationToken);

    public async Task<IReadOnlyList<RemoteLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
        => await GetJsonAsync<List<RemoteLibrary>>(RemoteApiRoutes.Libraries, cancellationToken).ConfigureAwait(false);

    public Task<RemoteItemPage> GetItemsAsync(Guid libraryId, int startIndex, int limit, CancellationToken cancellationToken)
        => GetJsonAsync<RemoteItemPage>(RemoteApiRoutes.Items(libraryId, startIndex, limit), cancellationToken);

    /// <summary>Fetches every item of a library, page by page.</summary>
    public static async Task<List<RemoteItem>> GetAllItemsAsync(IRemoteShareClient client, Guid libraryId, CancellationToken cancellationToken, int pageSize = PageSize)
    {
        var all = new List<RemoteItem>();
        var start = 0;
        while (true)
        {
            var page = await client.GetItemsAsync(libraryId, start, pageSize, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            start += page.Items.Count;
            if (page.Items.Count == 0 || start >= page.TotalRecordCount)
            {
                return all;
            }
        }
    }

    public async Task<byte[]?> GetImageAsync(Guid itemId, string imageType, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, RemoteApiRoutes.Image(itemId, imageType));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a media stream. The caller owns (and must dispose) the response. Non-success
    /// responses are returned as-is so a proxy can pass them through.
    /// </summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(Guid itemId, string? query, string? rangeHeader, CancellationToken cancellationToken)
    {
        var path = RemoteApiRoutes.Stream(itemId);
        if (!string.IsNullOrEmpty(query))
        {
            path += query.StartsWith('?') ? query : "?" + query;
        }

        using var request = CreateRequest(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        return await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a request with the credential header (exposed for tests and the proxy).</summary>
    public HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, UrlHelper.Combine(_invite.ServerUrl, relativePath));
        request.Headers.TryAddWithoutValidation(ShareCredential.HeaderName, _invite.CredentialHeaderValue);
        request.Headers.TryAddWithoutValidation("X-P2PShare-Protocol", RemoteApiRoutes.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, cancellationToken).ConfigureAwait(false);
            return result ?? throw new RemoteShareException("Peer returned an empty response.", response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new RemoteShareException("Peer returned invalid JSON. Is the P2P Share plugin installed there?", response.StatusCode, ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteShareException($"Could not reach {_invite.ServerUrl}: {ex.Message}", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteShareException($"Timed out contacting {_invite.ServerUrl}.", null, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RemoteError>(RemoteJson.Options, cancellationToken).ConfigureAwait(false);
            message = error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Not our JSON error body (e.g. reverse proxy page or plugin not installed).
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = response.StatusCode == HttpStatusCode.NotFound
                ? "The peer does not expose the P2P Share API (is the plugin installed and up to date?)."
                : $"Peer responded with {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        throw new RemoteShareException(message, response.StatusCode);
    }
}
