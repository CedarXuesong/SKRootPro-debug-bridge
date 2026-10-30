using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SkpdbDesktop;

internal static class SoakTests
{
    public static async Task RunAsync(string host, string key, string[] args)
    {
        var seconds = int.Parse(args.FirstOrDefault(a => a.StartsWith("--seconds="))?.Split('=')[1] ?? "120");
        var holdOnly = args.Contains("--hold-only");
        using var client = new SkpdbClient();
        await client.ConnectAsync(new(host, Key: key));
        key = string.Empty;
        using var health = await client.OpenIndependentChannelAsync();
        var initial = await client.GetInfoAsync();
        var device = TouchDeviceInfo.Parse(await client.TouchInfoAsync());
        var display = TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
        if (display is { } d) device = device.WithDisplay(d.Rotation, d.Width, d.Height);
        var local = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
            .First(a => a.AddressFamily == AddressFamily.InterNetwork &&
                a.ToString().StartsWith(string.Join('.', host.Split('.').Take(3)) + "."));
        var run = "soak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var folder = Path.GetFullPath(".scratch/mouse-lab/" + run);
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { BaseAddress = new("http://127.0.0.1:8766"), Timeout = TimeSpan.FromSeconds(4) };
        await client.ExecAsync($"input keyevent 224; am start -a android.intent.action.VIEW -d http://{local}:8766/");
        await Task.Delay(2000);
        (await http.PostAsync("/reset", new StringContent(JsonSerializer.Serialize(new { run }),
            System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        bool ready = false;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var state = await http.GetFromJsonAsync<JsonElement>("/state");
            if (state.TryGetProperty("run", out var r) && r.GetString() == run) { ready = true; break; }
        }
        if (!ready) throw new InvalidOperationException("USB phone simulator not reporting");
        for (var slot = 0; slot <= device.SlotMax; slot++) await client.TouchUpAsync(slot);
        await client.CursorProbeAsync();
        await client.CursorSetAsync(device.DisplayWidth / 2, device.DisplayHeight / 2, true);
        await using var engine = new GamepadEngine();
        var profile = GamepadProfileStore.CreateDefaultProfile();
        engine.ConfigureFastPath(host, initial.FastPort, initial.FastToken, initial.FastProtocol, initial.FastMaxRecords);
        var clock = Stopwatch.StartNew();
        var trace = new ConcurrentQueue<object>();
        engine.Trace += (_, message) => trace.Enqueue(new { t = clock.Elapsed.TotalSeconds, message });
        engine.Log += (_, message) => { trace.Enqueue(new { t = clock.Elapsed.TotalSeconds, message }); Console.WriteLine("ENGINE " + message); };
        await engine.StartAsync(client, device, profile);
        clock.Restart();
        var samples = new ConcurrentQueue<object>();
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var watch = Stopwatch.StartNew();
                    var info = await health.GetInfoAsync(deadline.Token);
                    var rtt = watch.Elapsed.TotalMilliseconds;
                    var row = new { t = clock.Elapsed.TotalSeconds, info.Pid, info.UptimeSeconds, info.Sessions,
                        info.FastReceived, info.FastApplied, info.FastDropped, info.FastStale,
                        rtt, running = engine.IsRunning, metrics = engine.Metrics };
                    samples.Enqueue(row);
                    Console.WriteLine(JsonSerializer.Serialize(row));
                }
                catch (Exception e) { errors.Enqueue("health: " + e); Console.WriteLine("HEALTH " + e.Message); }
                try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
        var queries = Task.Run(async () =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    await client.ExecAsync(++n % 4 == 0 ? "dumpsys display" :
                        "dumpsys activity activities 2>/dev/null | grep -m1 -E 'mResumedActivity|topResumedActivity'", deadline.Token);
                }
                catch (Exception e) { errors.Enqueue("query: " + e); Console.WriteLine("QUERY " + e.Message); break; }
                try { await Task.Delay(750, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
        bool down = false, look = false;
        int expectedDrags = 0;
        try
        {
            engine.PointerPositionProvider = () =>
            {
                var t = clock.Elapsed.TotalSeconds;
                return (.5 + .20 * Math.Sin(t * 13.1), .50 + .13 * Math.Sin(t * 17.3));
            };
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
            double last = 0;
            while (await timer.WaitForNextTickAsync())
            {
                var t = clock.Elapsed.TotalSeconds;
                if (t >= seconds) break;
                if (!engine.IsRunning) throw new InvalidOperationException("Engine stopped at " + t + ": " + engine.Metrics.LastError);
                if (!holdOnly && t >= seconds / 2.0 && !look)
                {
                    if (down) { engine.PointerTouchUp(); down = false; }
                    engine.SetViewMode(true);
                    engine.SetKey("W", true);
                    engine.SetMouseButton("MouseLeft", true);
                    look = true;
                }
                var x = .5 + .20 * Math.Sin(t * 13.1);
                var y = .50 + .13 * Math.Sin(t * 17.3);
                if (!look)
                {
                    var wanted = holdOnly || t % .9 < .72;
                    if (wanted && !down) { engine.PointerTouchDown(x, y); down = true; expectedDrags++; }
                    if (!wanted && down) { engine.PointerTouchUp(x, y); down = false; }
                }
                else
                {
                    engine.AddMouseDelta((t - last) * 5500 * Math.Cos(t * 6.1), (t - last) * 3500 * Math.Sin(t * 8.7));
                    engine.SetKey("D", (int)(t * 2) % 2 == 0);
                }
                engine.SendCursorMove((int)(x * (device.DisplayWidth - 64)), (int)(y * (device.DisplayHeight - 64)));
                last = t;
            }
        }
        catch (Exception e) { errors.Enqueue("motion: " + e); Console.WriteLine("MOTION " + e.Message); }
        finally
        {
            engine.PointerPositionProvider = null;
            if (down) engine.PointerTouchUp();
            engine.SetViewMode(false);
            await engine.StopAsync();
            stop.Cancel();
            await Task.WhenAll(monitor, queries);
        }
        await Task.Delay(500);
        JsonElement result = await http.GetFromJsonAsync<JsonElement>("/results");
        string raw = "unavailable";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await health.CursorSetAsync(0, 0, false, deadline.Token);
            raw = (await health.ExecAsync($"/data/local/tmp/skpdb_evdev_state {device.EventPath}; ps -A -T | grep -E 'skpdb|app_process'", deadline.Token)).Output;
        }
        catch (Exception e) { errors.Enqueue("final: " + e); }
        await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new {
            host, seconds, holdOnly, module = initial.Version, initialPid = initial.Pid,
            elapsed = clock.Elapsed.TotalSeconds, expectedDrags, metrics = engine.Metrics,
            samples, errors, trace, raw, result }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESULT " + folder);
        Console.WriteLine($"PHONE gestures={result.GetProperty("gestures").GetArrayLength()} expectedDirect={expectedDrags} active={result.GetProperty("state").GetProperty("n")}");
        if (!errors.IsEmpty || engine.Metrics.LastError is not null || result.GetProperty("state").GetProperty("n").GetInt32() != 0)
            throw new InvalidOperationException("Soak failed; inspect result.json");
        Console.WriteLine("PASS continuous motion, cursor UDP, background queries and module liveness");
    }
}
