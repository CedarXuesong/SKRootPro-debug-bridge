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

    public static bool UseDebugOverlay(ModuleInfo? module) =>
        IsCanary && module?.DebugOverlayEnabled != false;
}
