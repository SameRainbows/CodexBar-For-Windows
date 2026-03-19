namespace CodexBar.Core.Models;

/// <summary>
/// A point-in-time snapshot of usage data from a provider.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>Session/short-window quota (e.g. 5-hour window).</summary>
    public Quota? SessionQuota { get; init; }

    /// <summary>Weekly/long-window quota.</summary>
    public Quota? WeeklyQuota { get; init; }

    /// <summary>Available credits balance (if the provider supports credits).</summary>
    public decimal? CreditsRemaining { get; init; }

    /// <summary>Whether the account has any credits at all.</summary>
    public bool? HasCredits { get; init; }

    /// <summary>Account email or identifier (never logged in full).</summary>
    public string? AccountIdentity { get; init; }

    /// <summary>Plan name (e.g. "Pro", "Max", "Team").</summary>
    public string? PlanName { get; init; }

    /// <summary>Optional per-model quotas for providers that expose detailed model usage.</summary>
    public IReadOnlyList<ModelQuota>? ModelQuotas { get; init; }

    /// <summary>When this snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional error message if the fetch partially failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Source label (e.g. "cli", "oauth", "cookies").</summary>
    public string? SourceLabel { get; init; }

    /// <summary>Authentication/availability state for actionable UI messaging.</summary>
    public ProviderAuthState AuthState { get; init; } = ProviderAuthState.Unknown;

    /// <summary>Actionable hint for fixing provider issues.</summary>
    public string? ActionHint { get; init; }

    /// <summary>Whether this snapshot only contains partial data.</summary>
    public bool IsPartial { get; init; }

    /// <summary>Format a one-line tooltip summary.</summary>
    public string ToTooltipLine(string providerName)
    {
        if (ErrorMessage is not null)
        {
            var action = string.IsNullOrWhiteSpace(ActionHint) ? string.Empty : $" ({ActionHint})";
            return $"{providerName}: ⚠ {ErrorMessage}{action}";
        }

        var parts = new List<string>();

        if (SessionQuota is not null)
            parts.Add($"Session: {SessionQuota.RemainingPercent:F0}%");

        if (WeeklyQuota is not null)
            parts.Add($"Weekly: {WeeklyQuota.RemainingPercent:F0}%");

        if (CreditsRemaining.HasValue)
            parts.Add($"Credits: ${CreditsRemaining:F2}");

        if (ModelQuotas is { Count: > 0 })
        {
            var minModel = ModelQuotas
                .OrderBy(m => m.RemainingPercent)
                .First();
            parts.Add($"{minModel.ModelName}: {minModel.RemainingPercent:F0}%");
        }

        if (parts.Count == 0)
            return $"{providerName}: No data";

        return $"{providerName}: {string.Join(" | ", parts)}";
    }
}
