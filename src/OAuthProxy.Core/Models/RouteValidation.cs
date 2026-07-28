namespace OAuthProxy.Core.Models;

/// <summary>
/// One place deciding whether a route's path prefix is usable, so the UI and the YARP config
/// builder cannot disagree about it.
///
/// The prefix is interpolated into an ASP.NET route template ("{prefix}/{**catch-all}"), which
/// gives some ordinary-looking characters structural meaning. A prefix containing '{' produces
/// a template RoutePatternFactory cannot parse; YARP then rejects the *entire* config update
/// and silently keeps the previous one, while the activity log has already announced the route
/// as active. One bad character therefore made every subsequent route edit appear to apply and
/// do nothing.
/// </summary>
public static class RouteValidation
{
    /// <summary>
    /// Structural characters in a route template, plus the ones that would terminate the path
    /// portion of a URL outright.
    /// </summary>
    private static readonly char[] ForbiddenCharacters = ['{', '}', '?', '#', '\\'];

    /// <summary>
    /// Validates a route path prefix. Returns null when acceptable, or a message suitable for
    /// showing in the UI footer.
    /// </summary>
    public static string? ValidatePathPrefix(string? pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return "Path prefix is required.";
        }

        var prefix = pathPrefix.Trim();

        if (!prefix.StartsWith('/'))
        {
            return "Path prefix must start with '/'.";
        }

        // A bare "/" builds the pattern "/{**catch-all}", which swallows every request to the
        // proxy and points all of it at one upstream with one credential attached — almost
        // certainly not what someone typing a single slash intended.
        if (prefix.TrimEnd('/').Length == 0)
        {
            return "'/' would capture every request to the proxy. Use a specific prefix such as '/gmail'.";
        }

        if (prefix.Split('/').Any(segment => segment == ".."))
        {
            return "Path prefix may not contain '..' segments.";
        }

        if (prefix.IndexOfAny(ForbiddenCharacters) >= 0)
        {
            return "Path prefix may not contain any of: { } ? # \\ — these have special meaning "
                   + "in a route template and would stop the route from loading.";
        }

        if (prefix.Any(char.IsControl) || prefix.Any(char.IsWhiteSpace))
        {
            return "Path prefix may not contain spaces or control characters.";
        }

        return null;
    }

    /// <summary>Convenience for callers that only need the yes/no answer.</summary>
    public static bool IsValidPathPrefix(string? pathPrefix) => ValidatePathPrefix(pathPrefix) is null;
}
