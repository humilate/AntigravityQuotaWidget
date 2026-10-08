using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using AntigravityQuotaWidget.Models;

namespace AntigravityQuotaWidget.Services;

public class SettingsService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AntigravityQuotaWidget"
    );

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "AntigravityQuotaWidget";

    public WidgetSettings CurrentSettings { get; private set; } = new();

    public SettingsService()
    {
        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var settings = JsonSerializer.Deserialize<WidgetSettings>(json);
                if (settings != null)
                {
                    CurrentSettings = settings;
                }
            }
        }
        catch
        {
            CurrentSettings = new WidgetSettings();
        }

        // Sync startup state from registry
        CurrentSettings.StartWithWindows = CheckStartupInRegistry();
        CurrentSettings.LaunchWithAntigravity = AntigravityBindingService.IsBindingActive();
    }

    public void SaveSettings(WidgetSettings newSettings)
    {
        CurrentSettings = newSettings;
        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                Directory.CreateDirectory(ConfigDir);
            }

            var json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);

            SetStartupInRegistry(CurrentSettings.StartWithWindows);
            AntigravityBindingService.ApplyBinding(CurrentSettings.LaunchWithAntigravity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    public void UpdatePosition(double x, double y)
    {
        CurrentSettings.WindowX = x;
        CurrentSettings.WindowY = y;
        SaveSettings(CurrentSettings);
    }

    private static bool CheckStartupInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupInRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to update registry startup: {ex.Message}");
        }
    }
}
