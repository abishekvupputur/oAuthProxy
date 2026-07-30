using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// Using a vault the user already has, instead of letting OAuthProxy create threeEyedRaven.
///
/// Two rules carry the weight here. Only an empty vault or one this app has written to may be
/// taken over — everything else has the user's own entries in it, and delete reconciliation runs
/// over vaults this app owns. And an adopted vault must be found again on the next launch without
/// anything being stored on the PC, which is what the config item in it is for.
/// </summary>
public class VaultAdoptionTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"oauthproxy-adopt-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-adopt-logs-{Guid.NewGuid()}");

    private readonly string _opStub;
    private readonly string _passStub;

    public VaultAdoptionTests()
    {
        Directory.CreateDirectory(_stubDir);
        _opStub = StubBinary("op.exe");
        _passStub = StubBinary("pass-cli.exe");
    }

    [Fact]
    public async Task ProtonPass_AnEmptyVaultIsAcceptedAndStampedSoItIsFoundAgain()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        var shareId = protonPass.AddVault("Agents");
        var provider = NewProtonPass(protonPass);

        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
        Assert.True((await provider.ProbeAsync()).IsReady);

        // The config item is the only thing that identifies this vault as OAuthProxy's, and the
        // name the user typed is deliberately not written down anywhere on this PC.
        Assert.Contains(protonPass.ItemsInVault(shareId),
            item => item["title"]?.GetValue<string>() == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task ProtonPass_AVaultWithTheUsersOwnItemsIsRefused()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        var shareId = protonPass.AddVault("Personal2");
        protonPass.AddItem(shareId, "Bank");

        var provider = NewProtonPass(protonPass);

        var refusal = await Assert.ThrowsAsync<VaultAdoptionException>(
            () => provider.UseExistingVaultAsync("Personal2"));

        Assert.Contains("Personal2", refusal.Message);

        // Nothing was written to it, and the provider did not quietly adopt it anyway.
        Assert.Single(protonPass.ItemsInVault(shareId));
        Assert.Equal(VaultAvailability.VaultMissing, (await provider.ProbeAsync()).Availability);
    }

    [Fact]
    public async Task ProtonPass_AVaultThatAlreadyHoldsTheConfigurationIsAcceptedAndRead()
    {
        // What reconnecting to an adopted vault from a second install looks like.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var first = NewProtonPass(protonPass);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        var second = NewProtonPass(protonPass);
        await second.UseExistingVaultAsync("Agents");

        var store = await second.LoadAsync();
        Assert.Equal("cred", Assert.Single(store.Credentials).Name);
    }

    [Fact]
    public async Task ProtonPass_AnAdoptedVaultIsFoundAgainByAFreshProviderWithNothingStoredLocally()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var first = NewProtonPass(protonPass);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        // A restart: a provider that has never heard of "Agents" and no threeEyedRaven to fall
        // back on. The configuration in the vault is the only evidence, and it is enough.
        var afterRestart = NewProtonPass(protonPass);
        var status = await afterRestart.ProbeAsync();

        Assert.True(status.IsReady);
        Assert.Equal("Agents", status.VaultName);
        Assert.Single((await afterRestart.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task ProtonPass_AMisspelledVaultIsRefusedWithTheNamesThatDoExist()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var refusal = await Assert.ThrowsAsync<VaultAdoptionException>(
            () => NewProtonPass(protonPass).UseExistingVaultAsync("Agnets"));

        Assert.Contains("Agents", refusal.Message);
    }

    [Fact]
    public async Task ProtonPass_TheNameIsMatchedWithoutRegardToCase()
    {
        // The user is typing a name they read in the Proton Pass UI, not one this app gave them.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        await provider.UseExistingVaultAsync("agents");

        Assert.Equal("Agents", provider.VaultName);
    }

    [Fact]
    public async Task OnePassword_AnEmptyVaultIsAcceptedAndStampedSoItIsFoundAgain()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        var vaultId = onePassword.AddVault("Agents");
        var provider = NewOnePassword(onePassword);

        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
        Assert.Contains(onePassword.ItemsInVault(vaultId),
            item => item["title"]?.GetValue<string>() == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task OnePassword_AVaultWithTheUsersOwnItemsIsRefused()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        var vaultId = onePassword.AddVault("Personal2");
        onePassword.AddItem(vaultId, "Bank");

        await Assert.ThrowsAsync<VaultAdoptionException>(
            () => NewOnePassword(onePassword).UseExistingVaultAsync("Personal2"));

        Assert.Single(onePassword.ItemsInVault(vaultId));
    }

    [Fact]
    public async Task OnePassword_AnAdoptedVaultIsFoundAgainByAFreshProvider()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        onePassword.AddVault("Agents");

        var first = NewOnePassword(onePassword);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        var status = await NewOnePassword(onePassword).ProbeAsync();

        Assert.True(status.IsReady);
        Assert.Equal("Agents", status.VaultName);
    }

    [Fact]
    public async Task TheDefaultVaultStillWinsOverAnythingElseHoldingAConfiguration()
    {
        // Scanning happens only when threeEyedRaven is absent. A user who has both should not have
        // the app quietly moved into the other one by a leftover config item.
        var protonPass = new FakeProtonPass();
        var otherShare = protonPass.AddVault("Agents");
        protonPass.AddItem(otherShare, VaultItemNaming.ConfigTitle);

        var status = await NewProtonPass(protonPass).ProbeAsync();

        Assert.Equal(VaultConstants.VaultName, status.VaultName);
        Assert.Equal(protonPass.ShareId, status.VaultId);
    }

    [Fact]
    public async Task AGateDisconnectDoesNotReconnectItselfOnTheNextProbe()
    {
        // Disconnecting has to survive the very next probe, or the single-ready-manager shortcut
        // would undo it before the user has taken their hand off the mouse.
        var protonPass = new FakeProtonPass();
        var gate = NewGate(SignedOutOnePassword(), protonPass.AsRunner());

        Assert.True((await gate.EvaluateAsync()).IsReady);

        gate.Disconnect();
        var afterDisconnect = await gate.EvaluateAsync();

        Assert.True(gate.IsDisconnected);
        Assert.False(afterDisconnect.IsReady);
        Assert.Equal(VaultBackendKind.None, afterDisconnect.Selected);

        // ...and the ready manager is offered as a choice, which is how the user connects back.
        Assert.True(afterDisconnect.NeedsAChoice);

        var reconnected = gate.SelectBackend(VaultBackendKind.ProtonPass);
        Assert.True(reconnected.IsReady);
        Assert.False(gate.IsDisconnected);
    }

    [Fact]
    public async Task DisconnectingMakesTheProvidersForgetTheVaultTheyResolved()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        var gate = new VaultGateService(
            new OnePasswordVaultProvider(SignedOutOnePassword(), Log(), _opStub), provider, Log());

        await gate.UseExistingVaultAsync(VaultBackendKind.ProtonPass, "Agents");
        Assert.Equal("Agents", provider.VaultName);

        gate.Disconnect();

        // Back to looking for threeEyedRaven: reconnecting must not keep writing to the vault the
        // user just walked away from.
        Assert.Equal(VaultConstants.VaultName, provider.VaultName);
    }

    private ProtonPassVaultProvider NewProtonPass(FakeProtonPass protonPass) =>
        new(protonPass.AsRunner(), Log(), _passStub);

    private OnePasswordVaultProvider NewOnePassword(FakeOnePassword onePassword) =>
        new(onePassword.AsRunner(), Log(), _opStub);

    private VaultGateService NewGate(FakeCliRunner onePassword, FakeCliRunner protonPass) =>
        new(new OnePasswordVaultProvider(onePassword, Log(), _opStub),
            new ProtonPassVaultProvider(protonPass, Log(), _passStub),
            Log());

    private static FakeCliRunner SignedOutOnePassword() =>
        new FakeCliRunner()
            .Respond(["--version"], "2.31.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not signed in");

    private ActivityLog Log() => new(_logPath);

    private static ConfigStore StoreWithSomethingInIt()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord { Name = "cred", ClientId = "id", ClientSecret = "secret" });
        return store;
    }

    private string StubBinary(string exeName)
    {
        var path = Path.Combine(_stubDir, exeName);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}
