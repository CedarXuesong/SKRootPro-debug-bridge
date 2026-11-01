using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using SkpdbDesktop;

if (args.Contains("--build-channel-test")) { await SnapshotTests.RunBuildChannelAsync(); return; }
if (args.Contains("--update-test")) { await UpdateTests.RunAsync(); return; }
if (args.Contains("--self-test")) { PointerRegionTests.Run(); RelayTests.Run(); return; }
if (args.Contains("--slots-test")) { await DynamicSlotTests.RunAsync(); return; }
if (args.Contains("--micro-test")) { await MicroLookTests.RunAsync(); return; }
if (args.Contains("--gyro-test")) { await GyroTests.RunAsync(); return; }
if (args.Contains("--overlay-test")) { await SnapshotTests.RunOverlayAsync(); return; }
if (args.Contains("--input-test")) { await InputTests.RunAsync(); return; }
if (args.Contains("--profile-test")) { ProfileTests.Run(); return; }
if (args.Contains("--snapshot-test")) { await SnapshotTests.RunAsync(); return; }
if (args.Contains("--snapshot-ui-peer")) { await SnapshotTests.RunUiPeerAsync(); return; }
if (args.Contains("--transport-test")) { await TransportTests.RunAsync(); return; }
if (args.Contains("--client-test")) { await ClientLifecycleTests.RunAsync(); return; }
if (args.Contains("--knock-test")) { await KnockRetryTests.RunAsync(); return; }
if (args.Contains("--control-test")) { ControlActionTests.Run(); return; }
if (args.Contains("--reconfigure-test")) { await ReconfigurationTests.RunAsync(); return; }
if (args.Contains("--joystick-test")) { JoystickTests.Run(); return; }
if (args.Contains("--preview-test")) { LocalPreviewTests.Run(); return; }
var direct = args.Contains("--direct-test");
var diagnostic = args.Any(arg => arg.StartsWith("--dispatch-", StringComparison.Ordinal) || arg == "--soak");
var host = args.ElementAtOrDefault(0) ?? throw new ArgumentException("Provide an explicit device address for live tests");
if (direct && host == "--direct-test") host = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Provide an explicit device address for direct tests");
var label = args.ElementAtOrDefault(1) ?? "look";
var rateX = direct || diagnostic ? 0 : double.Parse(args.ElementAtOrDefault(2) ?? "1000", System.Globalization.CultureInfo.InvariantCulture);
var rateY = direct || diagnostic ? 0 : double.Parse(args.ElementAtOrDefault(3) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var duration = direct || diagnostic ? 0 : double.Parse(args.ElementAtOrDefault(4) ?? "6", System.Globalization.CultureInfo.InvariantCulture);
var port = args.Contains("--lab") ? 40076 : 39876;
Console.WriteLine("KEY_STDIN (never saved)");
string ReadKey()
{
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? throw new InvalidOperationException("Missing key on stdin");
    var secret = new System.Text.StringBuilder();
    for (;;)
    {
        var input = Console.ReadKey(intercept: true);
        if (input.Key == ConsoleKey.Enter) return secret.ToString();
        if (input.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
        else if (!char.IsControl(input.KeyChar)) secret.Append(input.KeyChar);
    }
}
var key = ReadKey();
if(args.Contains("--gyro-lab")){await GyroLabSession.RunAsync(host,key);return;}
if(args.Contains("--gyro-device-test")){await GyroDeviceTests.RunAsync(host,key);return;}
if (args.Contains("--soak")) { await SoakTests.RunAsync(host, key, args); return; }
if (args.Contains("--dispatch-snapshot")) { await DispatchDiagnostics.SnapshotAsync(host, key); return; }
if (args.Contains("--dispatch-test")) { await DispatchDiagnostics.TestAsync(host, key, args.Contains("--lab"), args.Contains("--dispatch-stay"), args.Contains("--dispatch-cursor")); return; }
if (args.Contains("--upgrade-module")) { await ModuleUpgrade.RunAsync(host, key); return; }
if (direct) { await DirectTests.RunAsync(host, key, args.Contains("--prepare"), args.Contains("--load"), args.Contains("--kernel"), args.Contains("--lab"), args.Contains("--wire-test"), args.Contains("--no-queries")); return; }
if (args.Contains("--suite"))
{
    var cases = new[]
    {
        new[] { "suite_forward", "1000", "0", "6" },
        new[] { "suite_reverse", "-1000", "0", "6" },
        new[] { "suite_edge_multi", "1000", "250", "6", "--edge-anchor", "--multi" },
        new[] { "suite_no_spare", "1000", "0", "4", "--no-spare" },
        new[] { "suite_wire", "1000", "0", "1", "--wire-test" }
    };
    foreach (var test in cases)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
          RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(host);
        foreach (var value in test) start.ArgumentList.Add(value);
        if (args.Contains("--lab")) start.ArgumentList.Add("--lab");
        using var child = Process.Start(start)!;
        var childOutput = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(key);
        child.StandardInput.Close();
        await child.WaitForExitAsync();
        Console.Write(await childOutput);
        Console.Error.Write(await errors);
        if (child.ExitCode != 0) throw new InvalidOperationException("Device suite failed: " + test[0]);
    }
    key = string.Empty;
    Console.WriteLine("All device suite cases passed.");
    return;
}
using var client = new SkpdbClient();
await client.ConnectAsync(new ConnectionOptions(host, Port: port, KnockPort: port + 1, Key: key));
var module = await client.GetInfoAsync();
Console.WriteLine($"MODULE {module.Version} UDP={module.FastPort}");
if (args.Contains("--stop-lab"))
{
    using var lab = new SkpdbClient();
    await lab.ConnectAsync(new ConnectionOptions(host, 40076, 40077, key));
    var temporary = await lab.GetInfoAsync();
    if (temporary.Id32 != module.Id32 || temporary.Pid <= 1 || temporary.Pid == module.Pid ||
        temporary.ServicePort != 40076 || module.ServicePort != 39876)
        throw new InvalidOperationException("Unexpected temporary bridge identity");
    // This is a detached test process, never an installed-module uninstall.
    var stopped = await client.ExecAsync("kill " + temporary.Pid);
    if (stopped.ExitCode != 0) throw new InvalidOperationException(stopped.Output);
    Console.WriteLine("Temporary bridge stopped; formal bridge remains active.");
    return;
}
if (args.Contains("--info-only"))
{
    Console.WriteLine($"PID {module.Pid} PORT {module.ServicePort}");
    return;
}
if (args.Contains("--inspect"))
{
    Console.WriteLine((await client.ExecAsync("ls -l /data/local/tmp/skpdb-look-lab; cat /data/local/tmp/skpdb-look-lab/loader.log; cat /data/local/tmp/skpdb-look-lab/skpdb.health; ps -A -o PID,PPID,STAT,ARGS | grep -E 'skpdb-look|40070' ")).Output);
    return;
}
if (args.Contains("--launch-lab"))
{
    if (args.Contains("--replace-lab"))
    {
        using var previous = new SkpdbClient();
        await previous.ConnectAsync(new ConnectionOptions(host, Port: 40076, KnockPort: 40077, Key: key));
        var old = await previous.GetInfoAsync();
        if (old.Id32 != module.Id32 || old.Pid <= 1 || old.Pid == module.Pid || old.ServicePort != 40076)
            throw new InvalidOperationException("Unexpected lab module identity");
        await client.ExecAsync($"kill {old.Pid}");
        await Task.Delay(200);
    }
    const string dir = "/data/local/tmp/skpdb-look-lab";
    var inputPort = Random.Shared.Next(41000, 55000);
    Console.WriteLine((await client.ExecAsync($"mkdir -p {dir}")).Output);
    await client.UploadFileAsync("skpdb_probe/libs/arm64-v8a/libskpdb_probe.so", dir + "/libskpdb_probe.so.next");
    Console.WriteLine("Uploaded module");
    await client.UploadFileAsync("skpdb_probe/libs/arm64-v8a/skpdb_loader", dir + "/loader.next");
    var published = await client.ExecAsync($"mv {dir}/libskpdb_probe.so.next {dir}/libskpdb_probe.so && mv {dir}/loader.next {dir}/loader");
    if (published.ExitCode != 0) throw new InvalidOperationException("Could not publish diagnostic binaries");
    Console.WriteLine("Uploaded loader");
    using var config = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("service_port=40076\nknock_port=40077\n"));
    await client.UploadFileAsync(config, dir + "/skpdb.conf");
    Console.WriteLine("Uploaded config");
    for (var slot = 0; slot < 10; slot++) await client.TouchUpAsync(slot);
    Console.WriteLine("Released old touches");
    var networkInput = args.Contains("--network-input");
    var bind = networkInput ? System.Net.IPAddress.Parse(host).ToString() : "127.0.0.1";
    var prepared = await client.ExecAsync($"chmod 711 {dir}; chmod 755 {dir}/loader; nohup toybox nc -s {bind} -l -p {inputPort} -W 20 {dir}/loader {dir}/libskpdb_probe.so {dir} __skroot_module_main_18fdf393885363712349ef3de5411bd5 > {dir}/loader.log 2>&1 < /dev/null &");
    if (prepared.ExitCode != 0) throw new InvalidOperationException(prepared.Output);
    if (!networkInput)
        using (var forward = Process.Start(new ProcessStartInfo("adb", $"forward tcp:{inputPort} tcp:{inputPort}") { UseShellExecute = false, CreateNoWindow = true })) await forward!.WaitForExitAsync();
    System.Net.Sockets.TcpClient? inputSocket = null;
    for (var attempt = 0; attempt < 30; attempt++)
    {
        var candidate = new System.Net.Sockets.TcpClient();
        try
        {
            await candidate.ConnectAsync(networkInput ? host : "127.0.0.1", inputPort);
            inputSocket = candidate;
            break;
        }
        catch (System.Net.Sockets.SocketException) { candidate.Dispose(); await Task.Delay(100); }
    }
    using (var input = inputSocket ?? throw new InvalidOperationException("Loader stdin did not open"))
    {
        var stream = input.GetStream();
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(key + "\n"));
        var response = new byte[4096];
        var count = await stream.ReadAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        if (count > 0) Console.WriteLine(System.Text.Encoding.UTF8.GetString(response, 0, count));
    }
    Console.WriteLine("Lab module starting (key passed to loader stdin, never stored).");
    await Task.Delay(13500);
    using var lab = new SkpdbClient();
    await lab.ConnectAsync(new ConnectionOptions(host, Port: 40076, KnockPort: 40077, Key: key));
    key = string.Empty;
    var info = await lab.GetInfoAsync();
    Console.WriteLine($"LAB {info.Version} pid={info.Pid} UDP protocol={info.FastProtocol}");
    Console.WriteLine((await client.ExecAsync($"cat {dir}/loader.log")).Output);
    if (!networkInput)
        using (var forward = Process.Start(new ProcessStartInfo("adb", $"forward --remove tcp:{inputPort}") { UseShellExecute = false, CreateNoWindow = true })) await forward!.WaitForExitAsync();
    return;
}
key = string.Empty;
var touchReport = await client.TouchInfoAsync();
var device = TouchDeviceInfo.Parse(touchReport);
Console.WriteLine(touchReport.Split('\n').FirstOrDefault(line => line.StartsWith("inject_backend=")) ?? "inject_backend=kernel-asm");
var display = TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
if (display is { } d) device = device.WithDisplay(d.Rotation, d.Width, d.Height);
Console.WriteLine($"DISPLAY {device.DisplayWidth}x{device.DisplayHeight} rotation={device.Rotation} slots=0..{device.SlotMax}");
if (args.Contains("--probe-input"))
{
    Console.WriteLine((await client.ExecAsync($"ls -lZ {device.EventPath}; toybox sendevent {device.EventPath} 0 0 0; echo evdev_write_rc=$?")).Output);
    return;
}
// Clear earlier diagnostic touches before starting a new run.
for (var slot = 0; slot <= device.SlotMax; slot++) await client.TouchUpAsync(slot);
var profile = GamepadProfileStore.CreateDefaultProfile();
profile.Look.Smoothing = 0.25;
if (args.Contains("--edge-anchor")) profile.Look.AnchorX = 0.98;
if (args.Contains("--no-spare"))
{
    foreach (var slot in new[] { 5, 6, 7, 8, 9 })
        profile.Buttons.Add(new GamepadButton { Id = $"spare{slot}", Binding = $"F{slot}", Slot = slot });
}
var both = args.Contains("--multi");
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(3) };
using var reset = await http.PostAsync("/reset", new StringContent(
    JsonSerializer.Serialize(new { run = label }), System.Text.Encoding.UTF8, "application/json"));
reset.EnsureSuccessStatusCode();
JsonElement state = default;
for (var attempt = 0; attempt < 60; attempt++)
{
    await Task.Delay(50);
    state = await http.GetFromJsonAsync<JsonElement>("/state");
    if (state.TryGetProperty("run", out var run) && run.GetString() == label) break;
}
if (!state.TryGetProperty("run", out var acknowledged) || acknowledged.GetString() != label)
    throw new InvalidOperationException("Phone simulator did not acknowledge reset");
if (args.Contains("--wire-test"))
{
    await WireTests.Run(client, module, device, http, host);
    return;
}
await using var engine = new GamepadEngine();
engine.ConfigureFastPath(host, module.FastPort, module.FastToken, module.FastProtocol, module.FastMaxRecords);
var trace = new List<object>();
var watch = Stopwatch.StartNew();
engine.Trace += (_, message) => trace.Add(new { t = watch.Elapsed.TotalMilliseconds, message });
await engine.StartAsync(client, device, profile);
engine.SetViewMode(true);
if (both) { engine.SetKey("W", true); engine.SetMouseButton("MouseLeft", true); }
double inputX = 0, inputY = 0, last = 0;
var inputFrames = new List<double>();
using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4)))
{
    watch.Restart();
    while (await timer.WaitForNextTickAsync())
    {
        var now = Math.Min(duration, watch.Elapsed.TotalSeconds);
        inputFrames.Add(now);
        var dx = rateX * (now - last);
        var dy = rateY * (now - last);
        engine.AddMouseDelta(dx, dy);
        inputX += dx; inputY += dy;
        last = now;
        if (now >= duration) break;
    }
}
await Task.Delay(100);
engine.SetViewMode(false);
await Task.Delay(50);
await engine.StopAsync();
await Task.Delay(180);
var result = await http.GetFromJsonAsync<JsonElement>("/results");
var output = Path.GetFullPath(Path.Combine(".scratch", "look-lab", $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{label}.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    label, rateX, rateY, duration, inputX, inputY,
    display = new { device.DisplayWidth, device.DisplayHeight, device.Rotation },
    sensitivity = profile.Look.Sensitivity,
    metrics = engine.Metrics,
    trace, inputFrames, result
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {output}");
Console.WriteLine(result.GetProperty("summary").GetRawText());
if (engine.Metrics.LastError is { } error) throw new InvalidOperationException(error);
if (result.GetProperty("state").GetProperty("n").GetInt32() != 0)
    throw new InvalidOperationException("Phone still has active touches after stop");
