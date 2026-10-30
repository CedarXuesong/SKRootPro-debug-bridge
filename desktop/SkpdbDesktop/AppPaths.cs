namespace SkpdbDesktop;

/// <summary>
/// Resolves a writable per-user data directory. Some hardened desktop
/// environments deny access to LocalApplicationData for unpackaged WinUI
/// processes; the app must keep working with a directory beside the executable
/// or in the temporary directory instead of losing settings and profiles.
/// </summary>
public static class AppPaths
{
    private static readonly object Gate = new();
    private static string? _dataDirectory;

    public static string? LastError { get; private set; }

    public static string DataDirectory
    {
        get
        {
            lock (Gate)
            {
                return _dataDirectory ?? Initialize();
            }
        }
    }

    private static string Initialize()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(Path.Combine(local, "SKPDB"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "SKPDB"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _dataDirectory = candidate;
                return candidate;
            }
            catch (Exception error)
            {
                LastError = candidate + ": " + error.Message;
            }
        }

        _dataDirectory = Path.GetTempPath();
        return _dataDirectory;
    }
}
