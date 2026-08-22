using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget.Services;

public class StartupService
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SpotifyTaskbarWidget";

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
