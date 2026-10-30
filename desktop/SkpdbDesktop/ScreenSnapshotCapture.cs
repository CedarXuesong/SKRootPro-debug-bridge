using System.Buffers.Binary;
using System.Globalization;

namespace SkpdbDesktop;

public sealed record ScreenSnapshot(byte[] Png, DateTimeOffset CapturedAt,
    int Width, int Height, int Rotation, string Id);

/// <summary>One still frame, downloaded into memory on a separate authenticated socket.</summary>
public static class ScreenSnapshotCapture
{
    private const int MaxBytes = 48 * 1024 * 1024;
    private const uint ChunkBytes = 900 * 1024;
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static async Task<ScreenSnapshot> CaptureAsync(SkpdbClient session,
        CancellationToken cancellationToken = default)
    {
        var generation = session.ConnectionGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var channel = await session.OpenIndependentChannelAsync(deadline.Token).ConfigureAwait(false);
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                 + "_" + Guid.NewGuid().ToString("N");
        var remote = "/data/local/tmp/skpdb-snapshot-" + id + ".png";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var before = await ReadDisplayAsync(channel, deadline.Token).ConfigureAwait(false);
                var capturedAt = DateTimeOffset.Now;
                var result = await channel.ExecAsync("screencap -p " + remote, deadline.Token).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException("获取屏幕快照失败：" + result.Output.Trim());
                var after = await ReadDisplayAsync(channel, deadline.Token).ConfigureAwait(false);
                if (before != after) continue;

                using var buffer = new MemoryStream();
                for (;;)
                {
                    var chunk = await channel.ReadFileAsync(remote, buffer.Length, ChunkBytes, deadline.Token)
                        .ConfigureAwait(false);
                    if (buffer.Length + chunk.Length > MaxBytes)
                        throw new InvalidDataException("屏幕快照超过 48 MiB，未加载到画布");
                    buffer.Write(chunk);
                    if (chunk.Length < ChunkBytes) break;
                }
                var png = buffer.ToArray();
                var (width, height) = ReadPngSize(png);
                if (width != after.Width || height != after.Height) continue;
                deadline.Token.ThrowIfCancellationRequested();
                if (!session.IsConnected || session.ConnectionGeneration != generation)
                    throw new OperationCanceledException("设备会话已改变", cancellationToken);
                return new ScreenSnapshot(png, capturedAt, width, height, after.Rotation, id);
            }
            throw new InvalidOperationException("采集期间屏幕方向或尺寸发生变化，请稳定屏幕后重新获取快照");
        }
        finally
        {
            // Cancellation can invalidate the capture socket. A short-lived cleanup
            // socket still removes only our timestamped temporary file on the same session.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                if (channel.IsConnected)
                    await channel.ExecAsync("rm -f " + remote, cleanup.Token).ConfigureAwait(false);
                else if (session.IsConnected && session.ConnectionGeneration == generation)
                {
                    using var replacement = await session.OpenIndependentChannelAsync(cleanup.Token).ConfigureAwait(false);
                    await replacement.ExecAsync("rm -f " + remote, cleanup.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Snapshot temporary-file cleanup failed: " + error.Message);
            }
        }
    }

    private static async Task<(int Rotation, int Width, int Height)> ReadDisplayAsync(
        SkpdbClient channel, CancellationToken cancellationToken)
    {
        var result = await channel.ExecAsync("dumpsys display", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || TouchDeviceInfo.ParseDisplay(result.Output) is not { } display ||
            display.Width <= 0 || display.Height <= 0 || display.Rotation is < 0 or > 3)
            throw new InvalidOperationException("无法读取手机显示方向，请稍后重试快照");
        return display;
    }

    internal static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> png)
    {
        // Bound the decoded allocation and reject incomplete transfers before handing
        // the image to WinUI's decoder. Decoding itself happens before replacing the old frame.
        if (png.Length < 57 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("设备返回的快照不是有效 PNG 图片");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (width is 0 or > 16384 || height is 0 or > 16384 || (ulong)width * height > 32_000_000)
            throw new InvalidDataException("屏幕快照尺寸无效或过大");
        var hasData = false;
        for (var offset = 8; offset <= png.Length - 12;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (size > png.Length - offset - 12) break;
            var count = (int)size;
            uint crc = uint.MaxValue;
            foreach (var value in png.Slice(offset + 4, count + 4))
                crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + count)..]))
                throw new InvalidDataException("屏幕快照 PNG 校验失败，请重新获取");
            var type = png.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8)) hasData = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (hasData && size == 0 && offset + 12 == png.Length) return ((int)width, (int)height);
                break;
            }
            offset += 12 + (int)size;
        }
        throw new InvalidDataException("屏幕快照下载不完整，请重新获取");
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
}
