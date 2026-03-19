namespace CodexBar.Core.Models;

/// <summary>
/// Aggregated usage record for a single provider, combining status and snapshot.
/// </summary>
public sealed record UsageRecord
{
    /// <summary>Provider identifier (e.g. "claude", "codex").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Human-readable provider name (e.g. "Claude", "Codex").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Current operational status.</summary>
    public ProviderStatus Status { get; init; } = ProviderStatus.Unavailable;

    /// <summary>Latest usage snapshot (null if never fetched).</summary>
    public UsageSnapshot? Snapshot { get; init; }

    /// <summary>Whether the user has enabled this provider.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Timestamp of the last successful fetch.</summary>
    public DateTimeOffset? LastSuccessfulFetch { get; init; }

    /// <summary>Timestamp of the last fetch attempt (success or failure).</summary>
    public DateTimeOffset LastAttemptAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message (if status is Error).</summary>
    public string? LastError { get; init; }
}
