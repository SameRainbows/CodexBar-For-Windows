using System.Text;
using System.Text.RegularExpressions;

namespace CodexBar.Core.Parsing;

/// <summary>
/// Utilities for parsing CLI output from AI provider tools.
/// Strips ANSI escape codes and extracts structured data from text.
/// </summary>
public static partial class CliOutputParser
{
    /// <summary>Remove ANSI escape sequences from CLI output.</summary>
    public static string StripAnsi(string input)
    {
        return AnsiPattern().Replace(input, string.Empty);
    }

    /// <summary>
    /// Extract a percentage value from text near a label.
    /// Looks for patterns like "42%", "42.5%", "42% used", "42% remaining".
    /// </summary>
    public static double? ExtractPercentage(string text, string nearLabel)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (!line.Contains(nearLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = PercentPattern().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
                return pct;
        }

        return null;
    }

    /// <summary>
    /// Extract reset time text from output (e.g. "Resets in 2h 14m").
    /// </summary>
    public static string? ExtractResetText(string text, string nearLabel)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (!line.Contains(nearLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = ResetPattern().Match(line);
            if (match.Success)
                return match.Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Try to parse a "Xh Ym" or "Xd Yh" string into a TimeSpan offset from now.
    /// </summary>
    public static DateTimeOffset? ParseResetTimeOffset(string resetText)
    {
        var now = DateTimeOffset.UtcNow;
        var total = TimeSpan.Zero;
        var found = false;

        var dayMatch = DayPattern().Match(resetText);
        if (dayMatch.Success)
        {
            total += TimeSpan.FromDays(int.Parse(dayMatch.Groups[1].Value));
            found = true;
        }

        var hourMatch = HourPattern().Match(resetText);
        if (hourMatch.Success)
        {
            total += TimeSpan.FromHours(int.Parse(hourMatch.Groups[1].Value));
            found = true;
        }

        var minMatch = MinutePattern().Match(resetText);
        if (minMatch.Success)
        {
            total += TimeSpan.FromMinutes(int.Parse(minMatch.Groups[1].Value));
            found = true;
        }

        return found ? now + total : null;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]|\x1b\].*?\x07", RegexOptions.Compiled)]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"[Rr]esets?\s+in\s+[\d]+[dhm][\s\d dhm]*", RegexOptions.Compiled)]
    private static partial Regex ResetPattern();

    [GeneratedRegex(@"(\d+)\s*d", RegexOptions.Compiled)]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"(\d+)\s*h", RegexOptions.Compiled)]
    private static partial Regex HourPattern();

    [GeneratedRegex(@"(\d+)\s*m", RegexOptions.Compiled)]
    private static partial Regex MinutePattern();
}
