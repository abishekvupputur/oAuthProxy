using OAuthProxy.Core.Models;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// The index in the config note is a cache, and everything has to keep working without it.
///
/// It goes stale for ordinary reasons: the note restored from an older version by the password
/// manager's own history, an item recreated by hand, a save that died before writing the note.
/// If a missing index meant missing secrets, any of those would look like the app losing every
/// credential at once.
/// </summary>
public class IndexRecoveryTests
{
    [Fact]
    public async Task SecretsAreFoundByTitleWhenTheIndexIsEmpty()
    {
        var (vault, store) = await SavedStoreAsync();

        vault.EditConfigNote(json =>
        {
            var document = VaultDocument.TryParse(json)!;
            document.Index = new VaultIndex();
            return document.Serialize();
        });

        var reloaded = await vault.LoadAsync();

        Assert.Equal(store.Credentials[0].ClientSecret, reloaded.Credentials[0].ClientSecret);
        Assert.Equal(store.Routes[0].Key.Value, reloaded.Routes[0].Key.Value);
        Assert.Equal(store.McpFunnels[0].Key.Value, reloaded.McpFunnels[0].Key.Value);
    }

    [Fact]
    public async Task SecretsAreFoundByTitleWhenTheIndexPointsAtItemsThatAreGone()
    {
        var (vault, store) = await SavedStoreAsync();

        vault.EditConfigNote(json =>
        {
            var document = VaultDocument.TryParse(json)!;

            foreach (var role in new[] { VaultItemRole.Credential, VaultItemRole.RouteKey, VaultItemRole.FunnelKey })
            {
                foreach (var key in document.Index.For(role).Keys.ToList())
                {
                    document.Index.For(role)[key] = "item-that-never-existed";
                }
            }

            return document.Serialize();
        });

        var reloaded = await vault.LoadAsync();

        Assert.Equal(store.Credentials[0].ClientSecret, reloaded.Credentials[0].ClientSecret);
        Assert.Equal(store.Routes[0].Key.Value, reloaded.Routes[0].Key.Value);
    }

    [Fact]
    public async Task ARebuiltIndexIsWrittenBackOnTheNextSave()
    {
        var (vault, store) = await SavedStoreAsync();

        vault.EditConfigNote(json =>
        {
            var document = VaultDocument.TryParse(json)!;
            document.Index = new VaultIndex();
            return document.Serialize();
        });

        await vault.SaveAsync(await vault.LoadAsync());

        var note = vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle);
        var rebuilt = VaultDocument.TryParse(note.Field(VaultFields.NoteContent)!)!;

        Assert.Contains(store.Credentials[0].Id, rebuilt.Index.Credentials.Keys);
        Assert.Contains(store.Routes[0].Id, rebuilt.Index.RouteKeys.Keys);
        Assert.Contains(store.McpFunnels[0].Id, rebuilt.Index.FunnelKeys.Keys);
    }

    [Fact]
    public async Task ACredentialWhoseItemIsDeletedLoadsWithoutItsSecretAndSaysSo()
    {
        // Deliberately not "the credential disappears". Dropping it would take the routes that
        // reference it with it; loading it empty keeps the configuration intact and makes the fix
        // a re-entry rather than a rebuild.
        var (vault, store) = await SavedStoreAsync();

        var credentialItem = vault.Items.Single(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out _) && role == VaultItemRole.Credential);

        vault.RemoveItem(credentialItem.ItemId);

        var reloaded = await vault.LoadAsync();

        Assert.Single(reloaded.Credentials);
        Assert.Equal("", reloaded.Credentials[0].ClientSecret);
        Assert.NotNull(vault.LastLoadWarning);
        Assert.Contains("entered again", vault.LastLoadWarning);

        // The rest of the configuration is untouched.
        Assert.Equal(store.Routes[0].PathPrefix, reloaded.Routes[0].PathPrefix);
    }

    [Fact]
    public async Task AnUnparseableNoteLoadsEmptyRatherThanThrowing()
    {
        // The note is free text in the user's password manager; they can open it and break it.
        // Coming up empty is recoverable, and failing to start is not.
        var (vault, _) = await SavedStoreAsync();

        vault.EditConfigNote(_ => "{ this is not json");

        var reloaded = await vault.LoadAsync();
        Assert.Empty(reloaded.Credentials);
        Assert.Empty(reloaded.Routes);
    }

    private static async Task<(InMemoryVault Vault, ConfigStore Store)> SavedStoreAsync()
    {
        var credential = new CredentialRecord { Name = "cred", ClientId = "id", ClientSecret = "the-secret" };
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
        store.McpFunnels.Add(new McpFunnelRecord { Name = "f", Slug = "f", Key = ProxyKey.Generate() });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        return (vault, store);
    }
}
