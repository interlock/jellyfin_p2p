using System;
using System.Collections.Generic;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Xunit;

namespace Jellyfin.Plugin.P2PShare.Tests.Model;

public class ValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Lib = Guid.NewGuid();
    private static readonly HashSet<Guid> Known = new() { Lib };

    private static ShareEdit ValidEdit() => new() { Name = "Bob", LibraryIds = new List<Guid> { Lib } };

    [Fact]
    public void ValidShare_HasNoErrors()
    {
        Assert.Empty(Validation.ValidateShare(ValidEdit(), Known, Now));
    }

    [Fact]
    public void Share_CollectsAllProblems()
    {
        var edit = new ShareEdit
        {
            Name = " ",
            LibraryIds = new List<Guid> { Guid.NewGuid() },
            MaxBandwidthMbps = -1,
            QuotaGB = double.NaN,
            MaxConcurrentStreams = -2,
            ExpiresAt = Now.UtcDateTime.AddDays(-1),
            AllowedNetworks = new List<string> { "10.0.0.0/8", "bogus/99" },
        };

        var errors = Validation.ValidateShare(edit, Known, Now);
        Assert.Contains(errors, e => e.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Max bandwidth", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Quota", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("concurrent", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Expiry", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bogus/99", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("10.0.0.0/8", StringComparison.Ordinal));
    }

    [Fact]
    public void Share_RequiresAtLeastOneLibrary()
    {
        var edit = ValidEdit();
        edit.LibraryIds.Clear();
        Assert.Contains("Select at least one library.", Validation.ValidateShare(edit, Known, Now));
    }

    [Fact]
    public void ApplyTo_NormalizesFields()
    {
        var edit = ValidEdit();
        edit.Name = "  Bob  ";
        edit.LibraryIds.Add(Lib);
        edit.AllowedNetworks = new List<string> { " 10.0.0.0/8 ", "", "10.0.0.0/8" };
        var share = new OutgoingShare { SecretHash = "keep" };

        edit.ApplyTo(share);

        Assert.Equal("Bob", share.Name);
        Assert.Single(share.LibraryIds);
        Assert.Equal(new List<string> { "10.0.0.0/8" }, share.AllowedNetworks);
        Assert.Equal("keep", share.SecretHash);
    }

    [Fact]
    public void DefaultSettings_AreValid()
    {
        Assert.Empty(Validation.ValidateSettings(new GlobalSettings()));
    }

    [Fact]
    public void Settings_RequireHttps_RejectsHttpPublicUrl()
    {
        var s = new GlobalSettings { PublicServerUrl = "http://example.com", RequireHttps = true };
        Assert.Contains(Validation.ValidateSettings(s), e => e.Contains("https", StringComparison.Ordinal));
        s.PublicServerUrl = "https://example.com";
        Assert.Empty(Validation.ValidateSettings(s));
    }

    [Fact]
    public void Settings_InvalidValues()
    {
        var s = new GlobalSettings
        {
            PublicServerUrl = "nope",
            LocalProxyBaseUrl = "",
            GlobalQuotaGB = -5,
            SyncIntervalHours = 0,
            LibraryNameTemplate = "{server}",
        };

        var errors = Validation.ValidateSettings(s);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void Normalize_TrimsTrailingSlash()
    {
        var s = new GlobalSettings { PublicServerUrl = "https://example.com/jf/", LocalProxyBaseUrl = "http://127.0.0.1:8096/" };
        Validation.Normalize(s);
        Assert.Equal("https://example.com/jf", s.PublicServerUrl);
        Assert.Equal("http://127.0.0.1:8096", s.LocalProxyBaseUrl);
    }
}
