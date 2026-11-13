using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Quotas;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Jellyfin.Plugin.P2PShare.Services;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Api;

/// <summary>
/// Peer-facing API (sharing side). Authenticated by the <c>X-P2PShare-Key</c> header, not by Jellyfin users.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("P2PShare/Remote")]
public class RemoteController : ControllerBase
{
    private readonly ConfigStore _config;
    private readonly CatalogService _catalog;
    private readonly PeerStreamService _streams;
    private readonly UsageService _usage;
    private readonly IServerApplicationHost _appHost;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteController> _logger;

    public RemoteController(
        ConfigStore config,
        CatalogService catalog,
        PeerStreamService streams,
        UsageService usage,
        IServerApplicationHost appHost,
        TimeProvider time,
        ILogger<RemoteController> logger)
    {
        _config = config;
        _catalog = catalog;
        _streams = streams;
        _usage = usage;
        _appHost = appHost;
        _time = time;
        _logger = logger;
    }

    /// <summary>Server identity plus the share's policy and current usage.</summary>
    [HttpGet("Info")]
    public IActionResult GetInfo()
    {
        if (!TryAuthorize(out var share, out var settings, out var error))
        {
            return error;
        }

        var usage = _usage.Ledger.GetEntry(share.UsageKey, share.QuotaInterval);
        return ApiHelpers.Json(new RemoteServerInfo
        {
            ServerName = string.IsNullOrWhiteSpace(settings.ServerDisplayName) ? _appHost.FriendlyName : settings.ServerDisplayName,
            ServerId = _appHost.SystemId,
            PluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            Share = new RemoteSharePolicy
            {
                Name = share.Name,
                AllowTranscoding = share.AllowTranscoding && settings.AllowTranscoding,
                MaxStreamingBitrateMbps = share.MaxStreamingBitrateMbps,
                MaxBandwidthMbps = share.MaxBandwidthMbps,
                QuotaGB = share.QuotaGB,
                QuotaInterval = share.QuotaInterval,
                QuotaUsedBytes = usage.WindowBytes,
                QuotaResetsAt = usage.WindowEnd,
                MaxConcurrentStreams = share.MaxConcurrentStreams,
                ExpiresAt = share.ExpiresAt,
            },
        });
    }

    /// <summary>Libraries included in the share.</summary>
    [HttpGet("Libraries")]
    public IActionResult GetLibraries()
    {
        if (!TryAuthorize(out var share, out _, out var error))
        {
            return error;
        }

        return ApiHelpers.Json(_catalog.GetSharedLibraries(share));
    }

    /// <summary>A page of a shared library's catalog.</summary>
    [HttpGet("Libraries/{libraryId}/Items")]
    public IActionResult GetItems([FromRoute] Guid libraryId, [FromQuery] int startIndex = 0, [FromQuery] int limit = 500)
    {
        if (!TryAuthorize(out var share, out _, out var error))
        {
            return error;
        }

        var page = _catalog.GetItems(share, libraryId, startIndex, limit);
        return page is null ? ApiHelpers.Error(StatusCodes.Status404NotFound, "Library not found in this share.") : ApiHelpers.Json(page);
    }

    /// <summary>Artwork of a shared item.</summary>
    [HttpGet("Items/{itemId}/Images/{imageType}")]
    public IActionResult GetImage([FromRoute] Guid itemId, [FromRoute] string imageType)
    {
        if (!TryAuthorize(out var share, out _, out var error))
        {
            return error;
        }

        if (!Enum.TryParse<ImageType>(imageType, true, out var type))
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Unknown image type.");
        }

        var item = _catalog.ResolveItem(share, itemId);
        var path = item is null ? null : CatalogService.GetImagePath(item, type);
        if (path is null)
        {
            return ApiHelpers.Error(StatusCodes.Status404NotFound, "Image not found.");
        }

        // Artwork is small and not counted against bandwidth limits, only against quotas.
        var length = new FileInfo(path).Length;
        _usage.Ledger.Add(share.UsageKey, share.QuotaInterval, length);
        _usage.Ledger.Add(UsageKeys.Global, _config.Snapshot.Global.GlobalQuotaInterval, length);
        return PhysicalFile(path, MimeTypes.GetMimeType(path));
    }

    /// <summary>Streams a shared item, directly (with ranges) or transcoded.</summary>
    [HttpGet("Items/{itemId}/Stream")]
    [HttpHead("Items/{itemId}/Stream")]
    public async Task<IActionResult> Stream(
        [FromRoute] Guid itemId,
        [FromQuery] bool transcode = false,
        [FromQuery] long maxBitrate = 0,
        [FromQuery] long startTimeTicks = 0)
    {
        if (!TryAuthorize(out var share, out var settings, out var error))
        {
            return error;
        }

        var item = _catalog.ResolveItem(share, itemId);
        if (item is null)
        {
            return ApiHelpers.Error(StatusCodes.Status404NotFound, "Item not found in this share.");
        }

        var path = CatalogService.GetServablePath(item);
        if (path is null)
        {
            return ApiHelpers.Error(StatusCodes.Status404NotFound, "This item cannot be streamed to peers.");
        }

        _logger.LogInformation("Peer stream of {Item} for share {Share} (transcode={Transcode})", item.Name, share.Name, transcode);
        var result = await _streams.ServeAsync(
            HttpContext,
            share,
            settings,
            item,
            path,
            new PeerStreamService.StreamParameters(transcode, Math.Max(0, maxBitrate), Math.Max(0, startTimeTicks)),
            HttpContext.RequestAborted).ConfigureAwait(false);

        return result is { } r ? ApiHelpers.Error(r.Status, r.Message) : new EmptyResult();
    }

    private bool TryAuthorize(out OutgoingShare share, out GlobalSettings settings, out IActionResult error)
    {
        var snapshot = _config.Snapshot;
        settings = snapshot.Global;
        var result = ShareAuthorizer.Authorize(
            Request.Headers[ShareCredential.HeaderName].ToString(),
            snapshot.Shares,
            settings,
            HttpContext.Connection.RemoteIpAddress,
            _time.GetUtcNow());

        if (result.Succeeded)
        {
            share = result.Share!;
            error = null!;
            return true;
        }

        _logger.LogDebug("Rejected peer request from {Address}: {Failure}", HttpContext.Connection.RemoteIpAddress, result.Failure);
        share = null!;
        error = ApiHelpers.Error(result.StatusCode, result.Message);
        return false;
    }
}
