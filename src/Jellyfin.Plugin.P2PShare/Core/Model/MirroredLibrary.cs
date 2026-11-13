using System;

namespace Jellyfin.Plugin.P2PShare.Core.Model;

/// <summary>
/// A remote library and its local mirror.
/// </summary>
public class MirroredLibrary
{
    /// <summary>Gets or sets the library id on the peer.</summary>
    public Guid RemoteLibraryId { get; set; }

    /// <summary>Gets or sets the library name on the peer.</summary>
    public string RemoteName { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type on the peer (movies, tvshows, music, ...).</summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the library is mirrored locally.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the local library name. Empty uses the global template.</summary>
    public string LocalName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer still offers this library.</summary>
    public bool AvailableRemotely { get; set; } = true;

    /// <summary>Gets or sets the number of items written in the last sync.</summary>
    public int ItemCount { get; set; }
}
