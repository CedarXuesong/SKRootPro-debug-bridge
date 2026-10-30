using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkpdbDesktop;

internal static class PageOperationUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static Task Call(object owner, string name, params object?[] args) =>
        (Task)owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args)!;
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static async Task<int> SlowCommands() => JsonNode.Parse((await AppSession.Current.Client.ExecAsync("fixture state")).Output)!["slowCommands"]!.GetValue<int>();

    public static async Task RunAsync(MainWindow window)
    {
        var session = AppSession.Current;
        var frame = Field<Frame>(window, "ContentFrame");
        window.Navigate("terminal"); await Until(() => frame.Content is TerminalPage);
        var terminal = (TerminalPage)frame.Content;
        var before = await SlowCommands();
        Field<TextBox>(terminal, "CommandBox").Text = "fixture slow";
        var first = Call(terminal, "RunCommandAsync");
        Field<TextBox>(terminal, "CommandBox").Text = "fixture fast";
        await Call(terminal, "RunCommandAsync");
        Check(Field<TextBox>(terminal, "CommandBox").Text == "fixture fast" && !Field<Button>(terminal, "SendButton").IsEnabled,
            "Repeated Enter consumed pending text or re-enabled Send");
        Check(await session.Client.PingAsync().WaitAsync(TimeSpan.FromMilliseconds(250)) == "PONG", "Terminal blocks primary control TCP");
        await first;
        Check(await SlowCommands() == before + 1 && !Field<ProgressRing>(terminal, "Progress").IsActive, "Terminal duplicated a command or leaked progress");
        Field<TextBox>(terminal, "CommandBox").Text = "fixture slow";
        var stale = Call(terminal, "RunCommandAsync");
        window.Navigate("home"); window.Navigate("terminal");
        Field<TextBlock>(terminal, "OutputText").Text = "returned-page";
        await stale;
        Check(Field<TextBlock>(terminal, "OutputText").Text == "returned-page", "Late terminal output overwrote returned page");
        Console.WriteLine("PASS real terminal page: repeated Enter preserves text, commands use independent TCP, late page output is ignored");

        window.Navigate("memory"); await Until(() => frame.Content is MemoryPage);
        var memory = (MemoryPage)frame.Content;
        var slow = Call(memory, "ReadAsync", 111);
        var fast = Call(memory, "ReadAsync", 222);
        await fast;
        Check(Field<TextBlock>(memory, "OutputText").Text == "maps=222" && Field<ProgressRing>(memory, "Progress").IsActive,
            "Newest memory result missing or concurrent progress hidden");
        await slow;
        Check(Field<TextBlock>(memory, "OutputText").Text == "maps=222" && !Field<ProgressRing>(memory, "Progress").IsActive,
            "Old memory result overwrote latest or left progress active");
        slow = Call(memory, "ReadAsync", 111);
        window.Navigate("home"); window.Navigate("memory");
        Field<TextBlock>(memory, "OutputText").Text = "returned-page";
        await slow;
        Check(Field<TextBlock>(memory, "OutputText").Text == "returned-page", "Memory output crossed page lifecycle");
        Console.WriteLine("PASS real memory page: latest query wins, concurrent work tracks progress, page return rejects old results");

        window.Navigate("touch"); await Until(() => frame.Content is TouchPage);
        var touch = (TouchPage)frame.Content;
        before = await SlowCommands();
        Func<SkpdbClient, CancellationToken, Task<string>> action = async (client, token) => (await client.ExecAsync("fixture slow", token)).Output;
        first = Call(touch, "RunAsync", action, "fixture gesture");
        await Call(touch, "RunAsync", action, "duplicate gesture");
        Check(Field<StackPanel>(touch, "ActionsPanel").Children.OfType<Button>().All(b => !b.IsEnabled) && Field<ProgressRing>(touch, "Progress").IsActive,
            "Touch page allowed overlapping gestures");
        await first;
        Check(await SlowCommands() == before + 1 && Field<StackPanel>(touch, "ActionsPanel").Children.OfType<Button>().All(b => b.IsEnabled), "Touch action duplicated or controls stayed disabled");
        Field<NumberBox>(touch, "XBox").Value = double.NaN;
        var valid = (bool)touch.GetType().GetMethod("ValidGesture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(touch, new object[] { false })!;
        Check(!valid, "Blank/NaN gesture input accepted");
        Field<NumberBox>(touch, "XBox").Value = 640;
        first = Call(touch, "RunAsync", action, "fixture stale gesture");
        window.Navigate("home"); window.Navigate("touch");
        Field<TextBlock>(touch, "OutputText").Text = "returned-page";
        await first;
        Check(Field<TextBlock>(touch, "OutputText").Text == "returned-page", "Touch result crossed page lifecycle");
        Console.WriteLine("PASS real touch page: overlapping gestures blocked, invalid input rejected, late page output ignored");

        window.Navigate("home"); await Until(() => frame.Content is HomePage);
        var home = (HomePage)frame.Content;
        var options = session.Options! with { Key = "local-snapshot-test" };
        await session.Client.ExecAsync("fixture slow-info");
        session.Client.Disconnect();
        Field<TextBox>(home, "HostBox").Text = options.Host;
        Field<TextBox>(home, "PortBox").Text = options.Port.ToString();
        Field<PasswordBox>(home, "KeyBox").Password = options.Key;
        first = Call(home, "ConnectAsync");
        await Task.Delay(50);
        session.AddActivity("fixture notification during connect");
        await Task.Delay(20);
        Check(Field<bool>(home, "_connecting") && !Field<Button>(home, "ConnectButton").IsEnabled, "Activity re-enabled a pending connect");
        await Call(home, "ConnectAsync"); // Reentrant click must be ignored.
        await session.Client.ConnectAsync(options);
        var replacementInfo = await session.Client.GetInfoAsync();
        session.Info = replacementInfo;
        await first;
        Check(ReferenceEquals(session.Info, replacementInfo) && await session.Client.PingAsync() == "PONG" &&
            !Field<bool>(home, "_connecting") && Field<Button>(home, "ConnectButton").IsEnabled,
            "Old home attempt overwrote or disconnected replacement session");
        Check(session.Options?.Key == "", "Home session retained plaintext options key");
        Console.WriteLine("PASS real home page: connect reentry blocked, notifications preserve busy state, old attempt cannot replace or close new session");

        window.Navigate("settings"); await Until(() => frame.Content is SettingsPage);
        var settingsPage = (SettingsPage)frame.Content;
        var previous = SettingsStore.Load();
        Field<NumberBox>(settingsPage, "KnockPortBox").Value = double.NaN;
        settingsPage.GetType().GetMethod("SaveButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(settingsPage, new object[] { settingsPage, new RoutedEventArgs() });
        Check(Field<TextBlock>(settingsPage, "StatusText").Text.Contains("请输入有效") && SettingsStore.Load().KnockPort == previous.KnockPort,
            "Blank settings were persisted or reported as saved");
        Console.WriteLine("PASS real settings page rejects empty numeric inputs without overwriting saved settings");
    }
}
