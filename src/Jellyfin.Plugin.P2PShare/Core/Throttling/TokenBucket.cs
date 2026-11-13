using System;

namespace Jellyfin.Plugin.P2PShare.Core.Throttling;

/// <summary>
/// A thread-safe token bucket measured in bytes, using a debt model: a reservation is
/// always granted immediately, the balance may go negative, and the caller is told how
/// long to wait until the balance is back to zero. Concurrent readers therefore share
/// the rate fairly.
/// </summary>
public sealed class TokenBucket
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _balance;
    private long _lastTimestamp;

    /// <param name="time">Clock.</param>
    /// <param name="bytesPerSecond">Rate; 0 or less is unlimited.</param>
    /// <param name="burstSeconds">Bucket capacity expressed in seconds of traffic.</param>
    public TokenBucket(TimeProvider time, long bytesPerSecond, double burstSeconds = 1.0)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        BurstSeconds = burstSeconds <= 0 ? 1.0 : burstSeconds;
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _lastTimestamp = _time.GetTimestamp();
        _balance = Capacity;
    }

    /// <summary>Gets the burst size in seconds.</summary>
    public double BurstSeconds { get; }

    /// <summary>Gets the current rate in bytes per second (0 = unlimited).</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _bytesPerSecond;
            }
        }
    }

    /// <summary>Gets a value indicating whether the bucket is unlimited.</summary>
    public bool IsUnlimited => BytesPerSecond <= 0;

    private double Capacity => _bytesPerSecond * BurstSeconds;

    /// <summary>Changes the rate. Takes effect for subsequent reservations.</summary>
    public void SetRate(long bytesPerSecond)
    {
        lock (_lock)
        {
            Refill();
            var newRate = Math.Max(0, bytesPerSecond);
            if (newRate == _bytesPerSecond)
            {
                return;
            }

            _bytesPerSecond = newRate;
            if (newRate == 0)
            {
                _balance = 0;
            }
            else
            {
                _balance = Math.Min(_balance, Capacity);
            }
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> and returns how long the caller must wait before sending them.
    /// </summary>
    public TimeSpan Reserve(long bytes)
    {
        if (bytes <= 0)
        {
            return TimeSpan.Zero;
        }

        lock (_lock)
        {
            if (_bytesPerSecond <= 0)
            {
                return TimeSpan.Zero;
            }

            Refill();
            _balance -= bytes;
            if (_balance >= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(-_balance / _bytesPerSecond);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;
        if (_bytesPerSecond <= 0 || elapsed <= 0)
        {
            return;
        }

        _balance = Math.Min(Capacity, _balance + (elapsed * _bytesPerSecond));
    }
}
