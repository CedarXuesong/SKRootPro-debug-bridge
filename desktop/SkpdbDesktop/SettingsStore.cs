using System.Text.Json;

namespace SkpdbDesktop;

public sealed class DesktopSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 39876;
    public int KnockPort { get; set; } = 39877;
    public int TimeoutSeconds { get; set; } = 10;
    public bool DarkTheme { get; set; } = true;
}

public static class SettingsStore
{
    public static string? LastError { get; private set; }
    private static string Folder => AppPaths.DataDirectory;

    private static string FilePath => Path.Combine(Folder, "desktop.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new DesktopSettings();
                settings.Host ??= "";
                if (settings.Port is < 1 or > 65535) settings.Port = 39876;
                if (settings.KnockPort is < 1 or > 65535) settings.KnockPort = 39877;
                settings.TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 1, 120);
                return settings;
            }
        }
        catch
        {
            // A damaged settings file must not prevent the app from starting.
        }
        return new DesktopSettings();
    }

    public static bool Save(DesktopSettings settings)
    {
        try
        {
            if (settings.Port is < 1 or > 65535 || settings.KnockPort is < 1 or > 65535 || settings.TimeoutSeconds is < 1 or > 120)
                throw new InvalidOperationException("端口或超时设置超出有效范围");
            Directory.CreateDirectory(Folder);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
    }
}
