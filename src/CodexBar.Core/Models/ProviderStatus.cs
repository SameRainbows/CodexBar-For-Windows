namespace CodexBar.Core.Models;

/// <summary>
/// Represents the current operational status of a provider.
/// </summary>
public enum ProviderStatus
{
    /// <summary>Provider is available and returning data normally.</summary>
    Available,

    /// <summary>Provider is not installed or not configured.</summary>
    Unavailable,

    /// <summary>Provider encountered an error during the last fetch.</summary>
    Error,

    /// <summary>Provider data is older than the expected refresh interval.</summary>
    Stale,

    /// <summary>Provider is currently fetching data.</summary>
    Fetching
}
