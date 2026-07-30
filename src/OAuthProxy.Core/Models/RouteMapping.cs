using System.Text.Json.Serialization;

namespace OAuthProxy.Core.Models;

public sealed class RouteMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string PathPrefix { get; set; }
    public Guid UpstreamId { get; set; }
    public bool StripPrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The secret a caller must present to use this route, and nothing else. It does not open any
    /// other route and it does not open a funnel — see <see cref="ProxyKey"/> for why the proxy
    /// no longer has one key for everything.
    /// </summary>
    public ProxyKey Key { get; set; } = new();

    /// <summary>
    /// Every credential this route attaches, each with its own placement. Empty is legitimate
    /// and means "forward without attaching anything" — the route is then an ordinary reverse
    /// proxy hop. See <see cref="RouteCredential"/>.
    /// </summary>
    public List<RouteCredential> Credentials { get; set; } = [];

    // ---- Superseded single-credential fields -------------------------------------------------
    //
    // A route used to carry exactly one credential in exactly one place, spelled out in these
    // four properties. They are still deserialized so a store written by an older build keeps
    // forwarding identically, and they are nullable so that once a store has been normalized
    // (see Normalize) they disappear from the file rather than sitting there contradicting the
    // list. Nothing in the proxy reads them directly — everything goes through
    // <see cref="EffectiveCredentials"/>.

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CredentialId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CredentialPlacement? CredentialPlacement { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialParameterName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialValuePrefix { get; set; }

    /// <summary>
    /// The credentials this route actually attaches: the list when it has entries, otherwise
    /// whatever the superseded single-credential fields describe.
    ///
    /// A legacy route whose <see cref="CredentialId"/> is absent or empty resolves to no
    /// credentials at all, which is the same answer the new model gives for an empty list.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<RouteCredential> EffectiveCredentials
    {
        get
        {
            if (Credentials.Count > 0) return Credentials;
            if (CredentialId is not { } id || id == Guid.Empty) return [];

            var placement = CredentialPlacement ?? Models.CredentialPlacement.Header;
            var defaults = CredentialInjection.DefaultFor(placement);

            return
            [
                new RouteCredential
                {
                    CredentialId = id,
                    Placement = placement,
                    ParameterName = string.IsNullOrWhiteSpace(CredentialParameterName)
                        ? defaults.Name
                        : CredentialParameterName,
                    ValuePrefix = CredentialValuePrefix ?? defaults.ValuePrefix,
                },
            ];
        }
    }

    /// <summary>
    /// Folds the superseded single-credential fields into <see cref="Credentials"/> and clears
    /// them, so the next save writes one representation instead of two. Idempotent; run once per
    /// route as the store is loaded.
    /// </summary>
    public RouteMapping Normalize()
    {
        var effective = EffectiveCredentials;
        if (!ReferenceEquals(effective, Credentials))
        {
            Credentials = [.. effective];
        }

        CredentialId = null;
        CredentialPlacement = null;
        CredentialParameterName = null;
        CredentialValuePrefix = null;

        return this;
    }

    /// <summary>Short description of everything this route attaches, for grids and logs.</summary>
    public string DescribeCredentials(Func<Guid, string?>? nameOf = null)
    {
        var credentials = EffectiveCredentials;
        if (credentials.Count == 0) return "no credential — forwarded unauthenticated";

        return string.Join(", ", credentials.Select(c =>
        {
            var name = nameOf?.Invoke(c.CredentialId);
            var described = c.ToCredentialInjection().Describe();
            return name is null ? described : $"{name} as {described}";
        }));
    }
}
