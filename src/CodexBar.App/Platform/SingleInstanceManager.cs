using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Serilog;

namespace CodexBar.App.Platform;

/// <summary>
/// Coordinates a single running instance and lightweight local commands
/// (e.g. toggle popup, show popup, exit).
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SingleInstanceManager>();

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public SingleInstanceManager(string appId)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";
        var safeSid = sid.Replace("\\", "_").Replace("/", "_");
        _mutexName = $@"Local\{appId}.{safeSid}.mutex";
        _pipeName = $"{appId}.{safeSid}.pipe";
    }

    public bool TryBecomePrimaryInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Single-instance mutex init failed");
            return true; // fail open
        }
    }

    public void StartListening(Action<ExternalAppCommand> onCommand)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _listenerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var raw = await reader.ReadLineAsync(ct);
                    var command = ParseCommand(raw);
                    onCommand(command);
                }
                catch (OperationCanceledException)
                {
                    // expected during shutdown
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Single-instance listener error");
                    await Task.Delay(200, ct);
                }
            }
        }, ct);
    }

    public bool SendCommandToPrimary(ExternalAppCommand command, int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.None);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(command.ToWireValue());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send command to primary instance");
            return false;
        }
    }

    private static ExternalAppCommand ParseCommand(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "exit" => ExternalAppCommand.Exit,
            "show" => ExternalAppCommand.Show,
            _ => ExternalAppCommand.Toggle,
        };

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // best effort
        }
        finally
        {
            _cts?.Dispose();
            _mutex?.Dispose();
        }
    }
}

public enum ExternalAppCommand
{
    Toggle,
    Show,
    Exit,
}

public static class ExternalAppCommandExtensions
{
    public static string ToWireValue(this ExternalAppCommand command) => command switch
    {
        ExternalAppCommand.Exit => "exit",
        ExternalAppCommand.Show => "show",
        _ => "toggle",
    };
}
