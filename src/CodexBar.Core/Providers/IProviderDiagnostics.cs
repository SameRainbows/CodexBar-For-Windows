using CodexBar.Core.Models;

namespace CodexBar.Core.Providers;

/// <summary>
/// Optional diagnostics contract for providers.
/// </summary>
public interface IProviderDiagnostics
{
    Task<ProviderDiagnostics> DiagnoseAsync(CancellationToken ct = default);
}

/// <summary>
/// Provider diagnostics payload for support and troubleshooting.
/// </summary>
public sealed record ProviderDiagnostics
{
    public required string ProviderId { get; init; }
    public ProviderAuthState AuthState { get; init; }
    public string? SuggestedAction { get; init; }
    public string[] Checks { get; init; } = Array.Empty<string>();
}
