namespace RavensPort.Core.Vault;

public readonly record struct CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>First line of stderr, trimmed and capped — safe to show a user and to log.</summary>
    public string FirstErrorLine()
    {
        var line = StdErr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>
/// Runs a password-manager CLI. An interface so providers can be tested without the real binary,
/// and so the rules that keep secrets off the command line live in exactly one implementation.
/// </summary>
public interface ICliRunner
{
    /// <param name="args">
    /// Passed through <c>ProcessStartInfo.ArgumentList</c>, never a joined string.
    /// **Must never contain a secret** — see <see cref="CliRunner"/> for why.
    /// </param>
    /// <param name="stdin">Where secrets go: piped, so they never appear in the process table.</param>
    /// <param name="env">Extra environment variables, for service-account and access tokens.</param>
    Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same rules as <see cref="RunAsync"/>, but hands each line over as it arrives instead of only
    /// at exit.
    ///
    /// It exists for exactly one call: <c>pass-cli login</c> prints the URL the user must open and
    /// then keeps running until they have opened it. <see cref="RunAsync"/> reads both pipes to
    /// EOF, so it could not surface that URL until the process it is waiting on had already
    /// finished waiting for the URL — a deadlock made of good intentions.
    /// </summary>
    /// <param name="onOutputLine">
    /// Called once per line of stdout and stderr, serialised so the callback never runs
    /// concurrently with itself. Lines are not logged by the runner — the caller decides, because
    /// only the caller knows whether this command's output is safe to write down.
    /// </param>
    Task<CliResult> RunStreamingAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string> onOutputLine,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
