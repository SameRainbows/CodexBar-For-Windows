using System.Diagnostics;
using System.Text;

namespace CodexBar.Core.Platform;

/// <summary>
/// Safe wrapper for executing CLI processes with timeout and output capture.
/// All external process calls must go through this class.
/// Platform-agnostic in interface (uses only System.Diagnostics).
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>Default timeout for CLI commands.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Additional PATH directories to search (npm global, cargo, etc.)
    /// These are computed once and cached.
    /// </summary>
    private static readonly Lazy<string> EnrichedPath = new(BuildEnrichedPath);

    /// <summary>
    /// Execute a command and capture its stdout.
    /// Returns a ProcessResult indicating success/failure with captured output.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments = "",
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        string? stdinInput = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        Process? process = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Enrich PATH so we can find npm-installed CLIs (claude, etc.)
            psi.Environment["PATH"] = EnrichedPath.Value;

            if (workingDirectory is not null)
                psi.WorkingDirectory = workingDirectory;

            process = new Process { StartInfo = psi };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdoutBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderrBuilder.AppendLine(e.Data);
            };

            if (!process.Start())
            {
                return ProcessResult.Failure($"Failed to start process: {fileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (stdinInput is not null)
            {
                await process.StandardInput.WriteAsync(stdinInput);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cts.Token);

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdoutBuilder.ToString(),
                Stderr = stderrBuilder.ToString(),
                Success = process.ExitCode == 0,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TryTerminateProcessAsync(process);
            return ProcessResult.Failure($"Process timed out after {effectiveTimeout.TotalSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProcessResult.Failure($"Process error: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Resolve the absolute path of a command in PATH using 'where.exe' (Windows) or 'which' (Unix).
    /// </summary>
    public async Task<string?> ResolveCommandPathAsync(string commandName, CancellationToken ct = default)
    {
        try
        {
            var whichCmd = OperatingSystem.IsWindows() ? "where.exe" : "which";
            var result = await RunAsync(whichCmd, commandName, TimeSpan.FromSeconds(5), ct: ct);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                var lines = result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return lines.FirstOrDefault()?.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Build an enriched PATH string that includes common CLI tool directories.
    /// This ensures we find tools installed via npm, cargo, pip, etc.
    /// </summary>
    private static string BuildEnrichedPath()
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH",
            EnvironmentVariableTarget.Machine) ?? "";
        var userPath = Environment.GetEnvironmentVariable("PATH",
            EnvironmentVariableTarget.User) ?? "";

        var combinedPath = $"{currentPath};{userPath}";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Common CLI tool directories
        var extraDirs = new[]
        {
            Path.Combine(appData, "npm"),           // npm global bin (claude CLI)
            Path.Combine(home, ".cargo", "bin"),     // Rust/cargo binaries
            Path.Combine(appData, "Python", "Scripts"), // pip global
            Path.Combine(home, "go", "bin"),         // Go binaries
        };

        foreach (var dir in extraDirs)
        {
            if (Directory.Exists(dir) && !combinedPath.Contains(dir, StringComparison.OrdinalIgnoreCase))
            {
                combinedPath = $"{combinedPath};{dir}";
            }
        }

        return combinedPath;
    }

    private static async Task TryTerminateProcessAsync(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

/// <summary>
/// Result of a CLI process execution.
/// </summary>
public sealed record ProcessResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProcessResult Failure(string error) => new()
    {
        ExitCode = -1,
        Success = false,
        ErrorMessage = error,
    };
}
