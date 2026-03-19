using System.Reflection;
using System.Text;
using CodexBar.Core.Models;
using CodexBar.Providers.Antigravity;

namespace CodexBar.Tests;

public sealed class AntigravityProviderParsingTests
{
    [Fact]
    public void ParseUserStatusPayload_ParsesModelRowsAndPlan()
    {
        var model1 = BuildMessage(
            StringField(1, "Gemini 3.1 Pro (High)"),
            BytesField(2, BuildMessage(VarintField(1, 1037))),
            BytesField(15, BuildMessage(
                Fixed32Field(1, 1.0f),
                BytesField(2, BuildMessage(VarintField(1, 1_773_948_389))))),
            StringField(16, "New"),
            BytesField(18, BuildMessage(StringField(1, "application/json"), VarintField(2, 1))),
            BytesField(18, BuildMessage(StringField(1, "text/plain"), VarintField(2, 1))));

        var model2 = BuildMessage(
            StringField(1, "Claude Sonnet 4.6 (Thinking)"),
            BytesField(2, BuildMessage(VarintField(1, 1035))),
            BytesField(15, BuildMessage(
                Fixed32Field(1, 0.2f),
                BytesField(2, BuildMessage(VarintField(1, 1_774_189_200))))));

        var modelsContainer = BuildMessage(
            BytesField(1, model1),
            BytesField(1, model2));

        var plan = BuildMessage(
            StringField(1, "g1-pro-tier"),
            StringField(2, "Google AI Pro"),
            StringField(7, "https://antigravity.google/g1-upgrade"));

        var payload = BuildMessage(
            StringField(3, "Avera"),
            StringField(7, "avvera@example.com"),
            BytesField(33, modelsContainer),
            BytesField(36, plan));

        var base64 = Convert.ToBase64String(payload);
        var parsed = InvokeParseUserStatusPayload(base64);

        Assert.Equal("Google AI Pro", GetStringProperty(parsed, "PlanLabel"));

        var modelQuotas = GetModelQuotas(parsed).ToList();
        Assert.Equal(2, modelQuotas.Count);
        Assert.Equal("Gemini 3.1 Pro (High)", modelQuotas[0].ModelName);
        Assert.Equal(100, modelQuotas[0].RemainingPercent, 2);
        Assert.Equal("New", modelQuotas[0].Badge);
        Assert.Equal(2, modelQuotas[0].CapabilityCount);
        Assert.Equal(1_773_948_389, modelQuotas[0].ResetsAt!.Value.ToUnixTimeSeconds());

        Assert.Equal("Claude Sonnet 4.6 (Thinking)", modelQuotas[1].ModelName);
        Assert.Equal(20, modelQuotas[1].RemainingPercent, 2);
        Assert.Equal(1_774_189_200, modelQuotas[1].ResetsAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParseModelCreditsPayload_ParsesCreditEntries()
    {
        var availableValue = Convert.ToBase64String(BuildMessage(VarintField(2, 1000)));
        var minimumValue = Convert.ToBase64String(BuildMessage(VarintField(2, 50)));

        var entry1 = BuildMessage(
            StringField(1, "availableCreditsSentinelKey"),
            StringField(2, availableValue));
        var entry2 = BuildMessage(
            StringField(1, "minimumCreditAmountForUsageKey"),
            StringField(2, minimumValue));

        var top = BuildMessage(
            BytesField(1, entry1),
            BytesField(1, entry2));

        var encoded = Convert.ToBase64String(top);
        var parsed = InvokeParseModelCreditsPayload(encoded);

        Assert.Equal(1000, parsed["availableCreditsSentinelKey"]);
        Assert.Equal(50, parsed["minimumCreditAmountForUsageKey"]);
    }

    private static object InvokeParseUserStatusPayload(string payloadBase64)
    {
        var method = typeof(AntigravityProvider).GetMethod(
            "ParseUserStatusPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var parsed = method!.Invoke(null, [payloadBase64]);
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static Dictionary<string, long> InvokeParseModelCreditsPayload(string encoded)
    {
        var method = typeof(AntigravityProvider).GetMethod(
            "ParseModelCreditsPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [encoded]);
        Assert.NotNull(result);
        return Assert.IsType<Dictionary<string, long>>(result);
    }

    private static IEnumerable<ModelQuota> GetModelQuotas(object parsed)
    {
        var prop = parsed.GetType().GetProperty("ModelQuotas");
        Assert.NotNull(prop);
        var value = prop!.GetValue(parsed);
        Assert.NotNull(value);
        return Assert.IsAssignableFrom<IEnumerable<ModelQuota>>(value);
    }

    private static string? GetStringProperty(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        return prop!.GetValue(obj) as string;
    }

    private static byte[] BuildMessage(params byte[][] fields) =>
        fields.SelectMany(x => x).ToArray();

    private static byte[] VarintField(int number, ulong value)
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeVarint(((ulong)number << 3) | 0u));
        bytes.AddRange(EncodeVarint(value));
        return bytes.ToArray();
    }

    private static byte[] Fixed32Field(int number, float value)
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeVarint(((ulong)number << 3) | 5u));
        bytes.AddRange(BitConverter.GetBytes(value));
        return bytes.ToArray();
    }

    private static byte[] StringField(int number, string value) =>
        BytesField(number, Encoding.UTF8.GetBytes(value));

    private static byte[] BytesField(int number, byte[] payload)
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeVarint(((ulong)number << 3) | 2u));
        bytes.AddRange(EncodeVarint((ulong)payload.Length));
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] EncodeVarint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                current |= 0x80;
            bytes.Add(current);
        } while (value != 0);

        return bytes.ToArray();
    }
}
