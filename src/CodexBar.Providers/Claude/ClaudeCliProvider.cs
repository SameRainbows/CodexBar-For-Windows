using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.Providers.Claude;

/// <summary>
/// Claude provider — reads credentials from ~/.claude/.credentials.json (OAuth)
/// or detects the 'claude' CLI in PATH, and calls the Anthropic usage API.
///
/// Strategy:
/// 1. Check if ~/.claude/.credentials.json exists (OAuth creds from Claude CLI)
/// 2. Or check if 'claude' CLI is in PATH
/// 3. If OAuth creds: call Anthropic usage API directly
/// 4. If CLI only: run 'claude' with /usage command
/// 5. Parse response into UsageSnapshot
/// </summary>
public sealed class ClaudeCliProvider : IUsageProvider, IProviderDiagnostics
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ClaudeCliProvider>();
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly CodexBar.Core.Platform.ProcessRunner _processRunner;
    private bool _isEnabled;

    public string Id => "claude";
    public string DisplayName => "Claude";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public ClaudeCliProvider(CodexBar.Core.Platform.ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Check credentials file first
        var credsPath = GetCredentialsPath();
        if (File.Exists(credsPath))
        {
            Log.Debug("Claude: found credentials at {Path}", credsPath);
            return true;
        }

        // Check CLI in PATH
        var cliName = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";
        var resolvedPath = await _processRunner.ResolveCommandPathAsync(cliName, ct);
        Log.Debug("Claude: CLI in PATH resolved to: {Path}", resolvedPath ?? "null");
        return resolvedPath != null;
    }

    public async Task<ProviderDiagnostics> DiagnoseAsync(CancellationToken ct = default)
    {
        var checks = new List<string>();
        var credsPath = GetCredentialsPath();
        var hasCreds = File.Exists(credsPath);
        checks.Add($"credentials file present: {hasCreds}");
        checks.Add($"credentials path: {credsPath}");

        var cliName = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";
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
            Log.Debug("Claude: starting usage fetch");

            // Try OAuth API first
            var creds = ReadCredentials();
            if (creds?.AccessToken is not null)
            {
                Log.Debug("Claude: attempting OAuth API fetch");
                var oauthResult = await FetchViaOAuthApi(creds.AccessToken, ct);
                if (oauthResult.ErrorMessage is null)
                    return oauthResult;

                Log.Debug("Claude: OAuth API failed ({Error}), falling back", oauthResult.ErrorMessage);
            }

            // Try CLI fallback
            var cliName = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";
            var resolvedPath = await _processRunner.ResolveCommandPathAsync(cliName, ct);
            if (resolvedPath != null)
            {
                Log.Debug("Claude: attempting CLI fetch using {Path}", resolvedPath);
                return await FetchViaCli(resolvedPath, ct);
            }

            // If we have creds but API failed, return the error
            if (creds is not null)
            {
                return new UsageSnapshot
                {
                    SourceLabel = "oauth",
                    ErrorMessage = "OAuth credentials found but API call failed",
                    PlanName = creds.Plan,
                    AuthState = ProviderAuthState.NetworkError,
                    ActionHint = "Check network and Claude authentication state",
                };
            }

            return new UsageSnapshot
            {
                ErrorMessage = "No Claude credentials or CLI found",
                AuthState = ProviderAuthState.MissingCli,
                ActionHint = "Install Claude CLI and run claude login",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Claude: fetch failed");
            return new UsageSnapshot
            {
                ErrorMessage = $"Fetch failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Check network and retry",
            };
        }
    }

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        return await FetchUsageAsync(ct);
    }

    private async Task<UsageSnapshot> FetchViaOAuthApi(string accessToken, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            var response = await HttpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                Log.Warning("Claude: OAuth API returned {StatusCode}", statusCode);
                return new UsageSnapshot
                {
                    SourceLabel = "oauth",
                    ErrorMessage = $"OAuth API HTTP {statusCode}",
                    AuthState = statusCode is 401 or 403
                        ? ProviderAuthState.NeedsLogin
                        : ProviderAuthState.NetworkError,
                    ActionHint = statusCode is 401 or 403
                        ? "Run claude login"
                        : "Check network and retry",
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            Log.Debug("Claude: got OAuth API response ({Length} chars)", json.Length);

            return ParseOAuthUsageResponse(json);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Claude: OAuth HTTP request failed");
            return new UsageSnapshot
            {
                SourceLabel = "oauth",
                ErrorMessage = $"Network error: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Check network and retry",
            };
        }
    }

    private static UsageSnapshot ParseOAuthUsageResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Quota? sessionQuota = null;
            Quota? weeklyQuota = null;

            // Parse five_hour window
            if (root.TryGetProperty("five_hour", out var fiveHour))
            {
                sessionQuota = ParseWindowQuota(fiveHour, "5h Session", 300);
            }

            // Parse seven_day window
            if (root.TryGetProperty("seven_day", out var sevenDay))
            {
                weeklyQuota = ParseWindowQuota(sevenDay, "Weekly", 10080);
            }

            // Parse plan info
            string? planName = null;
            if (root.TryGetProperty("rate_limit_tier", out var tier))
            {
                planName = tier.GetString() switch
                {
                    "max" => "Max",
                    "pro" => "Pro",
                    "team" => "Team",
                    "enterprise" => "Enterprise",
                    var s => s,
                };
            }

            return new UsageSnapshot
            {
                SessionQuota = sessionQuota,
                WeeklyQuota = weeklyQuota,
                PlanName = planName,
                SourceLabel = "oauth",
                AuthState = ProviderAuthState.Authenticated,
                IsPartial = sessionQuota is null || weeklyQuota is null,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude: failed to parse OAuth response");
            return new UsageSnapshot
            {
                SourceLabel = "oauth",
                ErrorMessage = "Failed to parse usage response",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Retry in a moment",
            };
        }
    }

    private static Quota? ParseWindowQuota(JsonElement window, string label, int windowMinutes)
    {
        try
        {
            double usedPercent = 0;
            DateTimeOffset? resetsAt = null;

            if (window.TryGetProperty("percent_used", out var pct))
                usedPercent = pct.GetDouble();
            else if (window.TryGetProperty("used_percent", out var pct2))
                usedPercent = pct2.GetDouble();

            if (window.TryGetProperty("resets_at", out var resetProp))
            {
                if (DateTimeOffset.TryParse(resetProp.GetString(), out var dt))
                    resetsAt = dt;
            }

            return new Quota
            {
                Label = label,
                UsedPercent = usedPercent,
                Reset = new ResetWindow
                {
                    ResetsAt = resetsAt,
                    WindowMinutes = windowMinutes,
                },
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<UsageSnapshot> FetchViaCli(string cliName, CancellationToken ct)
    {
        // Try 'claude usage' direct command
        var result = await _processRunner.RunAsync(cliName, "usage",
            timeout: TimeSpan.FromSeconds(10), ct: ct);

        var combinedOutput = result.Stdout + "\n" + result.Stderr;
        var strippedOutput = CodexBar.Core.Parsing.CliOutputParser.StripAnsi(combinedOutput);

        if (strippedOutput.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            strippedOutput.Contains("Browser didn't open", StringComparison.OrdinalIgnoreCase) ||
            strippedOutput.Contains("Welcome to Claude Code", StringComparison.OrdinalIgnoreCase))
        {
            return new UsageSnapshot
            {
                SourceLabel = "cli",
                ErrorMessage = "Please log in to Claude CLI",
                AuthState = ProviderAuthState.NeedsLogin,
                ActionHint = "Run claude login",
            };
        }

        if (!string.IsNullOrWhiteSpace(strippedOutput))
        {
            var sessionPct = CodexBar.Core.Parsing.CliOutputParser.ExtractPercentage(strippedOutput, "session")
                          ?? CodexBar.Core.Parsing.CliOutputParser.ExtractPercentage(strippedOutput, "5h");

            var weeklyPct = CodexBar.Core.Parsing.CliOutputParser.ExtractPercentage(strippedOutput, "week")
                          ?? CodexBar.Core.Parsing.CliOutputParser.ExtractPercentage(strippedOutput, "7-day");

            if (sessionPct.HasValue || weeklyPct.HasValue)
            {
                return new UsageSnapshot
                {
                    SessionQuota = sessionPct.HasValue ? new Quota
                    {
                        Label = "5h Session",
                        UsedPercent = sessionPct.Value,
                        Reset = new ResetWindow { WindowMinutes = 300 },
                    } : null,
                    WeeklyQuota = weeklyPct.HasValue ? new Quota
                    {
                        Label = "Weekly",
                        UsedPercent = weeklyPct.Value,
                        Reset = new ResetWindow { WindowMinutes = 10080 },
                    } : null,
                    SourceLabel = "cli",
                    AuthState = ProviderAuthState.Authenticated,
                    IsPartial = !(sessionPct.HasValue && weeklyPct.HasValue),
                };
            }
        }

        return new UsageSnapshot
        {
            SourceLabel = "cli",
            ErrorMessage = result.ErrorMessage ?? "Could not parse Claude CLI output",
            AuthState = ProviderAuthState.Unknown,
            ActionHint = "Run claude /usage to verify CLI output",
        };
    }

    private static string GetCredentialsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
    }

    private static ClaudeCredentials? ReadCredentials()
    {
        try
        {
            var path = GetCredentialsPath();
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClaudeCredentials>(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude: failed to read credentials");
            return null;
        }
    }

    private sealed class ClaudeCredentials
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("plan")]
        public string? Plan { get; set; }
    }
}
