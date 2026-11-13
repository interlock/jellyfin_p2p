using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Jellyfin.Plugin.P2PShare.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.P2PShare.Api;

/// <summary>
/// Administrator API used by the plugin's configuration page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("P2PShare/Admin")]
public class AdminController : ControllerBase
{
    private readonly ConfigStore _config;
    private readonly CatalogService _catalog;
    private readonly UsageService _usage;
    private readonly PeerStreamService _streams;
    private readonly PeerClientFactory _clients;
    private readonly RemoteSyncService _sync;
    private readonly IServerApplicationHost _appHost;
    private readonly TimeProvider _time;

    public AdminController(
        ConfigStore config,
        CatalogService catalog,
        UsageService usage,
        PeerStreamService streams,
        PeerClientFactory clients,
        RemoteSyncService sync,
        IServerApplicationHost appHost,
        TimeProvider time)
    {
        _config = config;
        _catalog = catalog;
        _usage = usage;
        _streams = streams;
        _clients = clients;
        _sync = sync;
        _appHost = appHost;
        _time = time;
    }

    // ---- Settings ---------------------------------------------------------------------

    [HttpGet("Settings")]
    public IActionResult GetSettings()
        => ApiHelpers.Json(new
        {
            settings = _config.Snapshot.Global,
            serverName = _appHost.FriendlyName,
            suggestedPublicUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}",
            mirrorRoot = _config.MirrorRoot,
        });

    [HttpPut("Settings")]
    public async Task<IActionResult> PutSettings(CancellationToken cancellationToken)
    {
        var settings = await ApiHelpers.ReadBodyAsync<GlobalSettings>(Request, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Invalid settings.");
        }

        var errors = Validation.ValidateSettings(settings);
        if (errors.Count > 0)
        {
            return ApiHelpers.ValidationErrors(errors);
        }

        Validation.Normalize(settings);
        _config.Update(c => c.Global = settings);
        return ApiHelpers.Json(settings);
    }

    [HttpGet("Libraries")]
    public IActionResult GetLibraries() => ApiHelpers.Json(_catalog.GetLocalLibraries());

    // ---- Status -----------------------------------------------------------------------

    [HttpGet("Status")]
    public IActionResult GetStatus()
    {
        var g = _config.Snapshot.Global;
        var usage = _usage.Ledger.GetEntry(UsageKeys.Global, g.GlobalQuotaInterval);
        return ApiHelpers.Json(new
        {
            global = new
            {
                usedBytes = usage.WindowBytes,
                previousBytes = usage.PreviousWindowBytes,
                lifetimeBytes = usage.LifetimeBytes,
                quotaBytes = Units.GBToBytes(g.GlobalQuotaGB),
                windowEnd = usage.WindowEnd,
                interval = g.GlobalQuotaInterval,
            },
            activeStreams = _streams.Registry.Snapshot(),
            syncRunning = _sync.IsRunning,
        });
    }

    // ---- Outgoing shares --------------------------------------------------------------

    [HttpGet("Shares")]
    public IActionResult GetShares() => ApiHelpers.Json(_config.Snapshot.Shares.Select(ToView).ToList());

    [HttpPost("Shares")]
    public async Task<IActionResult> CreateShare(CancellationToken cancellationToken)
    {
        var edit = await ApiHelpers.ReadBodyAsync<ShareEdit>(Request, cancellationToken).ConfigureAwait(false);
        if (edit is null)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Invalid share.");
        }

        var settings = _config.Snapshot.Global;
        if (string.IsNullOrWhiteSpace(settings.PublicServerUrl))
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Set the public server URL in Settings before creating shares.");
        }

        if (edit.ExpiresAt is null && settings.DefaultTokenLifetimeDays > 0)
        {
            edit.ExpiresAt = _time.GetUtcNow().UtcDateTime.AddDays(settings.DefaultTokenLifetimeDays);
        }

        var errors = Validation.ValidateShare(edit, ShareableLibraryIds(), _time.GetUtcNow());
        if (errors.Count > 0)
        {
            return ApiHelpers.ValidationErrors(errors);
        }

        var secret = ShareSecrets.NewSecret();
        var share = new OutgoingShare { SecretHash = ShareSecrets.Hash(secret), CreatedAt = _time.GetUtcNow().UtcDateTime };
        edit.ApplyTo(share);
        _config.Update(c => c.Shares.Add(share));
        return ApiHelpers.Json(new { share = ToView(share), token = BuildToken(share, secret, settings) }, StatusCodes.Status201Created);
    }

    [HttpPut("Shares/{id}")]
    public async Task<IActionResult> UpdateShare([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var edit = await ApiHelpers.ReadBodyAsync<ShareEdit>(Request, cancellationToken).ConfigureAwait(false);
        if (edit is null)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Invalid share.");
        }

        var existing = _config.FindShare(id);
        if (existing is null)
        {
            return NotFound();
        }

        // Allow keeping an already-past expiry only if it is unchanged.
        var now = existing.ExpiresAt == edit.ExpiresAt?.ToUniversalTime() ? DateTimeOffset.MinValue : _time.GetUtcNow();
        var errors = Validation.ValidateShare(edit, ShareableLibraryIds(), now);
        if (errors.Count > 0)
        {
            return ApiHelpers.ValidationErrors(errors);
        }

        OutgoingShare? updated = null;
        _config.Update(c =>
        {
            updated = c.Shares.FirstOrDefault(s => s.Id == id);
            if (updated is not null)
            {
                edit.ApplyTo(updated);
            }
        });
        return updated is null ? NotFound() : ApiHelpers.Json(ToView(updated));
    }

    [HttpPost("Shares/{id}/Regenerate")]
    public IActionResult RegenerateShare([FromRoute] Guid id)
    {
        var settings = _config.Snapshot.Global;
        if (string.IsNullOrWhiteSpace(settings.PublicServerUrl))
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Set the public server URL in Settings first.");
        }

        var secret = ShareSecrets.NewSecret();
        OutgoingShare? share = null;
        _config.Update(c =>
        {
            share = c.Shares.FirstOrDefault(s => s.Id == id);
            if (share is not null)
            {
                share.SecretHash = ShareSecrets.Hash(secret);
            }
        });
        return share is null ? NotFound() : ApiHelpers.Json(new { share = ToView(share), token = BuildToken(share, secret, settings) });
    }

    [HttpDelete("Shares/{id}")]
    public IActionResult DeleteShare([FromRoute] Guid id)
    {
        var removed = _config.Update(c => c.Shares.RemoveAll(s => s.Id == id));
        if (removed == 0)
        {
            return NotFound();
        }

        _usage.Ledger.Remove(UsageKeys.ForShare(id));
        return NoContent();
    }

    // ---- Incoming connections ---------------------------------------------------------

    public sealed class TokenRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    public sealed class ConnectionEdit
    {
        public bool Enabled { get; set; } = true;

        public bool PreferTranscode { get; set; }

        public double PreferredMaxBitrateMbps { get; set; }

        public List<LibraryEdit> Libraries { get; set; } = new();
    }

    public sealed class LibraryEdit
    {
        public Guid RemoteLibraryId { get; set; }

        public bool Enabled { get; set; } = true;

        public string LocalName { get; set; } = string.Empty;
    }

    [HttpGet("Connections")]
    public IActionResult GetConnections() => ApiHelpers.Json(_config.Snapshot.Connections.Select(ToView).ToList());

    [HttpPost("Connections/Preview")]
    public async Task<IActionResult> PreviewConnection(CancellationToken cancellationToken)
    {
        var request = await ApiHelpers.ReadBodyAsync<TokenRequest>(Request, cancellationToken).ConfigureAwait(false);
        try
        {
            var invite = PeerClientFactory.DecodeToken(request?.Token ?? string.Empty, _config.Snapshot.Global);
            var client = _clients.Create(invite);
            var info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            var libraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
            return ApiHelpers.Json(new { serverUrl = invite.ServerUrl, info, libraries });
        }
        catch (ArgumentException ex)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (RemoteShareException ex)
        {
            return ApiHelpers.Error(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    [HttpPost("Connections")]
    public async Task<IActionResult> AddConnection(CancellationToken cancellationToken)
    {
        var request = await ApiHelpers.ReadBodyAsync<TokenRequest>(Request, cancellationToken).ConfigureAwait(false);
        ShareInvite invite;
        RemoteServerInfo info;
        IReadOnlyList<RemoteLibrary> libraries;
        try
        {
            invite = PeerClientFactory.DecodeToken(request?.Token ?? string.Empty, _config.Snapshot.Global);
            if (invite.ServerId == _appHost.SystemId)
            {
                return ApiHelpers.Error(StatusCodes.Status400BadRequest, "This token was created by this server.");
            }

            var client = _clients.Create(invite);
            info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            libraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (RemoteShareException ex)
        {
            return ApiHelpers.Error(StatusCodes.Status502BadGateway, ex.Message);
        }

        var duplicate = _config.Snapshot.Connections.Any(c =>
            ShareInviteCodec.TryDecode(c.Token, out var other, out _) && other!.ShareId == invite.ShareId && other.ServerUrl == invite.ServerUrl);
        if (duplicate)
        {
            return ApiHelpers.Error(StatusCodes.Status409Conflict, "This share has already been added.");
        }

        var connection = new IncomingConnection
        {
            Token = request!.Token.Trim(),
            RemoteServerName = string.IsNullOrWhiteSpace(info.ServerName) ? invite.ServerName : info.ServerName,
            RemoteServerUrl = invite.ServerUrl,
            ProxyKey = ShareSecrets.NewKey(),
        };
        ConnectionSynchronizer.MergeLibraries(connection, libraries);
        _config.Update(c => c.Connections.Add(connection));
        _sync.QueueSync(connection.Id);
        return ApiHelpers.Json(ToView(connection), StatusCodes.Status201Created);
    }

    [HttpPut("Connections/{id}")]
    public async Task<IActionResult> UpdateConnection([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var edit = await ApiHelpers.ReadBodyAsync<ConnectionEdit>(Request, cancellationToken).ConfigureAwait(false);
        if (edit is null || edit.PreferredMaxBitrateMbps < 0)
        {
            return ApiHelpers.Error(StatusCodes.Status400BadRequest, "Invalid connection settings.");
        }

        IncomingConnection? updated = null;
        _config.Update(c =>
        {
            updated = c.Connections.FirstOrDefault(x => x.Id == id);
            if (updated is null)
            {
                return;
            }

            updated.Enabled = edit.Enabled;
            updated.PreferTranscode = edit.PreferTranscode;
            updated.PreferredMaxBitrateMbps = edit.PreferredMaxBitrateMbps;
            foreach (var libEdit in edit.Libraries)
            {
                var lib = updated.Libraries.FirstOrDefault(l => l.RemoteLibraryId == libEdit.RemoteLibraryId);
                if (lib is not null)
                {
                    lib.Enabled = libEdit.Enabled;
                    lib.LocalName = libEdit.LocalName?.Trim() ?? string.Empty;
                }
            }
        });

        if (updated is null)
        {
            return NotFound();
        }

        if (updated.Enabled)
        {
            _sync.QueueSync(id);
        }

        return ApiHelpers.Json(ToView(updated));
    }

    [HttpPost("Connections/{id}/Sync")]
    public IActionResult SyncConnection([FromRoute] Guid id)
    {
        if (_config.FindConnection(id) is null)
        {
            return NotFound();
        }

        _sync.QueueSync(id);
        return Accepted();
    }

    [HttpDelete("Connections/{id}")]
    public async Task<IActionResult> DeleteConnection([FromRoute] Guid id)
    {
        var connection = _config.FindConnection(id);
        if (connection is null)
        {
            return NotFound();
        }

        _config.Update(c => c.Connections.RemoveAll(x => x.Id == id));
        await _sync.RemoveConnectionArtifactsAsync(connection).ConfigureAwait(false);
        return NoContent();
    }

    // ---- Helpers ----------------------------------------------------------------------

    private HashSet<Guid> ShareableLibraryIds()
        => _catalog.GetLocalLibraries().Where(l => !l.IsMirror).Select(l => l.Id).ToHashSet();

    private string BuildToken(OutgoingShare share, string secret, GlobalSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(settings.ServerDisplayName) ? _appHost.FriendlyName : settings.ServerDisplayName;
        return ShareInviteCodec.Encode(new ShareInvite(settings.PublicServerUrl, name, _appHost.SystemId, share.Id, secret));
    }

    private object ToView(OutgoingShare s)
    {
        var usage = _usage.Ledger.GetEntry(s.UsageKey, s.QuotaInterval);
        return new
        {
            s.Id,
            s.Name,
            s.LibraryIds,
            s.Enabled,
            s.CreatedAt,
            s.ExpiresAt,
            expired = s.ExpiresAt is DateTime e && e <= _time.GetUtcNow().UtcDateTime,
            s.AllowTranscoding,
            s.MaxStreamingBitrateMbps,
            s.MaxBandwidthMbps,
            s.QuotaGB,
            s.QuotaInterval,
            s.MaxConcurrentStreams,
            s.AllowedNetworks,
            s.Notes,
            usage = new
            {
                usedBytes = usage.WindowBytes,
                previousBytes = usage.PreviousWindowBytes,
                lifetimeBytes = usage.LifetimeBytes,
                quotaBytes = Units.GBToBytes(s.QuotaGB),
                windowEnd = usage.WindowEnd,
                lastActivity = usage.LastActivity,
            },
            activeStreams = _streams.Registry.CountForShare(s.Id),
        };
    }

    private static object ToView(IncomingConnection c) => new
    {
        c.Id,
        c.RemoteServerName,
        c.RemoteServerUrl,
        c.Enabled,
        c.PreferTranscode,
        c.PreferredMaxBitrateMbps,
        c.Libraries,
        c.LastSyncAt,
        c.LastSyncStatus,
        c.LastSyncFailed,
    };
}
