using CodexBar.Core.Platform;
using CodexBar.Core.Models;
using CodexBar.Core.Parsing;
using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.Providers.Gemini;

/// <summary>
/// Gemini CLI provider — detects 'gemini' CLI and fetches quota data.
/// </summary>
public sealed class GeminiCliProvider : IUsageProvider, IProviderDiagnostics
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GeminiCliProvider>();

    private readonly ProcessRunner _processRunner;
    private bool _isEnabled;

    public string Id => "gemini";
    public string DisplayName => "Gemini";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public GeminiCliProvider(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var cliName = OperatingSystem.IsWindows() ? "gemini.cmd" : "gemini";
            return await _processRunner.ResolveCommandPathAsync(cliName, ct) != null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Gemini: availability check failed");
            return false;
        }
    }

    public async Task<ProviderDiagnostics> DiagnoseAsync(CancellationToken ct = default)
    {
        var checks = new List<string>();
        var cliName = OperatingSystem.IsWindows() ? "gemini.cmd" : "gemini";
        var resolvedPath = await _processRunner.ResolveCommandPathAsync(cliName, ct);

        checks.Add($"cli found: {resolvedPath is not null}");
        if (resolvedPath is not null)
            checks.Add($"cli path: {resolvedPath}");

        var snapshot = await FetchUsageAsync(ct);
        checks.Add($"last fetch source: {snapshot.SourceLabel ?? "none"}");
        checks.Add($"last fetch error: {snapshot.ErrorMessage ?? "none"}");

        return new ProviderDiagnostics
        {
            ProviderId = Id,
            AuthState = snapshot.AuthState,
            SuggestedAction = snapshot.ActionHint,
            Checks = checks.ToArray(),
        };
    }

    public async Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            Log.Debug("Gemini: starting usage fetch");
            var cliName = OperatingSystem.IsWindows() ? "gemini.cmd" : "gemini";
            var cliPath = await _processRunner.ResolveCommandPathAsync(cliName, ct);
            if (cliPath is null)
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "Gemini CLI not found",
                    SourceLabel = "cli",
                    AuthState = ProviderAuthState.MissingCli,
                    ActionHint = "Install Gemini CLI and ensure it is on PATH",
                };
            }

            // Try running gemini with a usage/status command
            var result = await _processRunner.RunAsync(
                cliPath,
                "",
                timeout: TimeSpan.FromSeconds(15),
                stdinInput: "/usage\n/exit\n",
                ct: ct);
            var parsed = TryParseUsage(result.Stdout, result.Stderr);
            if (parsed is not null)
                return parsed;

            // Fallback command strategy for CLI variants.
            var fallback = await _processRunner.RunAsync(
                cliPath,
                "usage",
                timeout: TimeSpan.FromSeconds(10),
                ct: ct);
            parsed = TryParseUsage(fallback.Stdout, fallback.Stderr);
            if (parsed is not null)
                return parsed;

            return new UsageSnapshot
            {
                ErrorMessage = fallback.ErrorMessage ?? result.ErrorMessage ?? "Could not parse Gemini usage",
                SourceLabel = "cli",
                AuthState = ProviderAuthState.Unknown,
                ActionHint = "Run gemini usage manually to verify CLI output format",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Gemini: fetch failed");
            return new UsageSnapshot
            {
                ErrorMessage = $"Fetch failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Retry in a moment",
            };
        }
    }

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        return await FetchUsageAsync(ct);
    }

    private static UsageSnapshot? TryParseUsage(string stdout, string stderr)
    {
        var merged = CliOutputParser.StripAnsi($"{stdout}\n{stderr}");
        if (string.IsNullOrWhiteSpace(merged))
            return null;

        if (merged.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            merged.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return new UsageSnapshot
            {
                ErrorMessage = "Please log in to Gemini CLI",
                SourceLabel = "cli",
                AuthState = ProviderAuthState.NeedsLogin,
                ActionHint = "Run gemini login",
            };
        }

        var sessionPct = CliOutputParser.ExtractPercentage(merged, "usage")
                      ?? CliOutputParser.ExtractPercentage(merged, "quota");
        if (!sessionPct.HasValue)
            return null;

        return new UsageSnapshot
        {
            SessionQuota = new Quota
            {
                Label = "Quota",
                UsedPercent = sessionPct.Value,
            },
            SourceLabel = "cli",
            AuthState = ProviderAuthState.Authenticated,
        };
    }
}
