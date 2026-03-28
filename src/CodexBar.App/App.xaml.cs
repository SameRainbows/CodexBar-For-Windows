using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CodexBar.App.Platform;
using CodexBar.App.Services;
using CodexBar.App.ViewModels;
using CodexBar.App.Views;
using CodexBar.Core.Models;
using CodexBar.Core.Platform;
using CodexBar.Core.Providers;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace CodexBar.App;

/// <summary>
/// Application entry point. Runs as a background tray application — no main window.
/// Wires up providers, polling scheduler, and tray icon.
/// </summary>
public partial class App : Application
{
    private static readonly int[] ExhaustionThresholds = [20, 10, 0];
    private const string AppInstanceId = "CodexBar.App";

    private TaskbarIcon? _trayIcon;
    private Icon? _currentTrayIcon;
    private MainViewModel _viewModel = null!;
    private PollingScheduler? _scheduler;
    private ProviderRegistry _registry = null!;
    private ConfigurationService _configService = null!;
    private UsagePopupWindow? _popupWindow;
    private readonly StartupManager _startupManager = new();
    private SingleInstanceManager? _singleInstance;

    // Dedupes notifications per provider/quota/reset-window: "provider:label:reset@threshold".
    private readonly HashSet<string> _notifiedThresholdKeys = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InitializeLogging();
        Log.Information("CodexBar starting up");

        try
        {
            _singleInstance = new SingleInstanceManager(AppInstanceId);
            var startupCommand = ParseStartupCommand(e.Args);
            if (!_singleInstance.TryBecomePrimaryInstance())
            {
                _singleInstance.SendCommandToPrimary(startupCommand);
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Single-instance setup failed; continuing");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled domain exception (isTerminating: {IsTerminating})", args.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _configService = new ConfigurationService();
            _registry = new ProviderRegistry(_configService);
            _viewModel = new MainViewModel();

            RegisterProviders();
            AutoEnableDetectedProviders();
            _startupManager.ApplyStartWithWindows(_configService.Config.StartWithWindows);

            _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
            SetupTrayIcon();

            _popupWindow = new UsagePopupWindow { DataContext = _viewModel };
            _popupWindow.RefreshRequested += async (_, _) => await RefreshAllAsync();
            _popupWindow.SettingsRequested += (_, _) => ShowSettings();
            _singleInstance?.StartListening(HandleExternalCommand);

            var interval = TimeSpan.FromSeconds(_configService.Config.PollIntervalSeconds);
            _scheduler = new PollingScheduler(
                _registry.GetAll(),
                (_, record) => OnProviderUpdated(record),
                ex => Log.Error(ex, "Polling error"));
            _scheduler.Start(interval);

            Log.Information("CodexBar started successfully");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start CodexBar");
            MessageBox.Show($"CodexBar failed to start:\n{ex.Message}", "CodexBar Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RegisterProviders()
    {
        var processRunner = new ProcessRunner();
        var providerTypes = typeof(CodexBar.Providers.Claude.ClaudeCliProvider).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IUsageProvider).IsAssignableFrom(t));

        foreach (var providerType in providerTypes)
        {
            try
            {
                IUsageProvider? provider;

                var ctor = providerType.GetConstructor([typeof(ProcessRunner)]);
                if (ctor is not null)
                {
                    provider = (IUsageProvider)ctor.Invoke([processRunner]);
                }
                else
                {
                    provider = (IUsageProvider?)Activator.CreateInstance(providerType);
                }

                if (provider is not null)
                    _registry.Register(provider);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to instantiate provider {Type}", providerType.Name);
            }
        }
    }

    private void AutoEnableDetectedProviders()
    {
        Log.Information("Auto-enabling providers that are available and not explicitly configured");
        foreach (var provider in _registry.GetAll())
        {
            var hasExplicitSetting = _configService.Config.HasProviderSetting(provider.Id);
            var shouldConsiderAutoEnable = _configService.IsFirstRun || !hasExplicitSetting;
            if (provider.IsEnabled || !shouldConsiderAutoEnable)
                continue;

            try
            {
                var isAvailable = Task.Run(() => provider.IsAvailableAsync(CancellationToken.None))
                    .GetAwaiter()
                    .GetResult();
                if (!isAvailable)
                    continue;

                _registry.SetEnabled(provider.Id, true);
                Log.Information("Auto-enabled provider: {ProviderId} (firstRun={FirstRun}, hadSetting={HadSetting})",
                    provider.Id, _configService.IsFirstRun, hasExplicitSetting);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to auto-enable provider {ProviderId}", provider.Id);
            }
        }
    }

    private void SetupTrayIcon()
    {
        if (_trayIcon is null) return;

        UpdateTrayIconGraphic();
        UpdateTrayTooltip();

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TooltipText))
            {
                Dispatcher.Invoke(UpdateTrayTooltip);
            }
            else if (args.PropertyName == nameof(MainViewModel.SessionRemainingPercent) ||
                     args.PropertyName == nameof(MainViewModel.WeeklyRemainingPercent))
            {
                Dispatcher.Invoke(UpdateTrayIconGraphic);
            }
        };
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null) return;
        var text = _viewModel.TooltipText;
        if (text.Length > 127)
            text = text[..124] + "...";
        _trayIcon.ToolTipText = text;
    }

    private void UpdateTrayIconGraphic()
    {
        if (_trayIcon is null) return;

        _currentTrayIcon?.Dispose();
        _currentTrayIcon = GenerateTrayIcon(
            _viewModel.SessionRemainingPercent,
            _viewModel.WeeklyRemainingPercent);
        _trayIcon.Icon = _currentTrayIcon;
    }

    /// <summary>Generate a small two-bar meter icon based on remaining percentages.</summary>
    private static Icon GenerateTrayIcon(double sessionRemaining, double weeklyRemaining)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        DrawMeterBar(g, x: 2, y: 3, width: 12, height: 4, remainingPercent: sessionRemaining);
        DrawMeterBar(g, x: 2, y: 9, width: 12, height: 3, remainingPercent: weeklyRemaining);

        var handle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawMeterBar(Graphics g, int x, int y, int width, int height, double remainingPercent)
    {
        remainingPercent = Math.Clamp(remainingPercent, 0, 100);
        var fillWidth = (int)Math.Round(width * (remainingPercent / 100.0));
        var bg = Color.FromArgb(65, 65, 80);
        using var bgBrush = new SolidBrush(bg);
        g.FillRectangle(bgBrush, x, y, width, height);

        if (fillWidth <= 0)
            return;

        var color = remainingPercent switch
        {
            <= 10 => Color.FromArgb(248, 113, 113),
            <= 20 => Color.FromArgb(251, 191, 36),
            _ => Color.FromArgb(74, 222, 128),
        };
        using var fgBrush = new SolidBrush(color);
        g.FillRectangle(fgBrush, x, y, fillWidth, height);
    }

    private void OnProviderUpdated(UsageRecord record)
    {
        _viewModel.UpdateProvider(record.ProviderId, record);
        Dispatcher.Invoke(() => MaybeNotifyThresholdCrossing(record));
    }

    private void MaybeNotifyThresholdCrossing(UsageRecord record)
    {
        if (_trayIcon is null || !_configService.Config.NotifyOnExhaustion)
            return;

        if (record.Snapshot is null || !record.IsEnabled)
            return;

        TryNotifyForQuota(record.DisplayName, record.ProviderId, record.Snapshot.SessionQuota);
        TryNotifyForQuota(record.DisplayName, record.ProviderId, record.Snapshot.WeeklyQuota);
    }

    private void TryNotifyForQuota(string providerName, string providerId, Quota? quota)
    {
        if (quota is null)
            return;

        var remaining = quota.RemainingPercent;
        var resetKey = quota.Reset?.ResetsAt?.ToUnixTimeSeconds().ToString() ?? "unknown-reset";
        var scope = $"{providerId}:{quota.Label}:{resetKey}";

        var threshold = ExhaustionThresholds
            .Where(t => remaining <= t)
            .OrderBy(t => t)
            .FirstOrDefault(int.MaxValue);
        if (threshold == int.MaxValue)
            return;

        var dedupeKey = $"{scope}@{threshold}";
        if (!_notifiedThresholdKeys.Add(dedupeKey))
            return;

        var title = threshold == 0 ? $"{providerName} quota exhausted" : $"{providerName} quota low";
        var message = threshold == 0
            ? $"{quota.Label} is exhausted."
            : $"{quota.Label} remaining: {remaining:F0}%";
        _trayIcon?.ShowBalloonTip(title, message, BalloonIcon.Warning);
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        TogglePopupVisibility();
    }

    private async void TrayRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
    }

    private async void TrayDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await ShowDiagnosticsAsync();
    }

    private void TraySettings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("CodexBar shutting down (user exit)");
        CleanShutdown();
    }

    private async Task ShowDiagnosticsAsync()
    {
        var lines = new List<string>();
        foreach (var provider in _registry.GetAll().OrderBy(p => p.DisplayName))
        {
            try
            {
                if (provider is IProviderDiagnostics diagnostics)
                {
                    // Use a timeout for diagnostics
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var resultTask = diagnostics.DiagnoseAsync();
                    if (await Task.WhenAny(resultTask, Task.Delay(5000)) == resultTask)
                    {
                        var result = await resultTask;
                        lines.Add($"{provider.DisplayName}: {result.AuthState}");
                        if (!string.IsNullOrWhiteSpace(result.SuggestedAction))
                            lines.Add($"  Action: {result.SuggestedAction}");
                        foreach (var check in result.Checks)
                            lines.Add($"  - {check}");
                    }
                    else
                    {
                        lines.Add($"{provider.DisplayName}: diagnostics timed out");
                    }
                }
                else
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var isAvailable = await provider.IsAvailableAsync(cts.Token);
                    lines.Add($"{provider.DisplayName}: {(isAvailable ? "Available" : "Unavailable")}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"{provider.DisplayName}: diagnostics failed ({ex.Message})");
                Log.Warning(ex, "Diagnostics failed for {Provider}", provider.DisplayName);
            }
        }

        if (lines.Count == 0) lines.Add("No providers found.");

        MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "CodexBar Diagnostics",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task RefreshAllAsync()
    {
        if (_scheduler is not null)
        {
            _viewModel.StatusText = "Refreshing...";
            await _scheduler.RefreshNowAsync();
        }
    }

    private void ShowSettings()
    {
        var settingsWindow = new SettingsWindow(_registry, _configService, _scheduler!);
        settingsWindow.ShowDialog();
    }

    private void CleanShutdown()
    {
        _scheduler?.Dispose();
        _trayIcon?.Dispose();
        _currentTrayIcon?.Dispose();
        _popupWindow?.Close();
        _singleInstance?.Dispose();
        Log.Information("Goodbye!");
        Log.CloseAndFlush();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.Dispose();
        _trayIcon?.Dispose();
        _currentTrayIcon?.Dispose();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private ExternalAppCommand ParseStartupCommand(string[] args)
    {
        foreach (var arg in args.Select(a => a.Trim().ToLowerInvariant()))
        {
            if (arg is "--exit" or "-exit" or "/exit")
                return ExternalAppCommand.Exit;
            if (arg is "--show" or "-show" or "/show")
                return ExternalAppCommand.Show;
        }

        return ExternalAppCommand.Toggle;
    }

    private void HandleExternalCommand(ExternalAppCommand command)
    {
        Dispatcher.Invoke(() =>
        {
            switch (command)
            {
                case ExternalAppCommand.Exit:
                    CleanShutdown();
                    break;
                case ExternalAppCommand.Show:
                    ShowPopup();
                    break;
                case ExternalAppCommand.Toggle:
                default:
                    TogglePopupVisibility();
                    break;
            }
        });
    }

    private void TogglePopupVisibility()
    {
        if (_popupWindow is null)
            return;

        if (_popupWindow.IsVisible)
            _popupWindow.Hide();
        else
            _popupWindow.ShowNearTray();
    }

    private void ShowPopup()
    {
        _popupWindow?.ShowNearTray();
    }

    private static void InitializeLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexBar", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "codexbar-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
