namespace CodexBar.Core.Models;

/// <summary>
/// Provider authentication and availability hints for actionable UX.
/// </summary>
public enum ProviderAuthState
{
    Unknown,
    Authenticated,
    NeedsLogin,
    MissingCli,
    MissingCredentials,
    NetworkError,
}
