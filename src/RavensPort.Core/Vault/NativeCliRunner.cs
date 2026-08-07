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

    /// <summary>
    /// When a reconnect was last attempted, and how long before another is allowed.
    ///
    /// A reconnect is not free. Against a running-but-locked 1Password it raises the authorization
    /// prompt, and the calls that reach this runner are not paced by the sync queue's backoff —
    /// loading the store fans out over items, and a probe walks the vault list. An activity log
    /// showed six failures inside four seconds, which without this would be six prompts.
    ///
    /// Only failing reconnects are throttled: a successful one clears the stamp, so the cooldown
    /// can never delay a connection that is actually working.
    /// </summary>
    private static DateTimeOffset _lastReconnectAttempt = DateTimeOffset.MinValue;

    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(10);

    public static void ResetInitialization()
    {
        lock (_initLock)
        {
            _initialized = false;

            // The user is asking, so nothing owed from earlier failures should stand in the way.
            _lastReconnectAttempt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Claims the right to rebuild the connection, or reports that one was tried too recently.
    /// </summary>
    private static bool TryBeginReconnect()
    {
        lock (_initLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastReconnectAttempt < ReconnectCooldown) return false;

            _lastReconnectAttempt = now;
            return true;
        }
    }

    /// <summary>
    /// Whether a failure is a stale connection worth rebuilding — see
    /// <see cref="VaultAuthorization.ClientIsDead"/>.
    ///
    /// A decline is excluded even if it arrives wearing the same message. Rebuilding the connection
    /// is itself a prompt, so doing it in answer to "no" is the app asking again immediately.
    /// </summary>
    private static bool ShouldReconnect(string? message) =>
        VaultAuthorization.ClientIsDead(message) && !VaultAuthorization.WasDeclined(message);

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
                // Left false on purpose — it already is, and it matters. Every later call retries
                // this, which is how the app heals itself once 1Password is running again: nothing
                // has to notice the restart, the next attempt simply connects.
                throw new VaultCliException(
                    $"Failed to connect to 1Password Desktop App for account '{accountName}': "
                    + Explain(ex.Message), ex);
            }
        }
    }

    /// <summary>
    /// Puts a sentence in front of the SDK's own wording when that wording is a bare Win32 error.
    ///
    /// "The system cannot find the file specified" is what a user is shown when 1Password is not
    /// running — the named pipe behind desktop app integration is the missing "file". Nothing about
    /// that tells them to start 1Password, and it reads like RavensPort has lost one of its own.
    /// </summary>
    private static string Explain(string message) =>
        VaultAuthorization.ClientIsDead(message)
            ? "1Password does not appear to be running, or its app integration is switched off. "
              + VaultLockGuidance.SignInSteps(VaultBackendKind.OnePassword)
              + $" ({message})"
            : message;

    /// <summary>
    /// Throws away the current SDK client and connects again. Throws if 1Password will not have it —
    /// still locked, or the authorization prompt declined again — which is the honest answer and
    /// leaves the change pending rather than reporting a save that did not happen.
    /// </summary>
    private void Reinitialize()
    {
        lock (_initLock)
        {
            _initialized = false;
        }

        EnsureInitialized();
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

        var stopwatch = Stopwatch.StartNew();

        try
        {
            EnsureInitialized();

            CliResult result;
            try
            {
                result = Dispatch(args, stdin);
            }
            catch (VaultCliException ex) when (ShouldReconnect(ex.Message))
            {
                // Rethrown rather than reconnected when one was tried moments ago: the answer would
                // be the same, and against a locked 1Password the asking is the cost. The failure
                // still reaches the caller, which leaves the change pending as it should.
                if (!TryBeginReconnect()) throw;

                // The connection died between calls — see VaultAuthorization.ClientIsDead. Rebuilding
                // it is the only way back, and it is safe to repeat the call: every operation here is
                // either a read or an idempotent write against an item id that does not change, and
                // the one that threw never reached 1Password at all.
                //
                // Once, not on a loop: a second failure is returned as the failure it is, so a user
                // who is deliberately leaving 1Password locked is asked at most once per attempt.
                _activityLog?.Log(
                    "VAULT the connection to 1Password was no longer usable — rebuilding it and "
                    + "trying the same operation again");

                Reinitialize();
                result = Dispatch(args, stdin);
            }

            // Here rather than after a successful connect, and the difference is not academic: a
            // rebuild can hand back a client that connects and still cannot talk to 1Password, and
            // clearing the cooldown on that put the burst straight back — one rebuild per call, the
            // prompts this exists to prevent. Reaching this line means the connection actually
            // carried an operation, which is the only evidence worth trusting.
            _lastReconnectAttempt = DateTimeOffset.MinValue;

            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (VaultCliException ex)
        {
            var result = new CliResult(1, "", ex.Message);
            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            var result = new CliResult(1, "", ex.ToString());
            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
    }

    /// <summary>
    /// Turns one <c>op</c> command line into the matching SDK call. Separate from
    /// <see cref="Execute"/> so a dead client can be reconnected and the identical call made again
    /// without duplicating the dispatch.
    /// </summary>
    private CliResult Dispatch(IReadOnlyList<string> args, string? stdin)
    {
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

        // A dead client can also arrive as a failed result rather than an exception, depending
        // on which call reported it. Raised so the one recovery path in Execute covers both.
        if (exitCode != 0 && ShouldReconnect(stderr)) throw new VaultCliException(stderr);

        return new CliResult(exitCode, stdout, stderr);
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
