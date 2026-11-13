using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Sync;

namespace Jellyfin.Plugin.P2PShare.Tests.Sync;

/// <summary>In-memory peer used by sync tests.</summary>
public sealed class FakeRemote : IRemoteShareClient
{
    public RemoteServerInfo Info { get; set; } = new() { ServerName = "Alice" };

    public List<RemoteLibrary> Libraries { get; } = new();

    public Dictionary<Guid, List<RemoteItem>> Items { get; } = new();

    public Exception? Failure { get; set; }

    public int ImageRequests { get; private set; }

    public int PageRequests { get; private set; }

    public Task<RemoteServerInfo> GetInfoAsync(CancellationToken cancellationToken)
        => Failure is not null ? Task.FromException<RemoteServerInfo>(Failure) : Task.FromResult(Info);

    public Task<IReadOnlyList<RemoteLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RemoteLibrary>>(Libraries.ToList());

    public Task<RemoteItemPage> GetItemsAsync(Guid libraryId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        PageRequests++;
        var all = Items.TryGetValue(libraryId, out var list) ? list : new List<RemoteItem>();
        return Task.FromResult(new RemoteItemPage { Items = all.Skip(startIndex).Take(limit).ToList(), StartIndex = startIndex, TotalRecordCount = all.Count });
    }

    public Task<byte[]?> GetImageAsync(Guid itemId, string imageType, CancellationToken cancellationToken)
    {
        ImageRequests++;
        return Task.FromResult<byte[]?>(Encoding.ASCII.GetBytes($"{itemId:N}-{imageType}"));
    }

    public static RemoteItem Movie(string name, int year, string? tmdb = null)
    {
        var m = new RemoteItem { Id = Guid.NewGuid(), Type = RemoteItemTypes.Movie, Name = name, ProductionYear = year, Bitrate = 5_000_000 };
        if (tmdb is not null)
        {
            m.ProviderIds["Tmdb"] = tmdb;
        }

        m.Images.Add(new RemoteImage { Type = "Primary", Tag = "p1", Extension = ".jpg" });
        return m;
    }

    public static (RemoteItem Series, List<RemoteItem> Episodes) Series(string name, int year, int seasons, int episodesPerSeason)
    {
        var s = new RemoteItem { Id = Guid.NewGuid(), Type = RemoteItemTypes.Series, Name = name, ProductionYear = year };
        s.ProviderIds["Tvdb"] = "1234";
        var eps = new List<RemoteItem>();
        for (var se = 1; se <= seasons; se++)
        {
            for (var e = 1; e <= episodesPerSeason; e++)
            {
                eps.Add(new RemoteItem
                {
                    Id = Guid.NewGuid(),
                    Type = RemoteItemTypes.Episode,
                    Name = $"Ep {e}",
                    SeriesId = s.Id,
                    SeriesName = name,
                    ParentIndexNumber = se,
                    IndexNumber = e,
                });
            }
        }

        return (s, eps);
    }

    public static HttpStatusCode Unused => HttpStatusCode.OK;
}
