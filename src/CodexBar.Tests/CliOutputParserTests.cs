using CodexBar.Core.Parsing;

namespace CodexBar.Tests;

public sealed class CliOutputParserTests
{
    [Fact]
    public void StripAnsi_RemovesEscapeCodes()
    {
        var input = "\u001b[32mUsage: 42%\u001b[0m";

        var result = CliOutputParser.StripAnsi(input);

        Assert.Equal("Usage: 42%", result);
    }

    [Fact]
    public void ExtractPercentage_ReturnsPercentageNearLabel()
    {
        var output = "Session usage: 63%\nWeekly usage: 11%";

        var session = CliOutputParser.ExtractPercentage(output, "session");
        var weekly = CliOutputParser.ExtractPercentage(output, "weekly");

        Assert.Equal(63, session);
        Assert.Equal(11, weekly);
    }

    [Fact]
    public void ParseResetTimeOffset_ParsesDayHourMinuteFormat()
    {
        var before = DateTimeOffset.UtcNow;
        var reset = CliOutputParser.ParseResetTimeOffset("Resets in 1d 2h 15m");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(reset);
        Assert.InRange(reset!.Value, before.AddHours(26).AddMinutes(14), after.AddHours(26).AddMinutes(16));
    }
}
