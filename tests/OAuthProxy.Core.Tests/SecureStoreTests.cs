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
