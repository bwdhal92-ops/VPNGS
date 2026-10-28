using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FlareVpn.Services;

public class AppSettings
{
    public bool TunMode { get; set; } = true;
    public int SettingsVersion { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int RefreshMinutes { get; set; } = 60;
    public string SelectedServerId { get; set; } = "";
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public bool SetSystemProxy { get; set; } = true;
    public string Language { get; set; } = "ar";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool ShowCloudflareServers { get; set; }
    public bool FirebaseTelemetryEnabled { get; set; } = true;
}

public static class SettingsService
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNGS");
    private static readonly string File = Path.Combine(Dir, "settings.json");
    private static readonly string LegacyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlareVpn", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settingsPath = System.IO.File.Exists(File) ? File : LegacyFile;
            if (System.IO.File.Exists(settingsPath))
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.FirebaseTelemetryEnabled = true;
                using var document = JsonDocument.Parse(json);
                var hasVersion = document.RootElement.TryGetProperty(nameof(AppSettings.SettingsVersion), out _);
                if (!hasVersion)
                {
                    // Older builds could turn TUN off automatically after a fallback.
                    // Restore the requested default once, while preserving future user choices.
                    settings.TunMode = true;
                    settings.SettingsVersion = 2;
                }
                if (settingsPath != File || !hasVersion) Save(settings);
                return settings;
            }
        }
        catch { }
        return new AppSettings { SettingsVersion = 2 };
    }

    public static void Save(AppSettings s)
    {
        try
        {
            s.FirebaseTelemetryEnabled = true;
            s.SettingsVersion = 2;
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(File, json);
        }
        catch { }
    }

    public static void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            const string name = "VPNGS";
            if (!enabled) { key.DeleteValue(name, false); return; }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            key.SetValue(name, $"\"{executable}\" --minimized");
        }
        catch { }
    }
}
