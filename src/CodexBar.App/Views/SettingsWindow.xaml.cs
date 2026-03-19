using System.Windows;
using System.Windows.Controls;
using CodexBar.App.Platform;
using CodexBar.App.Services;
using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.App.Views;

/// <summary>
/// Settings window for configuring providers and polling interval.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SettingsWindow>();

    private readonly ProviderRegistry _registry;
    private readonly ConfigurationService _config;
    private readonly PollingScheduler _scheduler;
    private readonly StartupManager _startupManager;
    private bool _isInitializing = true;

    public SettingsWindow(ProviderRegistry registry, ConfigurationService config, PollingScheduler scheduler)
    {
        _registry = registry;
        _config = config;
        _scheduler = scheduler;
        _startupManager = new StartupManager();

        InitializeComponent();
        PopulateProviderToggles();
        SelectCurrentInterval();
        LoadGeneralSettings();
        _isInitializing = false;
    }

    private void PopulateProviderToggles()
    {
        ProviderToggles.Children.Clear();

        foreach (var provider in _registry.GetAll())
        {
            var cb = new CheckBox
            {
                Content = $"  {provider.DisplayName}",
                IsChecked = provider.IsEnabled,
                Tag = provider.Id,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(224, 224, 232)),
                Margin = new Thickness(0, 4, 0, 4),
                FontSize = 13,
            };

            cb.Checked += ProviderToggle_Changed;
            cb.Unchecked += ProviderToggle_Changed;

            ProviderToggles.Children.Add(cb);
        }
    }

    private void SelectCurrentInterval()
    {
        var currentSeconds = _config.Config.PollIntervalSeconds;
        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (item.Tag is string tagStr && int.TryParse(tagStr, out var tag) && tag == currentSeconds)
            {
                IntervalCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void ProviderToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string providerId)
        {
            _registry.SetEnabled(providerId, cb.IsChecked == true);
        }
    }

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item
            && item.Tag is string tagStr
            && int.TryParse(tagStr, out var seconds))
        {
            _config.Update(c => c.PollIntervalSeconds = seconds);
            _scheduler.SetInterval(TimeSpan.FromSeconds(seconds));
        }
    }

    private void LoadGeneralSettings()
    {
        StartWithWindowsCheck.IsChecked = _config.Config.StartWithWindows;
        NotifyOnExhaustionCheck.IsChecked = _config.Config.NotifyOnExhaustion;
    }

    private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var enabled = StartWithWindowsCheck.IsChecked == true;
        _config.Update(c => c.StartWithWindows = enabled);

        if (!_startupManager.ApplyStartWithWindows(enabled))
        {
            Log.Warning("Failed to apply start-with-Windows setting: {Enabled}", enabled);
            MessageBox.Show(
                "Could not update startup registration. Try running the app with appropriate permissions.",
                "CodexBar",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void NotifyOnExhaustionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var enabled = NotifyOnExhaustionCheck.IsChecked == true;
        _config.Update(c => c.NotifyOnExhaustion = enabled);
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>();
        foreach (var provider in _registry.GetAll().OrderBy(p => p.DisplayName))
        {
            try
            {
                if (provider is IProviderDiagnostics diagnostics)
                {
                    var result = await diagnostics.DiagnoseAsync();
                    lines.Add($"{provider.DisplayName}: {result.AuthState}");
                    if (!string.IsNullOrWhiteSpace(result.SuggestedAction))
                        lines.Add($"  Action: {result.SuggestedAction}");
                    foreach (var check in result.Checks)
                        lines.Add($"  - {check}");
                }
                else
                {
                    var available = await provider.IsAvailableAsync();
                    lines.Add($"{provider.DisplayName}: {(available ? "Available" : "Unavailable")}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"{provider.DisplayName}: diagnostics failed ({ex.Message})");
            }
        }

        MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "Provider Diagnostics",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
