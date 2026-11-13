using System;
using System.Collections.Generic;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Tokens;

public class ShareSecretsTests
{
    [Fact]
    public void NewSecret_IsUniqueAndBase64Url()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var s = ShareSecrets.NewSecret();
            Assert.True(seen.Add(s));
            Assert.True(Base64Url.TryDecode(s, out var bytes));
            Assert.Equal(ShareSecrets.SecretBytes, bytes.Length);
        }
    }

    [Fact]
    public void Verify_AcceptsCorrectSecret_RejectsOthers()
    {
        var secret = ShareSecrets.NewSecret();
        var hash = ShareSecrets.Hash(secret);

        Assert.True(ShareSecrets.Verify(secret, hash));
        Assert.True(ShareSecrets.Verify(secret, hash.ToUpperInvariant()));
        Assert.False(ShareSecrets.Verify(ShareSecrets.NewSecret(), hash));
        Assert.False(ShareSecrets.Verify(string.Empty, hash));
        Assert.False(ShareSecrets.Verify(null, hash));
        Assert.False(ShareSecrets.Verify(secret, null));
    }

    [Fact]
    public void Hash_DoesNotContainSecret()
    {
        var secret = ShareSecrets.NewSecret();
        Assert.DoesNotContain(secret, ShareSecrets.Hash(secret));
        Assert.Equal(64, ShareSecrets.Hash(secret).Length);
    }

    [Fact]
    public void Credential_FormatAndParse_RoundTrip()
    {
        var id = Guid.NewGuid();
        var header = ShareCredential.Format(id, "s3cr.et"); // secret may contain dots after the first

        Assert.True(ShareCredential.TryParse(header, out var parsedId, out var secret));
        Assert.Equal(id, parsedId);
        Assert.Equal("s3cr.et", secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("not-a-guid.secret")]
    [InlineData(".secret")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e.")]
    public void Credential_Parse_RejectsMalformed(string? value)
    {
        Assert.False(ShareCredential.TryParse(value, out _, out _));
    }
}
