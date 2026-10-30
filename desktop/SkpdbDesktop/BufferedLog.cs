using System.Threading.Channels;

namespace SkpdbDesktop;

/// <summary>Bounded diagnostics: never block the sampling thread on disk I/O.</summary>
internal static class BufferedLog
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private static readonly Task Writer = WriteAsync();

    public static void Write(string message) => Queue.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

    private static async Task WriteAsync()
    {
        while (await Queue.Reader.WaitToReadAsync())
        {
            await Task.Delay(40).ConfigureAwait(false);
            var lines = new List<string>(256);
            while (lines.Count < 256 && Queue.Reader.TryRead(out var line)) lines.Add(line);
            try
            {
                var path = Path.Combine(AppPaths.DataDirectory, "gamepad.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                await File.AppendAllLinesAsync(path, lines).ConfigureAwait(false);
            }
            catch { /* Diagnostics must never stop input. */ }
        }
    }
}
