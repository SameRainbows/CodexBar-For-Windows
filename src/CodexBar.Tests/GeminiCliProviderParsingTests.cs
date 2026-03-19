using System.Reflection;
using CodexBar.Core.Models;
using CodexBar.Providers.Gemini;

namespace CodexBar.Tests;

public sealed class GeminiCliProviderParsingTests
{
    [Fact]
    public void TryParseUsage_ReturnsQuotaSnapshot()
    {
        var snapshot = InvokeTryParseUsage("quota usage: 33%", "");

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.SessionQuota);
        Assert.Equal(33, snapshot.SessionQuota!.UsedPercent);
        Assert.Equal(ProviderAuthState.Authenticated, snapshot.AuthState);
    }

    [Fact]
    public void TryParseUsage_ReturnsNeedsLoginForAuthPrompt()
    {
        var snapshot = InvokeTryParseUsage("Please sign in to continue", "");

        Assert.NotNull(snapshot);
        Assert.Equal(ProviderAuthState.NeedsLogin, snapshot!.AuthState);
        Assert.NotNull(snapshot.ErrorMessage);
    }

    private static UsageSnapshot? InvokeTryParseUsage(string stdout, string stderr)
    {
        var method = typeof(GeminiCliProvider).GetMethod(
            "TryParseUsage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, [stdout, stderr]) as UsageSnapshot;
    }
}
