using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Jellyfin.Plugin.P2PShare.Core.Throttling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Sharing side: writes a media stream for a peer to the HTTP response, applying
/// policy, concurrency limits, bandwidth throttling and quota metering.
/// </summary>
public sealed class PeerStreamService
{
    private readonly UsageService _usage;
    private readonly BandwidthManager _bandwidth;
    private readonly ActiveStreamRegistry _registry;
    private readonly ThrottledCopier _copier;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<PeerStreamService> _logger;

    public PeerStreamService(
        UsageService usage,
        BandwidthManager bandwidth,
        ActiveStreamRegistry registry,
        ThrottledCopier copier,
        IMediaEncoder mediaEncoder,
        ILogger<PeerStreamService> logger)
    {
        _usage = usage;
        _bandwidth = bandwidth;
        _registry = registry;
        _copier = copier;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Parameters of a peer stream request.</summary>
    public sealed record StreamParameters(bool Transcode, long MaxBitrate, long StartTimeTicks);

    /// <summary>Gets the running streams.</summary>
    public ActiveStreamRegistry Registry => _registry;

    /// <summary>Serves <paramref name="item"/>. Returns an error (status, message) or null when the stream was written.</summary>
    public async Task<(int Status, string Message)?> ServeAsync(
        HttpContext context,
        OutgoingShare share,
        GlobalSettings settings,
        BaseItem item,
        string path,
        StreamParameters parameters,
        CancellationToken cancellationToken)
    {
        var decision = StreamPolicy.Decide(
            new StreamRequest(parameters.Transcode, parameters.MaxBitrate, CatalogService.EstimateBitrate(item)),
            share,
            settings,
            _usage.Ledger);
        if (!decision.Allowed)
        {
            return (decision.StatusCode, decision.Message);
        }

        var isTranscode = decision.Mode == StreamMode.Transcode;
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var failure = _registry.TryBegin(share, settings, isTranscode, item.Id, item.Name ?? string.Empty, remote, out var lease);
        if (failure != LeaseFailure.None || lease is null)
        {
            return (StatusCodes.Status429TooManyRequests, ActiveStreamRegistry.Describe(failure));
        }

        using (lease)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var buckets = _bandwidth.GetBuckets(share, settings);
            var meter = new QuotaMeter(_usage.Ledger, share, settings, lease);

            try
            {
                if (isTranscode)
                {
                    await TranscodeAsync(context, item, path, decision.TargetBitrate, parameters.StartTimeTicks, buckets, meter, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    return await DirectAsync(context, path, buckets, meter, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Peer disconnected.
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Peer stream for share {Share} ended with an I/O error", share.Name);
            }

            if (meter.StoppedBecause != DenyReason.None)
            {
                _logger.LogInformation("Stopped peer stream for share {Share}: {Reason}", share.Name, meter.StoppedBecause);
            }
        }

        return null;
    }

    private async Task<(int Status, string Message)?> DirectAsync(HttpContext context, string path, System.Collections.Generic.IReadOnlyList<TokenBucket> buckets, QuotaMeter meter, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            var total = file.Length;
            var response = context.Response;
            response.Headers.AcceptRanges = "bytes";
            response.ContentType = MimeTypes.GetMimeType(path);

            var rangeResult = ByteRange.TryParse(context.Request.Headers.Range.ToString(), total, out var range);
            if (rangeResult == RangeParseResult.NotSatisfiable)
            {
                response.Headers.ContentRange = "bytes */" + total.ToString(CultureInfo.InvariantCulture);
                return (StatusCodes.Status416RangeNotSatisfiable, "Requested range not satisfiable.");
            }

            long start = 0;
            long length = total;
            if (rangeResult == RangeParseResult.Satisfiable)
            {
                start = range.Start;
                length = range.Length;
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = range.ToContentRange(total);
            }
            else
            {
                response.StatusCode = StatusCodes.Status200OK;
            }

            response.ContentLength = length;
            if (HttpMethods.IsHead(context.Request.Method))
            {
                return null;
            }

            file.Seek(start, SeekOrigin.Begin);
            await _copier.CopyAsync(file, response.Body, buckets, length, meter.OnChunk, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async Task TranscodeAsync(HttpContext context, BaseItem item, string path, long bitrate, long startTimeTicks, System.Collections.Generic.IReadOnlyList<TokenBucket> buckets, QuotaMeter meter, CancellationToken cancellationToken)
    {
        var isAudio = item is Audio;
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = isAudio ? "audio/aac" : "video/mp2t";
        response.Headers.AcceptRanges = "none";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in TranscodeArguments.Build(path, bitrate, startTimeTicks, isAudio))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogDebug("ffmpeg (peer transcode): {Line}", e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        try
        {
            await _copier.CopyAsync(process.StandardOutput.BaseStream, response.Body, buckets, null, meter.OnChunk, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }
}
