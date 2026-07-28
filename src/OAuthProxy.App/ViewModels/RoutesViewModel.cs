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
    public ObservableCollection<RouteMapping> Routes { get; } = [];
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

        Routes.Clear();
        foreach (var r in store.Routes) Routes.Add(r);

        Credentials.Clear();
        foreach (var c in store.Credentials) Credentials.Add(c);

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

        var upstream = new UpstreamRecord { Name = NewUpstreamName.Trim(), BaseUrl = NewUpstreamBaseUrl.Trim().TrimEnd('/') };
        _configStoreCache.Current.Upstreams.Add(upstream);
        await SaveAndRebuildAsync();
        Upstreams.Add(upstream);

        NewUpstreamName = "";
        NewUpstreamBaseUrl = "";
    }

    [RelayCommand]
    private async Task DeleteUpstreamAsync(UpstreamRecord? upstream)
    {
        if (upstream is null) return;
        _configStoreCache.Current.Upstreams.Remove(upstream);
        await SaveAndRebuildAsync();
        Upstreams.Remove(upstream);
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

        _configStoreCache.Current.Routes.Add(route);
        await SaveAndRebuildAsync();
        Routes.Add(route);

        NewRoutePathPrefix = "";
        StatusMessage = $"Route '{prefix}' added.";
    }

    [RelayCommand]
    private async Task DeleteRouteAsync(RouteMapping? route)
    {
        if (route is null) return;
        _configStoreCache.Current.Routes.Remove(route);
        await SaveAndRebuildAsync();
        Routes.Remove(route);
    }

    private async Task SaveAndRebuildAsync()
    {
        await _configStoreCache.SaveAsync();
        _proxyConfigChangeNotifier.Rebuild();
    }
}
