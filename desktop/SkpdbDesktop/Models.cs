using System.Collections.ObjectModel;

namespace SkpdbDesktop;

public sealed record ConnectionOptions(
    string Host,
    int Port = 39876,
    int KnockPort = 39877,
    string Key = "",
    int TimeoutSeconds = 10);

public sealed record ModuleInfo(
    string Name,
    string Version,
    string Id32,
    int Pid,
    long UptimeSeconds,
    int ServicePort,
    int KnockPort,
    int Sessions,
    int Peers,
    int SessionsTotal,
    string MemoryBackends,
    bool Listening,
    int FastPort = 0,
    string FastToken = "",
    int FastProtocol = 1,
    long FastStale = 0,
    string AdminSocket = "",
    int FastMaxRecords = 8,
    long FastReceived = 0,
    long FastApplied = 0,
    long FastDropped = 0,
    bool FastRealtime = false,
    string BuildChannel = "",
    bool DebugOverlayEnabled = true,
    bool GestureLeaseSupported = false);

public sealed record ExecResult(int ExitCode, string Output);

public sealed record FileEntry(char Kind, string Name)
{
    public string Glyph => Kind == 'd' ? "\uE8B7" : Kind == 'l' ? "\uE8A5" : "\uE8A5";
    public string TypeLabel => Kind == 'd' ? "文件夹" : Kind == 'l' ? "链接" : "文件";
    public bool IsDirectory => Kind == 'd';
}

public sealed record TouchReport(string Text);

public sealed record ActivityItem(string Time, string Text, bool IsError)
{
    public string BrushKey => IsError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
}

public sealed class AppSession
{
    public static AppSession Current { get; } = new();

    public SkpdbClient Client { get; } = new();
    public ConnectionOptions? Options { get; set; }
    public ModuleInfo? Info { get; set; }
    public bool IsConnected => Client.IsConnected;
    public ObservableCollection<ActivityItem> Activity { get; } = new();

    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void AddActivity(string text, bool error = false)
    {
        Activity.Insert(0, new ActivityItem(DateTime.Now.ToString("HH:mm:ss"), text, error));
        while (Activity.Count > 8)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
        NotifyChanged();
    }
}
