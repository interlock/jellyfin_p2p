using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.P2PShare.Core.Sync;

/// <summary>The difference between the desired files and what the mirror folder holds.</summary>
public sealed record SyncPlan(IReadOnlyList<DesiredFile> ToWrite, IReadOnlyList<string> ToDelete, int Unchanged)
{
    public bool HasChanges => ToWrite.Count > 0 || ToDelete.Count > 0;
}

/// <summary>Result of applying a plan.</summary>
public sealed record SyncResult(int Written, int Deleted, int Unchanged, int Failed, IReadOnlyList<string> Errors)
{
    public bool Changed => Written > 0 || Deleted > 0;
}

/// <summary>
/// Applies desired file sets to a mirror folder, tracking what it owns in a manifest.
/// Only files listed in the manifest are ever deleted.
/// </summary>
public sealed class MirrorSynchronizer
{
    /// <summary>Name of the manifest file kept in each mirror folder.</summary>
    public const string ManifestFileName = ".p2pshare-manifest.json";

    private readonly Func<RemoteImageRef, CancellationToken, Task<byte[]?>> _fetchImage;

    public MirrorSynchronizer(Func<RemoteImageRef, CancellationToken, Task<byte[]?>> fetchImage)
    {
        _fetchImage = fetchImage ?? throw new ArgumentNullException(nameof(fetchImage));
    }

    /// <summary>Computes the plan for <paramref name="desired"/> against the current folder.</summary>
    public static SyncPlan Diff(string root, IReadOnlyCollection<DesiredFile> desired)
    {
        var manifest = LoadManifest(root);
        var toWrite = new List<DesiredFile>();
        var unchanged = 0;
        var desiredPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in desired)
        {
            desiredPaths.Add(file.RelativePath);
            if (manifest.TryGetValue(file.RelativePath, out var fp)
                && fp == file.Fingerprint
                && File.Exists(ResolveSafe(root, file.RelativePath)))
            {
                unchanged++;
            }
            else
            {
                toWrite.Add(file);
            }
        }

        var toDelete = manifest.Keys.Where(p => !desiredPaths.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return new SyncPlan(toWrite, toDelete, unchanged);
    }

    /// <summary>Brings the folder in line with <paramref name="desired"/>.</summary>
    public async Task<SyncResult> SyncAsync(string root, IReadOnlyCollection<DesiredFile> desired, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var manifest = LoadManifest(root);
        var plan = Diff(root, desired);
        var errors = new List<string>();
        int written = 0, deleted = 0;

        foreach (var path in plan.ToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var full = ResolveSafe(root, path);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }

                manifest.Remove(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"delete {path}: {ex.Message}");
            }
        }

        foreach (var file in plan.ToWrite)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[]? bytes = file.Text is not null
                    ? new UTF8Encoding(false).GetBytes(file.Text)
                    : await _fetchImage(file.Image!, cancellationToken).ConfigureAwait(false);

                if (bytes is null)
                {
                    // Image no longer exists remotely; skip without failing the sync.
                    continue;
                }

                var full = ResolveSafe(root, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var temp = full + ".p2ptmp";
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temp, full, overwrite: true);
                manifest[file.RelativePath] = file.Fingerprint;
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"write {file.RelativePath}: {ex.Message}");
            }
        }

        SaveManifest(root, manifest);
        PruneEmptyDirectories(root);
        return new SyncResult(written, deleted, plan.Unchanged, errors.Count, errors);
    }

    /// <summary>Deletes every file the manifest owns, then the folder if it ends up empty.</summary>
    public static void RemoveMirror(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in LoadManifest(root).Keys)
        {
            try
            {
                var full = ResolveSafe(root, path);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Best effort.
            }
        }

        var manifestPath = Path.Combine(root, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        PruneEmptyDirectories(root);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    /// <summary>Combines root and a relative path, refusing anything that escapes the root.</summary>
    public static string ResolveSafe(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Invalid relative path: " + relativePath);
        }

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path escapes mirror root: " + relativePath);
        }

        return full;
    }

    private static Dictionary<string, string> LoadManifest(string root)
    {
        var path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SaveManifest(string root, Dictionary<string, string> manifest)
    {
        var path = Path.Combine(root, ManifestFileName);
        var sorted = manifest.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void PruneEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException)
            {
                // Ignore races with Jellyfin's scanner.
            }
        }
    }
}
