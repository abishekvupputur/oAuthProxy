using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>Where a route puts the credential on the outgoing request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialPlacement
{
    /// <summary>A request header — "Authorization: Bearer &lt;token&gt;" by default.</summary>
    Header,

    /// <summary>A query-string parameter, e.g. "?access_token=&lt;token&gt;".</summary>
    Query,

    /// <summary>A field in the request body (JSON object or urlencoded form).</summary>
    Body,
}

/// <summary>
/// The resolved "how do I attach the token" decision for one route: placement, the header /
/// parameter / field name, and a prefix stuck in front of the token value.
///
/// Bearer-in-a-header is the default and covers nearly every OAuth upstream. The other shapes
/// exist because plenty of real APIs never adopted RFC 6750: some want "?access_token=", some
/// want a bespoke header ("X-Api-Key", "PRIVATE-TOKEN"), and some want the token as a field in
/// a JSON or form body.
/// </summary>
/// <param name="Placement">Header, query, or body.</param>
/// <param name="Name">Header name, query parameter name, or body field name.</param>
/// <param name="ValuePrefix">Text placed immediately before the token, e.g. "Bearer ".</param>
public sealed record CredentialInjection(CredentialPlacement Placement, string Name, string ValuePrefix)
{
    /// <summary>What every route gets unless the user says otherwise.</summary>
    public static CredentialInjection BearerHeader { get; } = new(CredentialPlacement.Header, "Authorization", "Bearer ");

    /// <summary>
    /// The name/prefix pair a placement starts with. The UI offers these when the user switches
    /// placement, so picking "Query" does something sensible without further typing.
    /// </summary>
    public static CredentialInjection DefaultFor(CredentialPlacement placement) => placement switch
    {
        CredentialPlacement.Query => new CredentialInjection(placement, "access_token", ""),
        CredentialPlacement.Body => new CredentialInjection(placement, "access_token", ""),
        _ => BearerHeader,
    };

    /// <summary>The value actually sent: prefix + token.</summary>
    public string FormatValue(string token) => ValuePrefix + token;

    /// <summary>Short one-line description for grids and tooltips.</summary>
    public string Describe() => Placement switch
    {
        CredentialPlacement.Query => $"query ?{Name}={ValuePrefix}<token>",
        CredentialPlacement.Body => $"body field \"{Name}\": \"{ValuePrefix}<token>\"",
        _ => $"header {Name}: {ValuePrefix}<token>",
    };
}
