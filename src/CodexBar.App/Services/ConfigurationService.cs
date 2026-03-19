using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace CodexBar.App.Services;

/// <summary>
/// Manages application configuration persisted as JSON.
/// Config file location: %APPDATA%\CodexBar\config.json
/// </summary>
public sealed class ConfigurationService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConfigurationService>();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configPath;
    private AppConfig _config;
    public bool IsFirstRun { get; }

    public AppConfig Config => _config;

    public ConfigurationService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexBar");

        Directory.CreateDirectory(appDataDir);
        _configPath = Path.Combine(appDataDir, "config.json");
        IsFirstRun = !File.Exists(_configPath);
        _config = Load();
    }

    /// <summary>Load config from disk. Returns defaults if file doesn't exist or is corrupted.</summary>
    private AppConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                Log.Information("No config file found, using defaults");
                return new AppConfig();
            }

            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            Log.Information("Loaded config from {Path}", _configPath);
            return config ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load config, using defaults");
            return new AppConfig();
        }
    }

    /// <summary>Save current config to disk.</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, SerializerOptions);
            File.WriteAllText(_configPath, json);
            Log.Debug("Config saved to {Path}", _configPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save config");
        }
    }

    /// <summary>Update config and save.</summary>
    public void Update(Action<AppConfig> modifier)
    {
        modifier(_config);
        Save();
    }
}

/// <summary>
/// Application configuration model.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Polling interval in seconds. Default: 300 (5 minutes).</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Per-provider enabled state. Key is provider ID.</summary>
    public Dictionary<string, bool> EnabledProviders { get; set; } = new()
    {
        ["claude"] = true,
        ["codex"] = false,
        ["gemini"] = false,
        ["antigravity"] = false,
        ["copilot"] = false,
    };

    /// <summary>Whether to start with Windows (startup shortcut).</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Whether to show notifications on quota exhaustion.</summary>
    public bool NotifyOnExhaustion { get; set; } = true;

    /// <summary>Check if a provider is enabled.</summary>
    public bool IsProviderEnabled(string providerId)
    {
        return EnabledProviders.TryGetValue(providerId, out var enabled) && enabled;
    }

    /// <summary>Whether the config already has an explicit entry for this provider.</summary>
    public bool HasProviderSetting(string providerId)
    {
        return EnabledProviders.ContainsKey(providerId);
    }

    /// <summary>Set provider enabled state.</summary>
    public void SetProviderEnabled(string providerId, bool enabled)
    {
        EnabledProviders[providerId] = enabled;
    }
}
