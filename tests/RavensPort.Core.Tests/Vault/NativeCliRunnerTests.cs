using System.Text.Json.Nodes;
using Moq;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

public class NativeCliRunnerTests
{
    private readonly Mock<IOnePasswordNativeClient> _mockClient;
    private readonly NativeCliRunner _runner;

    public NativeCliRunnerTests()
    {
        _mockClient = new Mock<IOnePasswordNativeClient>();
        _runner = new NativeCliRunner(client: _mockClient.Object);
        Environment.SetEnvironmentVariable("OP_ACCOUNT", "TestAccount");
        NativeCliRunner.ResetInitialization();
    }

    [Fact]
    public async Task Version_DoesNotConnectToTheDesktopApp()
    {
        // Initialize() is what raises the 1Password unlock prompt, so a --version that ran it made
        // merely *looking* for 1Password an interruption — which is what the setup page's discovery
        // probe exists to avoid. The answer is a constant; there is no CLI here to ask.
        var result = await _runner.RunAsync("native", ["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0.4.1", result.StdOut);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Version_DoesNotConnectEvenWithNoAccountNameConfigured()
    {
        // The state a first run is actually in. Initialising would throw "1Password SDK requires
        // your Account Name", which the setup page would then show as though something were wrong —
        // on a card whose only honest status is "installed, not connected".
        Environment.SetEnvironmentVariable("OP_ACCOUNT", null);
        NativeCliRunner.ResetInitialization();

        try
        {
            var result = await _runner.RunAsync("native", ["--version"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("0.4.1", result.StdOut);
            _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OP_ACCOUNT", "TestAccount");
            NativeCliRunner.ResetInitialization();
        }
    }

    [Fact]
    public async Task VaultList_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.ListVaults()).Returns(new JsonArray());
        
        var result = await _runner.RunAsync("op", ["vault", "list"]);
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.ListVaults(), Times.Once);
    }

    [Fact]
    public async Task VaultCreate_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.CreateVault("MyVault", "Desc")).Returns(new JsonObject { ["id"] = "123" });
        
        var result = await _runner.RunAsync("op", ["vault", "create", "MyVault", "--description", "Desc"]);
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.CreateVault("MyVault", "Desc"), Times.Once);
    }

    [Fact]
    public async Task ItemList_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.ListItems("vault123")).Returns(new JsonArray());
        
        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Once);
    }

    [Fact]
    public async Task ItemGet_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.GetItem("vault123", "item456")).Returns(new JsonObject { ["id"] = "item456" });
        
        var result = await _runner.RunAsync("op", ["item", "get", "item456", "--vault", "vault123"]);
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.GetItem("vault123", "item456"), Times.Once);
    }

    [Fact]
    public async Task ItemCreate_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.CreateItem("vault123", "{}")).Returns(new JsonObject { ["id"] = "item456" });
        
        var result = await _runner.RunAsync("op", ["item", "create", "--vault", "vault123"], stdin: "{}");
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.CreateItem("vault123", "{}"), Times.Once);
    }

    [Fact]
    public async Task ItemEdit_ParsesArgumentsAndCallsNativeClient()
    {
        _mockClient.Setup(c => c.EditItem("vault123", "item456", "{}")).Returns(new JsonObject { ["id"] = "item456" });
        
        var result = await _runner.RunAsync("op", ["item", "edit", "item456", "--vault", "vault123"], stdin: "{}");
        
        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.EditItem("vault123", "item456", "{}"), Times.Once);
    }

    [Fact]
    public async Task ItemDelete_ParsesArgumentsAndCallsNativeClient()
    {
        var result = await _runner.RunAsync("op", ["item", "delete", "item456", "--vault", "vault123"]);

        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.DeleteItem("vault123", "item456"), Times.Once);
    }

    [Fact]
    public async Task InvalidClientId_ReconnectsAndRunsTheSameCallAgain()
    {
        // The SDK invalidates its client id when the desktop app's authorization goes away — the
        // user locks 1Password, or dismisses the connect prompt. Every later call then failed in
        // 0ms with "invalid client id" and stayed that way, because the runner remembered that it
        // had initialised. Unlocking did nothing; only disconnecting and reconnecting the vault
        // recovered, and everything pending was lost on exit.
        _mockClient.SetupSequence(c => c.ListItems("vault123"))
            .Throws(new VaultCliException("invalid client id"))
            .Returns(new JsonArray());

        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Exactly(2));
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidClientId_ThatSurvivesTheReconnectIsReportedRatherThanRetriedForever()
    {
        // Reconnecting asks the desktop app for authorization, which is a prompt. A user who is
        // deliberately leaving 1Password locked is asked once per attempt, not in a loop.
        _mockClient.Setup(c => c.ListItems("vault123")).Throws(new VaultCliException("invalid client id"));

        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid client id", result.StdErr);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Exactly(2));
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidClientId_OnAWriteReconnectsAndWritesAgain()
    {
        // The path that actually loses a user's work: a save that cannot reach the vault leaves the
        // change in memory only. An edit is idempotent against an item id that does not change, and
        // the call that threw never reached 1Password, so repeating it is safe.
        _mockClient.SetupSequence(c => c.EditItem("vault123", "item456", "{}"))
            .Throws(new VaultCliException("invalid client id"))
            .Returns(new JsonObject { ["id"] = "item456" });

        var result = await _runner.RunAsync(
            "op", ["item", "edit", "item456", "--vault", "vault123"], stdin: "{}");

        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.EditItem("vault123", "item456", "{}"), Times.Exactly(2));
    }

    [Fact]
    public async Task ARestartedDesktopAppGetsAFreshConnection()
    {
        // Desktop app integration runs over a named pipe. Quitting 1Password takes the pipe down,
        // and the client made before the restart still points at it — so every call fails instantly
        // with Win32 error 2, wearing the least informative message Windows owns. The SDK does not
        // rebuild the connection itself (1Password's own issue #266, open, no fix), so this does.
        _mockClient.SetupSequence(c => c.ListItems("vault123"))
            .Throws(new VaultCliException("The system cannot find the file specified."))
            .Returns(new JsonArray());

        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(0, result.ExitCode);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Exactly(2));
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Exactly(2));
    }

    [Fact]
    public async Task WhileTheDesktopAppIsDownEveryCallTriesToConnectAgain()
    {
        // The self-heal. A failed connect must not be remembered as "connected", or the app would
        // stay dead after 1Password came back — which is the shape of the original bug.
        _mockClient.Setup(c => c.Initialize(It.IsAny<string>()))
            .Throws(new InvalidOperationException("The system cannot find the file specified."));

        var first = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);
        Assert.Equal(1, first.ExitCode);

        // Says what to do about it, rather than handing the user a bare Win32 error.
        Assert.Contains("1Password does not appear to be running", first.StdErr);

        _mockClient.Reset();
        _mockClient.Setup(c => c.ListItems("vault123")).Returns(new JsonArray());

        var second = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(0, second.ExitCode);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RepeatedFailuresDoNotBecomeRepeatedReconnects()
    {
        // Calls reaching this runner are not paced by the sync queue: loading the store fans out
        // over items, and a probe walks the vault list. An activity log showed six failures inside
        // four seconds — and against a running-but-locked 1Password each reconnect is a prompt.
        _mockClient.Setup(c => c.ListItems("vault123"))
            .Throws(new VaultCliException("The system cannot find the file specified."));

        for (var i = 0; i < 6; i++)
        {
            var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);
            Assert.Equal(1, result.ExitCode);
        }

        // The one initial connect, plus exactly one rebuild for the burst.
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ADeclinedAuthorizationIsNotAnsweredByAskingAgain()
    {
        // Reconnecting raises the "allow RavensPort to connect" prompt, so answering a decline with
        // one is the app immediately re-asking a question the user has just said no to.
        _mockClient.Setup(c => c.ListItems("vault123")).Throws(new VaultCliException(
            "An error occurred when processing SDK request: Error { msg: Denied authorization for "
            + "SDK client, inner: None }"));

        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Denied authorization", result.StdErr);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Once);
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Once);
    }

    [Fact]
    public async Task AnOrdinaryFailureIsNotTreatedAsALostConnection()
    {
        // Reconnecting raises a prompt, so it has to be reserved for the one thing it fixes.
        _mockClient.Setup(c => c.ListItems("vault123")).Throws(new VaultCliException("vault not found"));

        var result = await _runner.RunAsync("op", ["item", "list", "--vault", "vault123"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("vault not found", result.StdErr);
        _mockClient.Verify(c => c.Initialize(It.IsAny<string>()), Times.Once);
        _mockClient.Verify(c => c.ListItems("vault123"), Times.Once);
    }
}
