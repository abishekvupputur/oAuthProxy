using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

/// <summary>One secret-bearing item, tied back to the record it belongs to.</summary>
public sealed record VaultSecretItem(VaultItemRole Role, Guid RecordId, VaultItemSpec Spec)
{
    /// <summary>
    /// Digest of everything a save would write. Lets a provider skip items whose secret has not
    /// changed, which is what keeps a port change or a single token refresh to one CLI call
    /// instead of one per credential and key in the store.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            // ASCII unit separator between every part, so two different field sets cannot
            // concatenate into the same string and collide.
            const char Separator = '\u001f';

            var builder = new StringBuilder(Spec.Title).Append(Separator).Append(Spec.Category);

            foreach (var field in Spec.Fields.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                builder.Append(Separator).Append(field.Name).Append(Separator).Append(field.Value);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }
}

/// <summary>
/// Translates between a <see cref="ConfigStore"/> and the items that represent it in a vault.
///
/// The split is deliberate. Secrets — client secrets, API keys, tokens, proxy keys — each get
/// their own item, so the password manager can conceal them, show them, and let the user copy one
/// out without reading JSON. Everything else goes in one note, because it is a graph (routes
/// reference upstreams reference credentials) and splitting a graph across items would turn every
/// save into a consistency problem.
///
/// Each field lives on exactly one side. A credential's scopes and endpoints are in the note and
/// nowhere else; its secret is in its item and nowhere else. There is no field with two homes, so
/// there is never a question of which copy wins.
/// </summary>
public static class VaultMapper
{
    /// <summary>
    /// The items that must exist for this store's secrets, in the order they should be written.
    /// Records with nothing secret to store are skipped — an OAuth credential that has never been
    /// connected has no item until it does.
    /// </summary>
    public static List<VaultSecretItem> BuildSecretItems(ConfigStore store, VaultIndex index)
    {
        var items = new List<VaultSecretItem>();

        foreach (var credential in store.Credentials)
        {
            if (BuildCredentialItem(credential, index) is { } item) items.Add(item);
        }

        foreach (var route in store.Routes)
        {
            if (!route.Key.IsConfigured) continue;

            items.Add(new VaultSecretItem(VaultItemRole.RouteKey, route.Id, new VaultItemSpec(
                VaultItemNaming.ForRouteKey(route.Id, route.PathPrefix),
                VaultItemCategory.Password,
                [
                    new VaultItemField(VaultFields.Password, route.Key.Value),
                    new VaultItemField(VaultFields.RecordId, route.Id.ToString("D")),
                ])
            {
                ItemId = index.Find(VaultItemRole.RouteKey, route.Id),
                Caption = $"Proxy key for {route.PathPrefix} — {route.Key.DescribeExpiry(DateTimeOffset.UtcNow)}",
            }));
        }

        foreach (var funnel in store.McpFunnels)
        {
            if (!funnel.Key.IsConfigured) continue;

            items.Add(new VaultSecretItem(VaultItemRole.FunnelKey, funnel.Id, new VaultItemSpec(
                VaultItemNaming.ForFunnelKey(funnel.Id, funnel.Slug),
                VaultItemCategory.Password,
                [
                    new VaultItemField(VaultFields.Password, funnel.Key.Value),
                    new VaultItemField(VaultFields.RecordId, funnel.Id.ToString("D")),
                ])
            {
                ItemId = index.Find(VaultItemRole.FunnelKey, funnel.Id),
                Caption = $"Proxy key for MCP funnel '{funnel.Name}' — {funnel.Key.DescribeExpiry(DateTimeOffset.UtcNow)}",
            }));
        }

        return items;
    }

    private static VaultSecretItem? BuildCredentialItem(CredentialRecord credential, VaultIndex index)
    {
        var fields = new List<VaultItemField>
        {
            new(VaultFields.RecordId, credential.Id.ToString("D")),
            new(VaultFields.Kind, credential.Kind.ToString()),
        };

        // The client id goes in the username slot and the secret in the password slot so the item
        // reads as a real login in the manager's UI, with the usual copy and conceal behaviour.
        if (!string.IsNullOrEmpty(credential.ClientId)) fields.Add(new(VaultFields.Username, credential.ClientId));
        if (!string.IsNullOrEmpty(credential.ClientSecret)) fields.Add(new(VaultFields.Password, credential.ClientSecret));
        if (!string.IsNullOrWhiteSpace(credential.Authority)) fields.Add(new(VaultFields.Website, credential.Authority));
        if (!string.IsNullOrEmpty(credential.ApiKey)) fields.Add(new(VaultFields.ApiKey, credential.ApiKey));

        if (credential.Token is { } token)
        {
            fields.Add(new(VaultFields.AccessToken, token.AccessToken));
            fields.Add(new(VaultFields.TokenType, token.TokenType));
            fields.Add(new(VaultFields.ExpiresAtUtc, VaultItemNaming.FormatTimestamp(token.ExpiresAtUtc)));
            fields.Add(new(VaultFields.ObtainedUtc, VaultItemNaming.FormatTimestamp(token.ObtainedUtc)));

            if (token.RefreshToken is { Length: > 0 } refreshToken)
            {
                fields.Add(new(VaultFields.RefreshToken, refreshToken));
            }
        }

        // Nothing secret yet — an OAuth credential that has never been connected. Writing an item
        // holding only a record id would clutter the vault with entries that mean nothing to the
        // user; the note already knows the credential exists.
        var hasSecret = fields.Any(f => f.Concealed);
        if (!hasSecret) return null;

        return new VaultSecretItem(VaultItemRole.Credential, credential.Id, new VaultItemSpec(
            VaultItemNaming.ForCredential(credential.Id, credential.Name),
            VaultItemCategory.Login,
            fields)
        {
            ItemId = index.Find(VaultItemRole.Credential, credential.Id),
            Caption = credential.Kind == CredentialKind.ApiKey
                ? $"API key for '{credential.Name}'"
                : $"OAuth credential for '{credential.Name}'",
        });
    }

    /// <summary>
    /// The topology note. Built <em>after</em> the secret items are written, with the index they
    /// produced, so the note can never reference an item that does not exist — a crash mid-save
    /// leaves orphan items, which the next save sweeps, rather than a dangling pointer.
    /// </summary>
    public static VaultItemSpec BuildConfigNote(ConfigStore store, VaultIndex index, long revision)
    {
        var document = new VaultDocument
        {
            Revision = revision,
            WrittenBy = SafeMachineName(),
            WrittenUtc = DateTimeOffset.UtcNow,
            Store = store,
            Index = index,
        };

        return new VaultItemSpec(
            VaultItemNaming.ConfigTitle,
            VaultItemCategory.SecureNote,
            [new VaultItemField(VaultFields.NoteContent, document.Serialize())]);
    }

    /// <summary>
    /// Rebuilds the store: the note's redacted graph with each record's secret merged back from
    /// its item.
    ///
    /// A record whose item is missing loads without its secret rather than being dropped. For a
    /// proxy key that is the state ConfigStoreCache's backfill already handles — it issues a fresh
    /// one. For a credential it means the user has to re-enter it, which <paramref name="warnings"/>
    /// says out loud, because a credential that looks fine and fails at the upstream hours later is
    /// the worse outcome.
    /// </summary>
    public static ConfigStore ComposeStore(
        VaultDocument document,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets,
        List<string> warnings)
    {
        // Round-trip through the full contract so the caller gets a store detached from the
        // document — mutating one must not silently edit the other.
        var store = JsonSerializer.Deserialize<ConfigStore>(
            JsonSerializer.Serialize(document.Store, VaultRedaction.FullOptions),
            VaultRedaction.FullOptions) ?? new ConfigStore();

        var missingCredentials = 0;

        foreach (var credential in store.Credentials)
        {
            if (!secrets.TryGetValue((VaultItemRole.Credential, credential.Id), out var item))
            {
                // Never connected and never given a key is normal and silent; having had one and
                // losing it is not.
                if (ExpectsASecretItem(credential)) missingCredentials++;
                continue;
            }

            ApplyCredentialSecrets(credential, item);
        }

        if (missingCredentials > 0)
        {
            warnings.Add($"{missingCredentials} credential(s) loaded without their stored secret — "
                         + "their vault item is missing, so they need to be entered again.");
        }

        foreach (var route in store.Routes)
        {
            if (secrets.TryGetValue((VaultItemRole.RouteKey, route.Id), out var item))
            {
                route.Key.Value = item.Field(VaultFields.Password) ?? "";
            }
        }

        foreach (var funnel in store.McpFunnels)
        {
            if (secrets.TryGetValue((VaultItemRole.FunnelKey, funnel.Id), out var item))
            {
                funnel.Key.Value = item.Field(VaultFields.Password) ?? "";
            }
        }

        return store;
    }

    private static void ApplyCredentialSecrets(CredentialRecord credential, VaultItemContents item)
    {
        credential.ClientSecret = item.Field(VaultFields.Password) ?? "";
        credential.ApiKey = item.Field(VaultFields.ApiKey);

        var accessToken = item.Field(VaultFields.AccessToken);

        // TokenSet requires an access token, and the rest of the app reads a null Token as "not
        // connected" — so a half-written item must produce null rather than a token set with an
        // empty string in it, which would look connected and fail at the upstream.
        if (string.IsNullOrEmpty(accessToken))
        {
            credential.Token = null;
            return;
        }

        credential.Token = new TokenSet(
            accessToken,
            item.Field(VaultFields.RefreshToken),
            VaultItemNaming.ParseTimestamp(item.Field(VaultFields.ExpiresAtUtc)) ?? DateTimeOffset.UtcNow,
            item.Field(VaultFields.TokenType) ?? "Bearer",
            VaultItemNaming.ParseTimestamp(item.Field(VaultFields.ObtainedUtc)) ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Whether this record was stored expecting a secret item to exist, so that a missing one is
    /// worth warning about. An API-key credential always has a key; an OAuth one that got as far
    /// as having a client id was entered with a secret alongside it.
    /// </summary>
    private static bool ExpectsASecretItem(CredentialRecord credential) =>
        credential.Kind == CredentialKind.ApiKey || !string.IsNullOrEmpty(credential.ClientId);

    /// <summary>
    /// The machine name is written into the note so a concurrent-write conflict can name the other
    /// side. Guarded because it is the one piece of environment data here that can throw.
    /// </summary>
    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "unknown";
        }
    }
}
