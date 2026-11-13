using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.P2PShare.Tests;

/// <summary>
/// A controllable clock for deterministic tests.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }

    public void Set(DateTimeOffset value)
    {
        lock (_lock)
        {
            _now = value;
        }
    }

    /// <summary>A delay function that advances this clock instead of sleeping.</summary>
    public Task FakeDelay(TimeSpan by, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance(by);
        return Task.CompletedTask;
    }
}
