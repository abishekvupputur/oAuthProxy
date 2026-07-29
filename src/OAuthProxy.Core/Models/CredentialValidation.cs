namespace OAuthProxy.Core.Models;

/// <summary>
/// One place deciding whether a credential's own fields are usable, so the editor and the code
/// that puts the secret on the wire cannot disagree about it.
/// </summary>
public static class CredentialValidation
{
    /// <summary>
    /// Validates a typed-in API key. Returns null when acceptable, or a message suitable for
    /// showing in the UI footer.
    ///
    /// The control-character check is the load-bearing one. An OAuth access token arrives from a
    /// provider and is structurally constrained; an API key is whatever someone pasted, and a
    /// stray CR or LF in a value written into a header ends the header line and lets the rest be
    /// read as further headers — request splitting, aimed at the upstream. A key pasted out of a
    /// wrapped email or a text editor picks those up without anyone noticing.
    /// </summary>
    public static string? ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "API key is required.";
        }

        return apiKey.Any(char.IsControl)
            ? "API key may not contain control characters (including newlines and tabs). "
              + "Check for a line break picked up when the key was copied."
            : null;
    }

    /// <summary>
    /// Validates the optional test endpoint. Returns null when acceptable — including when it is
    /// blank, since the field is optional — or a message suitable for the UI footer.
    ///
    /// Held to the same transport rule as every other endpoint: the credential's secret is sent
    /// there, so plain http off-localhost would put it on the wire in cleartext.
    /// </summary>
    public static string? ValidateTestEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        return UrlValidation.ValidateEndpoint(url, "Test endpoint");
    }

    /// <summary>
    /// Validates everything about a credential that does not depend on which provider it is:
    /// its name, the secret it holds, where that secret goes, and the optional test endpoint.
    /// </summary>
    public static string? Validate(CredentialRecord credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Name))
        {
            return "Name is required.";
        }

        if (credential.Kind == CredentialKind.ApiKey &&
            ValidateApiKey(credential.ApiKey) is { } keyError)
        {
            return keyError;
        }

        return RouteValidation.ValidateCredentialInjection(
                   credential.DefaultPlacement, credential.DefaultParameterName, credential.DefaultValuePrefix)
               ?? ValidateTestEndpoint(credential.TestEndpoint);
    }
}
