using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Receiving side: creates and removes the Jellyfin libraries that point at mirror folders.
/// </summary>
public sealed class LibraryRegistrar
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryRegistrar> _logger;

    public LibraryRegistrar(ILibraryManager libraryManager, ILogger<LibraryRegistrar> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Maps a peer collection type string to Jellyfin's enum.</summary>
    public static CollectionTypeOptions? ParseCollectionType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<CollectionTypeOptions>(value, ignoreCase: true, out var parsed) && parsed != CollectionTypeOptions.boxsets
            ? parsed
            : null;
    }

    /// <summary>Library options for mirror libraries: nothing that would read whole remote files during scans.</summary>
    public static LibraryOptions CreateOptions(string path) => new()
    {
        PathInfos = new[] { new MediaPathInfo(path) },
        EnableRealtimeMonitor = false,
        EnableChapterImageExtraction = false,
        ExtractChapterImagesDuringLibraryScan = false,
        EnableTrickplayImageExtraction = false,
        ExtractTrickplayImagesDuringLibraryScan = false,
        EnableLUFSScan = false,
        EnablePhotos = false,
        SaveLocalMetadata = false,
    };

    /// <summary>Returns true if a library for <paramref name="path"/> exists.</summary>
    public bool Exists(string path) => FindByPath(path) is not null;

    /// <summary>Ensures a library pointing at <paramref name="path"/> exists. Returns true if one was created.</summary>
    public async Task<bool> EnsureAsync(string name, string? collectionType, string path)
    {
        if (FindByPath(path) is not null)
        {
            return false;
        }

        Directory.CreateDirectory(path);
        var existingNames = _libraryManager.GetVirtualFolders().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalName = SafeLibraryName(name);
        for (var i = 2; existingNames.Contains(finalName); i++)
        {
            finalName = SafeLibraryName(name) + " " + i;
        }

        _logger.LogInformation("Creating P2P Share library {Name} at {Path}", finalName, path);
        await _libraryManager.AddVirtualFolder(finalName, ParseCollectionType(collectionType), CreateOptions(path), refreshLibrary: false).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes the library pointing at <paramref name="path"/>, if any.</summary>
    public async Task<bool> RemoveAsync(string path)
    {
        var folder = FindByPath(path);
        if (folder is null)
        {
            return false;
        }

        _logger.LogInformation("Removing P2P Share library {Name}", folder.Name);
        await _libraryManager.RemoveVirtualFolder(folder.Name, refreshLibrary: false).ConfigureAwait(false);
        return true;
    }

    /// <summary>Queues a library scan.</summary>
    public void QueueScan() => _libraryManager.QueueLibraryScan();

    private VirtualFolderInfo? FindByPath(string path)
    {
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => f.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.Ordinal)));
    }

    private static string SafeLibraryName(string name)
    {
        // Library names become folder names in Jellyfin's root; strip characters that are invalid there.
        var safe = Core.Sync.FileNameSanitizer.Sanitize(name, "Shared Library");
        return safe.Trim();
    }
}
