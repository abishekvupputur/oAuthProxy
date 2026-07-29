using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OAuthProxy.Core.Models;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace OAuthProxy.App.ViewModels;

/// <summary>Thin bindable wrapper around a CredentialRecord — call Refresh() to re-pull display text after the record changes.</summary>
public sealed partial class CredentialItemViewModel(CredentialRecord record) : ObservableObject
{
    public CredentialRecord Record => record;

    public Guid Id => record.Id;
    public string Name => record.Name;
    public string ScopesDisplay => record.Kind == CredentialKind.ApiKey
        ? record.ToDefaultInjection().Describe()
        : string.Join(", ", record.Scopes);

    public string KindDisplay => record.Kind == CredentialKind.ApiKey ? "API key" : "OAuth2";

    /// <summary>
    /// Connect / Disconnect / Refresh are browser-flow and token operations; an API key has
    /// nothing to authorize and nothing to refresh, so offering them advertises actions that
    /// would do nothing.
    /// </summary>
    public bool IsOAuth => record.Kind == CredentialKind.OAuth2;

    /// <summary>Whether the Test button is worth offering — it needs an endpoint to call.</summary>
    public bool CanTest => !string.IsNullOrWhiteSpace(record.TestEndpoint);

    [ObservableProperty] private string _statusDisplay = "Not connected";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isConnected;

    public CredentialItemViewModel Refresh()
    {
        (StatusDisplay, var brushKey, IsConnected) = record switch
        {
            // An API key does not expire and is never "connected" in the OAuth sense; it is
            // either stored or it is not. Reporting it through the token states would have shown
            // every API key as permanently "Not connected".
            { Kind: CredentialKind.ApiKey } c => string.IsNullOrEmpty(c.ApiKey)
                ? ("No API key stored", "ErrorBrush", false)
                : ("API key stored", "SuccessBrush", true),
            { NeedsReconnect: true } => ("Needs reconnect", "ErrorBrush", false),
            { Token: null } => ("Not connected", "MutedTextBrush", false),
            { Token: { } t } when t.IsExpiringWithin(TimeSpan.Zero) => ("Expired", "ErrorBrush", false),
            { Token: { } t } => ($"Connected · expires {t.ExpiresAtUtc.ToLocalTime():t}", "SuccessBrush", true),
        };

        StatusBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;

        // Name/ScopesDisplay read straight from the record rather than caching, so after an
        // edit we still need to raise change notifications for them explicitly.
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ScopesDisplay));
        OnPropertyChanged(nameof(KindDisplay));
        OnPropertyChanged(nameof(IsOAuth));
        OnPropertyChanged(nameof(CanTest));
        return this;
    }
}
