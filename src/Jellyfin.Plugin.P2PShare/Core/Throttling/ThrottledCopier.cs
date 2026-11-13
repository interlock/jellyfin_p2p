using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.P2PShare.Core.Throttling;

/// <summary>Why a throttled copy ended.</summary>
public enum CopyStopReason
{
    /// <summary>The source was fully copied (or <c>maxBytes</c> reached).</summary>
    Completed,

    /// <summary>The metering callback asked to stop (e.g. quota exhausted).</summary>
    LimitReached,
}

/// <summary>Result of a throttled copy.</summary>
public readonly record struct CopyResult(long BytesCopied, CopyStopReason StopReason);

/// <summary>
/// Copies a stream through one or more token buckets, metering every chunk.
/// </summary>
public sealed class ThrottledCopier
{
    /// <summary>Largest chunk size.</summary>
    public const int MaxChunk = 64 * 1024;

    /// <summary>Smallest chunk size.</summary>
    public const int MinChunk = 4 * 1024;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Delay function; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Tests inject a fake clock.</param>
    public ThrottledCopier(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Chooses a chunk size of roughly 1/10 s of traffic at the slowest limited rate.</summary>
    public static int ChooseChunkSize(IReadOnlyList<TokenBucket> buckets)
    {
        long slowest = 0;
        foreach (var b in buckets)
        {
            var rate = b.BytesPerSecond;
            if (rate > 0 && (slowest == 0 || rate < slowest))
            {
                slowest = rate;
            }
        }

        if (slowest == 0)
        {
            return MaxChunk;
        }

        return (int)Math.Clamp(slowest / 10, MinChunk, MaxChunk);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">Source stream.</param>
    /// <param name="destination">Destination stream.</param>
    /// <param name="buckets">Buckets every chunk must pass through.</param>
    /// <param name="maxBytes">Maximum bytes to copy, or null for the whole stream.</param>
    /// <param name="onChunk">Called after each chunk is written with its size; return false to stop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<CopyResult> CopyAsync(
        Stream source,
        Stream destination,
        IReadOnlyList<TokenBucket> buckets,
        long? maxBytes,
        Func<int, bool>? onChunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        buckets ??= Array.Empty<TokenBucket>();

        var buffer = ArrayPool<byte>.Shared.Rent(MaxChunk);
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = ChooseChunkSize(buckets);
                if (maxBytes.HasValue)
                {
                    var remaining = maxBytes.Value - total;
                    if (remaining <= 0)
                    {
                        return new CopyResult(total, CopyStopReason.Completed);
                    }

                    chunk = (int)Math.Min(chunk, remaining);
                }

                var read = await source.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new CopyResult(total, CopyStopReason.Completed);
                }

                var wait = TimeSpan.Zero;
                foreach (var bucket in buckets)
                {
                    var w = bucket.Reserve(read);
                    if (w > wait)
                    {
                        wait = w;
                    }
                }

                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;

                if (onChunk is not null && !onChunk(read))
                {
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return new CopyResult(total, CopyStopReason.LimitReached);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
