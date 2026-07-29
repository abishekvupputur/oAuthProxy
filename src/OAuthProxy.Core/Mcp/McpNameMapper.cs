using System.Text;

namespace OAuthProxy.Core.Mcp;

/// <summary>
/// Translation between what a funnel's client sees and what the upstream actually called things.
///
/// Every name a source contributes is prefixed with that source's alias, unconditionally — not
/// only when two sources collide. Conditional prefixing was the alternative and it is a trap: a
/// tool would keep its bare name until some unrelated source was added, then silently rename
/// itself, breaking every agent prompt that had hardcoded it. Stable names beat pretty ones.
///
/// This is also the routing table. A tools/call arrives carrying only the exposed name, so the
/// prefix is the sole evidence of which upstream it belongs to.
/// </summary>
public static class McpNameMapper
{
    /// <summary>
    /// Double underscore, because a single one is common inside real tool names ("create_issue")
    /// while "__" is not. Splitting on the *first* occurrence keeps upstream names containing
    /// "__" intact — the alias is guaranteed not to contain it (see McpFunnelValidation).
    /// </summary>
    public const string Separator = "__";

    /// <summary>
    /// MCP caps a tool name at 128 characters. Exceeding it does not fail loudly — some clients
    /// simply drop the tool — so the prefixed form is truncated to fit instead.
    /// </summary>
    public const int MaxNameLength = 128;

    public const string ResourceUriScheme = "funnel";

    /// <summary>
    /// Prefixes a tool or prompt name. Truncation, when it happens, eats the *upstream* portion
    /// and never the alias: the alias is what routes the call back, so losing a character of it
    /// would send the request to the wrong source (or nowhere).
    /// </summary>
    public static string Encode(string alias, string upstreamName)
    {
        var prefix = alias + Separator;
        var budget = MaxNameLength - prefix.Length;

        if (budget <= 0)
        {
            // Only reachable if the alias itself is near the cap, which validation prevents.
            return prefix[..MaxNameLength];
        }

        return upstreamName.Length <= budget
            ? prefix + upstreamName
            : prefix + upstreamName[..budget];
    }

    /// <summary>
    /// Splits an exposed name back into alias and upstream name. Returns false for a name with no
    /// separator, which means a client invented it or is replaying one from a different funnel —
    /// either way there is no source to send it to.
    /// </summary>
    public static bool TryDecode(string exposedName, out string alias, out string upstreamName)
    {
        alias = "";
        upstreamName = "";

        if (string.IsNullOrEmpty(exposedName)) return false;

        var index = exposedName.IndexOf(Separator, StringComparison.Ordinal);
        if (index <= 0) return false;

        alias = exposedName[..index];
        upstreamName = exposedName[(index + Separator.Length)..];

        return upstreamName.Length > 0;
    }

    /// <summary>
    /// Whether <paramref name="upstreamName"/> survived <see cref="Encode"/> intact. The call
    /// path needs this: a truncated name cannot be sent upstream as-is, and guessing which of
    /// several tools sharing a 120-character prefix was meant would be worse than refusing.
    /// </summary>
    public static bool IsTruncated(string alias, string upstreamName) =>
        alias.Length + Separator.Length + upstreamName.Length > MaxNameLength;

    /// <summary>
    /// Rewrites a resource URI so it carries its source with it: "funnel://{alias}/{escaped}".
    ///
    /// A resource is addressed by URI rather than by name, so the alias cannot simply be
    /// prepended to a label — the whole original URI is escaped into a single path segment.
    /// Escaping (rather than embedding "https://x/y" verbatim) is what keeps a client's URI
    /// normalization from collapsing the inner scheme's slashes before it sends the read back.
    /// </summary>
    public static string EncodeResourceUri(string alias, string upstreamUri) =>
        $"{ResourceUriScheme}://{alias}/{Uri.EscapeDataString(upstreamUri)}";

    /// <summary>
    /// Same idea for a resource *template*, where "{placeholder}" spans must survive unescaped or
    /// the client cannot expand them. Only the literal text between placeholders is escaped.
    /// </summary>
    public static string EncodeResourceUriTemplate(string alias, string upstreamTemplate)
    {
        var builder = new StringBuilder($"{ResourceUriScheme}://{alias}/");
        var index = 0;

        while (index < upstreamTemplate.Length)
        {
            var open = upstreamTemplate.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(Uri.EscapeDataString(upstreamTemplate[index..]));
                break;
            }

            var close = upstreamTemplate.IndexOf('}', open);
            if (close < 0)
            {
                // Unbalanced brace: not a placeholder, so treat the remainder as literal.
                builder.Append(Uri.EscapeDataString(upstreamTemplate[index..]));
                break;
            }

            builder.Append(Uri.EscapeDataString(upstreamTemplate[index..open]));
            builder.Append(upstreamTemplate[open..(close + 1)]);
            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reverses <see cref="EncodeResourceUri"/> and <see cref="EncodeResourceUriTemplate"/>.
    /// Parsed by hand rather than through <see cref="Uri"/>, which would normalize away the very
    /// escaping this depends on.
    /// </summary>
    public static bool TryDecodeResourceUri(string exposedUri, out string alias, out string upstreamUri)
    {
        alias = "";
        upstreamUri = "";

        if (string.IsNullOrEmpty(exposedUri)) return false;

        const string marker = ResourceUriScheme + "://";
        if (!exposedUri.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = exposedUri[marker.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;

        alias = rest[..slash];

        try
        {
            upstreamUri = Uri.UnescapeDataString(rest[(slash + 1)..]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return upstreamUri.Length > 0;
    }
}
