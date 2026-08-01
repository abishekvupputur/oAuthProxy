using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>One call the provider made.</summary>
/// <param name="Env">
/// Environment variable <em>names</em> only. The values are tokens, and a test double that
/// recorded them would be a place secrets accumulate in test output and CI logs.
/// </param>
public sealed record CliInvocation(
    string ExePath,
    IReadOnlyList<string> Args,
    string? Stdin,
    IReadOnlyList<string> Env)
{
    public string Command => string.Join(' ', Args);

    public bool Matches(params string[] leadingArgs) =>
        Args.Count >= leadingArgs.Length && Args.Take(leadingArgs.Length).SequenceEqual(leadingArgs);
}

/// <summary>
/// An <see cref="ICliRunner"/> that answers from a script instead of launching anything, and
/// records every call.
///
/// This is how the providers get tested at all: the alternative is a real 1Password account, a
/// real unlock prompt, and a test suite that cannot run in CI. It is also the only place the
/// "no secret ever reaches an argument" rule can actually be checked, because the check needs to
/// see the arguments — which a real process would have already published to the process table.
/// </summary>
public sealed class FakeCliRunner : ICliRunner
{
    private readonly List<Func<IReadOnlyList<string>, CliResult?>> _handlers = [];
    private readonly List<CliInvocation> _invocations = [];

    public IReadOnlyList<CliInvocation> Invocations => _invocations;

    /// <summary>Every argument of every call, for the secret-leak assertions.</summary>
    public IEnumerable<string> AllArguments => _invocations.SelectMany(i => i.Args);

    public IEnumerable<CliInvocation> CallsMatching(params string[] leadingArgs) =>
        _invocations.Where(i => i.Matches(leadingArgs));

    /// <summary>Answers calls whose leading arguments match, with a fixed result.</summary>
    public FakeCliRunner Respond(string[] leadingArgs, string stdout = "", int exitCode = 0, string stderr = "")
    {
        _handlers.Add(args =>
            args.Count >= leadingArgs.Length && args.Take(leadingArgs.Length).SequenceEqual(leadingArgs)
                ? new CliResult(exitCode, stdout, stderr)
                : null);

        return this;
    }

    /// <summary>Answers with a delegate, for a response that depends on the arguments or on state.</summary>
    public FakeCliRunner Respond(Func<IReadOnlyList<string>, CliResult?> handler)
    {
        _handlers.Add(handler);
        return this;
    }

    public Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        _invocations.Add(new CliInvocation(exePath, [.. args], stdin, [.. env?.Keys ?? []]));

        foreach (var handler in _handlers)
        {
            if (handler(args) is { } result) return Task.FromResult(result);
        }

        // Loud rather than a default empty success: an unscripted call means the provider is doing
        // something the test did not anticipate, and silently succeeding would hide it.
        throw new InvalidOperationException(
            $"No scripted response for: {string.Join(' ', args)}");
    }

    /// <summary>
    /// Replays the scripted stdout a line at a time, so a caller that parses output as it streams
    /// is exercised the same way the real runner would exercise it.
    /// </summary>
    public async Task<CliResult> RunStreamingAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string> onOutputLine,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(exePath, args, stdin: null, env, timeout, ct);

        foreach (var line in (result.StdOut + result.StdErr)
                 .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            onOutputLine(line.TrimEnd('\r'));
        }

        return result;
    }
}
