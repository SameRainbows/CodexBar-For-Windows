namespace CodexBar.Core.Models;

/// <summary>
/// Per-model quota row for providers that expose model-level limits (e.g. AntiGravity).
/// </summary>
public sealed record ModelQuota
{
    /// <summary>Display name of the model (e.g. "Gemini 3.1 Pro (High)").</summary>
    public required string ModelName { get; init; }

    /// <summary>Remaining fraction in range [0, 1].</summary>
    public double RemainingRatio { get; init; }

    /// <summary>Remaining percent in range [0, 100].</summary>
    public double RemainingPercent => Math.Clamp(RemainingRatio * 100.0, 0, 100);

    /// <summary>Quota reset time, if provided by the source.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Optional tag/badge text (e.g. "New").</summary>
    public string? Badge { get; init; }

    /// <summary>Optional metadata value that can help distinguish quota classes.</summary>
    public int? ModelClass { get; init; }

    /// <summary>Count of capability entries attached to this model.</summary>
    public int CapabilityCount { get; init; }
}
