using System.Reflection;

namespace SkpdbDesktop;

public static class BuildInfo
{
#if SKPDB_CANARY
    public static bool IsCanary => true;
    public const string Channel = "Canary";
#else
    public static bool IsCanary => false;
    public const string Channel = "Release";
#endif

    private static string InformationalVersion =>
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Build version metadata is missing");

    public static string BaseVersion => InformationalVersion.Split('+')[0];
    public static string Commit => typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GitCommit")?.Value
        ?? InformationalVersion.Split('+').ElementAtOrDefault(1)
        ?? throw new InvalidOperationException("Build commit metadata is missing");
    public static string ShortCommit => Commit[..Math.Min(12, Commit.Length)];
    public static string DisplayVersion => $"{BaseVersion}+{ShortCommit}";

    public static bool UseDebugOverlay(ModuleInfo? module) =>
        IsCanary && module?.DebugOverlayEnabled != false;
}
