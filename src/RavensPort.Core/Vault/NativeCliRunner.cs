using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

public sealed class NativeCliRunner : ICliRunner
{

    private static bool _initialized = false;
    private static readonly object _initLock = new();

    public NativeCliRunner()
    {
    }

    public static void ResetInitialization()
    {
        lock (_initLock)
        {
            _initialized = false;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            var accountName = LocalSettings.Current.OnePasswordAccountName;
            if (string.IsNullOrWhiteSpace(accountName))
            {
                accountName = Environment.GetEnvironmentVariable("OP_ACCOUNT");
            }
            if (string.IsNullOrWhiteSpace(accountName))
            {
                throw new VaultCliException("1Password SDK requires your Account Name (as shown in the top-left sidebar of the 1Password app) to connect. Please configure it in the Setup page.");
            }

            try 
            {
                OnePasswordNativeClient.Initialize(accountName);
                _initialized = true;
            }
            catch (Exception ex)
            {
                throw new VaultCliException($"Failed to connect to 1Password Desktop App for account '{accountName}': {ex.Message}", ex);
            }
        }
    }

    public Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        try
        {
            EnsureInitialized();

            var cmd = string.Join(" ", args);
            string stdout = "";
            string stderr = "";
            int exitCode = 0;

            if (args.Contains("--version"))
            {
                stdout = "2.99.0";
            }
            else if (args.Count >= 2 && args[0] == "vault" && args[1] == "list")
            {
                var vaults = OnePasswordNativeClient.ListVaults();
                stdout = vaults?.ToJsonString() ?? "[]";
            }
            else if (args.Count >= 3 && args[0] == "vault" && args[1] == "create")
            {
                string name = args[2];
                string desc = "";
                var descIdx = args.ToList().IndexOf("--description");
                if (descIdx != -1 && args.Count > descIdx + 1) desc = args[descIdx + 1];

                var vault = OnePasswordNativeClient.CreateVault(name, desc);
                stdout = vault?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 2 && args[0] == "item" && args[1] == "list")
            {
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";
                var items = OnePasswordNativeClient.ListItems(vaultId);
                stdout = items?.ToJsonString() ?? "[]";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "get")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                var item = OnePasswordNativeClient.GetItem(vaultId, itemId);
                if (item == null)
                {
                    exitCode = 1;
                    stderr = "isn't an item";
                }
                else
                {
                    stdout = item.ToJsonString();
                }
            }
            else if (args.Count >= 2 && args[0] == "item" && args[1] == "create")
            {
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                var item = OnePasswordNativeClient.CreateItem(vaultId, stdin ?? "");
                stdout = item?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "edit")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                var item = OnePasswordNativeClient.EditItem(vaultId, itemId, stdin ?? "");
                stdout = item?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "delete")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                OnePasswordNativeClient.DeleteItem(vaultId, itemId);
            }
            else
            {
                exitCode = 1;
                stderr = "Command not supported by NativeCliRunner: " + cmd;
            }

            return Task.FromResult(new CliResult(exitCode, stdout, stderr));
        }
        catch (VaultCliException ex)
        {
            return Task.FromResult(new CliResult(1, "", ex.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CliResult(1, "", ex.ToString()));
        }
    }

    public Task<CliResult> RunStreamingAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string> onOutputLine,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        throw new NotImplementedException("Streaming not needed for NativeCliRunner");
    }
}
