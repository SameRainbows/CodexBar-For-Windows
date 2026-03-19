using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexBar.Core.Models;
using Serilog;

namespace CodexBar.App.ViewModels;

/// <summary>
/// Main viewmodel for the CodexBar tray application.
/// Binding source for tooltip text and the popup window.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainViewModel>();

    private string _tooltipText = "CodexBar — Loading...";
    private string _statusText = "Initializing...";
    private DateTimeOffset? _lastRefresh;
    private double _sessionRemainingPercent = 100;
    private double _weeklyRemainingPercent = 100;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Observable collection of provider usage records for the popup.</summary>
    public ObservableCollection<UsageRecord> Providers { get; } = new();

    /// <summary>Tooltip text shown on hover over the tray icon.</summary>
    public string TooltipText
    {
        get => _tooltipText;
        set => SetField(ref _tooltipText, value);
    }

    /// <summary>Status line in the popup footer.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>Small tray meter value for session remaining percentage (0-100).</summary>
    public double SessionRemainingPercent
    {
        get => _sessionRemainingPercent;
        set => SetField(ref _sessionRemainingPercent, value);
    }

    /// <summary>Small tray meter value for weekly remaining percentage (0-100).</summary>
    public double WeeklyRemainingPercent
    {
        get => _weeklyRemainingPercent;
        set => SetField(ref _weeklyRemainingPercent, value);
    }

    /// <summary>Last successful refresh time.</summary>
    public DateTimeOffset? LastRefresh
    {
        get => _lastRefresh;
        set
        {
            SetField(ref _lastRefresh, value);
            OnPropertyChanged(nameof(LastRefreshText));
        }
    }

    /// <summary>Human-readable last refresh time.</summary>
    public string LastRefreshText => _lastRefresh.HasValue
        ? $"Last refresh: {_lastRefresh.Value.ToLocalTime():HH:mm:ss}"
        : "Not yet refreshed";

    /// <summary>Update a single provider's record. Called from polling scheduler.</summary>
    public void UpdateProvider(string providerId, UsageRecord record)
    {
        // Must be called on UI thread
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Providers.FirstOrDefault(p => p.ProviderId == providerId);
            if (existing is not null)
            {
                var idx = Providers.IndexOf(existing);
                Providers[idx] = record;
            }
            else
            {
                Providers.Add(record);
            }

            UpdateTooltip();
            UpdateMeters();
            UpdateStatusLine();

            if (record.Status == ProviderStatus.Available)
            {
                LastRefresh = DateTimeOffset.UtcNow;
            }
        });
    }

    /// <summary>Remove a provider from the display.</summary>
    public void RemoveProvider(string providerId)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Providers.FirstOrDefault(p => p.ProviderId == providerId);
            if (existing is not null)
                Providers.Remove(existing);

            UpdateTooltip();
            UpdateMeters();
            UpdateStatusLine();
        });
    }

    private void UpdateTooltip()
    {
        var lines = Providers
            .Where(p => p.IsEnabled && p.Status != ProviderStatus.Unavailable)
            .Select(p => BuildTooltipLine(p))
            .ToList();

        TooltipText = lines.Count > 0
            ? $"CodexBar\n{string.Join("\n", lines)}"
            : "CodexBar — No active providers";
    }

    private string BuildTooltipLine(UsageRecord record)
    {
        if (record.Snapshot is not null)
            return record.Snapshot.ToTooltipLine(record.DisplayName);

        var status = record.Status switch
        {
            ProviderStatus.Available => "Available",
            ProviderStatus.Fetching => "Fetching",
            ProviderStatus.Error => "Error",
            ProviderStatus.Stale => "Stale",
            ProviderStatus.Unavailable => "Unavailable",
            _ => "Unknown",
        };
        return $"{record.DisplayName}: {status}";
    }

    private void UpdateMeters()
    {
        var enabledWithData = Providers
            .Where(p => p.IsEnabled && p.Snapshot is not null)
            .ToList();

        var sessionValues = enabledWithData
            .Where(p => p.Snapshot?.SessionQuota is not null)
            .Select(p => p.Snapshot!.SessionQuota!.RemainingPercent)
            .ToList();
        var weeklyValues = enabledWithData
            .Where(p => p.Snapshot?.WeeklyQuota is not null)
            .Select(p => p.Snapshot!.WeeklyQuota!.RemainingPercent)
            .ToList();

        SessionRemainingPercent = sessionValues.Count > 0 ? sessionValues.Min() : 100;
        WeeklyRemainingPercent = weeklyValues.Count > 0 ? weeklyValues.Min() : 100;
    }

    private void UpdateStatusLine()
    {
        var enabled = Providers.Where(p => p.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            StatusText = "No providers enabled";
            return;
        }

        if (enabled.Any(p => p.Status == ProviderStatus.Fetching))
        {
            StatusText = "Refreshing...";
            return;
        }

        var errorCount = enabled.Count(p => p.Status == ProviderStatus.Error);
        var staleCount = enabled.Count(p => p.Status == ProviderStatus.Stale);
        var unavailableCount = enabled.Count(p => p.Status == ProviderStatus.Unavailable);

        if (errorCount > 0 || staleCount > 0 || unavailableCount > 0)
        {
            StatusText = $"Issues: {errorCount} error, {staleCount} stale, {unavailableCount} unavailable";
            return;
        }

        StatusText = "OK";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
