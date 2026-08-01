using System.Diagnostics;
using System.Text;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Runs a password-manager CLI as a child process.
///
/// Two rules shape everything here.
///
/// **No secret ever goes in an argument.** A Windows process command line is readable by any
/// other process in the same session — no API call, no permission, just an enumeration. That is
/// strictly worse than the DPAPI file this replaced, so a design that put a client secret in
/// argv would be a downgrade wearing a password manager's clothes. Secrets travel on stdin (as
/// JSON item templates) or in the child's environment block, which is not enumerable the same way.
///
/// **No captured stdout is ever logged.** The output of any get/list is item contents. Logging
/// records the command, the exit code, and how long it took; on failure it adds the first line of
/// stderr, which these CLIs use for diagnostics rather than data.
/// </summary>
public sealed class CliRunner(ActivityLog activityLog) : ICliRunner
{
    /// <summary>Reads are quick. A hung one means something is badly wrong, not slow.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Writes get much longer: a write can sit waiting on a Windows Hello prompt the user has not
    /// noticed yet, and killing that would look like a random save failure.
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A browser sign-in is a human doing something in another window. Anything short of minutes
    /// here would cancel people mid-login.
    /// </summary>
    public static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(5);

    public async Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var startInfo = BuildStartInfo(exePath, args, env);

        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeout = timeout ?? ReadTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        using var process = Start(startInfo, exePath, args);

        // Start draining both pipes before writing stdin. A child that fills its stdout buffer
        // blocks on the write, and if this side is still busy sending stdin neither can move —
        // the classic redirected-process deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            }

            // Closed unconditionally: a CLI reading a template from stdin waits for EOF, so
            // skipping this on the no-stdin path would hang every such call.
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var result = new CliResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));

            Log(exePath, args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out rather than cancelled by the caller. Kill the whole tree: `op` shells out
            // to the desktop app and `pass-cli` can leave a helper behind, and an orphan holding
            // the vault open would make every later call fail for no visible reason.
            TryKill(process);

            activityLog.Log($"VAULT {Describe(exePath, args)} timed out after {effectiveTimeout.TotalSeconds:0}s");
            throw new VaultCliException(
                $"'{Path.GetFileName(exePath)}' did not respond within {effectiveTimeout.TotalSeconds:0} seconds. "
                + "If your password manager was waiting for you to unlock it, unlock it and try again.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public async Task<CliResult> RunStreamingAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string> onOutputLine,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var startInfo = BuildStartInfo(exePath, args, env);

        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeout = timeout ?? InteractiveTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        using var process = Start(startInfo, exePath, args);

        // Nothing to send, but the child still needs to see EOF rather than a pipe that might
        // one day produce input.
        process.StandardInput.Close();

        var gate = new object();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdout),
            PumpAsync(process.StandardError, stderr));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            // After exit, not before: the pumps end when the pipes close, which is what tells us
            // every line has actually been delivered to the callback.
            await pumps.ConfigureAwait(false);

            var result = new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());

            Log(exePath, args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);

            activityLog.Log($"VAULT {Describe(exePath, args)} timed out after {effectiveTimeout.TotalSeconds:0}s");
            throw new VaultCliException(
                $"'{Path.GetFileName(exePath)}' did not finish within {effectiveTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        async Task PumpAsync(StreamReader reader, StringBuilder sink)
        {
            while (await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false) is { } line)
            {
                sink.AppendLine(line);

                // Serialised so a caller's callback never has to be thread-safe: stdout and stderr
                // are two independent pumps, and this is the one place they meet.
                lock (gate) onOutputLine(line);
            }
        }
    }

    /// <summary>
    /// The one description of how a password-manager CLI is launched, shared by both paths so the
    /// argument-quoting and no-window rules cannot drift apart between them.
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(
        string exePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // UTF8Encoding(false) rather than Encoding.UTF8, which emits a byte-order mark. These
            // CLIs read a JSON template from stdin, and a BOM ahead of the opening brace is not
            // valid JSON — pass-cli rejects it with "expected value at line 1 column 1", which
            // reads like a bug in the template rather than three invisible bytes in front of it.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        // ArgumentList, not a joined Arguments string: .NET applies the exact quoting rules the
        // Windows command-line parser expects. Hand-rolled quoting is where argument-injection
        // bugs live, and a vault name or route prefix is user-controlled text.
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        if (env is not null)
        {
            foreach (var (key, value) in env) startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private Process Start(ProcessStartInfo startInfo, string exePath, IReadOnlyList<string> args)
    {
        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            return process;
        }
        catch (Exception ex)
        {
            process.Dispose();

            activityLog.LogError($"VAULT {Describe(exePath, args)} could not start", ex);
            throw new VaultCliException($"Could not run '{Path.GetFileName(exePath)}': {ex.Message}", ex);
        }
    }

    private void Log(string exePath, IReadOnlyList<string> args, CliResult result, long elapsedMs)
    {
        // Deliberately no stdout: for a get or a list that is the item contents.
        var message = $"VAULT {Describe(exePath, args)} -> exit {result.ExitCode} in {elapsedMs}ms";

        if (!result.Succeeded && result.FirstErrorLine() is { Length: > 0 } error)
        {
            message += $" ({error})";
        }

        activityLog.Log(message);
    }

    /// <summary>
    /// The command as it is safe to write down. Arguments are included because none of them may
    /// contain a secret — that is the invariant this class exists to hold, and a test asserts it.
    /// </summary>
    private static string Describe(string exePath, IReadOnlyList<string> args) =>
        $"{Path.GetFileNameWithoutExtension(exePath)} {string.Join(' ', args)}".TrimEnd();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or not ours to kill. Nothing useful left to do.
        }
    }
}

/// <summary>The CLI could not be run, or did not answer in time. Distinct from a non-zero exit,
/// which is a normal answer that callers interpret themselves.</summary>
public sealed class VaultCliException(string message, Exception? innerException = null)
    : Exception(message, innerException);
