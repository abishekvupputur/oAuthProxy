using OAuthProxy.Core.Models;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// A save deletes the items whose records are gone, and nothing else.
///
/// This is the riskiest thing the mapper does. threeEyedRaven is a vault in the user's own
/// password manager and they may well keep other things in it; a reconciler that enumerated
/// everything and deleted what it did not recognise would destroy data this app never owned.
/// </summary>
public class DeleteReconciliationTests
{
    [Fact]
    public async Task AnItemThisAppDoesNotOwnSurvivesEverySave()
    {
        var vault = InMemoryVault.Empty();
        vault.AddForeignItem("My bank login", "not-ours");
        vault.AddForeignItem("OAuthProxyish thing without a guid", "also-not-ours");

        var store = StoreWithOneOfEverything();
        await vault.SaveAsync(store);

        store.Credentials.Clear();
        store.Routes.Clear();
        store.McpFunnels.Clear();
        await vault.SaveAsync(store);

        Assert.Contains(vault.Items, i => i.Title == "My bank login");
        Assert.Contains(vault.Items, i => i.Title == "OAuthProxyish thing without a guid");
    }

    [Fact]
    public async Task AnOrphanedItemThisAppOwnsIsSweptUp()
    {
        var vault = InMemoryVault.Empty();
        var store = StoreWithOneOfEverything();
        await vault.SaveAsync(store);

        var funnelId = store.McpFunnels[0].Id;
        store.McpFunnels.Clear();
        await vault.SaveAsync(store);

        Assert.DoesNotContain(vault.Items, i =>
            VaultItemNaming.TryParse(i.Title, out var role, out var id)
            && role == VaultItemRole.FunnelKey
            && id == funnelId);
    }

    [Fact]
    public async Task TheConfigNoteIsNeverDeleted()
    {
        // It has no record guid, so a reconciler keying purely on "is there a matching record"
        // would delete the one item that makes the vault readable at all.
        var vault = InMemoryVault.Empty();

        await vault.SaveAsync(StoreWithOneOfEverything());
        await vault.SaveAsync(new ConfigStore());

        Assert.Contains(vault.Items, i => i.Title == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task EmptyingTheStoreLeavesOnlyTheConfigNote()
    {
        var vault = InMemoryVault.Empty();

        await vault.SaveAsync(StoreWithOneOfEverything());
        await vault.SaveAsync(new ConfigStore());

        var remaining = Assert.Single(vault.Items);
        Assert.Equal(VaultItemNaming.ConfigTitle, remaining.Title);
    }

    private static ConfigStore StoreWithOneOfEverything()
    {
        var credential = new CredentialRecord { Name = "cred", ClientId = "id", ClientSecret = "secret" };
        var upstream = new UpstreamRecord { Name = "api", BaseUrl = "https://api.test" };

        var store = new ConfigStore();
        store.Credentials.Add(credential);
        store.Upstreams.Add(upstream);
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/api",
            UpstreamId = upstream.Id,
            Key = ProxyKey.Generate(),
        });
        store.McpFunnels.Add(new McpFunnelRecord
        {
            Name = "funnel",
            Slug = "funnel",
            Key = ProxyKey.Generate(),
        });

        return store;
    }
}
