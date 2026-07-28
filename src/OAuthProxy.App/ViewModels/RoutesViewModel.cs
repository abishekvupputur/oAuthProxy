using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.App.ViewModels;

public sealed partial class RoutesViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly ProxyConfigChangeNotifier _proxyConfigChangeNotifier;

    public ObservableCollection<UpstreamRecord> Upstreams { get; } = [];
    public ObservableCollection<RouteItemViewModel> Routes { get; } = [];
    public ObservableCollection<CredentialRecord> Credentials { get; } = [];

    [ObservableProperty] private string _newUpstreamName = "";
    [ObservableProperty] private string _newUpstreamBaseUrl = "";

    [ObservableProperty] private string _newRoutePathPrefix = "";
    [ObservableProperty] private UpstreamRecord? _newRouteUpstream;
    [ObservableProperty] private CredentialRecord? _newRouteCredential;
    [ObservableProperty] private bool _newRouteStripPrefix = true;

    [ObservableProperty] private string _statusMessage = "Ready.";

    public bool HasUpstreams => Upstreams.Count > 0;
    public bool HasNoUpstreams => Upstreams.Count == 0;
    public bool HasRoutes => Routes.Count > 0;
    public bool HasNoRoutes => Routes.Count == 0;

    public RoutesViewModel(ConfigStoreCache configStoreCache, ProxyConfigChangeNotifier proxyConfigChangeNotifier)
    {
        _configStoreCache = configStoreCache;
        _proxyConfigChangeNotifier = proxyConfigChangeNotifier;

        // Empty-state visibility is derived from these collections, so re-evaluate on change.
        Upstreams.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUpstreams));
            OnPropertyChanged(nameof(HasNoUpstreams));
        };
        Routes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRoutes));
            OnPropertyChanged(nameof(HasNoRoutes));
        };

        Reload();
    }

    /// <summary>
    /// Re-reads everything from the shared config cache. Needed because this view model is a
    /// singleton that used to snapshot the credential list once at construction — a credential
    /// added on the Credentials tab afterwards never appeared in the Routes dropdown.
    /// Called on construction, from the Refresh button, and whenever the Routes tab is shown.
    /// </summary>
    public void Reload()
    {
        // Selections are object references into the collections we're about to clear, so
        // remember them by id and restore afterwards to avoid resetting the user's pickers.
        var selectedUpstreamId = NewRouteUpstream?.Id;
        var selectedCredentialId = NewRouteCredential?.Id;

        var store = _configStoreCache.Current;

        Upstreams.Clear();
        foreach (var u in store.Upstreams) Upstreams.Add(u);

        Credentials.Clear();
        foreach (var c in store.Credentials) Credentials.Add(c);

        // Resolved against the current upstream/credential lists so the grid can show names
        // and the real local URL rather than bare ids.
        Routes.Clear();
        foreach (var r in store.Routes)
        {
            Routes.Add(new RouteItemViewModel(
                r,
                store.Upstreams.FirstOrDefault(u => u.Id == r.UpstreamId),
                store.Credentials.FirstOrDefault(c => c.Id == r.CredentialId),
                store.Settings.ListenPort,
                OnRouteToggled));
        }

        NewRouteUpstream = Upstreams.FirstOrDefault(u => u.Id == selectedUpstreamId);
        NewRouteCredential = Credentials.FirstOrDefault(c => c.Id == selectedCredentialId);
    }

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        StatusMessage = $"Refreshed — {Credentials.Count} credential(s), {Upstreams.Count} upstream(s), {Routes.Count} route(s).";
    }

    [RelayCommand]
    private async Task AddUpstreamAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUpstreamName) || string.IsNullOrWhiteSpace(NewUpstreamBaseUrl)) return;

        var baseUrl = NewUpstreamBaseUrl.Trim().TrimEnd('/');

        // The access token is attached to every request forwarded here, so a plain-http
        // upstream would put it on the wire in cleartext.
        if (UrlValidation.ValidateEndpoint(baseUrl, "Upstream base URL") is { } error)
        {
            StatusMessage = error;
            return;
        }

        var upstream = new UpstreamRecord { Name = NewUpstreamName.Trim(), BaseUrl = baseUrl };
        await SaveAndRebuildAsync(store => store.Upstreams.Add(upstream));
        Upstreams.Add(upstream);

        NewUpstreamName = "";
        NewUpstreamBaseUrl = "";
        StatusMessage = $"Upstream '{upstream.Name}' added.";
    }

    [RelayCommand]
    private async Task DeleteUpstreamAsync(UpstreamRecord? upstream)
    {
        if (upstream is null) return;

        var affected = _configStoreCache.Current.Routes.Count(r => r.UpstreamId == upstream.Id);

        await SaveAndRebuildAsync(store => store.Upstreams.Remove(upstream));
        // Reload so any route that pointed at this upstream immediately shows as broken
        // rather than silently continuing to display a name that no longer exists.
        Reload();

        StatusMessage = affected == 0
            ? $"Upstream '{upstream.Name}' deleted."
            : $"Upstream '{upstream.Name}' deleted — {affected} route(s) now have no upstream and will not be served.";
    }

    [RelayCommand]
    private async Task AddRouteAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRoutePathPrefix) || NewRouteUpstream is null || NewRouteCredential is null)
        {
            StatusMessage = "Path prefix, upstream, and credential are required.";
            return;
        }

        var prefix = NewRoutePathPrefix.Trim();
        if (!prefix.StartsWith('/')) prefix = "/" + prefix;

        // A bare "/" builds the pattern "/{**catch-all}", which swallows every request to the
        // proxy and points all of it at one upstream with one credential attached — almost
        // certainly not what someone typing a single slash intended.
        if (prefix.TrimEnd('/').Length == 0)
        {
            StatusMessage = "'/' would capture every request to the proxy. Use a specific prefix such as '/gmail'.";
            return;
        }

        if (prefix.Contains("..") || prefix.Contains('\\'))
        {
            StatusMessage = "Path prefix may not contain '..' or backslashes.";
            return;
        }

        // Two routes with the same prefix produce two ASP.NET endpoints with identical match
        // patterns. That loads without complaint but throws AmbiguousMatchException on every
        // request, so the whole prefix 500s. Reject it here rather than let it fail silently.
        var normalized = prefix.TrimEnd('/');
        if (_configStoreCache.Current.Routes.Any(r =>
                string.Equals(r.PathPrefix.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A route for '{prefix}' already exists. Path prefixes must be unique — " +
                            "duplicates make every request to that prefix fail with an ambiguous-match error.";
            return;
        }

        var route = new RouteMapping
        {
            PathPrefix = prefix,
            UpstreamId = NewRouteUpstream.Id,
            CredentialId = NewRouteCredential.Id,
            StripPrefix = NewRouteStripPrefix,
            Enabled = true,
        };

        await SaveAndRebuildAsync(store => store.Routes.Add(route));
        // Reload rather than Add: the row needs its upstream/credential names resolved.
        Reload();

        NewRoutePathPrefix = "";
        StatusMessage = $"Route '{prefix}' added.";
    }

    [RelayCommand]
    private async Task DeleteRouteAsync(RouteItemViewModel? item)
    {
        if (item is null) return;
        await SaveAndRebuildAsync(store => store.Routes.Remove(item.Route));
        Routes.Remove(item);
        StatusMessage = $"Route '{item.PathPrefix}' deleted.";
    }

    /// <summary>
    /// Called when a route's Enabled/Strip checkbox is toggled in the grid. The property
    /// setter is synchronous, so persistence is kicked off here and any failure is reported
    /// in the footer rather than surfacing as an unobserved task exception.
    /// </summary>
    private void OnRouteToggled(RouteItemViewModel item)
    {
        _ = PersistToggleAsync(item);
    }

    private async Task PersistToggleAsync(RouteItemViewModel item)
    {
        try
        {
            await SaveAndRebuildAsync();
            StatusMessage = item.Enabled
                ? $"Route '{item.PathPrefix}' enabled."
                : $"Route '{item.PathPrefix}' disabled — requests to it will no longer be proxied.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save change to '{item.PathPrefix}': {ex.Message}";
        }
    }

    /// <summary>
    /// Applies an edit and persists it under the store's write lock, then hot-reloads YARP.
    /// The mutation has to happen inside the lock — the token refresh loop serializes the same
    /// object on a background thread, and a list edit landing mid-serialization throws.
    /// </summary>
    private async Task SaveAndRebuildAsync(Action<ConfigStore>? mutate = null)
    {
        if (mutate is null)
        {
            await _configStoreCache.SaveAsync();
        }
        else
        {
            await _configStoreCache.MutateAsync(mutate);
        }

        _proxyConfigChangeNotifier.Rebuild();
    }
}
