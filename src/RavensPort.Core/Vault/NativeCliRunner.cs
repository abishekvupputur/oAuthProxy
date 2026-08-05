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

    /// <summary>
    /// One call into the DLL at a time. <c>InitializeOP</c> leaves a client in a package-level
    /// variable on the Go side, so every exported function reads shared state — and the callers
    /// here do fan out: <c>FindAdoptableVaultsAsync</c> lists every candidate vault concurrently.
    /// That was harmless only because the calls used to run inline on one thread; moving them to
    /// the pool would make the concurrency real, so this puts back the serialisation that was
    /// previously an accident of the threading.
    /// </summary>
    private static readonly SemaphoreSlim _callGate = new(1, 1);

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

    /// <summary>
    /// Every call here is a blocking P/Invoke into onepassword.dll, so unlike <see cref="CliRunner"/>
    /// — which awaits real process I/O — there is nothing in the body that yields. Returning
    /// <c>Task.FromResult</c> from a synchronous body meant the work ran inline on whichever thread
    /// awaited it, and for anything driven by a button that thread is the WPF dispatcher: Disconnect
    /// flushes pending changes before it asks for confirmation, so the window froze for as long as
    /// the 1Password SDK took to write every changed item. Proton Pass never showed it because its
    /// runner yields at the first await.
    ///
    /// <c>Task.Run</c> rather than making the DLL calls asynchronous, because they cannot be: the
    /// exported functions are synchronous and there is no overlapped variant to await. The blocking
    /// has to happen on some thread, and a pool thread is the right one.
    ///
    /// <paramref name="timeout"/> is still not honoured. A P/Invoke in progress cannot be abandoned,
    /// and returning early while the DLL keeps writing would be worse than waiting — the caller
    /// would report a failure for a save that is about to succeed. The SDK's own timeouts apply.
    /// </summary>
    public async Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await _callGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ConfigureAwait(false) all the way out, so the continuation does not queue back onto
            // the dispatcher — that would put the JSON parsing this returns into back on the UI
            // thread for every vault read.
            return await Task.Run(() => Execute(args, stdin), ct).ConfigureAwait(false);
        }
        finally
        {
            _callGate.Release();
        }
    }

    private CliResult Execute(IReadOnlyList<string> args, string? stdin)
    {
        // Answered before EnsureInitialized, and that ordering is the whole point.
        //
        // InitializeOP connects to the 1Password desktop app, which is what raises the unlock
        // prompt — so anything that runs it is an interruption to the user, whether or not it
        // needed the connection. --version needs nothing: the answer below is a constant, because
        // there is no CLI here to ask. Initialising first meant merely *looking* for 1Password
        // demanded that the user unlock it, which is exactly the prompt the setup page's discovery
        // probe exists to avoid — see VaultProbeDepth.
        if (args.Contains("--version"))
        {
            return new CliResult(0, "0.4.1", "");
        }

        try
        {
            EnsureInitialized();

            var stopwatch = Stopwatch.StartNew();

            var cmd = string.Join(" ", args);
            string stdout = "";
            string stderr = "";
            int exitCode = 0;

            if (args.Count >= 2 && args[0] == "vault" && args[1] == "list")
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
            return result;
        }
        catch (VaultCliException ex)
        {
            var result = new CliResult(1, "", ex.Message);
            Log(args, result, 0);
            return result;
        }
        catch (Exception ex)
        {
            var result = new CliResult(1, "", ex.ToString());
            Log(args, result, 0);
            return result;
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
