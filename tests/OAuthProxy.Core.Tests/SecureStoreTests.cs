using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Tests;

public class SecureStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"oauthproxy-test-{Guid.NewGuid()}.dat");

    [Fact]
    public async Task SaveAndLoad_RoundTripsEncryptedStore()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "Gmail",
            ClientId = "client-id-123",
            ClientSecret = "super-secret-value",
            Scopes = ["openid", "email"],
            Authority = "https://accounts.google.com",
            RequiresIdToken = true,
            Token = new TokenSet("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        });

        var secureStore = new SecureStore(_tempFile);
        await secureStore.SaveAsync(store);

        // The file on disk must not contain the plaintext secret.
        var rawBytes = await File.ReadAllBytesAsync(_tempFile);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("super-secret-value", rawText);

        var loaded = await secureStore.LoadAsync();
        var credential = Assert.Single(loaded.Credentials);
        Assert.Equal("Gmail", credential.Name);
        Assert.Equal("super-secret-value", credential.ClientSecret);
        Assert.Equal("access-token", credential.Token!.AccessToken);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAnApiKeyCredentialWithoutWritingTheKeyInPlaintext()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "shodan",
            Kind = CredentialKind.ApiKey,
            ApiKey = "super-secret-api-key",
            DefaultPlacement = CredentialPlacement.Query,
            DefaultParameterName = "key",
            DefaultValuePrefix = "",
            TestEndpoint = "https://api.example.com/account/profile",
        });

        var secureStore = new SecureStore(_tempFile);
        await secureStore.SaveAsync(store);

        // The key is a secret exactly like a client secret, and gets the same treatment on disk.
        var rawText = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_tempFile));
        Assert.DoesNotContain("super-secret-api-key", rawText);

        var credential = Assert.Single((await secureStore.LoadAsync()).Credentials);
        Assert.Equal(CredentialKind.ApiKey, credential.Kind);
        Assert.Equal("super-secret-api-key", credential.ApiKey);
        Assert.Equal(CredentialPlacement.Query, credential.DefaultPlacement);
        Assert.Equal("key", credential.DefaultParameterName);
        Assert.Equal("https://api.example.com/account/profile", credential.TestEndpoint);
    }

    [Fact]
    public async Task Load_CredentialWrittenBeforeApiKeysExisted_IsStillAnOAuthBearerCredential()
    {
        // Kind, ApiKey, the placement defaults and TestEndpoint are all simply absent from a
        // store an older build wrote. The property initializers are the only thing keeping such
        // a credential behaving exactly as it did.
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Credentials": [
                {
                  "Id": "00000000-0000-0000-0000-000000000002",
                  "Name": "Gmail",
                  "ClientId": "client-id",
                  "ClientSecret": "client-secret",
                  "Scopes": ["openid"],
                  "IsGoogleProvider": true,
                  "UsesPkce": true
                }
              ],
              "Upstreams": [],
              "Routes": [],
              "Settings": { "ListenPort": 8722, "StartWithWindows": false, "LocalApiKey": "legacy-key" }
            }
            """;

        await File.WriteAllBytesAsync(_tempFile, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(legacyJson),
            optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var credential = Assert.Single((await new SecureStore(_tempFile).LoadAsync()).Credentials);

        Assert.Equal(CredentialKind.OAuth2, credential.Kind);
        Assert.Null(credential.ApiKey);
        Assert.Null(credential.TestEndpoint);
        Assert.Equal(CredentialInjection.BearerHeader, credential.ToDefaultInjection());
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsARoutesCredentialPlacement()
    {
        var credentialId = Guid.NewGuid();
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/app/api",
            Credentials =
            [
                new RouteCredential
                {
                    CredentialId = credentialId,
                    Placement = CredentialPlacement.Body,
                    ParameterName = "auth_token",
                    ValuePrefix = "token ",
                },
            ],
        });

        var secureStore = new SecureStore(_tempFile);
        await secureStore.SaveAsync(store);

        var route = Assert.Single((await secureStore.LoadAsync()).Routes);
        var credential = Assert.Single(route.Credentials);
        Assert.Equal(credentialId, credential.CredentialId);
        Assert.Equal(CredentialPlacement.Body, credential.Placement);
        Assert.Equal("auth_token", credential.ParameterName);
        Assert.Equal("token ", credential.ValuePrefix);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSeveralCredentialsOnOneRouteInOrder()
    {
        // Order matters on the wire only for body fields sharing a JSON object, but it is also
        // what the UI lists, so a reordering on disk would be visible and confusing.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/app/api",
            Credentials =
            [
                new RouteCredential { CredentialId = first, Placement = CredentialPlacement.Header, ParameterName = "Authorization", ValuePrefix = "Bearer " },
                new RouteCredential { CredentialId = second, Placement = CredentialPlacement.Query, ParameterName = "key", ValuePrefix = "" },
                new RouteCredential { CredentialId = first, Placement = CredentialPlacement.Body, ParameterName = "token", ValuePrefix = "" },
            ],
        });

        await new SecureStore(_tempFile).SaveAsync(store);

        var route = Assert.Single((await new SecureStore(_tempFile).LoadAsync()).Routes);
        Assert.Equal(3, route.Credentials.Count);
        Assert.Equal([first, second, first], route.Credentials.Select(c => c.CredentialId));
        Assert.Equal(
            [CredentialPlacement.Header, CredentialPlacement.Query, CredentialPlacement.Body],
            route.Credentials.Select(c => c.Placement));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsARouteWithNoCredentials()
    {
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping { PathPrefix = "/app/public" });

        await new SecureStore(_tempFile).SaveAsync(store);

        var route = Assert.Single((await new SecureStore(_tempFile).LoadAsync()).Routes);
        Assert.Empty(route.Credentials);
        Assert.Empty(route.EffectiveCredentials);

        // An empty list must not be mistaken for "the legacy fields were never filled in", which
        // would resurrect a credential the user removed.
        Assert.Null(route.CredentialId);
    }

    [Fact]
    public async Task Load_StoreWrittenBeforeCredentialPlacementExisted_DefaultsToBearerHeader()
    {
        // The literal JSON an older build wrote, encrypted the same way: these fields are simply
        // absent, so the property initializers are the only thing keeping an upgraded install's
        // routes forwarding exactly as they did before.
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Credentials": [],
              "Upstreams": [],
              "Routes": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                  "PathPrefix": "/app/api",
                  "UpstreamId": "00000000-0000-0000-0000-000000000001",
                  "CredentialId": "00000000-0000-0000-0000-000000000002",
                  "StripPrefix": true,
                  "Enabled": true
                }
              ],
              "Settings": { "ListenPort": 8722, "StartWithWindows": false, "LocalApiKey": "legacy-key" }
            }
            """;

        await File.WriteAllBytesAsync(_tempFile, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(legacyJson),
            optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var route = Assert.Single((await new SecureStore(_tempFile).LoadAsync()).Routes);
        Assert.Equal("/app/api", route.PathPrefix);

        var credential = Assert.Single(route.Credentials);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), credential.CredentialId);
        Assert.Equal(CredentialInjection.BearerHeader, credential.ToCredentialInjection());
    }

    [Fact]
    public async Task Load_StoreWithTheSupersededSingleCredentialFields_FoldsThemIntoTheList()
    {
        // The shape written by the build that had placements but not multiple credentials. Load
        // has to translate it once and clear the old fields, so the next save writes one
        // representation rather than two that can drift apart.
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Credentials": [],
              "Upstreams": [],
              "Routes": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                  "PathPrefix": "/app/api",
                  "UpstreamId": "00000000-0000-0000-0000-000000000001",
                  "CredentialId": "00000000-0000-0000-0000-000000000002",
                  "StripPrefix": true,
                  "Enabled": true,
                  "CredentialPlacement": "Query",
                  "CredentialParameterName": "access_token",
                  "CredentialValuePrefix": ""
                }
              ],
              "Settings": { "ListenPort": 8722, "StartWithWindows": false, "LocalApiKey": "legacy-key" }
            }
            """;

        await File.WriteAllBytesAsync(_tempFile, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(legacyJson),
            optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var secureStore = new SecureStore(_tempFile);
        var loaded = await secureStore.LoadAsync();

        var credential = Assert.Single(Assert.Single(loaded.Routes).Credentials);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), credential.CredentialId);
        Assert.Equal(CredentialPlacement.Query, credential.Placement);
        Assert.Equal("access_token", credential.ParameterName);
        Assert.Equal("", credential.ValuePrefix);

        // Round-tripping must not resurrect the old fields alongside the list.
        await secureStore.SaveAsync(loaded);
        var reloaded = Assert.Single((await secureStore.LoadAsync()).Routes);

        Assert.Null(reloaded.CredentialId);
        Assert.Null(reloaded.CredentialPlacement);
        Assert.Single(reloaded.Credentials);
    }

    [Fact]
    public async Task Load_LegacyRouteWithNoCredentialId_ResolvesToNoCredentials()
    {
        // Before routes could exist without a credential, an empty CredentialId could only mean
        // "the credential this pointed at is gone". Either way there is nothing to attach.
        const string legacyJson = """
            {
              "SchemaVersion": 1,
              "Credentials": [],
              "Upstreams": [],
              "Routes": [
                {
                  "Id": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                  "PathPrefix": "/app/api",
                  "UpstreamId": "00000000-0000-0000-0000-000000000001",
                  "CredentialId": "00000000-0000-0000-0000-000000000000",
                  "StripPrefix": true,
                  "Enabled": true
                }
              ],
              "Settings": { "ListenPort": 8722, "StartWithWindows": false, "LocalApiKey": "legacy-key" }
            }
            """;

        await File.WriteAllBytesAsync(_tempFile, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(legacyJson),
            optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var route = Assert.Single((await new SecureStore(_tempFile).LoadAsync()).Routes);

        Assert.Empty(route.Credentials);
        Assert.Empty(route.EffectiveCredentials);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmptyStore()
    {
        var secureStore = new SecureStore(_tempFile);
        var loaded = await secureStore.LoadAsync();
        Assert.Empty(loaded.Credentials);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        var tmp = _tempFile + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
