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
    public async Task SaveAndLoad_RoundTripsARoutesCredentialPlacement()
    {
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/app/api",
            CredentialPlacement = CredentialPlacement.Body,
            CredentialParameterName = "auth_token",
            CredentialValuePrefix = "token ",
        });

        var secureStore = new SecureStore(_tempFile);
        await secureStore.SaveAsync(store);

        var route = Assert.Single((await secureStore.LoadAsync()).Routes);
        Assert.Equal(CredentialPlacement.Body, route.CredentialPlacement);
        Assert.Equal("auth_token", route.CredentialParameterName);
        Assert.Equal("token ", route.CredentialValuePrefix);
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
        Assert.Equal(CredentialInjection.BearerHeader, route.ToCredentialInjection());
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
