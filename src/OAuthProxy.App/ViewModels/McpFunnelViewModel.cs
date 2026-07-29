using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.App.ViewModels;

/// <summary>
/// The MCP Funnel tab: pool several MCP servers behind one local endpoint per agent, and decide
/// exactly which of their tools that agent may see.
///
/// Unlike the Routes tab there is no YARP config to rebuild — the funnel reads its configuration
/// per request — so saving is all that is needed for a change to take effect. What does need
/// invalidating is the pool of upstream sessions, since a source's address or credentials may
/// have changed underneath them.
/// </summary>
public sealed partial class McpFunnelViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly McpSourceConnectionPool _connectionPool;
    private readonly McpCatalogCache _catalogCache;
    private readonly ActivityLog _activityLog;

    public ObservableCollection<McpSourceItemViewModel> Sources { get; } = [];
    public ObservableCollection<McpFunnelItemViewModel> Funnels { get; } = [];
    public ObservableCollection<RouteMapping> Routes { get; } = [];

    /// <summary>Rows of the selected funnel's source editor.</summary>
    public ObservableCollection<McpFunnelSourceItemViewModel> FunnelSources { get; } = [];

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty] private string _newSourceName = "";
    [ObservableProperty] private string _newSourceAlias = "";
    [ObservableProperty] private McpSourceKind _newSourceKind = McpSourceKind.RemoteUrl;
    [ObservableProperty] private RouteMapping? _newSourceRoute;
    [ObservableProperty] private string _newSourceUrl = "";
    [ObservableProperty] private McpTransportPreference _newSourceTransport = McpTransportPreference.Auto;

    [ObservableProperty] private string _newFunnelName = "";
    [ObservableProperty] private string _newFunnelSlug = "";

    [ObservableProperty] private McpFunnelItemViewModel? _selectedFunnel;

    [ObservableProperty] private string _statusMessage = "Ready.";

    public IReadOnlyList<McpSourceKind> AllKinds { get; } = Enum.GetValues<McpSourceKind>();
    public IReadOnlyList<McpTransportPreference> AllTransports { get; } = Enum.GetValues<McpTransportPreference>();

    public bool IsRouteSource => NewSourceKind == McpSourceKind.ProxyRoute;
    public bool IsUrlSource => NewSourceKind == McpSourceKind.RemoteUrl;

    public bool HasSources => Sources.Count > 0;
    public bool HasNoSources => Sources.Count == 0;
    public bool HasFunnels => Funnels.Count > 0;
    public bool HasNoFunnels => Funnels.Count == 0;
    public bool HasSelectedFunnel => SelectedFunnel is not null;

    public string SelectedFunnelTitle => SelectedFunnel is null
        ? "Select a funnel above to choose what it exposes"
        : $"What '{SelectedFunnel.Name}' exposes";

    public McpFunnelViewModel(
        ConfigStoreCache configStoreCache,
        McpSourceConnectionPool connectionPool,
        McpCatalogCache catalogCache,
        ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _connectionPool = connectionPool;
        _catalogCache = catalogCache;
        _activityLog = activityLog;

        Sources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasNoSources));
        };
        Funnels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFunnels));
            OnPropertyChanged(nameof(HasNoFunnels));
        };

        Reload();
    }

    partial void OnNewSourceKindChanged(McpSourceKind value)
    {
        OnPropertyChanged(nameof(IsRouteSource));
        OnPropertyChanged(nameof(IsUrlSource));
    }

    partial void OnSelectedFunnelChanged(McpFunnelItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedFunnel));
        OnPropertyChanged(nameof(SelectedFunnelTitle));
        LoadFunnelSources();
    }

    /// <summary>
    /// Re-reads everything from the shared config cache. This view model is a singleton, and a
    /// route added on the Routes tab afterwards has to appear in the source picker here.
    /// </summary>
    public void Reload()
    {
        var store = _configStoreCache.Current;
        var selectedFunnelId = SelectedFunnel?.Funnel.Id;
        var selectedRouteId = NewSourceRoute?.Id;

        IsEnabled = store.Settings.McpFunnelEnabled;

        Routes.Clear();
        foreach (var route in store.Routes) Routes.Add(route);

        Sources.Clear();
        foreach (var source in store.McpSources)
        {
            Sources.Add(new McpSourceItemViewModel(
                source,
                store.Routes.FirstOrDefault(r => r.Id == source.RouteId),
                _catalogCache.Get(source.Id),
                OnSourceEdited));
        }

        Funnels.Clear();
        foreach (var funnel in store.McpFunnels)
        {
            Funnels.Add(new McpFunnelItemViewModel(
                funnel, store.Settings.ListenPort, funnel.Sources.Count, OnFunnelEdited));
        }

        NewSourceRoute = Routes.FirstOrDefault(r => r.Id == selectedRouteId);
        SelectedFunnel = Funnels.FirstOrDefault(f => f.Funnel.Id == selectedFunnelId);

        LoadFunnelSources();
    }

    private void LoadFunnelSources()
    {
        FunnelSources.Clear();
        if (SelectedFunnel is not { } selected) return;

        foreach (var source in _configStoreCache.Current.McpSources)
        {
            FunnelSources.Add(new McpFunnelSourceItemViewModel(
                selected.Funnel,
                source,
                selected.Funnel.Sources.FirstOrDefault(s => s.SourceId == source.Id),
                _catalogCache.Get(source.Id),
                OnFunnelContentEdited));
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        StatusMessage = $"Refreshed — {Sources.Count} source(s), {Funnels.Count} funnel(s).";
    }

    partial void OnIsEnabledChanged(bool value)
    {
        // Synchronous setter, so the save is started and not awaited. App.OnExit drains any
        // in-flight write before the process ends, so a toggle immediately followed by Exit is
        // not lost.
        _ = PersistAsync(
            store => store.Settings.McpFunnelEnabled = value,
            value
                ? "MCP funnel enabled — endpoints under /mcp are now served."
                : "MCP funnel disabled — endpoints under /mcp now return 404.");

        _activityLog.Log($"SETTINGS MCP funnel {(value ? "enabled" : "disabled")}");
    }

    // ---- sources ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        var store = _configStoreCache.Current;

        if (string.IsNullOrWhiteSpace(NewSourceName))
        {
            StatusMessage = "Source name is required.";
            return;
        }

        // Default the alias from the name so the common case needs no thought, but keep it a
        // real field: it ends up in every tool name the agent sees.
        var alias = string.IsNullOrWhiteSpace(NewSourceAlias)
            ? new string([.. NewSourceName.Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')])
            : NewSourceAlias.Trim();

        if (McpFunnelValidation.ValidateAlias(alias, store.McpSources) is { } aliasError)
        {
            StatusMessage = aliasError;
            return;
        }

        var routeId = NewSourceRoute?.Id ?? Guid.Empty;
        var url = NewSourceUrl.Trim();

        if (McpFunnelValidation.ValidateTarget(NewSourceKind, routeId, url, store.Routes) is { } targetError)
        {
            StatusMessage = targetError;
            return;
        }

        var source = new McpSourceRecord
        {
            Name = NewSourceName.Trim(),
            Alias = alias,
            Kind = NewSourceKind,
            RouteId = routeId,
            Url = NewSourceKind == McpSourceKind.RemoteUrl ? url : "",
            Transport = NewSourceTransport,
        };

        await PersistAsync(s => s.McpSources.Add(source), $"Source '{source.Name}' added — its tools appear as {alias}{McpNameMapper.Separator}…");

        NewSourceName = "";
        NewSourceAlias = "";
        NewSourceUrl = "";

        Reload();
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(McpSourceItemViewModel? item)
    {
        if (item is null) return;

        var affected = _configStoreCache.Current.McpFunnels.Count(f => f.Sources.Any(s => s.SourceId == item.Source.Id));

        await PersistAsync(store =>
        {
            store.McpSources.RemoveAll(s => s.Id == item.Source.Id);

            // Otherwise the funnel keeps a membership row pointing at nothing, which serves fine
            // but leaves the UI unable to show or remove it.
            foreach (var funnel in store.McpFunnels)
            {
                funnel.Sources.RemoveAll(s => s.SourceId == item.Source.Id);
            }
        }, affected == 0
            ? $"Source '{item.Name}' deleted."
            : $"Source '{item.Name}' deleted and removed from {affected} funnel(s).");

        await _connectionPool.InvalidateSourceAsync(item.Source.Id);
        _catalogCache.Remove(item.Source.Id);

        Reload();
    }

    [RelayCommand]
    private async Task RefreshSourceAsync(McpSourceItemViewModel? item)
    {
        if (item is null) return;

        StatusMessage = $"Connecting to '{item.Name}'…";

        var catalog = await _connectionPool.DiscoverAsync(item.Source);
        _catalogCache.Set(item.Source.Id, catalog);

        StatusMessage = catalog.Error is null
            ? $"'{item.Name}': {catalog.Describe()}"
            : $"'{item.Name}' could not be reached — {catalog.Error}";

        Reload();
    }

    [RelayCommand]
    private async Task RefreshAllSourcesAsync()
    {
        foreach (var item in Sources.ToList())
        {
            if (!item.Enabled) continue;

            _catalogCache.Set(item.Source.Id, await _connectionPool.DiscoverAsync(item.Source));
        }

        StatusMessage = "All sources refreshed.";
        Reload();
    }

    // ---- funnels ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddFunnelAsync()
    {
        var store = _configStoreCache.Current;

        if (string.IsNullOrWhiteSpace(NewFunnelName))
        {
            StatusMessage = "Funnel name is required.";
            return;
        }

        var slug = string.IsNullOrWhiteSpace(NewFunnelSlug)
            ? Slugify(NewFunnelName)
            : NewFunnelSlug.Trim().ToLowerInvariant();

        if (McpFunnelValidation.ValidateSlug(slug, store.McpFunnels) is { } slugError)
        {
            StatusMessage = slugError;
            return;
        }

        var funnel = new McpFunnelRecord { Name = NewFunnelName.Trim(), Slug = slug };

        await PersistAsync(s => s.McpFunnels.Add(funnel),
            $"Funnel '{funnel.Name}' added at /mcp/{slug} — now choose which sources it pools.");

        NewFunnelName = "";
        NewFunnelSlug = "";

        Reload();
        SelectedFunnel = Funnels.FirstOrDefault(f => f.Funnel.Id == funnel.Id);
    }

    [RelayCommand]
    private async Task DeleteFunnelAsync(McpFunnelItemViewModel? item)
    {
        if (item is null) return;

        await PersistAsync(store => store.McpFunnels.RemoveAll(f => f.Id == item.Funnel.Id),
            $"Funnel '{item.Name}' deleted — clients pointed at /mcp/{item.Slug} will now get 404.");

        await _connectionPool.InvalidateFunnelAsync(item.Funnel.Id);

        Reload();
    }

    /// <summary>Turns a display name into something usable as a path segment.</summary>
    private static string Slugify(string name)
    {
        var slug = new string([.. name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    // ---- persistence -----------------------------------------------------------------------

    private void OnSourceEdited(McpSourceItemViewModel item, string message) =>
        _ = PersistEditAsync(message, () => _connectionPool.InvalidateSourceAsync(item.Source.Id));

    private void OnFunnelEdited(McpFunnelItemViewModel item, string message) =>
        _ = PersistEditAsync(message, () => _connectionPool.InvalidateFunnelAsync(item.Funnel.Id));

    private void OnFunnelContentEdited(string message)
    {
        var funnelId = SelectedFunnel?.Funnel.Id;

        // Only the session pool needs telling; the funnel's tool list is rebuilt per request, so
        // the agent sees the change on its next call with nothing else to do.
        _ = PersistEditAsync(message, () => funnelId is { } id
            ? _connectionPool.InvalidateFunnelAsync(id)
            : Task.CompletedTask);
    }

    private async Task PersistEditAsync(string message, Func<Task> invalidate)
    {
        try
        {
            await _configStoreCache.SaveAsync();
            await invalidate();
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save change: {ex.Message}";
        }
    }

    private async Task PersistAsync(Action<ConfigStore> mutate, string message)
    {
        try
        {
            await _configStoreCache.MutateAsync(mutate);
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
    }
}
