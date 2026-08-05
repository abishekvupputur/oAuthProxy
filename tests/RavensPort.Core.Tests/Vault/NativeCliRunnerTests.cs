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
}
