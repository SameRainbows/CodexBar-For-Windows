using CodexBar.Core.Providers;
using Serilog;

namespace CodexBar.App.Services;

/// <summary>
/// Registry of all available usage providers.
/// Providers are registered at startup and managed centrally.
/// </summary>
public sealed class ProviderRegistry
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ProviderRegistry>();
    private readonly Dictionary<string, IUsageProvider> _providers = new();
    private readonly ConfigurationService _config;

    public ProviderRegistry(ConfigurationService config)
    {
        _config = config;
    }

    /// <summary>Register a provider. Applies saved enabled state from config.</summary>
    public void Register(IUsageProvider provider)
    {
        provider.IsEnabled = _config.Config.IsProviderEnabled(provider.Id);
        _providers[provider.Id] = provider;
        Log.Information("Registered provider: {Id} ({Name}), enabled: {Enabled}",
            provider.Id, provider.DisplayName, provider.IsEnabled);
    }

    /// <summary>Get all registered providers.</summary>
    public List<IUsageProvider> GetAll() => _providers.Values.ToList();

    /// <summary>Get only enabled providers.</summary>
    public List<IUsageProvider> GetEnabled() => _providers.Values.Where(p => p.IsEnabled).ToList();

    /// <summary>Get a provider by ID.</summary>
    public IUsageProvider? Get(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider : null;

    /// <summary>Toggle a provider's enabled state and persist to config.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        if (_providers.TryGetValue(id, out var provider))
        {
            provider.IsEnabled = enabled;
            _config.Update(c => c.SetProviderEnabled(id, enabled));
            Log.Information("Provider {Id} enabled: {Enabled}", id, enabled);
        }
    }
}
