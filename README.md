# CodexBar for Windows 🎚️

A Windows system tray application that tracks your AI coding tool usage — Claude, Codex, Gemini, AntiGravity, and more — without logging into web dashboards.

**Windows equivalent of [CodexBar](https://github.com/steipete/CodexBar) for macOS.**

---

## Features

- **System tray icon** with two-bar usage meter
- **Dark-themed popup** showing per-provider usage with session & weekly bars
- **Periodic polling** with configurable intervals (1m, 2m, 5m, 15m)
- **Provider toggles** — enable/disable any provider instantly from Settings
- **CLI-based providers** — detects installed AI CLI tools automatically
- **Sleep/wake resilient** — polling recovers gracefully after hibernation
- **No admin privileges** required

## Supported Providers

| Provider | Source | Status |
|----------|--------|--------|
| **Claude** | `claude` CLI | ✅ Implemented |
| **Codex** | `codex` CLI | ✅ Implemented |
| **Gemini** | `gemini` CLI | ✅ Implemented |
| **AntiGravity** | Local state DB (`state.vscdb`) | ✅ Implemented |

## Requirements

- Windows 10+
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK for development)
- At least one supported usage source available (`claude`, `codex`, `gemini`, or AntiGravity)

## Quick Start

```bash
# Build
dotnet build CodexBar.sln

# Run
dotnet run --project src/CodexBar.App/CodexBar.App.csproj
```

The app starts with **no visible window** — look for the 🎚️ icon in your system tray notification area.

- **Left-click** the tray icon → usage popup
- **Right-click** → context menu (Refresh, Settings, Exit)

### Easier Start / Close (Windows)

Use the helper scripts from the repo root:

```bat
start-codexbar.cmd
close-codexbar.cmd
```

You can also control an existing instance directly:

```bat
src\CodexBar.App\bin\Debug\net8.0-windows\CodexBar.App.exe --show
src\CodexBar.App\bin\Debug\net8.0-windows\CodexBar.App.exe --exit
```

Behavior:
- Launching again does **not** create duplicates (single-instance mode).
- `--show` opens the popup on the running instance.
- `--exit` cleanly shuts down the running instance.

## Architecture

```
CodexBar.Core          (platform-agnostic domain models + interface)
  ├── Models/          UsageRecord, Quota, ResetWindow, UsageSnapshot
  ├── Providers/       IUsageProvider interface
  ├── Parsing/         CliOutputParser (ANSI stripping, % extraction)
  └── Platform/        ProcessRunner (CLI execution with timeout)

CodexBar.Providers     (independent provider implementations)
  ├── Claude/          ClaudeCliProvider
  ├── Codex/           CodexCliProvider
  └── Gemini/          GeminiCliProvider

CodexBar.App           (WPF tray application, Windows-specific)
  ├── Platform/        PollingScheduler, SecureStorage
  ├── Services/        ConfigurationService, ProviderRegistry
  ├── ViewModels/      MainViewModel, ProviderViewModel
  └── Views/           UsagePopupWindow, SettingsWindow
```

**Key design rule:** `CodexBar.Core` has **zero Windows API references**. All platform-specific code lives in `CodexBar.App`.

## Adding a New Provider

1. Create a new folder in `src/CodexBar.Providers/YourProvider/`
2. Implement `IUsageProvider`:

```csharp
public class YourCliProvider : IUsageProvider
{
    public string Id => "yourprovider";
    public string DisplayName => "Your Provider";
    public bool IsEnabled { get; set; }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        return await _processRunner.ExistsInPathAsync("your-cli", ct);
    }

    public async Task<UsageSnapshot> FetchUsageAsync(CancellationToken ct)
    {
        var result = await _processRunner.RunAsync("your-cli", "usage", ct: ct);
        // Parse result.Stdout into UsageSnapshot
        return new UsageSnapshot { /* ... */ };
    }

    public Task<UsageSnapshot> RefreshAsync(CancellationToken ct)
        => FetchUsageAsync(ct);
}
```

3. Add default enabled state in `ConfigurationService.AppConfig.EnabledProviders`
4. The provider is auto-discovered via assembly reflection at startup

## Security Model

| Concern | Implementation |
|---------|---------------|
| Token storage | Windows Credential Manager (DPAPI-encrypted) |
| Logging | Serilog with rolling files; **never logs raw tokens** |
| Browser cookies | Phase 2 (not yet implemented); read-only, opt-in |
| Provider isolation | Each provider runs independently; failures are isolated |
| Admin privileges | **Not required** for any functionality |

## Configuration

Config file: `%APPDATA%\CodexBar\config.json`

```json
{
  "pollIntervalSeconds": 300,
  "enabledProviders": {
    "claude": true,
    "codex": false,
    "gemini": false,
    "antigravity": false
  },
  "startWithWindows": false,
  "notifyOnExhaustion": true
}
```

Logs: `%APPDATA%\CodexBar\logs\` (7-day rolling retention)

## Known Limitations

- **Mixed data sources** — some providers use CLI, AntiGravity uses local state DB; direct cloud API parity is still incremental
- **No Codex RPC** — Codex uses interactive `/status` fallback, not JSON-RPC
- **No auto-update** — manual updates only for now
- **Tooltip truncation** — Windows limits tray tooltip text to 127 characters

## License

MIT
