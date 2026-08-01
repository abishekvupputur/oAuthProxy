using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

/// <summary>
/// The key model on its own. Expiry is the part worth pinning: it is the only thing in the app
/// that can lock a working client out on a timer, so "never expires" has to stay genuinely
/// unbounded and a set lifetime has to mean what the picker said.
/// </summary>
public class ProxyKeyTests
{
    [Fact]
    public void AKeyWithNoExpiry_NeverExpires()
    {
        var key = ProxyKey.Generate();

        Assert.Null(key.ExpiresUtc);
        Assert.False(key.IsExpired(DateTimeOffset.UtcNow.AddYears(50)));
        Assert.Equal("never expires", key.DescribeExpiry(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AKeyWithALifetime_ExpiresWhenItRunsOut()
    {
        var key = ProxyKey.Generate(TimeSpan.FromDays(30));
        var issued = key.CreatedUtc;

        Assert.False(key.IsExpired(issued.AddDays(29)));

        // Inclusive at the boundary: the moment it lapses it is no longer accepted, rather than
        // lingering for one more request.
        Assert.True(key.IsExpired(issued.AddDays(30)));
        Assert.True(key.IsExpired(issued.AddDays(31)));
    }

    [Fact]
    public void Regenerate_ReplacesTheValueAndRestartsTheSameLifetime()
    {
        var key = ProxyKey.Generate(TimeSpan.FromDays(7));
        var original = key.Value;
        var originalExpiry = key.ExpiresUtc!.Value;

        key.Regenerate();

        Assert.NotEqual(original, key.Value);

        // The user chose "7 days", not "7 days from the first time I pressed the button" — a
        // regenerated key that inherited the old expiry could be dead on arrival.
        Assert.True(key.ExpiresUtc > originalExpiry);
        Assert.Equal(7, Math.Round((key.ExpiresUtc!.Value - key.CreatedUtc).TotalDays));
    }

    [Fact]
    public void Regenerate_OnANeverExpiringKey_LeavesItNeverExpiring()
    {
        var key = ProxyKey.Generate();

        key.Regenerate();

        Assert.Null(key.ExpiresUtc);
    }

    [Fact]
    public void SetLifetime_MeasuresFromIssueNotFromNow()
    {
        // Changing the picker re-describes how long this secret was ever meant to live; it does
        // not restart the countdown. Otherwise re-picking the same option would quietly extend a
        // key that has already been sitting in client configs for weeks.
        var key = ProxyKey.Generate(TimeSpan.FromDays(1));
        key.CreatedUtc = DateTimeOffset.UtcNow.AddDays(-10);

        key.SetLifetime(TimeSpan.FromDays(90));
        Assert.Equal(90, Math.Round((key.ExpiresUtc!.Value - key.CreatedUtc).TotalDays));
        Assert.Equal(80, Math.Round((key.ExpiresUtc!.Value - DateTimeOffset.UtcNow).TotalDays));

        key.SetLifetime(null);
        Assert.Null(key.ExpiresUtc);
        Assert.False(key.IsExpired(DateTimeOffset.UtcNow.AddYears(10)));
    }

    [Fact]
    public void SetLifetime_ShorterThanTheKeysAge_ExpiresItImmediately()
    {
        // The flip side of anchoring on issue time, and the reason Regenerate exists: dropping a
        // month-old key to "1 hour" ends it now rather than granting it another hour.
        var key = ProxyKey.Generate();
        key.CreatedUtc = DateTimeOffset.UtcNow.AddDays(-30);

        key.SetLifetime(TimeSpan.FromHours(1));

        Assert.True(key.IsExpired(DateTimeOffset.UtcNow));

        key.Regenerate();

        Assert.False(key.IsExpired(DateTimeOffset.UtcNow));
        Assert.Equal(1, Math.Round((key.ExpiresUtc!.Value - DateTimeOffset.UtcNow).TotalHours));
    }

    [Fact]
    public void ForKey_MatchesThePresetTheKeyWasIssuedWith()
    {
        // What the drop-down shows when the tab is reopened. Matched on the configured length
        // rather than on time remaining, which shrinks every second and would drift off the
        // preset within a minute of being set.
        var key = ProxyKey.Generate(TimeSpan.FromDays(90));
        key.CreatedUtc = DateTimeOffset.UtcNow.AddDays(-45);
        key.ExpiresUtc = key.CreatedUtc.AddDays(90);

        Assert.Equal("90 days", ProxyKeyLifetime.ForKey(key).Label);
        Assert.Equal("Never expires", ProxyKeyLifetime.ForKey(ProxyKey.Generate()).Label);
    }

    [Fact]
    public void ForKey_OnAnUnrecognisedLifetime_StillDescribesIt()
    {
        // A store edited by hand, or written by a build with a different set of presets. The
        // picker has to be able to show it rather than opening blank and silently rewriting it.
        var key = ProxyKey.Generate(TimeSpan.FromDays(3));

        var option = ProxyKeyLifetime.ForKey(key);

        Assert.DoesNotContain(option, ProxyKeyLifetime.All);
        Assert.Contains("until", option.Label);
    }

    [Fact]
    public void GeneratedValues_AreDistinctAndUrlSafe()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ProxyKey.Generate().Value).ToList();

        Assert.Equal(100, keys.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.True(key.Length >= 40, "32 random bytes should survive base64 at >= 40 chars");
            Assert.DoesNotContain('+', key);
            Assert.DoesNotContain('/', key);
            Assert.DoesNotContain('=', key);
        });
    }
}
