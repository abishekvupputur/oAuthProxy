using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// What kind of secret a credential holds, and therefore how the proxy obtains a usable value
/// for it on each request.
///
/// The default is <see cref="OAuth2"/> so a store written before this existed deserializes into
/// exactly what every credential in it already was.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialKind
{
    /// <summary>An OAuth2 grant: a token obtained by a browser flow and refreshed in the background.</summary>
    OAuth2,

    /// <summary>
    /// A static API key typed in by the user. No authorization flow, no expiry, nothing to
    /// refresh — plenty of APIs never offered OAuth at all, and routing to them previously
    /// meant either leaving the route unauthenticated or inventing a fake OAuth credential.
    /// </summary>
    ApiKey,
}
