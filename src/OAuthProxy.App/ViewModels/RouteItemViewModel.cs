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

    /// <summary>Raised when Enabled/StripPrefix are toggled, so the owner can persist and rebuild.</summary>
    private readonly Action<RouteItemViewModel>? _onChanged;

    public RouteItemViewModel(
        RouteMapping route,
        UpstreamRecord? upstream,
        CredentialRecord? credential,
        int listenPort,
        Action<RouteItemViewModel>? onChanged = null)
    {
        Route = route;
        _onChanged = onChanged;

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
            _onChanged?.Invoke(this);
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
            _onChanged?.Invoke(this);
        }
    }

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
        + $"\nToken: {CredentialName}";
}
