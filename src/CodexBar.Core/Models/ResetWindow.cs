namespace CodexBar.Core.Models;

/// <summary>
/// Represents a usage window reset countdown.
/// </summary>
public sealed record ResetWindow
{
    /// <summary>When the usage window resets.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Duration of the window in minutes (e.g. 300 for 5h, 10080 for 7d).</summary>
    public int? WindowMinutes { get; init; }

    /// <summary>
    /// Human-readable reset description (e.g. "Resets in 2h 14m").
    /// Computed from <see cref="ResetsAt"/> if not explicitly set.
    /// </summary>
    public string ResetDescription
    {
        get
        {
            if (_resetDescription is not null)
                return _resetDescription;

            if (ResetsAt is null)
                return "Unknown";

            var remaining = ResetsAt.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return "Resetting now";

            if (remaining.TotalDays >= 1)
                return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";

            if (remaining.TotalHours >= 1)
                return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";

            return $"Resets in {remaining.Minutes}m";
        }
    }

    private readonly string? _resetDescription;

    public ResetWindow() { }

    public ResetWindow(string resetDescription)
    {
        _resetDescription = resetDescription;
    }
}
