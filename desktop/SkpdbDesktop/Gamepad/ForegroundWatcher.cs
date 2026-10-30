using System.Text.RegularExpressions;

namespace SkpdbDesktop;

public sealed class ForegroundChangedEventArgs(string? package) : EventArgs
{
    public string? Package { get; } = package;
}

public sealed class ForegroundWatcher : IAsyncDisposable
{
    private static readonly Regex ActivityPackage = new(
        @"(?:mResumedActivity|mFocusedApp|topResumedActivity|mCurrentFocus)[^\n]*?\s(?<package>[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+)/",
        RegexOptions.Compiled);

    private readonly SkpdbClient _controlClient;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _package;
    private string? _candidate;
    private DateTime _candidateSince;
    private readonly object _gate = new();

    public ForegroundWatcher(SkpdbClient client)
    {
        _controlClient = client;
    }

    public string? Package
    {
        get { lock (_gate) return _package; }
    }

    public event EventHandler<ForegroundChangedEventArgs>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _candidate = null;
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        cts?.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        // Canceling a framed reply invalidates its socket. This watcher owns a
        // separate authenticated channel so leaving the page cannot cancel the
        // main session or interrupt reliable touch transitions.
        SkpdbClient? client = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    query.CancelAfter(TimeSpan.FromSeconds(4));
                    client ??= await _controlClient.OpenIndependentChannelAsync(query.Token).ConfigureAwait(false);
                    var result = await client.ExecAsync(
                        "dumpsys activity activities 2>/dev/null | grep -m1 -E 'mResumedActivity|topResumedActivity'",
                        query.Token).ConfigureAwait(false);
                    var package = ParsePackage(result.Output);
                    if (package is null)
                    {
                        var window = await client.ExecAsync(
                            "dumpsys window 2>/dev/null | grep -m1 mCurrentFocus",
                            query.Token).ConfigureAwait(false);
                        package = ParsePackage(window.Output);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Observe(package);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 前台检测失败下一轮重试，不影响外设主循环。
                    client?.Dispose();
                    client = null;
                }
            }
        }
        finally { client?.Dispose(); }
    }

    private void Observe(string? package)
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        lock (_gate)
        {
            if (package == _candidate)
            {
                if (package != _package &&
                    (now - _candidateSince).TotalMilliseconds >= 500)
                {
                    _package = package;
                    changed = true;
                }
            }
            else
            {
                _candidate = package;
                _candidateSince = now;
            }
        }
        if (changed) Changed?.Invoke(this, new ForegroundChangedEventArgs(package));
    }

    private static string? ParsePackage(string text)
    {
        var match = ActivityPackage.Match(text);
        return match.Success ? match.Groups["package"].Value : null;
    }
}
