using System.Reflection;
using CodexBar.Core.Models;
using CodexBar.Providers.Codex;

namespace CodexBar.Tests;

public sealed class CodexCliProviderParsingTests
{
    [Fact]
    public void ParseWhamResponse_ParsesLegacyQuotaNodes()
    {
        const string json = """
        {
          "plan_type": "pro",
          "five_hour_limit": { "used_percent": 23.5, "resets_at": 1893456000 },
          "seven_day_limit": { "pct_used": 47.0, "resets_at": 1894060800 }
        }
        """;

        var snapshot = InvokeParseWhamResponse(json, "o3");

        Assert.NotNull(snapshot.SessionQuota);
        Assert.NotNull(snapshot.WeeklyQuota);
        Assert.Equal(23.5, snapshot.SessionQuota!.UsedPercent);
        Assert.Equal(47.0, snapshot.WeeklyQuota!.UsedPercent);
        Assert.Equal("wham", snapshot.SourceLabel);
    }

    [Fact]
    public void ParseWhamResponse_ParsesPrimaryWindowSchema()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": {
              "limit_window_seconds": 604800,
              "used_percent": 76.0,
              "reset_after_seconds": 3600
            }
          }
        }
        """;

        var snapshot = InvokeParseWhamResponse(json, "o4");

        Assert.NotNull(snapshot.WeeklyQuota);
        Assert.Equal(76.0, snapshot.WeeklyQuota!.UsedPercent);
        Assert.Equal("Weekly", snapshot.WeeklyQuota.Label);
    }

    private static UsageSnapshot InvokeParseWhamResponse(string json, string? model)
    {
        var providerType = typeof(CodexCliProvider);
        var method = providerType.GetMethod("ParseWhamResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var configType = providerType.GetNestedType("CodexConfig", BindingFlags.NonPublic);
        Assert.NotNull(configType);
        var config = Activator.CreateInstance(configType!);
        Assert.NotNull(config);

        var modelField = configType!.GetField("model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(modelField);
        modelField!.SetValue(config, model);

        var snapshot = method!.Invoke(null, [json, config]) as UsageSnapshot;
        Assert.NotNull(snapshot);
        return snapshot!;
    }
}
