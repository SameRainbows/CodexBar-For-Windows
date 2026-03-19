using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.App.Platform;

/// <summary>
/// Adaptive polling scheduler that periodically fetches usage from all enabled providers.
/// Handles sleep/wake gaps, stale state, urgency near quota exhaustion, and failure backoff.
/// </summary>
public sealed class PollingScheduler : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PollingScheduler>();

    private readonly List<IUsageProvider> _providers;
    private readonly Action<string, UsageRecord> _onUpdate;
    private readonly Action<Exception> _onError;
    private readonly Dictionary<string, UsageRecord> _latestRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failureStreaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _jitter = new();
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private DateTimeOffset _lastPollTime = DateTimeOffset.MinValue;

    /// <summary>Configured base polling interval.</summary>
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most recently used adaptive interval (includes urgency/backoff, pre-jitter).</summary>
    public TimeSpan EffectiveInterval { get; private set; } = TimeSpan.FromMinutes(5);

    public PollingScheduler(
        List<IUsageProvider> providers,
        Action<string, UsageRecord> onUpdate,
        Action<Exception> onError)
    {
        _providers = providers;
        _onUpdate = onUpdate;
        _onError = onError;
    }

    /// <summary>Start polling with the given base interval.</summary>
    public void Start(TimeSpan? interval = null)
    {
        Stop();

        if (interval.HasValue)
            Interval = interval.Value;

        _cts = new CancellationTokenSource();
        _pollingTask = PollLoopAsync(_cts.Token);

        Log.Information("Polling started with base interval {Interval}", Interval);

        // Immediate first poll.
        _ = PollAllProvidersAsync(_cts.Token);
    }

    /// <summary>Stop polling.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _pollingTask = null;
        Log.Information("Polling stopped");
    }

    /// <summary>Change base polling interval.</summary>
    public void SetInterval(TimeSpan interval)
    {
        Interval = interval;
        Log.Information("Polling base interval changed to {Interval}", interval);
    }

    /// <summary>Force an immediate poll of all providers.</summary>
    public async Task RefreshNowAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await PollAllProvidersAsync(ct);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                EffectiveInterval = ComputeAdaptiveInterval();
                var delay = ApplyJitter(EffectiveInterval);
                await Task.Delay(delay, ct);

                var now = DateTimeOffset.UtcNow;
                if (_lastPollTime != DateTimeOffset.MinValue)
                {
                    var gap = now - _lastPollTime;
                    if (gap > Interval * 2)
                        Log.Information("Detected wake from sleep (gap: {Gap})", gap);
                }

                await PollAllProvidersAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Polling loop crashed");
            _onError(ex);
        }
    }

    private async Task PollAllProvidersAsync(CancellationToken ct)
    {
        _lastPollTime = DateTimeOffset.UtcNow;

        var enabledProviders = _providers.Where(p => p.IsEnabled).ToList();
        if (enabledProviders.Count == 0)
            return;

        Log.Debug("Polling {Count} enabled providers (base={Base}, effective={Effective})",
            enabledProviders.Count, Interval, EffectiveInterval);

        var tasks = enabledProviders.Select(p => PollSingleProviderAsync(p, ct));
        await Task.WhenAll(tasks);
    }

    private async Task PollSingleProviderAsync(IUsageProvider provider, CancellationToken ct)
    {
        var attemptAt = DateTimeOffset.UtcNow;
        var previous = GetPreviousRecord(provider.Id);
        var previousSuccess = previous?.LastSuccessfulFetch;

        try
        {
            var isAvailable = await provider.IsAvailableAsync(ct);
            if (!isAvailable)
            {
                var unavailable = new UsageRecord
                {
                    ProviderId = provider.Id,
                    DisplayName = provider.DisplayName,
                    Status = ApplyStaleStatus(ProviderStatus.Unavailable, previousSuccess, attemptAt),
                    Snapshot = previous?.Snapshot,
                    IsEnabled = provider.IsEnabled,
                    LastSuccessfulFetch = previousSuccess,
                    LastAttemptAt = attemptAt,
                    LastError = "Provider not installed/configured",
                };
                UpdateState(unavailable, success: false);
                return;
            }

            var fetching = new UsageRecord
            {
                ProviderId = provider.Id,
                DisplayName = provider.DisplayName,
                Status = ProviderStatus.Fetching,
                Snapshot = previous?.Snapshot,
                IsEnabled = provider.IsEnabled,
                LastSuccessfulFetch = previousSuccess,
                LastAttemptAt = attemptAt,
            };
            _onUpdate(provider.Id, fetching);

            var snapshot = await provider.FetchUsageAsync(ct);
            var baseStatus = snapshot.ErrorMessage is null
                ? ProviderStatus.Available
                : ProviderStatus.Error;

            var successAt = baseStatus == ProviderStatus.Available
                ? attemptAt
                : previousSuccess;

            // If current fetch failed but we had prior data, keep last known snapshot for stale view.
            var snapshotForUi = baseStatus == ProviderStatus.Available
                ? snapshot
                : previous?.Snapshot ?? snapshot;

            var finalStatus = ApplyStaleStatus(baseStatus, successAt, attemptAt);

            var record = new UsageRecord
            {
                ProviderId = provider.Id,
                DisplayName = provider.DisplayName,
                Status = finalStatus,
                Snapshot = snapshotForUi,
                IsEnabled = provider.IsEnabled,
                LastSuccessfulFetch = successAt,
                LastAttemptAt = attemptAt,
                LastError = snapshot.ErrorMessage,
            };

            Log.Information("Provider {Id}: status={Status}, session={SessionPct}, weekly={WeeklyPct}, error={Error}",
                provider.Id, finalStatus,
                snapshot.SessionQuota?.UsedPercent,
                snapshot.WeeklyQuota?.UsedPercent,
                snapshot.ErrorMessage);

            UpdateState(record, success: baseStatus == ProviderStatus.Available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Failed to poll provider {Provider}", provider.Id);

            var errored = new UsageRecord
            {
                ProviderId = provider.Id,
                DisplayName = provider.DisplayName,
                Status = ApplyStaleStatus(ProviderStatus.Error, previousSuccess, attemptAt),
                Snapshot = previous?.Snapshot,
                IsEnabled = provider.IsEnabled,
                LastSuccessfulFetch = previousSuccess,
                LastAttemptAt = attemptAt,
                LastError = ex.Message,
            };
            UpdateState(errored, success: false);
        }
    }

    private UsageRecord? GetPreviousRecord(string providerId)
    {
        lock (_lock)
        {
            return _latestRecords.TryGetValue(providerId, out var record) ? record : null;
        }
    }

    private void UpdateState(UsageRecord record, bool success)
    {
        lock (_lock)
        {
            _latestRecords[record.ProviderId] = record;
            if (success)
            {
                _failureStreaks[record.ProviderId] = 0;
            }
            else
            {
                _failureStreaks.TryGetValue(record.ProviderId, out var streak);
                _failureStreaks[record.ProviderId] = streak + 1;
            }
        }

        _onUpdate(record.ProviderId, record);
    }

    private ProviderStatus ApplyStaleStatus(
        ProviderStatus baseStatus,
        DateTimeOffset? lastSuccessfulFetch,
        DateTimeOffset now)
    {
        if (baseStatus is ProviderStatus.Available or ProviderStatus.Fetching)
            return baseStatus;

        if (lastSuccessfulFetch.HasValue && (now - lastSuccessfulFetch.Value) > Interval * 2)
            return ProviderStatus.Stale;

        return baseStatus;
    }

    private TimeSpan ComputeAdaptiveInterval()
    {
        var effective = Interval;

        List<UsageRecord> enabledRecords;
        int maxFailureStreak;
        lock (_lock)
        {
            enabledRecords = _latestRecords.Values.Where(r => r.IsEnabled).ToList();
            maxFailureStreak = _failureStreaks.Count > 0 ? _failureStreaks.Values.Max() : 0;
        }

        foreach (var record in enabledRecords)
        {
            var minRemaining = GetMinRemainingPercent(record.Snapshot);
            if (!minRemaining.HasValue)
                continue;

            if (minRemaining.Value <= 0)
                effective = Min(effective, TimeSpan.FromSeconds(20));
            else if (minRemaining.Value <= 10)
                effective = Min(effective, TimeSpan.FromSeconds(30));
            else if (minRemaining.Value <= 20)
                effective = Min(effective, TimeSpan.FromSeconds(60));
            else if (minRemaining.Value <= 40)
                effective = Min(effective, TimeSpan.FromSeconds(120));

            var nearestReset = GetNearestReset(record.Snapshot);
            if (nearestReset.HasValue)
            {
                var untilReset = nearestReset.Value - DateTimeOffset.UtcNow;
                if (untilReset <= TimeSpan.FromMinutes(15))
                    effective = Min(effective, TimeSpan.FromSeconds(60));
            }
        }

        // Back off polling after persistent failures unless urgency already demanded faster cadence.
        if (effective >= Interval && maxFailureStreak >= 3)
        {
            var backoffMultiplier = Math.Min(4, 1 + (maxFailureStreak / 3));
            var backedOff = TimeSpan.FromMilliseconds(Interval.TotalMilliseconds * backoffMultiplier);
            effective = Max(effective, Min(backedOff, TimeSpan.FromMinutes(15)));
        }

        return Clamp(effective, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));
    }

    private TimeSpan ApplyJitter(TimeSpan interval)
    {
        var ratio = 1.0 + (_jitter.NextDouble() * 0.2 - 0.1); // +/-10%
        var jittered = TimeSpan.FromMilliseconds(interval.TotalMilliseconds * ratio);
        return Clamp(jittered, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));
    }

    private static double? GetMinRemainingPercent(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        var values = new List<double>();
        if (snapshot.SessionQuota is not null)
            values.Add(snapshot.SessionQuota.RemainingPercent);
        if (snapshot.WeeklyQuota is not null)
            values.Add(snapshot.WeeklyQuota.RemainingPercent);

        return values.Count > 0 ? values.Min() : null;
    }

    private static DateTimeOffset? GetNearestReset(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        var candidates = new List<DateTimeOffset>();
        if (snapshot.SessionQuota?.Reset?.ResetsAt is { } sessionReset)
            candidates.Add(sessionReset);
        if (snapshot.WeeklyQuota?.Reset?.ResetsAt is { } weeklyReset)
            candidates.Add(weeklyReset);

        return candidates.Count > 0 ? candidates.Min() : null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public void Dispose()
    {
        Stop();
    }
}
