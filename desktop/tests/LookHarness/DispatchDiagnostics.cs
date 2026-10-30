using SkpdbDesktop;
using System.Net.Http.Json;
using System.Text.Json;

internal static class DispatchDiagnostics
{
    public static async Task TestAsync(string host, string key, bool lab, bool stay, bool cursor)
    {
        using var client = new SkpdbClient();
        await client.ConnectAsync(new(host, lab ? 40076 : 39876, lab ? 40077 : 39877, key));
        key = string.Empty;
        var module = await client.GetInfoAsync();
        var device = TouchDeviceInfo.Parse(await client.TouchInfoAsync());
        var local = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
            .First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                a.ToString().StartsWith(string.Join('.', host.Split('.').Take(3)) + "."));
        var run = "dispatch_test_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var folder = Path.GetFullPath(".scratch/mouse-lab/" + run);
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8766") };
        if (!stay)
        {
            var opened = await client.ExecAsync($"input keyevent 224; am start -a android.intent.action.VIEW -d http://{local}:8766/");
            Console.WriteLine(opened.Output);
        }
        await Task.Delay(1600);
        (await http.PostAsync("/reset", new StringContent(JsonSerializer.Serialize(new { run }),
            System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        bool ready = false;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var state = await http.GetFromJsonAsync<JsonElement>("/state");
            if (state.TryGetProperty("run", out var ack) && ack.GetString() == run) { ready = true; break; }
        }
        if (!ready) throw new InvalidOperationException("Phone dispatch page not reporting");
        var before = await client.ExecAsync($"/data/local/tmp/skpdb_evdev_state {device.EventPath}");
        if (before.ExitCode != 0) throw new InvalidOperationException("Read-only evdev state probe is unavailable");
        await File.WriteAllTextAsync(Path.Combine(folder, "before.txt"), before.Output);
        for (var slot = 0; slot <= device.SlotMax; slot++) await client.TouchUpAsync(slot);
        using var recorder = await client.OpenIndependentChannelAsync();
        var capture = recorder.ExecAsync($"timeout 22 getevent -lt {device.EventPath}");
        await Task.Delay(200);
        if (cursor) await client.CursorProbeAsync();
        var requests = new List<object>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        async Task Send(byte action, int x, int y, int slot)
        {
            var report = await client.TouchBatchAsync(new[] { new TouchBatchEvent(action, x, y, (ushort)slot) });
            requests.Add(new { t = clock.Elapsed.TotalMilliseconds, action, x, y, slot, report });
        }
        async Task HeldSnapshot(string label)
        {
            var raw = await client.ExecAsync($"/data/local/tmp/skpdb_evdev_state {device.EventPath}");
            await File.WriteAllTextAsync(Path.Combine(folder, label + "_raw.txt"), raw.Output);
            var dump = await client.ExecAsync("dumpsys input");
            await File.WriteAllTextAsync(Path.Combine(folder, label + "_input.txt"), dump.Output);
            Console.WriteLine(label + " " + raw.Output.Replace('\n', ' '));
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var remote = $"/data/local/tmp/skpdb_dispatch_{stamp}.png";
            await client.ExecAsync("screencap -p " + remote);
            await client.DownloadFileAsync(remote, Path.Combine(folder, label + "_" + stamp + ".png"));
            await client.ExecAsync("rm " + remote);
        }
        try
        {
            for (var i = 0; i < 24; i++)
            {
                var slot = i % 2 == 0 ? 5 : 9;
                if (cursor) { await client.CursorSetAsync(512, 1400, i % 2 != 0); await Task.Delay(80); }
                await Send(1, 512, 1400, slot);
                await Task.Delay(50);
                if (i == 0 || cursor && i == 1) await HeldSnapshot("held_" + i);
                for (var step = 1; step <= 8; step++)
                {
                    await Send(2, 512 + 24 * step, 1400 + step * 10, slot);
                    if (cursor) await client.CursorSetAsync(512 + 24 * step, 1400 + step * 10, i % 2 != 0);
                    await Task.Delay(8);
                }
                await Send(3, 0, 0, slot);
                await Task.Delay(60);
            }
            await Task.Delay(300);
            var baseline = await http.GetFromJsonAsync<JsonElement>("/results");
            await File.WriteAllTextAsync(Path.Combine(folder, "baseline.json"), baseline.GetRawText());
            Console.WriteLine($"BASELINE {baseline.GetProperty("gestures").GetArrayLength()}/24 gestures");
            if (cursor)
            {
                var gestures = baseline.GetProperty("gestures").EnumerateArray().ToArray();
                var dpr = baseline.GetProperty("state").GetProperty("dpr").GetDouble();
                if (gestures.Length != 24 || baseline.GetProperty("state").GetProperty("n").GetInt32() != 0 ||
                    baseline.GetProperty("state").GetProperty("pointerCount").GetInt32() != 24 ||
                    gestures.Any(g => g.GetProperty("cancelled").GetBoolean() ||
                        Math.Abs(g.GetProperty("dx").GetDouble() - 192 / dpr) > 2 ||
                        Math.Abs(g.GetProperty("dy").GetDouble() - 80 / dpr) > 2))
                    throw new InvalidOperationException("Cursor occluded or interrupted application touches");
                var visibleInput = await File.ReadAllTextAsync(Path.Combine(folder, "held_1_input.txt"));
                var cursorWindow = visibleInput.Split('\n').Single(line => line.Contains("name=") && line.Contains("SKPDB Cursor"));
                if (!cursorWindow.Contains("TRUSTED_OVERLAY") || !cursorWindow.Contains("NO_INPUT_CHANNEL"))
                    throw new InvalidOperationException("Cursor is not a trusted visual-only layer");
                Console.WriteLine("PASS 12 hidden-cursor and 12 visible-cursor drags, pointer callbacks and endpoints");
            }

        }
        finally
        {
            for (var slot = 0; slot <= device.SlotMax; slot++) await client.TouchUpAsync(slot);
            if (cursor) await client.CursorSetAsync(0, 0, false);
        }
        var events = await capture;
        var logs = await client.ExecAsync("logcat -d -t 5000 -v threadtime | grep -iE 'inputdispatcher|inputreader|touchinput|dropping.*(input|touch|event)|untrusted.*touch|touch.*(cancel|drop|block)|input.*(cancel|drop|block|frozen)'");
        await File.WriteAllTextAsync(Path.Combine(folder, "dispatch_log.txt"), logs.Output);
        await File.WriteAllTextAsync(Path.Combine(folder, "evdev.txt"), events.Output);
        var final = await http.GetFromJsonAsync<JsonElement>("/results");
        await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new { module = module.Version, cursor, stay, requests, final }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FINAL gestures={final.GetProperty("gestures").GetArrayLength()} active={final.GetProperty("state").GetProperty("n")}");
        Console.WriteLine("RESULT " + folder);
    }

    public static async Task SnapshotAsync(string host, string key)
    {
        using var client = new SkpdbClient();
        await client.ConnectAsync(new(host, Key: key));
        key = string.Empty;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var folder = Path.GetFullPath($".scratch/mouse-lab/dispatch_{stamp}");
        Directory.CreateDirectory(folder);
        var touch = await client.TouchInfoAsync();
        await File.WriteAllTextAsync(Path.Combine(folder, "touch.txt"), touch);
        Console.WriteLine(touch);
        const string probe = "/data/local/tmp/skpdb_evdev_state";
        var device = TouchDeviceInfo.Parse(touch);
        await client.UploadFileAsync(".scratch/mouse-lab/evdev_state", probe);
        var raw = await client.ExecAsync($"chmod 700 {probe}; {probe} {device.EventPath}");
        if (raw.ExitCode != 0) throw new InvalidOperationException("Read-only evdev state probe failed: " + raw.Output);
        await File.WriteAllTextAsync(Path.Combine(folder, "raw_state.txt"), raw.Output);
        Console.WriteLine(raw.Output);
        foreach (var (name, command) in new[] {
            ("input", "dumpsys input"),
            ("windows", "dumpsys window windows"),
            ("device", "getevent -lp"),
            ("input_log", "logcat -d -t 1200 -v threadtime -s InputDispatcher InputReader TouchInputMapper"),
            ("settings", "settings get system pointer_location; settings get system show_touches; settings get secure enabled_accessibility_services; getprop ro.build.version.release") })
        {
            var result = await client.ExecAsync(command);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".txt"), result.Output);
            Console.WriteLine($"{name}: exit={result.ExitCode} chars={result.Output.Length}");
        }
        var remote = $"/data/local/tmp/skpdb_dispatch_{stamp}.png";
        var screen = await client.ExecAsync("screencap -p " + remote);
        if (screen.ExitCode == 0)
        {
            await client.DownloadFileAsync(remote, Path.Combine(folder, "phone_" + stamp + ".png"));
            await client.ExecAsync("rm " + remote);
        }
        Console.WriteLine("SNAPSHOT " + folder);
    }
}
