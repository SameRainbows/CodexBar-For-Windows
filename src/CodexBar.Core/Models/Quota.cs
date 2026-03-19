namespace CodexBar.Core.Models;

/// <summary>
/// Represents usage quota for a single window (session or weekly).
/// </summary>
public sealed record Quota
{
    /// <summary>Label for this quota window (e.g. "5h Session", "Weekly").</summary>
    public required string Label { get; init; }

    /// <summary>Percentage of quota used (0.0 to 100.0).</summary>
    public double UsedPercent { get; init; }

    /// <summary>Percentage of quota remaining (100.0 - UsedPercent).</summary>
    public double RemainingPercent => Math.Max(0, 100.0 - UsedPercent);

    /// <summary>Reset window information for this quota.</summary>
    public ResetWindow? Reset { get; init; }

    /// <summary>Whether this quota window is currently exhausted (>= 100%).</summary>
    public bool IsExhausted => UsedPercent >= 100.0;

    /// <summary>Format as a short human-readable string.</summary>
    public string ToDisplayString()
    {
        var pct = $"{RemainingPercent:F0}% remaining";
        var reset = Reset?.ResetDescription;
        return reset is not null ? $"{Label}: {pct} ({reset})" : $"{Label}: {pct}";
    }
}
