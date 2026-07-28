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
    public string ScopesDisplay => string.Join(", ", record.Scopes);

    [ObservableProperty] private string _statusDisplay = "Not connected";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isConnected;

    public CredentialItemViewModel Refresh()
    {
        (StatusDisplay, var brushKey, IsConnected) = record switch
        {
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
        return this;
    }
}
