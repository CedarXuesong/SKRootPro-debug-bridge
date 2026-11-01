using System.Diagnostics;
using System.Text.Json;
using SkpdbDesktop;

internal static class GyroLabSession
{
    public static async Task RunAsync(string host, string key)
    {
        using var client = new SkpdbClient();
        await client.ConnectAsync(new(host, Key: key));
        var info = await client.GetInfoAsync();
        var device = TouchDeviceInfo.Parse(await client.TouchInfoAsync()).WithDisplay(0, 1280, 2772);
        var profile = GamepadProfileStore.CreateDefaultProfile("com.android.shell");
        profile.Move.Enabled = false;
        profile.Buttons.Clear();
        profile.Look.Sensitivity = 1;
        profile.Look.Smoothing = 0;
        await using var engine = new GamepadEngine();
        engine.ConfigureFastPath(host, info.FastPort, info.FastToken, info.FastProtocol, info.FastMaxRecords);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var gate = new object();
        double x = 0, y = 0;
        Task? movement = null;
        try
        {
            await engine.StartAsync(client, device, profile, stop.Token);
            engine.SetViewMode(true);
            await engine.SetGyroModeAsync(true, stop.Token);
            Console.WriteLine("GYRO_LAB_READY");
            movement = Task.Run(async () =>
            {
                var previous = Stopwatch.GetTimestamp();
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
                    previous = now;
                    lock (gate) engine.AddMouseDelta(x * elapsed, y * elapsed);
                }
            }, stop.Token);
            while (await Console.In.ReadLineAsync(stop.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var command = json.RootElement;
                if (command.TryGetProperty("stop", out _)) break;
                lock (gate)
                {
                    x = command.GetProperty("x").GetDouble();
                    y = command.GetProperty("y").GetDouble();
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    running = engine.IsRunning, gyro = engine.GyroMode, confirmed = engine.GyroEventsConfirmed,
                    error = engine.Metrics.LastError
                }));
            }
        }
        finally
        {
            stop.Cancel();
            if (movement is not null)
            {
                try { await movement; } catch (OperationCanceledException) { }
            }
            await engine.StopAsync();
            await client.GyroRemoveAsync();
            Console.WriteLine("GYRO_LAB_STOPPED");
        }
    }
}
