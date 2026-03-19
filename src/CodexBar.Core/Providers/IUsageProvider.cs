using CodexBar.Core.Models;

namespace CodexBar.Core.Providers;

/// <summary>
/// Contract that all AI usage providers must implement.
/// Each provider is responsible for detecting its availability,
/// fetching usage data, and reporting its current state.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable identifier (e.g. "claude", "codex"). Used as config key.</summary>
    string Id { get; }

    /// <summary>Human-readable name (e.g. "Claude", "Codex").</summary>
    string DisplayName { get; }

    /// <summary>Whether the user has enabled this provider in settings.</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Check whether the provider's data source is available on this machine.
    /// For CLI providers, this means the CLI tool is found in PATH.
    /// Must not throw — return false on failure.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the latest usage snapshot. Must respect the cancellation token
    /// and enforce its own timeout. Must not throw — return a snapshot with
    /// an ErrorMessage on failure.
    /// </summary>
    Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct = default);

    /// <summary>
    /// Force a refresh, bypassing any caching. Calls FetchUsageAsync internally.
    /// </summary>
    Task<UsageSnapshot> RefreshAsync(CancellationToken ct = default);
}
