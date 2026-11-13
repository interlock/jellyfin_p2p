using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.P2PShare.Core.Sharing;

namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// Editable fields of a share, as sent by the admin UI.
/// </summary>
public class ShareEdit
{
    public string Name { get; set; } = string.Empty;

    public List<Guid> LibraryIds { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public DateTime? ExpiresAt { get; set; }

    public bool AllowTranscoding { get; set; }

    public double MaxStreamingBitrateMbps { get; set; }

    public double MaxBandwidthMbps { get; set; }

    public double QuotaGB { get; set; }

    public QuotaInterval QuotaInterval { get; set; } = QuotaInterval.Monthly;

    public int MaxConcurrentStreams { get; set; } = 2;

    public List<string> AllowedNetworks { get; set; } = new();

    public string Notes { get; set; } = string.Empty;

    /// <summary>Copies the edit onto a share.</summary>
    public void ApplyTo(OutgoingShare share)
    {
        share.Name = Name.Trim();
        share.LibraryIds = LibraryIds.Distinct().ToList();
        share.Enabled = Enabled;
        share.ExpiresAt = ExpiresAt?.ToUniversalTime();
        share.AllowTranscoding = AllowTranscoding;
        share.MaxStreamingBitrateMbps = MaxStreamingBitrateMbps;
        share.MaxBandwidthMbps = MaxBandwidthMbps;
        share.QuotaGB = QuotaGB;
        share.QuotaInterval = QuotaInterval;
        share.MaxConcurrentStreams = MaxConcurrentStreams;
        share.AllowedNetworks = AllowedNetworks.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        share.Notes = Notes ?? string.Empty;
    }
}

/// <summary>
/// Validation of admin input. Each method returns a list of human-readable problems (empty = valid).
/// </summary>
public static class Validation
{
    public static List<string> ValidateShare(ShareEdit edit, ISet<Guid> knownLibraryIds, DateTimeOffset now)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            errors.Add("Name is required.");
        }
        else if (edit.Name.Length > 100)
        {
            errors.Add("Name must be 100 characters or fewer.");
        }

        if (edit.LibraryIds.Count == 0)
        {
            errors.Add("Select at least one library.");
        }

        foreach (var id in edit.LibraryIds.Where(id => !knownLibraryIds.Contains(id)))
        {
            errors.Add($"Library {id} does not exist or cannot be shared.");
        }

        NonNegative(errors, edit.MaxStreamingBitrateMbps, "Max streaming bitrate");
        NonNegative(errors, edit.MaxBandwidthMbps, "Max bandwidth");
        NonNegative(errors, edit.QuotaGB, "Quota");
        if (edit.MaxConcurrentStreams < 0)
        {
            errors.Add("Max concurrent streams cannot be negative.");
        }

        if (!Enum.IsDefined(edit.QuotaInterval))
        {
            errors.Add("Unknown quota interval.");
        }

        if (edit.ExpiresAt is DateTime expires && expires.ToUniversalTime() <= now.UtcDateTime)
        {
            errors.Add("Expiry must be in the future.");
        }

        foreach (var network in edit.AllowedNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (!ShareAuthorizer.TryParseNetwork(network, out _))
            {
                errors.Add($"'{network}' is not a valid IP address or CIDR range.");
            }
        }

        return errors;
    }

    public static List<string> ValidateSettings(GlobalSettings settings)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.PublicServerUrl))
        {
            if (!UrlHelper.TryNormalizeServerUrl(settings.PublicServerUrl, out var normalized))
            {
                errors.Add("Public server URL must be an absolute http(s) URL.");
            }
            else if (settings.RequireHttps && !UrlHelper.IsHttps(normalized))
            {
                errors.Add("Public server URL must use https when 'Require HTTPS' is on.");
            }
        }

        if (!UrlHelper.TryNormalizeServerUrl(settings.LocalProxyBaseUrl, out _))
        {
            errors.Add("Local proxy base URL must be an absolute http(s) URL (e.g. http://127.0.0.1:8096).");
        }

        NonNegative(errors, settings.GlobalMaxBandwidthMbps, "Global max bandwidth");
        NonNegative(errors, settings.GlobalQuotaGB, "Global quota");
        if (settings.GlobalMaxConcurrentStreams < 0 || settings.MaxConcurrentTranscodes < 0 || settings.DefaultTokenLifetimeDays < 0)
        {
            errors.Add("Limits cannot be negative.");
        }

        if (settings.SyncIntervalHours < 1 || settings.SyncIntervalHours > 24 * 7)
        {
            errors.Add("Sync interval must be between 1 and 168 hours.");
        }

        if (!Enum.IsDefined(settings.GlobalQuotaInterval))
        {
            errors.Add("Unknown quota interval.");
        }

        if (string.IsNullOrWhiteSpace(settings.LibraryNameTemplate) || !settings.LibraryNameTemplate.Contains("{library}", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Library name template must contain {library}.");
        }

        return errors;
    }

    /// <summary>Normalizes URLs in place (call after successful validation).</summary>
    public static void Normalize(GlobalSettings settings)
    {
        if (UrlHelper.TryNormalizeServerUrl(settings.PublicServerUrl, out var pub))
        {
            settings.PublicServerUrl = pub;
        }

        if (UrlHelper.TryNormalizeServerUrl(settings.LocalProxyBaseUrl, out var local))
        {
            settings.LocalProxyBaseUrl = local;
        }

        settings.ServerDisplayName = settings.ServerDisplayName?.Trim() ?? string.Empty;
        settings.MirrorRootPath = settings.MirrorRootPath?.Trim() ?? string.Empty;
    }

    private static void NonNegative(List<string> errors, double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            errors.Add($"{label} cannot be negative.");
        }
    }
}
