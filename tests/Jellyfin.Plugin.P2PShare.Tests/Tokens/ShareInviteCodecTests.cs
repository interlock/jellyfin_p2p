using System;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Tokens;

public class ShareInviteCodecTests
{
    private static ShareInvite Sample() => new(
        "https://media.example.com/",
        "Alice's Jellyfin",
        "abc123",
        Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        ShareSecrets.NewSecret());

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var invite = Sample();
        var token = ShareInviteCodec.Encode(invite);

        Assert.StartsWith("jfp2p1.", token);
        Assert.True(ShareInviteCodec.TryDecode(token, out var decoded, out var error), error);
        Assert.NotNull(decoded);
        Assert.Equal("https://media.example.com", decoded!.ServerUrl); // trailing slash normalized
        Assert.Equal(invite.ServerName, decoded.ServerName);
        Assert.Equal(invite.ServerId, decoded.ServerId);
        Assert.Equal(invite.ShareId, decoded.ShareId);
        Assert.Equal(invite.Secret, decoded.Secret);
    }

    [Fact]
    public void Decode_ToleratesWhitespaceFromCopyPaste()
    {
        var token = ShareInviteCodec.Encode(Sample());
        var mangled = "  " + token.Insert(20, "\n").Insert(10, " ") + "\r\n";

        Assert.True(ShareInviteCodec.TryDecode(mangled, out var decoded, out _));
        Assert.NotNull(decoded);
    }

    [Fact]
    public void Decode_RejectsTruncatedToken()
    {
        var token = ShareInviteCodec.Encode(Sample());
        Assert.False(ShareInviteCodec.TryDecode(token[..^3], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_RejectsTamperedPayload()
    {
        var token = ShareInviteCodec.Encode(Sample());
        var parts = token.Split('.');
        var chars = parts[1].ToCharArray();
        chars[5] = chars[5] == 'A' ? 'B' : 'A';
        var tampered = parts[0] + "." + new string(chars) + "." + parts[2];

        Assert.False(ShareInviteCodec.TryDecode(tampered, out _, out var error));
        Assert.Contains("checksum", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("jfp2p2.abc.def")]
    [InlineData("jfp2p1.abc")]
    [InlineData("jfp2p1.!!!.abcdefgh")]
    public void Decode_RejectsGarbage(string token)
    {
        Assert.False(ShareInviteCodec.TryDecode(token, out var invite, out var error));
        Assert.Null(invite);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void Encode_RejectsInvalidUrls(string url)
    {
        var invite = Sample() with { ServerUrl = url };
        Assert.Throws<ArgumentException>(() => ShareInviteCodec.Encode(invite));
    }

    [Fact]
    public void Encode_KeepsPathPrefixForReverseProxies()
    {
        var invite = Sample() with { ServerUrl = "https://example.com/jellyfin/" };
        Assert.True(ShareInviteCodec.TryDecode(ShareInviteCodec.Encode(invite), out var decoded, out _));
        Assert.Equal("https://example.com/jellyfin", decoded!.ServerUrl);
    }
}
