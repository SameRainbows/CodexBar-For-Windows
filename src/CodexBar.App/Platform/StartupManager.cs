using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace CodexBar.App.Platform;

/// <summary>
/// Manages startup registration through a Startup-folder shortcut.
/// </summary>
public sealed class StartupManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<StartupManager>();
    private const string ShortcutName = "CodexBar.lnk";

    public bool ApplyStartWithWindows(bool enabled)
    {
        try
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var shortcutPath = Path.Combine(startupFolder, ShortcutName);

            if (!enabled)
            {
                if (File.Exists(shortcutPath))
                    File.Delete(shortcutPath);
                Log.Information("Start with Windows disabled");
                return true;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Log.Warning("Could not determine executable path for startup shortcut");
                return false;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Log.Warning("WScript.Shell COM type not available");
                return false;
            }

            object? shellObj = null;
            object? shortcutObj = null;
            try
            {
                shellObj = Activator.CreateInstance(shellType);
                if (shellObj is null) return false;

                dynamic shell = shellObj;
                shortcutObj = shell.CreateShortcut(shortcutPath);
                dynamic shortcut = shortcutObj;
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
                shortcut.IconLocation = $"{exePath},0";
                shortcut.Description = "CodexBar";
                shortcut.Save();
            }
            finally
            {
                if (shortcutObj is not null && Marshal.IsComObject(shortcutObj))
                    Marshal.FinalReleaseComObject(shortcutObj);
                if (shellObj is not null && Marshal.IsComObject(shellObj))
                    Marshal.FinalReleaseComObject(shellObj);
            }

            Log.Information("Start with Windows enabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update startup shortcut");
            return false;
        }
    }

    public bool IsEnabled()
    {
        try
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return File.Exists(Path.Combine(startupFolder, ShortcutName));
        }
        catch
        {
            return false;
        }
    }
}
