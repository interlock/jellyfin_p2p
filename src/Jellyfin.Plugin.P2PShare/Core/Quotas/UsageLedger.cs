using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.P2PShare.Core.Model;

namespace Jellyfin.Plugin.P2PShare.Core.Quotas;

/// <summary>
/// Persistable usage counters for one key.
/// </summary>
public class UsageEntry
{
    public string Key { get; set; } = string.Empty;

    public QuotaInterval Interval { get; set; }

    public DateTimeOffset WindowStart { get; set; }

    public DateTimeOffset WindowEnd { get; set; }

    /// <summary>Gets or sets bytes used in the current window.</summary>
    public long WindowBytes { get; set; }

    /// <summary>Gets or sets bytes used in the previous window (for display).</summary>
    public long PreviousWindowBytes { get; set; }

    /// <summary>Gets or sets bytes used since the key was first seen.</summary>
    public long LifetimeBytes { get; set; }

    /// <summary>Gets or sets when the key last served data.</summary>
    public DateTimeOffset? LastActivity { get; set; }

    public UsageEntry Clone() => (UsageEntry)MemberwiseClone();
}

/// <summary>
/// Thread-safe usage counters with calendar windows.
/// </summary>
public sealed class UsageLedger
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, UsageEntry> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    public UsageLedger(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Gets a value indicating whether there are unsaved changes.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_lock)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Adds bytes to a key, rolling its window if needed.</summary>
    public void Add(string key, QuotaInterval interval, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_lock)
        {
            var entry = GetCurrentLocked(key, interval);
            entry.WindowBytes += bytes;
            entry.LifetimeBytes += bytes;
            entry.LastActivity = _time.GetUtcNow();
            _dirty = true;
        }
    }

    /// <summary>Returns the bytes used by a key in its current window.</summary>
    public long GetWindowUsage(string key, QuotaInterval interval)
    {
        lock (_lock)
        {
            return GetCurrentLocked(key, interval).WindowBytes;
        }
    }

    /// <summary>Returns the remaining bytes for a quota (null = unlimited).</summary>
    public long? GetRemaining(string key, QuotaInterval interval, long quotaBytes)
    {
        if (quotaBytes <= 0)
        {
            return null;
        }

        return Math.Max(0, quotaBytes - GetWindowUsage(key, interval));
    }

    /// <summary>Returns true if the quota is exhausted. A quota of 0 is unlimited.</summary>
    public bool IsExhausted(string key, QuotaInterval interval, long quotaBytes)
        => GetRemaining(key, interval, quotaBytes) is long remaining && remaining <= 0;

    /// <summary>Returns a copy of a key's entry with its window rolled to now.</summary>
    public UsageEntry GetEntry(string key, QuotaInterval interval)
    {
        lock (_lock)
        {
            return GetCurrentLocked(key, interval).Clone();
        }
    }

    /// <summary>Removes a key (e.g. when a share is deleted).</summary>
    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_entries.Remove(key))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>Returns a snapshot of all entries for persistence and clears the dirty flag.</summary>
    public List<UsageEntry> Snapshot()
    {
        lock (_lock)
        {
            _dirty = false;
            return _entries.Values.Select(e => e.Clone()).ToList();
        }
    }

    /// <summary>Loads persisted entries, replacing current state.</summary>
    public void Load(IEnumerable<UsageEntry>? entries)
    {
        lock (_lock)
        {
            _entries.Clear();
            if (entries is not null)
            {
                foreach (var e in entries)
                {
                    if (!string.IsNullOrEmpty(e.Key))
                    {
                        _entries[e.Key] = e.Clone();
                    }
                }
            }

            _dirty = false;
        }
    }

    private UsageEntry GetCurrentLocked(string key, QuotaInterval interval)
    {
        var now = _time.GetUtcNow();
        if (!_entries.TryGetValue(key, out var entry))
        {
            var window = QuotaWindow.For(interval, now);
            entry = new UsageEntry { Key = key, Interval = interval, WindowStart = window.Start, WindowEnd = window.End };
            _entries[key] = entry;
            return entry;
        }

        if (entry.Interval != interval)
        {
            // Interval changed by the admin: start a fresh window of the new kind, keep lifetime totals.
            var window = QuotaWindow.For(interval, now);
            entry.PreviousWindowBytes = entry.WindowBytes;
            entry.WindowBytes = 0;
            entry.Interval = interval;
            entry.WindowStart = window.Start;
            entry.WindowEnd = window.End;
            _dirty = true;
        }
        else if (now >= entry.WindowEnd || now < entry.WindowStart)
        {
            var window = QuotaWindow.For(interval, now);
            // Only the immediately preceding window counts as "previous".
            entry.PreviousWindowBytes = window.Start == entry.WindowEnd ? entry.WindowBytes : 0;
            entry.WindowBytes = 0;
            entry.WindowStart = window.Start;
            entry.WindowEnd = window.End;
            _dirty = true;
        }

        return entry;
    }
}
