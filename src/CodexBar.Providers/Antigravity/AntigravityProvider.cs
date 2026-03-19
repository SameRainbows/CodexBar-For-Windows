using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;
using Microsoft.Data.Sqlite;
using Serilog;

namespace CodexBar.Providers.Antigravity;

/// <summary>
/// AntiGravity provider — reads model quota state from AntiGravity's local VS Code-style state DB.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider, IProviderDiagnostics
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AntigravityProvider>();

    private const string AuthStatusKey = "antigravityAuthStatus";
    private const string UnifiedUserStatusKey = "antigravityUnifiedStateSync.userStatus";
    private const string ModelCreditsKey = "antigravityUnifiedStateSync.modelCredits";

    private bool _isEnabled;

    public string Id => "antigravity";
    public string DisplayName => "AntiGravity";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public AntigravityProvider(CodexBar.Core.Platform.ProcessRunner _) { }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var dbPath = GetStateDbPath();
            return Task.FromResult(File.Exists(dbPath));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var dbPath = GetStateDbPath();
            if (!File.Exists(dbPath))
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "AntiGravity local state DB not found",
                    AuthState = ProviderAuthState.MissingCredentials,
                    ActionHint = "Open AntiGravity and sign in",
                    SourceLabel = "state.vscdb",
                };
            }

            var values = await ReadStateEntriesAsync(
                dbPath,
                [AuthStatusKey, UnifiedUserStatusKey, ModelCreditsKey],
                ct);

            if (!values.TryGetValue(AuthStatusKey, out var authJson) || string.IsNullOrWhiteSpace(authJson))
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "AntiGravity auth state not found",
                    AuthState = ProviderAuthState.NeedsLogin,
                    ActionHint = "Open AntiGravity and complete sign-in",
                    SourceLabel = "state.vscdb",
                };
            }

            var authStatus = ParseAuthStatus(authJson);
            if (authStatus is null)
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "Could not parse AntiGravity auth state",
                    AuthState = ProviderAuthState.NeedsLogin,
                    ActionHint = "Re-open AntiGravity and refresh account state",
                    SourceLabel = "state.vscdb",
                };
            }

            var userStatusPayload = authStatus.UserStatusProtoBinaryBase64;
            if (string.IsNullOrWhiteSpace(userStatusPayload)
                && values.TryGetValue(UnifiedUserStatusKey, out var fallbackUserStatus))
            {
                userStatusPayload = ExtractWrappedUserStatusBase64(fallbackUserStatus);
            }

            if (string.IsNullOrWhiteSpace(userStatusPayload))
            {
                return new UsageSnapshot
                {
                    ErrorMessage = "AntiGravity user status payload missing",
                    AuthState = ProviderAuthState.NeedsLogin,
                    ActionHint = "Open AntiGravity Settings > Models and refresh",
                    SourceLabel = "state.vscdb",
                };
            }

            var parsedUserStatus = ParseUserStatusPayload(userStatusPayload);
            var credits = values.TryGetValue(ModelCreditsKey, out var modelCreditsRaw)
                ? ParseModelCreditsPayload(modelCreditsRaw)
                : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            credits.TryGetValue("availableCreditsSentinelKey", out var availableCredits);
            credits.TryGetValue("minimumCreditAmountForUsageKey", out var minCreditAmount);

            var quotas = parsedUserStatus.ModelQuotas;
            var session = BuildSessionQuota(quotas);
            var weekly = BuildWeeklyQuota(quotas);

            var snapshot = new UsageSnapshot
            {
                SourceLabel = "state.vscdb",
                AccountIdentity = MaskIdentity(authStatus.Email ?? authStatus.Name),
                PlanName = parsedUserStatus.PlanLabel,
                CreditsRemaining = availableCredits > 0 ? availableCredits : null,
                HasCredits = availableCredits > 0,
                SessionQuota = session,
                WeeklyQuota = weekly,
                ModelQuotas = quotas,
                AuthState = quotas.Count > 0 ? ProviderAuthState.Authenticated : ProviderAuthState.Unknown,
                IsPartial = quotas.Count == 0 || session is null,
                ActionHint = BuildActionHint(quotas.Count, minCreditAmount),
            };

            if (quotas.Count == 0)
            {
                return snapshot with
                {
                    ErrorMessage = "No AntiGravity model quota rows found",
                    AuthState = ProviderAuthState.Unknown,
                };
            }

            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "AntiGravity: usage fetch failed");
            return new UsageSnapshot
            {
                ErrorMessage = $"Fetch failed: {ex.Message}",
                AuthState = ProviderAuthState.NetworkError,
                ActionHint = "Retry in a moment",
                SourceLabel = "state.vscdb",
            };
        }
    }

    public Task<UsageSnapshot> RefreshAsync(CancellationToken ct = default) =>
        FetchUsageAsync(ct);

    public async Task<ProviderDiagnostics> DiagnoseAsync(CancellationToken ct = default)
    {
        var checks = new List<string>();
        var dbPath = GetStateDbPath();
        checks.Add($"state DB exists: {File.Exists(dbPath)}");
        checks.Add($"state DB path: {dbPath}");

        if (!File.Exists(dbPath))
        {
            return new ProviderDiagnostics
            {
                ProviderId = Id,
                AuthState = ProviderAuthState.MissingCredentials,
                SuggestedAction = "Open AntiGravity and sign in once",
                Checks = checks.ToArray(),
            };
        }

        try
        {
            var values = await ReadStateEntriesAsync(dbPath, [AuthStatusKey, UnifiedUserStatusKey, ModelCreditsKey], ct);
            checks.Add($"auth status key present: {values.ContainsKey(AuthStatusKey)}");
            checks.Add($"unified user status key present: {values.ContainsKey(UnifiedUserStatusKey)}");
            checks.Add($"model credits key present: {values.ContainsKey(ModelCreditsKey)}");

            var snapshot = await FetchUsageAsync(ct);
            checks.Add($"last fetch error: {snapshot.ErrorMessage ?? "none"}");
            checks.Add($"model rows parsed: {snapshot.ModelQuotas?.Count ?? 0}");
            checks.Add($"plan: {snapshot.PlanName ?? "unknown"}");

            return new ProviderDiagnostics
            {
                ProviderId = Id,
                AuthState = snapshot.AuthState,
                SuggestedAction = snapshot.ActionHint,
                Checks = checks.ToArray(),
            };
        }
        catch (Exception ex)
        {
            checks.Add($"diagnostic read failed: {ex.Message}");
            return new ProviderDiagnostics
            {
                ProviderId = Id,
                AuthState = ProviderAuthState.NetworkError,
                SuggestedAction = "Retry diagnostics after AntiGravity fully starts",
                Checks = checks.ToArray(),
            };
        }
    }

    private static async Task<Dictionary<string, string>> ReadStateEntriesAsync(
        string dbPath,
        string[] keys,
        CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        var parameterNames = new List<string>(keys.Length);
        await using var command = connection.CreateCommand();
        for (var i = 0; i < keys.Length; i++)
        {
            var paramName = $"@k{i}";
            parameterNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, keys[i]);
        }

        command.CommandText = $"SELECT key, value FROM ItemTable WHERE key IN ({string.Join(", ", parameterNames)})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            var raw = reader.GetValue(1);
            var value = raw switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string s => s,
                _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty,
            };
            values[key] = value;
        }

        return values;
    }

    private static string GetStateDbPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity", "User", "globalStorage", "state.vscdb");

    private static string BuildActionHint(int modelCount, long minCreditAmount)
    {
        if (modelCount == 0)
            return "Open AntiGravity Settings > Models and click Refresh";

        if (minCreditAmount > 0)
            return $"Minimum credits required for usage: {minCreditAmount}";

        return string.Empty;
    }

    private static Quota? BuildSessionQuota(IReadOnlyList<ModelQuota> modelQuotas)
    {
        if (modelQuotas.Count == 0)
            return null;

        var grouped = modelQuotas
            .Where(m => m.ResetsAt.HasValue)
            .GroupBy(m => m.ResetsAt!.Value.ToUnixTimeSeconds())
            .OrderBy(g => g.Key)
            .ToList();

        if (grouped.Count == 0)
        {
            var min = modelQuotas.Min(m => m.RemainingPercent);
            return new Quota
            {
                Label = "Model Quota",
                UsedPercent = 100.0 - min,
            };
        }

        var first = grouped[0].ToList();
        var firstReset = DateTimeOffset.FromUnixTimeSeconds(grouped[0].Key);
        var minRemaining = first.Min(m => m.RemainingPercent);
        return new Quota
        {
            Label = grouped.Count > 1 ? "Session" : "Model Quota",
            UsedPercent = 100.0 - minRemaining,
            Reset = new ResetWindow
            {
                ResetsAt = firstReset,
                WindowMinutes = Math.Max(1, (int)Math.Round((firstReset - DateTimeOffset.UtcNow).TotalMinutes)),
            },
        };
    }

    private static Quota? BuildWeeklyQuota(IReadOnlyList<ModelQuota> modelQuotas)
    {
        var grouped = modelQuotas
            .Where(m => m.ResetsAt.HasValue)
            .GroupBy(m => m.ResetsAt!.Value.ToUnixTimeSeconds())
            .OrderBy(g => g.Key)
            .ToList();

        if (grouped.Count < 2)
            return null;

        var last = grouped[^1].ToList();
        var reset = DateTimeOffset.FromUnixTimeSeconds(grouped[^1].Key);
        var minRemaining = last.Min(m => m.RemainingPercent);
        return new Quota
        {
            Label = "Weekly",
            UsedPercent = 100.0 - minRemaining,
            Reset = new ResetWindow
            {
                ResetsAt = reset,
                WindowMinutes = Math.Max(1, (int)Math.Round((reset - DateTimeOffset.UtcNow).TotalMinutes)),
            },
        };
    }

    private static string? MaskIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.Contains('@'))
        {
            var at = value.IndexOf('@');
            if (at > 1)
                return $"{value[0]}***{value[at - 1]}{value[at..]}";
        }

        return value.Length <= 3 ? "***" : $"{value[..2]}***";
    }

    private static AuthStatusPayload? ParseAuthStatus(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AuthStatusPayload>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractWrappedUserStatusBase64(string rawBase64)
    {
        if (!TryDecodeBase64(rawBase64, out var bytes))
            return null;

        var current = bytes;
        for (var i = 0; i < 6; i++)
        {
            if (!TryParseMessage(current, out var fields))
                break;

            if (TryGetSingleNestedMessage(fields, out var nested))
            {
                current = nested;
                continue;
            }

            var base64Text = fields
                .Where(f => f.WireType == ProtoWireType.LengthDelimited)
                .Select(f => TryGetUtf8(f.LengthDelimited))
                .FirstOrDefault(IsLikelyBase64);

            if (base64Text is null || !TryDecodeBase64(base64Text, out var decoded))
                break;

            current = decoded;
        }

        if (!TryParseMessage(current, out var finalFields))
            return null;

        var candidate = finalFields
            .Where(f => f.WireType == ProtoWireType.LengthDelimited)
            .Select(f => TryGetUtf8(f.LengthDelimited))
            .FirstOrDefault(IsLikelyBase64);
        return candidate;
    }

    internal static ParsedUserStatus ParseUserStatusPayload(string userStatusBase64)
    {
        if (!TryDecodeBase64(userStatusBase64, out var bytes))
            return new ParsedUserStatus();

        bytes = UnwrapToModelPayload(bytes);
        if (!TryParseMessage(bytes, out var fields))
            return new ParsedUserStatus();

        var result = new ParsedUserStatus
        {
            AccountName = TryGetStringField(fields, 3),
            AccountEmail = TryGetStringField(fields, 7),
        };

        if (TryGetLengthDelimitedField(fields, 33, out var modelsContainer)
            && TryParseMessage(modelsContainer, out var modelContainerFields))
        {
            var modelQuotas = new List<ModelQuota>();
            foreach (var modelField in modelContainerFields.Where(f => f.Number == 1 && f.WireType == ProtoWireType.LengthDelimited))
            {
                if (!TryParseMessage(modelField.LengthDelimited, out var modelFields))
                    continue;

                var modelName = TryGetStringField(modelFields, 1);
                if (string.IsNullOrWhiteSpace(modelName))
                    continue;

                int? modelClass = null;
                if (TryGetNestedMessageField(modelFields, 2, out var classMsg)
                    && TryParseMessage(classMsg, out var classFields)
                    && TryGetVarintField(classFields, 1, out var classValue))
                {
                    modelClass = (int)classValue;
                }

                var badge = TryGetStringField(modelFields, 16);
                var capabilityCount = modelFields.Count(f => f.Number == 18);

                double remainingRatio = 0;
                DateTimeOffset? resetAt = null;
                if (TryGetNestedMessageField(modelFields, 15, out var quotaMsg)
                    && TryParseMessage(quotaMsg, out var quotaFields))
                {
                    if (TryGetFixed32Field(quotaFields, 1, out var ratioRaw))
                        remainingRatio = Math.Clamp(BitConverter.ToSingle(ratioRaw, 0), 0, 1);

                    if (TryGetNestedMessageField(quotaFields, 2, out var resetMsg)
                        && TryParseMessage(resetMsg, out var resetFields)
                        && TryGetVarintField(resetFields, 1, out var resetEpoch))
                    {
                        resetAt = ParseEpochSeconds(resetEpoch);
                    }
                }

                modelQuotas.Add(new ModelQuota
                {
                    ModelName = modelName,
                    RemainingRatio = remainingRatio,
                    ResetsAt = resetAt,
                    Badge = badge,
                    ModelClass = modelClass,
                    CapabilityCount = capabilityCount,
                });
            }

            result.ModelQuotas = modelQuotas;
        }

        if (TryGetLengthDelimitedField(fields, 36, out var planMsg)
            && TryParseMessage(planMsg, out var planFields))
        {
            result.PlanTier = TryGetStringField(planFields, 1);
            result.PlanLabel = TryGetStringField(planFields, 2) ?? TryGetStringField(planFields, 3);
            result.UpgradeUrl = TryGetStringField(planFields, 7);
            result.UpgradeMessage = TryGetStringField(planFields, 8);
        }

        return result;
    }

    internal static Dictionary<string, long> ParseModelCreditsPayload(string encodedPayload)
    {
        var credits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!TryDecodeBase64(encodedPayload, out var bytes))
            return credits;

        if (!TryParseMessage(bytes, out var fields))
            return credits;

        foreach (var entryField in fields.Where(f => f.Number == 1 && f.WireType == ProtoWireType.LengthDelimited))
        {
            if (!TryParseMessage(entryField.LengthDelimited, out var entryFields))
                continue;

            var key = TryGetStringField(entryFields, 1);
            var valueEncoded = TryGetStringField(entryFields, 2);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(valueEncoded))
                continue;

            if (!TryDecodeBase64(valueEncoded, out var valueBytes))
                continue;

            if (!TryParseMessage(valueBytes, out var valueFields))
                continue;

            if (TryGetVarintField(valueFields, 2, out var value))
                credits[key] = (long)value;
        }

        return credits;
    }

    private static DateTimeOffset? ParseEpochSeconds(ulong raw)
    {
        try
        {
            if (raw > 100_000_000_000UL)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)raw);

            if (raw > 1_000_000_000UL)
                return DateTimeOffset.FromUnixTimeSeconds((long)raw);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] UnwrapToModelPayload(byte[] bytes)
    {
        var current = bytes;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!TryParseMessage(current, out var fields))
                break;

            if (fields.Any(f => f.Number == 33 && f.WireType == ProtoWireType.LengthDelimited))
                return current;

            if (TryGetSingleNestedMessage(fields, out var nested))
            {
                current = nested;
                continue;
            }

            var base64Text = fields
                .Where(f => f.WireType == ProtoWireType.LengthDelimited)
                .Select(f => TryGetUtf8(f.LengthDelimited))
                .FirstOrDefault(IsLikelyBase64);

            if (base64Text is null || !TryDecodeBase64(base64Text, out var decoded))
                break;

            current = decoded;
        }

        return current;
    }

    private static bool TryGetSingleNestedMessage(IReadOnlyList<ProtoField> fields, out byte[] nested)
    {
        nested = [];
        if (fields.Count != 1 || fields[0].WireType != ProtoWireType.LengthDelimited)
            return false;

        var candidate = fields[0].LengthDelimited ?? [];
        if (candidate.Length > 0 && TryParseMessage(candidate, out _))
        {
            nested = candidate;
            return true;
        }

        if (TryDecodeBase64(TryGetUtf8(candidate), out var decoded) && TryParseMessage(decoded, out _))
        {
            nested = decoded;
            return true;
        }

        return false;
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!IsLikelyBase64(value))
            return false;

        try
        {
            if (value.Length % 4 != 0)
                value = value.PadRight(value.Length + (4 - value.Length % 4), '=');

            bytes = Convert.FromBase64String(value);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (value.Length < 4)
            return false;

        return value.All(ch =>
            (ch >= 'A' && ch <= 'Z')
            || (ch >= 'a' && ch <= 'z')
            || (ch >= '0' && ch <= '9')
            || ch is '+' or '/' or '=');
    }

    private static string? TryGetStringField(IReadOnlyList<ProtoField> fields, int number)
    {
        var field = fields.FirstOrDefault(f => f.Number == number && f.WireType == ProtoWireType.LengthDelimited);
        return field?.LengthDelimited is null ? null : TryGetUtf8(field.LengthDelimited);
    }

    private static string? TryGetUtf8(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetLengthDelimitedField(IReadOnlyList<ProtoField> fields, int number, out byte[] bytes)
    {
        var field = fields.FirstOrDefault(f => f.Number == number && f.WireType == ProtoWireType.LengthDelimited);
        bytes = field?.LengthDelimited ?? [];
        return bytes.Length > 0;
    }

    private static bool TryGetNestedMessageField(IReadOnlyList<ProtoField> fields, int number, out byte[] bytes)
    {
        if (!TryGetLengthDelimitedField(fields, number, out var candidate))
        {
            bytes = [];
            return false;
        }

        if (!TryParseMessage(candidate, out _))
        {
            bytes = [];
            return false;
        }

        bytes = candidate;
        return true;
    }

    private static bool TryGetVarintField(IReadOnlyList<ProtoField> fields, int number, out ulong value)
    {
        var field = fields.FirstOrDefault(f => f.Number == number && f.WireType == ProtoWireType.Varint);
        if (field is null)
        {
            value = 0;
            return false;
        }

        value = field.Varint;
        return true;
    }

    private static bool TryGetFixed32Field(IReadOnlyList<ProtoField> fields, int number, out byte[] bytes)
    {
        var field = fields.FirstOrDefault(f => f.Number == number && f.WireType == ProtoWireType.Fixed32);
        bytes = field?.Fixed32 ?? [];
        return bytes.Length == 4;
    }

    private static bool TryParseMessage(ReadOnlySpan<byte> bytes, out List<ProtoField> fields)
    {
        fields = [];
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref offset, out var key))
                return false;

            var number = (int)(key >> 3);
            var wireType = (ProtoWireType)(key & 0x07);
            if (number <= 0)
                return false;

            switch (wireType)
            {
                case ProtoWireType.Varint:
                {
                    if (!TryReadVarint(bytes, ref offset, out var value))
                        return false;

                    fields.Add(new ProtoField(number, wireType) { Varint = value });
                    break;
                }
                case ProtoWireType.Fixed64:
                {
                    if (offset + 8 > bytes.Length)
                        return false;

                    var data = bytes[offset..(offset + 8)].ToArray();
                    offset += 8;
                    fields.Add(new ProtoField(number, wireType) { Fixed64 = data });
                    break;
                }
                case ProtoWireType.LengthDelimited:
                {
                    if (!TryReadVarint(bytes, ref offset, out var length))
                        return false;

                    if (length > int.MaxValue || offset + (int)length > bytes.Length)
                        return false;

                    var data = bytes[offset..(offset + (int)length)].ToArray();
                    offset += (int)length;
                    fields.Add(new ProtoField(number, wireType) { LengthDelimited = data });
                    break;
                }
                case ProtoWireType.Fixed32:
                {
                    if (offset + 4 > bytes.Length)
                        return false;

                    var data = bytes[offset..(offset + 4)].ToArray();
                    offset += 4;
                    fields.Add(new ProtoField(number, wireType) { Fixed32 = data });
                    break;
                }
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < bytes.Length && shift <= 63)
        {
            var b = bytes[offset++];
            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }

        return false;
    }

    private enum ProtoWireType : int
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5,
    }

    private sealed record ProtoField(int Number, ProtoWireType WireType)
    {
        public ulong Varint { get; init; }
        public byte[]? Fixed64 { get; init; }
        public byte[]? LengthDelimited { get; init; }
        public byte[]? Fixed32 { get; init; }
    }

    private sealed class AuthStatusPayload
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("userStatusProtoBinaryBase64")]
        public string? UserStatusProtoBinaryBase64 { get; init; }
    }
}

internal sealed record ParsedUserStatus
{
    public string? AccountName { get; init; }
    public string? AccountEmail { get; init; }
    public string? PlanTier { get; set; }
    public string? PlanLabel { get; set; }
    public string? UpgradeUrl { get; set; }
    public string? UpgradeMessage { get; set; }
    public IReadOnlyList<ModelQuota> ModelQuotas { get; set; } = [];
}
