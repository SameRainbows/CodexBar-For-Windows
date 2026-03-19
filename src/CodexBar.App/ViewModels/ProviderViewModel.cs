using System.ComponentModel;
using CodexBar.Core.Models;

namespace CodexBar.App.ViewModels;

/// <summary>
/// ViewModel wrapper for a single provider displayed in the popup.
/// </summary>
public sealed class ProviderViewModel : INotifyPropertyChanged
{
    private UsageRecord _record;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProviderViewModel(UsageRecord record)
    {
        _record = record;
    }

    public string ProviderId => _record.ProviderId;
    public string DisplayName => _record.DisplayName;
    public ProviderStatus Status => _record.Status;
    public bool IsEnabled => _record.IsEnabled;
    public UsageSnapshot? Snapshot => _record.Snapshot;

    public string StatusIcon => Status switch
    {
        ProviderStatus.Available => "✅",
        ProviderStatus.Error => "⚠️",
        ProviderStatus.Stale => "🕐",
        ProviderStatus.Fetching => "⏳",
        ProviderStatus.Unavailable => "❌",
        _ => "❓"
    };

    public double SessionPercent => Snapshot?.SessionQuota?.UsedPercent ?? 0;
    public double WeeklyPercent => Snapshot?.WeeklyQuota?.UsedPercent ?? 0;
    public string SessionText => Snapshot?.SessionQuota?.ToDisplayString() ?? "No session data";
    public string WeeklyText => Snapshot?.WeeklyQuota?.ToDisplayString() ?? "No weekly data";

    public void Update(UsageRecord record)
    {
        _record = record;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
