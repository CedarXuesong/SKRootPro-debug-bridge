using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SkpdbDesktop;
using System.Runtime.InteropServices;

// Hosts the published UI with a public API connection. No credential is
// entered into or saved by an automated authentication dialog.
internal static class InputHarness
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--isolate-overlays"))
        {
            // Optional fixture isolation only; production must allow vendor DLLs.
            if (!TestOverlayIsolation.TryEnable(out var error))
                throw new System.ComponentModel.Win32Exception(error);
            Console.WriteLine("UI_FIXTURE overlay isolation enabled");
        }
        Console.WriteLine("KEY_STDIN (never saved)");
        var snapshotTest = args.Contains("--snapshot-ui-test") || args.Contains("--input-ui-test");
        var deviceOverlayTest = args.Contains("--device-overlay-test");
        var secret = new System.Text.StringBuilder();
        if (snapshotTest) secret.Append("local-snapshot-test");
        else if (Console.IsInputRedirected) secret.Append(Console.ReadLine());
        else for (;;)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!char.IsControl(k.KeyChar)) secret.Append(k.KeyChar);
        }
        var session = AppSession.Current;
        var port = int.Parse(args.FirstOrDefault(a => a.StartsWith("--port="))?[7..] ?? "39876");
        if (snapshotTest || deviceOverlayTest)
        {
            if (snapshotTest && args.FirstOrDefault() != "127.0.0.1") throw new InvalidOperationException("UI fixture must use loopback");
            var isolated = Path.GetFullPath(".scratch/snapshot-ui-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(isolated);
            typeof(AppPaths).GetField("_dataDirectory", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(null, isolated);
        }
        var options = new ConnectionOptions(args.FirstOrDefault() ?? throw new ArgumentException("Provide an explicit test host"), Port: port,
            KnockPort: port == 65535 ? port - 1 : port + 1, Key: secret.ToString());
        secret.Clear();
        session.Client.ConnectAsync(options).GetAwaiter().GetResult();
        session.Options = options with { Key = "" };
        options = options with { Key = "" };
        session.Info = session.Client.GetInfoAsync().GetAwaiter().GetResult();
        var settings = SettingsStore.Load();
        settings.Host = session.Options.Host;
        settings.Port = session.Options.Port;
        settings.KnockPort = session.Options.KnockPort;
        SettingsStore.Save(settings);
        Console.WriteLine("CONNECTED module " + session.Info.Version);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
            if (deviceOverlayTest)
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                { await Task.Delay(800); await DeviceOverlayUiTests.RunAsync(); });
            if (snapshotTest)
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    await Task.Delay(600);
                    if (args.Contains("--input-ui-test")) await InputUiTests.RunAsync();
                    else await SnapshotUiTests.RunAsync();
                });
            if (args.Contains("--app-switch-test"))
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    await Task.Delay(1000);
                    await AppSwitchTests.RunAsync();
                });
        });
    }
}

internal static class TestOverlayIsolation
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint flags, nuint size);

    internal static bool TryEnable(out int errorCode)
    {
        uint microsoftSignedOnly = 1;
        var enabled = SetProcessMitigationPolicy(8, ref microsoftSignedOnly, sizeof(uint));
        errorCode = enabled ? 0 : Marshal.GetLastWin32Error();
        return enabled;
    }
}
