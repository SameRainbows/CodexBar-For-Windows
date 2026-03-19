using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.Providers.Codex;

/// <summary>
/// Codex/OpenAI provider — reads auth from ~/.codex/auth.json.
/// 
/// Supports two auth modes:
/// 1. "apikey" — uses OpenAI API key, shows rate limit info from API headers
/// 2. "chatgpt"/"oauth" — uses OAuth access token, calls WHAM usage API for session/weekly quotas
/// </summary>
public sealed class CodexCliProvider : IUsageProvider, IProviderDiagnostics
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CodexCliProvider>();
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private bool _isEnabled;

    public string Id => "codex";
    public string DisplayName => "Codex";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public CodexCliProvider(CodexBar.Core.Platform.ProcessRunner _) { }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var authPath = GetAuthJsonPath();
            var exists = File.Exists(authPath);
            Log.Debug("Codex: auth.json at {Path}: {Exists}", authPath, exists);
            return Task.FromResult(exists);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex: availability check failed");
            return Task.FromResult(false);
        }
    }

    public async Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            Log.Debug("Codex: starting usage fetch");

            var auth = ReadAuthJson();
            if (auth is null)
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "Could not read ~/.codex/auth.json",
                    AuthState = ProviderAuthState.MissingCredentials,
                    ActionHint = "Sign in with Codex CLI to create auth.json",
                };
            }

            var config = ReadConfigToml();

            // Route based on auth mode
            var authMode = auth.AuthMode?.ToLowerInvariant() ?? "";
            Log.Debug("Codex: auth_mode={Mode}", authMode);

            if (authMode == "apikey" && !string.IsNullOrEmpty(auth.OpenAiApiKey))
            {
                return await FetchApiKeyMode(auth.OpenAiApiKey, config, ct);
            }
            else if (!string.IsNullOrEmpty(auth.AccessToken))
            {
                // OAuth/ChatGPT mode — has session/weekly capped usage
                return await FetchOAuthMode(auth.AccessToken, config, ct);
            }
            else
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "No valid auth credentials in auth.json",
                    AuthState = ProviderAuthState.MissingCredentials,
                    ActionHint = "Run codex login to refresh auth credentials",
                };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Codex: fetch failed");
            return new UsageSnapshot
            {
                ErrorMessage = $"Fetch failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Check network/auth and retry",
            };
        }
    }

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken ct = default)
        => await FetchUsageAsync(ct);

    public async Task<ProviderDiagnostics> DiagnoseAsync(CancellationToken ct = default)
    {
        var checks = new List<string>();
        var authPath = GetAuthJsonPath();
        var authExists = File.Exists(authPath);
        checks.Add($"auth.json present: {authExists}");
        checks.Add($"auth path: {authPath}");

        if (!authExists)
        {
            return new ProviderDiagnostics
            {
                ProviderId = Id,
                AuthState = ProviderAuthState.MissingCredentials,
                SuggestedAction = "Run codex login",
                Checks = checks.ToArray(),
            };
        }

        var auth = ReadAuthJson();
        if (auth is null)
        {
            checks.Add("auth parse: failed");
            return new ProviderDiagnostics
            {
                ProviderId = Id,
                AuthState = ProviderAuthState.MissingCredentials,
                SuggestedAction = "Re-run codex login to regenerate auth.json",
                Checks = checks.ToArray(),
            };
        }

        checks.Add($"auth mode: {auth.AuthMode ?? "unknown"}");
        checks.Add($"has API key: {!string.IsNullOrWhiteSpace(auth.OpenAiApiKey)}");
        checks.Add($"has access token: {!string.IsNullOrWhiteSpace(auth.AccessToken)}");

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

    // ─── API KEY MODE ─────────────────────────────────────────────────

    private async Task<UsageSnapshot> FetchApiKeyMode(string apiKey, CodexConfig config, CancellationToken ct)
    {
        Log.Debug("Codex: using API key mode");

        // Verify the key works
        var rateLimits = await FetchRateLimitHeaders(apiKey, ct);

        return new UsageSnapshot
        {
            SourceLabel = "api",
            AccountIdentity = MaskApiKey(apiKey),
            PlanName = "API Key",
            AuthState = ProviderAuthState.Authenticated,
            SessionQuota = rateLimits.requestsRemaining.HasValue
                ? new Quota
                {
                    Label = $"Requests ({config.model ?? "codex"})",
                    UsedPercent = rateLimits.requestsLimit > 0
                        ? 100.0 * (1.0 - (double)rateLimits.requestsRemaining.Value / rateLimits.requestsLimit)
                        : 0,
                    Reset = rateLimits.resetSeconds > 0
                        ? new ResetWindow
                        {
                            ResetsAt = DateTimeOffset.UtcNow.AddSeconds(rateLimits.resetSeconds),
                            WindowMinutes = (int)(rateLimits.resetSeconds / 60.0),
                        }
                        : null,
                }
                : new Quota { Label = $"Model: {config.model ?? "codex"}", UsedPercent = 0 },
            WeeklyQuota = rateLimits.tokensRemaining.HasValue && rateLimits.tokensLimit > 0
                ? new Quota
                {
                    Label = "Tokens",
                    UsedPercent = 100.0 * (1.0 - (double)rateLimits.tokensRemaining.Value / rateLimits.tokensLimit),
                }
                : null,
            IsPartial = !rateLimits.requestsRemaining.HasValue && !rateLimits.tokensRemaining.HasValue,
        };
    }

    // ─── OAUTH / CHATGPT MODE (CAPPED USAGE) ─────────────────────────

    private async Task<UsageSnapshot> FetchOAuthMode(string accessToken, CodexConfig config, CancellationToken ct)
    {
        Log.Debug("Codex: using OAuth/capped mode");

        try
        {
            // Try WHAM usage first (preferred source for capped plans).
            var wham = await FetchWhamUsage(accessToken, config, ct);
            if (wham.ErrorMessage is null || (wham.SessionQuota is not null || wham.WeeklyQuota is not null))
                return wham;

            // Fallback: billing endpoint
            var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.openai.com/v1/dashboard/billing/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await HttpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                Log.Warning("Codex: OAuth usage API returned {StatusCode}", status);
                return new UsageSnapshot
                {
                    SourceLabel = "oauth",
                    PlanName = $"Capped ({config.model ?? "codex"})",
                    ErrorMessage = $"OAuth usage API returned HTTP {status}",
                    AuthState = status is 401 or 403 ? ProviderAuthState.NeedsLogin : ProviderAuthState.NetworkError,
                    ActionHint = status is 401 or 403 ? "Run codex login again" : "Check network and retry",
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            Log.Debug("Codex: OAuth usage response ({Length} chars)", json.Length);

            return ParseOAuthUsageResponse(json, config);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Codex: OAuth HTTP request failed");
            return new UsageSnapshot
            {
                SourceLabel = "oauth",
                PlanName = $"Capped ({config.model ?? "codex"})",
                ErrorMessage = $"OAuth request failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Check network and retry",
            };
        }
    }

    private async Task<UsageSnapshot> FetchWhamUsage(string accessToken, CodexConfig config, CancellationToken ct)
    {
        try
        {
            // WHAM endpoint used by ChatGPT/Codex for capped plans
            var request = new HttpRequestMessage(HttpMethod.Get,
                "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await HttpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                Log.Warning("Codex: WHAM usage API returned {StatusCode}", status);
                return new UsageSnapshot
                {
                    SourceLabel = "wham",
                    PlanName = $"Capped ({config.model ?? "codex"})",
                    ErrorMessage = $"Usage API returned HTTP {status}",
                    AuthState = status is 401 or 403 ? ProviderAuthState.NeedsLogin : ProviderAuthState.NetworkError,
                    ActionHint = status is 401 or 403 ? "Run codex login again" : "Check network and retry",
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            Log.Debug("Codex: WHAM usage response ({Length} chars)", json.Length);

            return ParseWhamResponse(json, config);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex: WHAM fetch failed");
            return new UsageSnapshot
            {
                SourceLabel = "wham",
                PlanName = $"Capped ({config.model ?? "codex"})",
                ErrorMessage = $"WHAM fetch failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Check network and retry",
            };
        }
    }

    private static UsageSnapshot ParseOAuthUsageResponse(string json, CodexConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Billing endpoint often doesn't expose capped quota windows.
            // Try reading WHAM-like nodes if present, then return partial data.
            Quota? sessionQuota = null;
            Quota? weeklyQuota = null;
            ParseQuotaFromWhamNode(root, "five_hour", "5h Session", 300, ref sessionQuota);
            ParseQuotaFromWhamNode(root, "five_hour_limit", "5h Session", 300, ref sessionQuota);
            ParseQuotaFromWhamNode(root, "seven_day", "Weekly", 10080, ref weeklyQuota);
            ParseQuotaFromWhamNode(root, "seven_day_limit", "Weekly", 10080, ref weeklyQuota);

            return new UsageSnapshot
            {
                SourceLabel = "oauth",
                PlanName = $"Capped ({config.model ?? "codex"})",
                SessionQuota = sessionQuota,
                WeeklyQuota = weeklyQuota,
                AuthState = ProviderAuthState.Authenticated,
                IsPartial = sessionQuota is null && weeklyQuota is null,
                ActionHint = sessionQuota is null && weeklyQuota is null
                    ? "Quota details unavailable from this endpoint"
                    : null,
            };
        }
        catch
        {
            return new UsageSnapshot
            {
                SourceLabel = "oauth",
                ErrorMessage = "Failed to parse usage response",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Retry in a moment",
            };
        }
    }

    private static UsageSnapshot ParseWhamResponse(string json, CodexConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Quota? sessionQuota = null;
            Quota? weeklyQuota = null;

            // Determine plan type from response if available
            var planLabel = config.model ?? "codex";
            if (root.TryGetProperty("plan_type", out var pt))
                planLabel = $"{pt.GetString()} ({planLabel})";

            // Support both old 'five_hour_limit'/'seven_day_limit' schemas and the new 'rate_limit' schema
            ParseQuotaFromWhamNode(root, "five_hour", "5h Session", 300, ref sessionQuota);
            ParseQuotaFromWhamNode(root, "five_hour_limit", "5h Session", 300, ref sessionQuota);
            ParseQuotaFromWhamNode(root, "seven_day", "Weekly", 10080, ref weeklyQuota);
            ParseQuotaFromWhamNode(root, "seven_day_limit", "Weekly", 10080, ref weeklyQuota);

            if (root.TryGetProperty("rate_limit", out var rl) && rl.TryGetProperty("primary_window", out var pw))
            {
                var windowSec = pw.TryGetProperty("limit_window_seconds", out var lws) ? lws.GetInt32() : 0;
                var pct = pw.TryGetProperty("used_percent", out var uPct) ? uPct.GetDouble() : 0;
                DateTimeOffset? resetAt = null;
                if (pw.TryGetProperty("reset_at", out var rAt))
                    resetAt = DateTimeOffset.FromUnixTimeSeconds(rAt.GetInt64());
                else if (pw.TryGetProperty("reset_after_seconds", out var ras))
                    resetAt = DateTimeOffset.UtcNow.AddSeconds(ras.GetDouble());

                var quota = new Quota
                {
                    Label = windowSec == 604800 ? "Weekly" : (windowSec == 18000 ? "5h Session" : "Quota"),
                    UsedPercent = pct,
                    Reset = new ResetWindow { ResetsAt = resetAt, WindowMinutes = windowSec / 60 },
                };

                if (windowSec >= 86400) weeklyQuota ??= quota; else sessionQuota ??= quota;
            }

            return new UsageSnapshot
            {
                SourceLabel = "wham",
                PlanName = planLabel,
                SessionQuota = sessionQuota,
                WeeklyQuota = weeklyQuota,
                AuthState = ProviderAuthState.Authenticated,
                IsPartial = sessionQuota is null || weeklyQuota is null,
                ActionHint = sessionQuota is null && weeklyQuota is null
                    ? "Quota data not found in WHAM response"
                    : null,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex: WHAM parse failed");
            return new UsageSnapshot
            {
                SourceLabel = "wham",
                ErrorMessage = "Failed to parse WHAM response",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Retry in a moment",
            };
        }
    }

    private static void ParseQuotaFromWhamNode(JsonElement root, string nodeName, string label, int windowMinutes, ref Quota? target)
    {
        if (target != null || !root.TryGetProperty(nodeName, out var node)) return;

        var pct = node.TryGetProperty("pct_used", out var pEl) ? pEl.GetDouble() :
                  node.TryGetProperty("percent_used", out pEl) ? pEl.GetDouble() :
                  node.TryGetProperty("used_percent", out pEl) ? pEl.GetDouble() : 0;

        DateTimeOffset? resetAt = null;
        if (node.TryGetProperty("resets_at", out var rEl))
        {
            if (rEl.ValueKind == JsonValueKind.Number)
                resetAt = DateTimeOffset.FromUnixTimeSeconds(rEl.GetInt64());
            else if (DateTimeOffset.TryParse(rEl.GetString(), out var dt))
                resetAt = dt;
        }

        target = new Quota
        {
            Label = label,
            UsedPercent = pct,
            Reset = new ResetWindow { ResetsAt = resetAt, WindowMinutes = windowMinutes },
        };
    }

    // ─── RATE LIMIT HEADERS ────────────────────────────────────────────

    private async Task<RateLimitInfo> FetchRateLimitHeaders(string apiKey, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await HttpClient.SendAsync(request, ct);
            Log.Debug("Codex: API key check returned {StatusCode}", (int)response.StatusCode);

            var info = new RateLimitInfo();

            if (response.Headers.TryGetValues("x-ratelimit-limit-requests", out var limitReq))
                if (int.TryParse(limitReq.FirstOrDefault(), out var lr)) info.requestsLimit = lr;
            if (response.Headers.TryGetValues("x-ratelimit-remaining-requests", out var remReq))
                if (int.TryParse(remReq.FirstOrDefault(), out var rr)) info.requestsRemaining = rr;
            if (response.Headers.TryGetValues("x-ratelimit-limit-tokens", out var limitTok))
                if (int.TryParse(limitTok.FirstOrDefault(), out var lt)) info.tokensLimit = lt;
            if (response.Headers.TryGetValues("x-ratelimit-remaining-tokens", out var remTok))
                if (int.TryParse(remTok.FirstOrDefault(), out var rt)) info.tokensRemaining = rt;
            if (response.Headers.TryGetValues("x-ratelimit-reset-requests", out var resetReq))
                info.resetSeconds = ParseResetDuration(resetReq.FirstOrDefault() ?? "");

            return info;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex: rate limit fetch failed");
            return new RateLimitInfo();
        }
    }

    // ─── HELPERS ────────────────────────────────────────────────────────

    private static double ParseResetDuration(string s)
    {
        double total = 0;
        var current = "";
        foreach (var c in s)
        {
            if (char.IsDigit(c) || c == '.') { current += c; }
            else
            {
                if (double.TryParse(current, out var num))
                    total += c switch { 'h' => num * 3600, 'm' => num * 60, 's' => num, _ => 0 };
                current = "";
            }
        }
        return total;
    }

    private static string GetAuthJsonPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrEmpty(codexHome))
            return Path.Combine(codexHome, "auth.json");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "auth.json");
    }

    private static CodexAuth? ReadAuthJson()
    {
        try
        {
            var path = GetAuthJsonPath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CodexAuth>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex: failed to read auth.json");
            return null;
        }
    }

    private static CodexConfig ReadConfigToml()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "config.toml");
            if (!File.Exists(path)) return new CodexConfig();

            var config = new CodexConfig();
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.StartsWith("model") && !t.StartsWith("model_"))
                    config.model = ExtractTomlValue(t);
                else if (t.StartsWith("model_reasoning_effort"))
                    config.reasoningEffort = ExtractTomlValue(t);
            }
            return config;
        }
        catch { return new CodexConfig(); }
    }

    private static string? ExtractTomlValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return null;
        var v = line[(eq + 1)..].Trim().Trim('"');
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string MaskApiKey(string key)
    {
        if (key.Length <= 12) return "sk-***";
        return $"{key[..8]}...{key[^4..]}";
    }

    // ─── TYPES ──────────────────────────────────────────────────────────

    private struct RateLimitInfo
    {
        public int requestsLimit;
        public int? requestsRemaining;
        public int tokensLimit;
        public int? tokensRemaining;
        public double resetSeconds;
    }

    private sealed class CodexConfig
    {
        public string? model;
        public string? reasoningEffort;
    }

    private sealed class CodexAuth
    {
        [JsonPropertyName("auth_mode")]
        public string? AuthMode { get; set; }

        [JsonPropertyName("OPENAI_API_KEY")]
        public string? OpenAiApiKey { get; set; }

        [JsonPropertyName("tokens")]
        public CodexTokens? Tokens { get; set; }
        
        // Backwards compatibility if it's stored at root
        [JsonPropertyName("access_token")]
        public string? RootAccessToken { get; set; }

        [JsonIgnore]
        public string? AccessToken => Tokens?.AccessToken ?? RootAccessToken;
    }

    private sealed class CodexTokens
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
