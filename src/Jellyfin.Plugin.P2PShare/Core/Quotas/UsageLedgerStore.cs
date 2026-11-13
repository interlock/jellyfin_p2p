using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.P2PShare.Core.Quotas;

/// <summary>
/// Saves and loads the usage ledger as JSON, writing atomically.
/// </summary>
public sealed class UsageLedgerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _ioLock = new();

    public UsageLedgerStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path => _path;

    /// <summary>Loads the ledger file into <paramref name="ledger"/>. Missing or corrupt files start empty.</summary>
    /// <returns>True if a file was loaded.</returns>
    public bool LoadInto(UsageLedger ledger)
    {
        lock (_ioLock)
        {
            if (!File.Exists(_path))
            {
                ledger.Load(null);
                return false;
            }

            try
            {
                var entries = JsonSerializer.Deserialize<List<UsageEntry>>(File.ReadAllText(_path), JsonOptions);
                ledger.Load(entries);
                return true;
            }
            catch (JsonException)
            {
                ledger.Load(null);
                return false;
            }
        }
    }

    /// <summary>Saves the ledger if it has changes (or always when <paramref name="force"/>).</summary>
    /// <returns>True if written.</returns>
    public bool Save(UsageLedger ledger, bool force = false)
    {
        if (!force && !ledger.IsDirty)
        {
            return false;
        }

        lock (_ioLock)
        {
            var snapshot = ledger.Snapshot();
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
    }
}
