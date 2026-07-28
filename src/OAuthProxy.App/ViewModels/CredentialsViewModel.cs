using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.App.ViewModels;

public sealed partial class CredentialsViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly OAuth2Service _oAuth2Service;
    private readonly ActivityLog _activityLog;
    private readonly DispatcherTimer _statusTimer;

    private CredentialItemViewModel? _editingItem;

    public ObservableCollection<CredentialItemViewModel> Credentials { get; } = [];
    public IReadOnlyList<OAuthProviderPreset> Presets { get; } = OAuthProviderPreset.All;

    [ObservableProperty] private OAuthProviderPreset _selectedPreset = OAuthProviderPreset.Google;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newClientId = "";
    [ObservableProperty] private string _newClientSecret = "";
    [ObservableProperty] private string _newScopes = "";
    [ObservableProperty] private string _newAuthority = "";
    [ObservableProperty] private string _newAuthorizationEndpoint = "";
    [ObservableProperty] private string _newTokenEndpoint = "";
    [ObservableProperty] private bool _newUsesPkce = true;
    [ObservableProperty] private string _redirectUriInfo = "";
    [ObservableProperty] private string _redirectUri = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _formHeaderText = "Add credential";
    [ObservableProperty] private string _saveButtonLabel = "Add credential";
    [ObservableProperty] private string _statusMessage = "";

    public CredentialsViewModel(ConfigStoreCache configStoreCache, OAuth2Service oAuth2Service, ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _oAuth2Service = oAuth2Service;
        _activityLog = activityLog;
        ApplyPresetDefaults(_selectedPreset);

        foreach (var record in _configStoreCache.Current.Credentials)
        {
            Credentials.Add(new CredentialItemViewModel(record).Refresh());
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _statusTimer.Start();
    }

    partial void OnSelectedPresetChanged(OAuthProviderPreset value) => ApplyPresetDefaults(value);

    private void ApplyPresetDefaults(OAuthProviderPreset preset)
    {
        NewAuthority = preset.Authority ?? "";
        NewAuthorizationEndpoint = preset.AuthorizationEndpointHint ?? "";
        NewTokenEndpoint = preset.TokenEndpointHint ?? "";
        NewScopes = string.Join(", ", preset.DefaultScopes);
        NewUsesPkce = preset.UsesPkce;

        if (preset.Name == "Google")
        {
            RedirectUri = GoogleOAuthService.RedirectUri;
            RedirectUriInfo = "Register this in Google Cloud Console if your client is 'Web application' type. Not required for 'Desktop app' type (Google accepts any loopback port automatically).";
        }
        else
        {
            RedirectUri = LoopbackBrowser.StaticRedirectUri;
            RedirectUriInfo = "Register this exact URI as the redirect/callback URL in your provider's OAuth client settings.";
        }
    }

    [RelayCommand]
    private async Task SaveCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewClientId))
        {
            StatusMessage = "Name and Client ID are required.";
            return;
        }

        var scopes = NewScopes.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var authority = string.IsNullOrWhiteSpace(NewAuthority) ? null : NewAuthority.Trim();
        var authorizationEndpoint = string.IsNullOrWhiteSpace(NewAuthorizationEndpoint) ? null : NewAuthorizationEndpoint.Trim();
        var tokenEndpoint = string.IsNullOrWhiteSpace(NewTokenEndpoint) ? null : NewTokenEndpoint.Trim();
        var isGoogle = ReferenceEquals(SelectedPreset, OAuthProviderPreset.Google);

        if (_editingItem is { } editing)
        {
            var record = editing.Record;
            record.Name = NewName.Trim();
            record.ClientId = NewClientId.Trim();
            if (!string.IsNullOrWhiteSpace(NewClientSecret))
            {
                // Blank means "keep the existing secret" — we never redisplay stored secrets.
                record.ClientSecret = NewClientSecret.Trim();
            }
            record.Scopes = scopes;
            record.Authority = authority;
            record.AuthorizationEndpoint = authorizationEndpoint;
            record.TokenEndpoint = tokenEndpoint;
            record.RequiresIdToken = SelectedPreset.RequiresIdToken;
            record.UsesPkce = NewUsesPkce;
            record.IsGoogleProvider = isGoogle;

            await _configStoreCache.SaveAsync();
            editing.Refresh();
            StatusMessage = $"Saved changes to '{record.Name}'.";
            CancelEdit();
        }
        else
        {
            var record = new CredentialRecord
            {
                Name = NewName.Trim(),
                ClientId = NewClientId.Trim(),
                ClientSecret = NewClientSecret.Trim(),
                Scopes = scopes,
                Authority = authority,
                AuthorizationEndpoint = authorizationEndpoint,
                TokenEndpoint = tokenEndpoint,
                RequiresIdToken = SelectedPreset.RequiresIdToken,
                UsesPkce = NewUsesPkce,
                IsGoogleProvider = isGoogle,
            };

            _configStoreCache.Current.Credentials.Add(record);
            await _configStoreCache.SaveAsync();
            Credentials.Add(new CredentialItemViewModel(record).Refresh());

            NewName = "";
            NewClientId = "";
            NewClientSecret = "";
            StatusMessage = $"Added '{record.Name}'. Click Connect to authorize.";
        }
    }

    [RelayCommand]
    private void EditCredential(CredentialItemViewModel? item)
    {
        if (item is null) return;

        _editingItem = item;
        IsEditing = true;
        FormHeaderText = $"Edit '{item.Name}'";
        SaveButtonLabel = "Save changes";

        // Best-effort preset match so provider-specific hints (redirect URI, help text) still
        // make sense while editing — SelectedPreset itself isn't persisted, only the resolved
        // fields below are, and those are set explicitly right after so they win either way.
        SelectedPreset = item.Record.IsGoogleProvider ? OAuthProviderPreset.Google : OAuthProviderPreset.Custom;

        NewName = item.Record.Name;
        NewClientId = item.Record.ClientId;
        NewClientSecret = "";
        NewScopes = string.Join(", ", item.Record.Scopes);
        NewAuthority = item.Record.Authority ?? "";
        NewAuthorizationEndpoint = item.Record.AuthorizationEndpoint ?? "";
        NewTokenEndpoint = item.Record.TokenEndpoint ?? "";
        NewUsesPkce = item.Record.UsesPkce;

        StatusMessage = "Leave Client secret blank to keep the current one.";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _editingItem = null;
        IsEditing = false;
        FormHeaderText = "Add credential";
        SaveButtonLabel = "Add credential";
        NewName = "";
        NewClientId = "";
        NewClientSecret = "";
        ApplyPresetDefaults(SelectedPreset);
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        if (ReferenceEquals(item, _editingItem)) CancelEdit();
        _configStoreCache.Current.Credentials.Remove(item.Record);
        await _configStoreCache.SaveAsync();
        Credentials.Remove(item);
    }

    [RelayCommand]
    private async Task ConnectAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        StatusMessage = $"Opening browser to authorize '{item.Name}'…";
        _activityLog.Log($"CONNECT '{item.Name}' starting OAuth flow");
        try
        {
            var outcome = await _oAuth2Service.StartAuthorizationAsync(item.Record);
            if (outcome.Success)
            {
                await _configStoreCache.SaveAsync();
                StatusMessage = $"'{item.Name}' connected.";
                _activityLog.Log($"CONNECT '{item.Name}' OK — token stored");
            }
            else
            {
                StatusMessage = $"Failed to connect '{item.Name}': {outcome.Error} {outcome.ErrorDescription}".Trim();
                _activityLog.Log($"CONNECT '{item.Name}' FAILED — {outcome.Error} {outcome.ErrorDescription}".Trim());
            }
        }
        catch (Exception ex)
        {
            // Provider/library errors (bad endpoints, missing userinfo, port already bound…)
            // must surface in the UI, never take down an always-on tray app.
            StatusMessage = $"Failed to connect '{item.Name}': {ex.Message}";
            _activityLog.LogError($"CONNECT '{item.Name}' threw", ex);
        }
        item.Refresh();
    }

    [RelayCommand]
    private async Task DisconnectAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;

        // Clears the locally stored token only — it does not revoke the grant at the
        // provider, so Connect re-authorizes without a fresh consent screen in most cases.
        item.Record.Token = null;
        item.Record.NeedsReconnect = false;
        await _configStoreCache.SaveAsync();
        item.Refresh();
        StatusMessage = $"'{item.Name}' disconnected — stored token cleared (not revoked at the provider).";
        _activityLog.Log($"DISCONNECT '{item.Name}' — stored token cleared");
    }

    [RelayCommand]
    private async Task RefreshNowAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            var token = await _oAuth2Service.RefreshAsync(item.Record);
            if (token is not null)
            {
                await _configStoreCache.SaveAsync();
                StatusMessage = $"'{item.Name}' refreshed.";
            }
            else
            {
                StatusMessage = $"Could not refresh '{item.Name}' — reconnect may be required.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh '{item.Name}': {ex.Message}";
        }
        item.Refresh();
    }

    private void RefreshStatuses()
    {
        foreach (var item in Credentials) item.Refresh();
    }
}
