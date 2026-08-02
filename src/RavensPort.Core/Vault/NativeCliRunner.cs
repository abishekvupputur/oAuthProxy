using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private readonly ActivityLog? _activityLog;
    private readonly IOnePasswordNativeClient _client;

    public NativeCliRunner(ActivityLog? activityLog = null, IOnePasswordNativeClient? client = null)
    {
        _activityLog = activityLog;
        _client = client ?? new OnePasswordNativeClientWrapper();
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
                _client.Initialize(accountName);
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
            
            var stopwatch = Stopwatch.StartNew();

            var cmd = string.Join(" ", args);
            string stdout = "";
            string stderr = "";
            int exitCode = 0;

            if (args.Contains("--version"))
            {
                stdout = "0.4.1";
            }
            else if (args.Count >= 2 && args[0] == "vault" && args[1] == "list")
            {
                var vaults = _client.ListVaults();
                stdout = vaults?.ToJsonString() ?? "[]";
            }
            else if (args.Count >= 3 && args[0] == "vault" && args[1] == "create")
            {
                string name = args[2];
                string desc = "";
                var descIdx = args.ToList().IndexOf("--description");
                if (descIdx != -1 && args.Count > descIdx + 1) desc = args[descIdx + 1];

                var vault = _client.CreateVault(name, desc);
                stdout = vault?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 2 && args[0] == "item" && args[1] == "list")
            {
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";
                var items = _client.ListItems(vaultId);
                stdout = items?.ToJsonString() ?? "[]";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "get")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                var item = _client.GetItem(vaultId, itemId);
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

                var item = _client.CreateItem(vaultId, stdin ?? "");
                stdout = item?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "edit")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                var item = _client.EditItem(vaultId, itemId, stdin ?? "");
                stdout = item?.ToJsonString() ?? "{}";
            }
            else if (args.Count >= 3 && args[0] == "item" && args[1] == "delete")
            {
                string itemId = args[2];
                var vaultIdx = args.ToList().IndexOf("--vault");
                string vaultId = vaultIdx != -1 ? args[vaultIdx + 1] : "";

                _client.DeleteItem(vaultId, itemId);
            }
            else
            {
                exitCode = 1;
                stderr = "Command not supported by NativeCliRunner: " + cmd;
            }

            var result = new CliResult(exitCode, stdout, stderr);
            Log(args, result, stopwatch.ElapsedMilliseconds);
            return Task.FromResult(result);
        }
        catch (VaultCliException ex)
        {
            var result = new CliResult(1, "", ex.Message);
            Log(args, result, 0);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            var result = new CliResult(1, "", ex.ToString());
            Log(args, result, 0);
            return Task.FromResult(result);
        }
    }

    private void Log(IReadOnlyList<string> args, CliResult result, long elapsedMs)
    {
        var message = $"VAULT op {string.Join(' ', args).TrimEnd()} -> exit {result.ExitCode} in {elapsedMs}ms";

        if (!result.Succeeded && result.FirstErrorLine() is { Length: > 0 } error)
        {
            message += $" ({error})";
        }

        _activityLog?.Log(message);
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
