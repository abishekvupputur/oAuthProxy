namespace OAuthProxy.Core.Models;

/// <summary>
/// One credential attached to one route, together with where it goes on the forwarded request.
///
/// A route holds a list of these. Zero means the route forwards without attaching anything —
/// a plain reverse proxy hop for an upstream that needs no token. One is the ordinary case.
/// Several is what real upstreams keep asking for: an OAuth token in the Authorization header
/// *and* a project key in a second header, two query parameters, or a header plus a field in
/// the JSON body. The entries are independent, so the same credential can appear twice in
/// different slots and two different credentials can appear side by side.
/// </summary>
public sealed class RouteCredential
{
    public Guid CredentialId { get; set; }

    public CredentialPlacement Placement { get; set; } = CredentialPlacement.Header;

    /// <summary>Header name, query parameter name, or body field name, per <see cref="Placement"/>.</summary>
    public string ParameterName { get; set; } = CredentialInjection.BearerHeader.Name;

    /// <summary>Text placed immediately before the token, e.g. "Bearer " or "token ". May be empty.</summary>
    public string ValuePrefix { get; set; } = CredentialInjection.BearerHeader.ValuePrefix;

    public CredentialInjection ToCredentialInjection() => new(Placement, ParameterName, ValuePrefix);

    /// <summary>A credential in the default shape for a placement, e.g. "?access_token=" for query.</summary>
    public static RouteCredential For(Guid credentialId, CredentialPlacement placement)
    {
        var defaults = CredentialInjection.DefaultFor(placement);

        return new RouteCredential
        {
            CredentialId = credentialId,
            Placement = placement,
            ParameterName = defaults.Name,
            ValuePrefix = defaults.ValuePrefix,
        };
    }

    public RouteCredential Clone() => new()
    {
        CredentialId = CredentialId,
        Placement = Placement,
        ParameterName = ParameterName,
        ValuePrefix = ValuePrefix,
    };

    /// <summary>
    /// Identity of the slot on the outgoing request this entry writes to. Two entries sharing a
    /// slot would overwrite each other, so this is what duplicate detection compares.
    ///
    /// Header names are compared case-insensitively because HTTP treats them that way — an
    /// upstream cannot receive both "Authorization" and "authorization". Query parameter and
    /// body field names are case-sensitive, and plenty of APIs do distinguish them.
    /// </summary>
    public string Slot => SlotOf(Placement, ParameterName);

    public static string SlotOf(CredentialPlacement placement, string? name)
    {
        var trimmed = (name ?? "").Trim();

        return placement == CredentialPlacement.Header
            ? $"header:{trimmed.ToLowerInvariant()}"
            : $"{placement.ToString().ToLowerInvariant()}:{trimmed}";
    }
}
