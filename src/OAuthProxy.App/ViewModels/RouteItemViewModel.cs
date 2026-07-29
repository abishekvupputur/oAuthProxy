using CommunityToolkit.Mvvm.ComponentModel;
using OAuthProxy.Core.Models;

namespace OAuthProxy.App.ViewModels;

/// <summary>
/// A route row with its upstream and credential resolved to names, so the grid can show the
/// whole hop (local endpoint -> upstream, and which credential's token gets attached) instead
/// of raw ids. Rebuilt by RoutesViewModel.Reload() rather than mutated in place.
/// </summary>
public sealed class RouteItemViewModel : ObservableObject
{
    private const string Missing = "(missing)";

    /// <summary>Raised when a field is edited, so the owner can persist, rebuild, and report.</summary>
    private readonly Action<RouteItemViewModel, string>? _onChanged;

    /// <summary>Raised when an edit was rejected, so the owner can show why.</summary>
    private readonly Action<string>? _onInvalid;

    public RouteItemViewModel(
        RouteMapping route,
        UpstreamRecord? upstream,
        CredentialRecord? credential,
        int listenPort,
        Action<RouteItemViewModel, string>? onChanged = null,
        Action<string>? onInvalid = null)
    {
        Route = route;
        _onChanged = onChanged;
        _onInvalid = onInvalid;

        UpstreamName = upstream?.Name ?? Missing;
        UpstreamBaseUrl = upstream?.BaseUrl ?? Missing;
        CredentialName = credential?.Name ?? Missing;
        LocalUrl = $"http://127.0.0.1:{listenPort}{route.PathPrefix}";

        // A route whose upstream is gone is silently dropped from the proxy config, so flag it
        // rather than let it look active. A missing credential still routes, but unauthenticated.
        IsBroken = upstream is null;
        IsMissingCredential = credential is null;
    }

    public RouteMapping Route { get; }

    public string PathPrefix => Route.PathPrefix;

    /// <summary>
    /// Settable so the grid's checkbox can turn a route off without deleting it. Writes
    /// straight through to the underlying record, then notifies the owner to save + rebuild.
    /// </summary>
    public bool Enabled
    {
        get => Route.Enabled;
        set
        {
            if (Route.Enabled == value) return;
            Route.Enabled = value;
            OnPropertyChanged();
            _onChanged?.Invoke(this, value
                ? $"Route '{PathPrefix}' enabled."
                : $"Route '{PathPrefix}' disabled — requests to it will no longer be proxied.");
        }
    }

    public bool StripPrefix
    {
        get => Route.StripPrefix;
        set
        {
            if (Route.StripPrefix == value) return;
            Route.StripPrefix = value;
            OnPropertyChanged();
            _onChanged?.Invoke(this, $"Route '{PathPrefix}' updated — prefix is now "
                                     + (value ? "removed" : "kept") + " when forwarding.");
        }
    }

    public IReadOnlyList<CredentialPlacement> Placements { get; } = RoutesViewModel.AllPlacements;

    /// <summary>
    /// Header / query / body. Switching carries the name and prefix over to the new placement's
    /// defaults *only* when they were still at the old placement's defaults, so picking "Query"
    /// on an untouched route gives "?access_token=" rather than "?Authorization=Bearer ".
    /// </summary>
    public CredentialPlacement CredentialPlacement
    {
        get => Route.CredentialPlacement;
        set
        {
            if (Route.CredentialPlacement == value) return;

            var previous = CredentialInjection.DefaultFor(Route.CredentialPlacement);
            var replacement = CredentialInjection.DefaultFor(value);

            if (Route.CredentialParameterName == previous.Name) Route.CredentialParameterName = replacement.Name;
            if (Route.CredentialValuePrefix == previous.ValuePrefix) Route.CredentialValuePrefix = replacement.ValuePrefix;

            Route.CredentialPlacement = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CredentialParameterName));
            OnPropertyChanged(nameof(CredentialValuePrefix));
            OnPropertyChanged(nameof(ParameterNameLabel));
            NotifyInjectionChanged();
        }
    }

    public string CredentialParameterName
    {
        get => Route.CredentialParameterName;
        set => SetInjectionField(
            value,
            Route.CredentialParameterName,
            candidate => RouteValidation.ValidateCredentialInjection(CredentialPlacement, candidate, CredentialValuePrefix),
            candidate => Route.CredentialParameterName = candidate.Trim(),
            nameof(CredentialParameterName));
    }

    public string CredentialValuePrefix
    {
        get => Route.CredentialValuePrefix;
        set => SetInjectionField(
            value,
            Route.CredentialValuePrefix,
            candidate => RouteValidation.ValidateCredentialInjection(CredentialPlacement, CredentialParameterName, candidate),
            candidate => Route.CredentialValuePrefix = candidate,
            nameof(CredentialValuePrefix));
    }

    /// <summary>Label for the name box — it means something different per placement.</summary>
    public string ParameterNameLabel => CredentialPlacement switch
    {
        Core.Models.CredentialPlacement.Query => "Query parameter name",
        Core.Models.CredentialPlacement.Body => "Body field name",
        _ => "Header name",
    };

    /// <summary>How the credential is attached, e.g. "header Authorization: Bearer &lt;token&gt;".</summary>
    public string InjectionSummary => Route.ToCredentialInjection().Describe();

    public string LocalUrl { get; }
    public string UpstreamName { get; }
    public string UpstreamBaseUrl { get; }
    public string CredentialName { get; }

    public bool IsBroken { get; }
    public bool IsMissingCredential { get; }

    /// <summary>Short human summary of what this route does, e.g. for tooltips.</summary>
    public string Summary =>
        $"{LocalUrl}  →  {UpstreamBaseUrl}"
        + (StripPrefix ? $"   (prefix '{PathPrefix}' removed before forwarding)" : "   (prefix kept)")
        + $"\nToken: {CredentialName}, sent as {InjectionSummary}";

    /// <summary>
    /// Rejected values are never written to the record: an unusable header name or a prefix
    /// carrying a newline makes ProxyConfigBuilder drop the route entirely, so accepting one
    /// here would silently take the route off the air. The property change notification puts
    /// the stored value back in the box, and the message says what was wrong.
    /// </summary>
    private void SetInjectionField(
        string? candidate,
        string current,
        Func<string, string?> validate,
        Action<string> assign,
        string propertyName)
    {
        var value = candidate ?? "";
        if (value == current) return;

        if (validate(value) is { } error)
        {
            OnPropertyChanged(propertyName);
            _onInvalid?.Invoke(error);
            return;
        }

        assign(value);
        OnPropertyChanged(propertyName);
        NotifyInjectionChanged();
    }

    private void NotifyInjectionChanged()
    {
        OnPropertyChanged(nameof(InjectionSummary));
        OnPropertyChanged(nameof(Summary));
        _onChanged?.Invoke(this, $"Route '{PathPrefix}' now sends its credential as {InjectionSummary}.");
    }
}
