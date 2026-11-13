using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Jellyfin.Plugin.P2PShare.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Api;

/// <summary>
/// Receiving side: the URL inside mirror .strm files. Forwards the request to the peer with
/// the share credential, so the secret never appears in .strm files or client URLs.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("P2PShare/Proxy")]
public class ProxyController : ControllerBase
{
    private static readonly string[] ForwardedResponseHeaders = { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    private readonly ConfigStore _config;
    private readonly PeerClientFactory _clients;
    private readonly ILogger<ProxyController> _logger;

    public ProxyController(ConfigStore config, PeerClientFactory clients, ILogger<ProxyController> logger)
    {
        _config = config;
        _clients = clients;
        _logger = logger;
    }

    [HttpGet("{connectionId}/{itemId}")]
    [HttpHead("{connectionId}/{itemId}")]
    public async Task<IActionResult> Stream(
        [FromRoute] Guid connectionId,
        [FromRoute] Guid itemId,
        [FromQuery] string? key,
        [FromQuery] bool transcode = false,
        [FromQuery] long maxBitrate = 0,
        [FromQuery] long startTimeTicks = 0)
    {
        var snapshot = _config.Snapshot;
        var connection = snapshot.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (connection is null || !ShareSecrets.KeysEqual(key, connection.ProxyKey))
        {
            return NotFound();
        }

        if (!connection.Enabled)
        {
            return ApiHelpers.Error(StatusCodes.Status503ServiceUnavailable, "This shared library is disabled.");
        }

        RemoteShareClient client;
        try
        {
            client = _clients.Create(connection, snapshot.Global);
        }
        catch (ArgumentException ex)
        {
            return ApiHelpers.Error(StatusCodes.Status502BadGateway, ex.Message);
        }

        var query = "transcode=" + (transcode ? "true" : "false");
        if (maxBitrate > 0)
        {
            query += "&maxBitrate=" + maxBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (startTimeTicks > 0)
        {
            query += "&startTimeTicks=" + startTimeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.OpenStreamAsync(itemId, query, Request.Headers.Range.ToString(), HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (RemoteShareException ex)
        {
            _logger.LogWarning("P2P Share proxy could not reach {Server}: {Message}", connection.RemoteServerName, ex.Message);
            return ApiHelpers.Error(StatusCodes.Status502BadGateway, ex.Message);
        }

        using (upstream)
        {
            Response.StatusCode = (int)upstream.StatusCode;
            foreach (var name in ForwardedResponseHeaders)
            {
                if (upstream.Headers.TryGetValues(name, out var values) || upstream.Content.Headers.TryGetValues(name, out values))
                {
                    Response.Headers[name] = values.ToArray();
                }
            }

            if (HttpMethods.IsHead(Request.Method))
            {
                return new EmptyResult();
            }

            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                var body = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await body.CopyToAsync(Response.Body, 64 * 1024, HttpContext.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Player stopped or seeked.
            }
            catch (System.IO.IOException)
            {
                // Either side disconnected.
            }
        }

        return new EmptyResult();
    }
}
