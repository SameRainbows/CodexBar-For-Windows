using System.Reflection;
using CodexBar.Core.Models;
using CodexBar.Providers.Claude;

namespace CodexBar.Tests;

public sealed class ClaudeCliProviderParsingTests
{
    [Fact]
    public void ParseOAuthUsageResponse_ParsesSessionAndWeekly()
    {
        const string json = """
        {
          "rate_limit_tier": "pro",
          "five_hour": {
            "percent_used": 40.0,
            "resets_at": "2026-04-01T00:00:00Z"
          },
          "seven_day": {
            "used_percent": 65.0,
            "resets_at": "2026-04-07T00:00:00Z"
          }
        }
        """;

        var method = typeof(ClaudeCliProvider).GetMethod(
            "ParseOAuthUsageResponse",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var snapshot = method!.Invoke(null, [json]) as UsageSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal("oauth", snapshot!.SourceLabel);
        Assert.NotNull(snapshot.SessionQuota);
        Assert.NotNull(snapshot.WeeklyQuota);
        Assert.Equal(40, snapshot.SessionQuota!.UsedPercent);
        Assert.Equal(65, snapshot.WeeklyQuota!.UsedPercent);
        Assert.Equal("Pro", snapshot.PlanName);
    }
}
