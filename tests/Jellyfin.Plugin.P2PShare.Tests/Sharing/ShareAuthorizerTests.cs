using System;
using System.Collections.Generic;
using System.Net;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sharing;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Sharing;

public class ShareAuthorizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _secret = ShareSecrets.NewSecret();
    private readonly OutgoingShare _share;
    private readonly GlobalSettings _settings = new();

    public ShareAuthorizerTests()
    {
        _share = new OutgoingShare { Name = "Bob", SecretHash = ShareSecrets.Hash(_secret) };
    }

    private AuthResult Auth(string? header, IPAddress? ip = null)
        => ShareAuthorizer.Authorize(header, new List<OutgoingShare> { new() { SecretHash = "x" }, _share }, _settings, ip ?? IPAddress.Loopback, Now);

    private string Header => ShareCredential.Format(_share.Id, _secret);

    [Fact]
    public void ValidCredential_Succeeds()
    {
        var result = Auth(Header);
        Assert.True(result.Succeeded);
        Assert.Same(_share, result.Share);
    }

    [Fact]
    public void SharingDisabled_Returns503()
    {
        _settings.SharingEnabled = false;
        var result = Auth(Header);
        Assert.Equal(AuthFailure.SharingDisabled, result.Failure);
        Assert.Equal(503, result.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void MissingOrMalformed_Returns401(string? header)
    {
        var result = Auth(header);
        Assert.Equal(AuthFailure.MissingCredentials, result.Failure);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public void WrongSecret_AndUnknownShare_AreIndistinguishable()
    {
        var wrongSecret = Auth(ShareCredential.Format(_share.Id, ShareSecrets.NewSecret()));
        var unknownShare = Auth(ShareCredential.Format(Guid.NewGuid(), _secret));

        Assert.Equal(AuthFailure.InvalidCredentials, wrongSecret.Failure);
        Assert.Equal(AuthFailure.InvalidCredentials, unknownShare.Failure);
        Assert.Equal(wrongSecret.Message, unknownShare.Message);
        Assert.Null(wrongSecret.Share);
    }

    [Fact]
    public void DisabledShare_Returns403()
    {
        _share.Enabled = false;
        Assert.Equal(AuthFailure.ShareDisabled, Auth(Header).Failure);
        Assert.Equal(403, Auth(Header).StatusCode);
    }

    [Fact]
    public void ExpiredShare_Returns403()
    {
        _share.ExpiresAt = Now.UtcDateTime.AddSeconds(-1);
        Assert.Equal(AuthFailure.ShareExpired, Auth(Header).Failure);

        _share.ExpiresAt = Now.UtcDateTime.AddDays(1);
        Assert.True(Auth(Header).Succeeded);
    }

    [Fact]
    public void AllowList_IsEnforced()
    {
        _share.AllowedNetworks = new List<string> { "203.0.113.0/24", "2001:db8::/32" };

        Assert.True(Auth(Header, IPAddress.Parse("203.0.113.77")).Succeeded);
        Assert.True(Auth(Header, IPAddress.Parse("2001:db8::1")).Succeeded);
        Assert.True(Auth(Header, IPAddress.Parse("::ffff:203.0.113.5")).Succeeded); // IPv4-mapped
        Assert.Equal(AuthFailure.AddressNotAllowed, Auth(Header, IPAddress.Parse("198.51.100.1")).Failure);
    }

    [Fact]
    public void AllowList_SingleAddressEntry()
    {
        _share.AllowedNetworks = new List<string> { "198.51.100.7" };
        Assert.True(Auth(Header, IPAddress.Parse("198.51.100.7")).Succeeded);
        Assert.False(Auth(Header, IPAddress.Parse("198.51.100.8")).Succeeded);
    }

    [Fact]
    public void AllowList_OnlyInvalidEntries_DoesNotLockOut()
    {
        _share.AllowedNetworks = new List<string> { "not-a-network", "" };
        Assert.True(Auth(Header, IPAddress.Parse("198.51.100.1")).Succeeded);
    }
}
