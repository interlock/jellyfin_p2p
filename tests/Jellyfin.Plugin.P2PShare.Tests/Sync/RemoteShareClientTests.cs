using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sync;

public class RemoteShareClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static readonly ShareInvite Invite = new("https://peer.example/jf", "Peer", "sid", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), "SECRET");

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, RemoteJson.Options), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendsCredentialHeader_ToCorrectUrl()
    {
        var handler = new StubHandler { Respond = _ => Json(new List<RemoteLibrary> { new() { Name = "Movies", CollectionType = "movies" } }) };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);

        var libs = await client.GetLibrariesAsync(CancellationToken.None);

        Assert.Equal("Movies", Assert.Single(libs).Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://peer.example/jf/P2PShare/Remote/Libraries", request.RequestUri!.ToString());
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e.SECRET", request.Headers.GetValues(ShareCredential.HeaderName).Single());
    }

    [Fact]
    public async Task ErrorBody_IsSurfaced_WithStatus()
    {
        var handler = new StubHandler { Respond = _ => Json(new RemoteError { Error = "This share has expired." }, HttpStatusCode.Forbidden) };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);

        var ex = await Assert.ThrowsAsync<RemoteShareException>(() => client.GetInfoAsync(CancellationToken.None));
        Assert.Equal("This share has expired.", ex.Message);
        Assert.True(ex.IsAccessDenied);
    }

    [Fact]
    public async Task NotFound_WithoutBody_ExplainsPluginMissing()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>nope</html>") } };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);

        var ex = await Assert.ThrowsAsync<RemoteShareException>(() => client.GetInfoAsync(CancellationToken.None));
        Assert.Contains("plugin", ex.Message);
        Assert.False(ex.IsAccessDenied);
    }

    [Fact]
    public async Task NetworkFailure_IsWrapped()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused") };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);
        var ex = await Assert.ThrowsAsync<RemoteShareException>(() => client.GetInfoAsync(CancellationToken.None));
        Assert.Contains("connection refused", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Image_404_ReturnsNull()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);
        Assert.Null(await client.GetImageAsync(Guid.NewGuid(), "Primary", CancellationToken.None));
    }

    [Fact]
    public async Task OpenStream_ForwardsRangeAndQuery()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.PartialContent) };
        var client = new RemoteShareClient(new HttpClient(handler), Invite);
        var id = Guid.NewGuid();

        using var response = await client.OpenStreamAsync(id, "transcode=true&maxBitrate=4000000", "bytes=100-", CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://peer.example/jf/P2PShare/Remote/Items/{id:N}/Stream?transcode=true&maxBitrate=4000000", request.RequestUri!.ToString());
        Assert.Equal("bytes=100-", request.Headers.GetValues("Range").Single());
    }

    [Fact]
    public void Json_RoundTripsEnumsAsStrings()
    {
        var info = new RemoteServerInfo { Share = new RemoteSharePolicy { QuotaInterval = Core.Model.QuotaInterval.Weekly } };
        var json = JsonSerializer.Serialize(info, RemoteJson.Options);
        Assert.Contains("\"quotaInterval\":\"Weekly\"", json);
        Assert.Equal(Core.Model.QuotaInterval.Weekly, JsonSerializer.Deserialize<RemoteServerInfo>(json, RemoteJson.Options)!.Share.QuotaInterval);
    }
}
