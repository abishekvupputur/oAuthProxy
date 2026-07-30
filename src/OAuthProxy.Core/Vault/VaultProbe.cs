using System.Text.RegularExpressions;

namespace OAuthProxy.Core.Vault;

/// <summary>
/// Finds a password-manager CLI on this machine.
///
/// Deliberately does not shell out to <c>where.exe</c>: that is a process launch to answer a
/// question about the filesystem, it inherits whatever PATH quirks the shell has, and it runs on
/// the startup path where the app is already blocking on a gate check. Walking PATH in-process is
/// both faster and easier to test.
/// </summary>
public static partial class VaultProbe
{
    /// <summary>
    /// Escape hatch for an install this does not know about — a portable copy, a package manager
    /// that puts binaries somewhere new, or a build under test. Cheaper for a user than waiting
    /// for a release that adds their path to the list below.
    /// </summary>
    public const string OnePasswordPathVariable = "OAUTHPROXY_OP_PATH";
    public const string ProtonPassPathVariable = "OAUTHPROXY_PASSCLI_PATH";

    /// <summary>
    /// `op` gained the `item`/`vault` nouns and --format json in 2.0. Anything older cannot do
    /// what this app needs, and failing here is clearer than a confusing parse error later.
    /// </summary>
    public static readonly Version MinimumOnePasswordVersion = new(2, 0);

    public static string? FindOnePassword() => Find(
        OnePasswordPathVariable,
        "op.exe",
        [
            Path.Combine(Env("ProgramFiles"), "1Password CLI", "op.exe"),
            Path.Combine(Env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", "op.exe"),
            Path.Combine(Env("LOCALAPPDATA"), "Programs", "1Password CLI", "op.exe"),
        ]);

    public static string? FindProtonPass() => Find(
        ProtonPassPathVariable,
        "pass-cli.exe",
        [
            Path.Combine(Env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", "pass-cli.exe"),
            Path.Combine(Env("ProgramFiles"), "Proton", "Pass CLI", "pass-cli.exe"),
            Path.Combine(Env("USERPROFILE"), ".cargo", "bin", "pass-cli.exe"),
        ]);

    /// <summary>
    /// Env override, then PATH, then the places installers actually use.
    /// </summary>
    /// <param name="pathValue">
    /// The PATH to search, defaulting to this process's. A parameter so tests can point it at a
    /// directory of stubs without mutating the real one — which the rest of the suite, and the
    /// .NET runtime underneath it, are entitled to rely on.
    /// </param>
    public static string? Find(
        string environmentVariable,
        string exeName,
        IReadOnlyList<string> wellKnownPaths,
        string? pathValue = null)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } overridden)
        {
            // Honoured even when it does not exist: silently falling back would leave the user
            // staring at "not installed" while their override sat there being ignored.
            return File.Exists(overridden) ? overridden : null;
        }

        var searchPath = pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in searchPath
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), exeName);
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid path characters. Common enough on a well-used machine
                // that it must not take the whole probe down.
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        return wellKnownPaths.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Pulls a version out of whatever the CLI prints for --version. Both print a bare semver
    /// today, but "op 2.30.0 (build ...)" is the sort of thing that changes without warning, so
    /// this looks for the first version-shaped run of digits rather than parsing the whole line.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output ?? "");
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionPattern();
}
