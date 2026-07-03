using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget.Services;

/// <summary>
/// Manages auto-start behavior via the Windows registry.
/// Adds/removes an entry in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public class StartupService
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SpotifyTaskbarWidget";

    /// <summary>
    /// Returns true if auto-start is currently enabled.
    /// </summary>
    public bool IsAutoStartEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Enables auto-start by writing the current executable path to the registry.
    /// </summary>
    public void EnableAutoStart()
    {
        try
        {
            var exePath = GetExecutablePath();
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            key?.SetValue(AppName, $"\"{exePath}\"");
            Debug.WriteLine($"[StartupService] Auto-start enabled: {exePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupService] Failed to enable auto-start: {ex.Message}");
        }
    }

    /// <summary>
    /// Disables auto-start by removing the registry entry.
    /// </summary>
    public void DisableAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
            key?.DeleteValue(AppName, false);
            Debug.WriteLine("[StartupService] Auto-start disabled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupService] Failed to disable auto-start: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggles auto-start.
    /// </summary>
    public void ToggleAutoStart()
    {
        if (IsAutoStartEnabled)
            DisableAutoStart();
        else
            EnableAutoStart();
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
               ?? Path.Combine(AppContext.BaseDirectory, "SpotifyTaskbarWidget.exe");
    }
}
