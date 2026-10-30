using System.Net.Http.Json;
using System.Text.Json;
using SkpdbDesktop;

internal static class DirectTests
{
    public static async Task RunAsync(string host, string key, bool prepare, bool load, bool kernel, bool lab, bool wire, bool noQueries)
    {
        using var client = new SkpdbClient();
        await client.ConnectAsync(new ConnectionOptions(host, Port: lab ? 40076 : 39876,
            KnockPort: lab ? 40077 : 39877, Key: key));
        key = string.Empty;
        var module = await client.GetInfoAsync();
        var device = TouchDeviceInfo.Parse(await client.TouchInfoAsync());
        var display = TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
        if (display is { } d) device = device.WithDisplay(d.Rotation, d.Width, d.Height);
        Console.WriteLine($"MODULE {module.Version}; DEVICE {device.DisplayWidth}x{device.DisplayHeight} rotation={device.Rotation}");
        if (prepare)
        {
            var local = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
                .First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            a.ToString().StartsWith(string.Join('.', host.Split('.').Take(3)) + "."));
            await client.ExecAsync($"input keyevent 224; am start -a android.intent.action.VIEW -d http://{local}:8766/");
            await Task.Delay(1200);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var remote = $"/data/local/tmp/skpdb_mouse_{stamp}.png";
            var path = Path.GetFullPath($".scratch/mouse-lab/phone_{stamp}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await client.ExecAsync($"screencap -p {remote}");
            await client.DownloadFileAsync(remote, path);
            await client.ExecAsync($"rm {remote}");
            Console.WriteLine("SCREENSHOT " + path);
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8766"), Timeout = TimeSpan.FromSeconds(3) };
        var run = "direct_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        using var reset = await http.PostAsync("/reset", new StringContent(
            JsonSerializer.Serialize(new { run }), System.Text.Encoding.UTF8, "application/json"));
        reset.EnsureSuccessStatusCode();
        JsonElement state = default;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            state = await http.GetFromJsonAsync<JsonElement>("/state");
            if (state.TryGetProperty("run", out var r) && r.GetString() == run) break;
        }
        if (!state.TryGetProperty("run", out var ready) || ready.GetString() != run)
            throw new InvalidOperationException("Phone mouse simulator is not reporting");
        for (var slot = 0; slot <= device.SlotMax; slot++) await client.TouchUpAsync(slot);
        if (wire) { await WireTests.Run(client, module, device, http, host); return; }
        var profile = GamepadProfileStore.CreateDefaultProfile();
        profile.DisplayName = "direct-test";
        profile.Validate();
        await using var engine = new GamepadEngine();
        using var recorder = kernel ? await client.OpenIndependentChannelAsync() : null;
        Task<ExecResult>? capture = null;
        if (recorder is not null)
        {
            // Capture evdev SYN timestamps rather than equating browser
            // dispatch/VSync frequency with the real input stream rate.
            capture = recorder.ExecAsync($"timeout 13 getevent -lt {device.EventPath} | grep SYN_REPORT");
            await Task.Delay(300);
        }
        engine.ConfigureFastPath(host, module.FastPort, module.FastToken, module.FastProtocol, module.FastMaxRecords);
        await engine.StartAsync(client, device, profile);
        if (!engine.DirectTouchAvailable) throw new InvalidOperationException("No direct-touch slot");
        double px = .40, py = .50;
        var expected = new List<object>();
        var queries = new List<object>();
        var providerSamples = new List<double>();
        GamepadMetrics? motionMetrics = null;
        var queriesClock = System.Diagnostics.Stopwatch.StartNew();
        using var queriesStop = new CancellationTokenSource();
        Task queriesTask = Task.CompletedTask;
        if (load && !noQueries)
        {
            // Reproduce a slow shell/metadata reply on the control connection.
            // No cancellation of an in-flight framed reply: drain it before stop.
            queriesTask = Task.Run(async () =>
            {
                while (!queriesStop.IsCancellationRequested)
                {
                    var startMs = queriesClock.Elapsed.TotalMilliseconds;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await client.ExecAsync("dumpsys activity activities 2>/dev/null | grep -m1 -E 'mResumedActivity|topResumedActivity'; sleep 0.12");
                    queries.Add(new { startMs, elapsedMs = watch.Elapsed.TotalMilliseconds });
                    await Task.Delay(350);
                }
            });
        }
        async Task Drag(bool provider, bool burst)
        {
            px = .40; py = .50;
            engine.PointerPositionProvider = provider ? () => (px, py) : null;
            engine.PointerTouchDown(px, py);
            await Task.Delay(60);
            for (var i = 1; i <= (burst ? 1000 : 12); i++)
            {
                px = .40 + .18 * i / (burst ? 1000 : 12);
                py = .50 + .05 * i / (burst ? 1000 : 12);
                if (!provider) engine.PointerTouchMove(px, py);
                if (!burst) await Task.Delay(8);
            }
            engine.PointerTouchUp(px, py);
            // A released physical cursor may immediately move elsewhere.
            // That later position must not replace this gesture's endpoint.
            if (provider) { px = .99; py = .99; }
            expected.Add(new { drag = true, provider, burst });
            await Task.Delay(burst ? 90 : 60);
        }
        if (load)
        {
            // Eight seconds of constant-speed movement makes gaps measurable
            // on Android itself, independently of the desktop RTT display.
            px = .3; py = .5;
            engine.PointerTouchDown(px, py);
            await Task.Delay(80);
            var motion = System.Diagnostics.Stopwatch.StartNew();
            engine.PointerPositionProvider = () =>
            {
                var now = motion.Elapsed.TotalMilliseconds;
                providerSamples.Add(now);
                var t = now / 1000 % 2;
                px = .3 + .4 * (t < 1 ? t : 2 - t);
                return (px, py);
            };
            await Task.Delay(8000);
            motionMetrics = engine.Metrics;
            engine.PointerPositionProvider = null;
            engine.PointerTouchMove(px, py);
            engine.PointerTouchUp();
            expected.Add(new { drag = true, load = true });
            await Task.Delay(160);
            queriesStop.Cancel();
            await queriesTask;
        }
        else
        {
            for (var i = 0; i < 20; i++) await Drag(true, false);
            for (var i = 0; i < 20; i++) await Drag(false, false);
            for (var i = 0; i < 5; i++) await Drag(false, true);
        }
        engine.PointerPositionProvider = null;
        for (var i = 0; i < (load ? 0 : 10); i++)
        {
            engine.PointerTouchDown(.5, .5);
            engine.PointerTouchUp();
            expected.Add(new { drag = false });
        }
        await Task.Delay(1200);
        engine.PointerTouchDown(.45, .5);
        await Task.Delay(80);
        engine.SetViewMode(true);
        expected.Add(new { drag = false, modeSwitch = true });
        await Task.Delay(100);
        engine.SetViewMode(false);
        engine.PointerTouchDown(.45, .5);
        await Task.Delay(80);
        expected.Add(new { drag = false, stop = true });
        await engine.StopAsync();
        var finalModule = await client.GetInfoAsync();
        var transport = new { received = finalModule.FastReceived - module.FastReceived,
            applied = finalModule.FastApplied - module.FastApplied,
            dropped = finalModule.FastDropped - module.FastDropped,
            stale = finalModule.FastStale - module.FastStale, realtime = finalModule.FastRealtime };
        Console.WriteLine("TRANSPORT " + JsonSerializer.Serialize(transport));
        string? kernelText = null;
        object? kernelStats = null;
        double kernelHz = 0, kernelP95 = 0;
        if (capture is not null)
        {
            kernelText = (await capture).Output;
            var times = System.Text.RegularExpressions.Regex.Matches(kernelText,
                @"\[\s*(\d+\.\d+)\]\s+EV_SYN\s+SYN_REPORT")
                .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            // Find the long moving segment, excluding initial/final taps.
            var segments = new List<List<double>>();
            foreach (var t in times)
            {
                if (segments.Count == 0 || t - segments[^1][^1] > .1) segments.Add(new());
                segments[^1].Add(t);
            }
            var main = segments.OrderByDescending(s => s.Count).FirstOrDefault()
                ?? throw new InvalidOperationException("No kernel SYN_REPORT timestamps");
            var from = main[0] + .2;
            var to = main[^1] - .1;
            main = main.Where(t => t >= from && t <= to).ToList();
            var gaps = main.Zip(main.Skip(1), (a, b) => (b - a) * 1000).Order().ToArray();
            var hz = (main.Count - 1) / (main[^1] - main[0]);
            kernelHz = hz;
            kernelP95 = gaps[(int)((gaps.Length-1)*.95)];
            kernelStats = new { frames = main.Count, hz,
                p50Ms = gaps[gaps.Length/2], p95Ms = gaps[(int)((gaps.Length-1)*.95)],
                p99Ms = gaps[(int)((gaps.Length-1)*.99)], maxMs = gaps.Last() };
            Console.WriteLine("KERNEL " + JsonSerializer.Serialize(kernelStats));
        }
        await Task.Delay(400);
        var result = await http.GetFromJsonAsync<JsonElement>("/results");
        var pathOut = Path.GetFullPath($".scratch/mouse-lab/{run}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pathOut)!);
        await File.WriteAllTextAsync(pathOut, JsonSerializer.Serialize(new
        { host, module = module.Version, device.DisplayWidth, device.DisplayHeight,
          expected, queries, providerSamples, motionMetrics, transport, kernelStats, kernelText, load, metrics = engine.Metrics, result }, new JsonSerializerOptions { WriteIndented = true }));
        var gestures = result.GetProperty("gestures").EnumerateArray().ToArray();
        Console.WriteLine($"RESULT {pathOut}");
        Console.WriteLine($"PHONE gestures={gestures.Length}/{expected.Count} active={result.GetProperty("state").GetProperty("n")} RTT={engine.Metrics.RttMs:F2}ms");
        if (gestures.Length != expected.Count || result.GetProperty("state").GetProperty("n").GetInt32() != 0)
            throw new InvalidOperationException("Missing gesture or unreleased phone touch");
        var dpr = result.GetProperty("state").GetProperty("dpr").GetDouble();
        var expectedDx = device.DisplayWidth * .18 / dpr;
        var expectedDy = device.DisplayHeight * .05 / dpr;
        if (load)
        {
            Console.WriteLine("PHONE SAMPLING " + gestures[0]);
            Console.WriteLine("MOTION METRICS " + JsonSerializer.Serialize(motionMetrics));
            var intervals = providerSamples.Zip(providerSamples.Skip(1), (a, b) => b - a).Order().ToArray();
            Console.WriteLine($"PROVIDER samples={providerSamples.Count} p95={intervals[(int)((intervals.Length-1)*.95)]:F2}ms max={intervals.Last():F2}ms");
            if (engine.Metrics.LastError is { } loadError) throw new InvalidOperationException(loadError);
            if (gestures[0].GetProperty("gapsOver80ms").GetInt32() != 0)
                throw new InvalidOperationException("Phone movement stalled for over 80ms under background load");
            if (kernel && (motionMetrics!.SamplingHz < 230 || motionMetrics.SampleP95Ms > 6))
                throw new InvalidOperationException("240 Hz sampler did not meet cadence under load");
            if (kernel && (kernelHz < 235 || kernelP95 > 6.5))
                throw new InvalidOperationException("Phone evdev did not sustain the 240 Hz target");
            return;
        }
        for (var i = 0; i < 45; i++)
        {
            var g = gestures[i];
            if (g.GetProperty("cancelled").GetBoolean() ||
                Math.Abs(g.GetProperty("dx").GetDouble() - expectedDx) > 2 ||
                Math.Abs(g.GetProperty("dy").GetDouble() - expectedDy) > 2)
                throw new InvalidOperationException($"Drag {i} has incomplete displacement: {g}");
        }
        if (engine.Metrics.LastError is { } error) throw new InvalidOperationException(error);
        Console.WriteLine("PASS 20 polled drags, 20 event drags, 5 burst/endpoints, 10 rapid taps, mode switch and stop.");
    }
}
