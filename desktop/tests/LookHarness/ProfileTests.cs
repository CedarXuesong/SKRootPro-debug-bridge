using System.Reflection;
using System.Text.Json.Nodes;
using SkpdbDesktop;

internal static class ProfileTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    public static void Run()
    {
        var profile = GamepadProfileStore.CreateDefaultProfile();
        profile.Buttons[0].Slot = -1;
        Reject(profile.Validate, "Negative slot accepted");
        profile = GamepadProfileStore.CreateDefaultProfile();
        profile.Buttons[1].Id = "FIRE";
        Reject(profile.Validate, "Duplicate ID accepted");
        profile = GamepadProfileStore.CreateDefaultProfile(); profile.Move.Curvature = double.NaN;
        Reject(profile.Validate, "NaN accepted");
        profile = GamepadProfileStore.CreateDefaultProfile(); profile.Look.MaxRadiusX = double.PositiveInfinity;
        Reject(profile.Validate, "Infinity accepted");
        profile = GamepadProfileStore.CreateDefaultProfile(); profile.Buttons[0].Kind = (GamepadControlKind)100;
        Reject(profile.Validate, "Unknown kind accepted");
        profile = GamepadProfileStore.CreateDefaultProfile(); profile.Buttons[0].Binding = "Tab";
        Reject(profile.Validate, "Mode binding conflict accepted");
        profile = GamepadProfileStore.CreateDefaultProfile(); profile.Buttons[2].Binding = "MouseRight";
        Reject(profile.Validate, "Unreachable mouse-mode switch accepted");
        Console.WriteLine("PASS invalid slots, duplicate IDs, nonfinite motion and unknown control kinds are rejected");

        var field = typeof(AppPaths).GetField("_dataDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = field.GetValue(null);
        var directory = Path.GetFullPath(".scratch/profile-regression-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(directory);
        field.SetValue(null, directory);
        try
        {
            var settings = new DesktopSettings { Host = "127.0.0.1" };
            Check(SettingsStore.Save(settings), "Settings save failed");
            var settingsPath = Path.Combine(directory, "desktop.json");
            var settingsText = File.ReadAllText(settingsPath);
            using (var blocked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                settings.Host = "changed";
                Check(!SettingsStore.Save(settings) && SettingsStore.LastError is not null, "Locked settings save claimed success");
            }
            Check(File.ReadAllText(settingsPath) == settingsText && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "Failed settings save changed the file or left temporary files");
            settings.KnockPort = -1;
            Check(!SettingsStore.Save(settings) && File.ReadAllText(settingsPath) == settingsText, "Invalid settings replaced valid settings");
            File.WriteAllText(settingsPath, "{\"Port\":-1,\"KnockPort\":0,\"TimeoutSeconds\":999,\"Host\":null}");
            settings = SettingsStore.Load();
            Check(settings.Port == 39876 && settings.KnockPort == 39877 && settings.TimeoutSeconds == 120 && settings.Host == "",
                "Malformed setting fields did not recover valid limits");
            Console.WriteLine("PASS settings report save failure, preserve prior file without temporary leaks and recover invalid loaded fields");

            var current = GamepadProfileStore.CreateDefaultProfile();
            current.Buttons[0].Kind = GamepadControlKind.Tap;
            current.Buttons[0].Y = .82; // Explicit current layout must not be migrated.
            var collection = new GamepadProfileCollection { Profiles = new() { current }, CursorEnabled = false };
            Check(GamepadProfileStore.Save(collection), "Initial save failed");
            var saved = File.ReadAllText(GamepadProfileStore.FilePath);
            var loaded = GamepadProfileStore.Load();
            var actual = GamepadProfileStore.Find(loaded, GamepadProfileStore.DefaultPackage)!;
            Check(actual.Buttons[0].Kind == GamepadControlKind.Tap && actual.Buttons[0].Y == .82 && !loaded.CursorEnabled,
                "Current explicit kind/layout or global cursor setting was changed on load");
            current.Buttons[0].Slot = -1;
            Check(!GamepadProfileStore.Save(collection) && File.ReadAllText(GamepadProfileStore.FilePath) == saved,
                "Failed save damaged the previous configuration");
            Check(!Directory.GetFiles(GamepadProfileStore.DirectoryPath, "*.tmp").Any(), "Temporary save files leaked");
            Console.WriteLine("PASS atomic save preserves old configuration on failure and explicit current control kinds/layouts round-trip unchanged");

            var root = JsonNode.Parse(saved)!.AsObject();
            var valid = root["profiles"]![0]!.DeepClone();
            var invalid = valid.DeepClone(); invalid["package"] = "invalid.app"; invalid["buttons"]![0]!["slot"] = -1;
            var legacy = valid.DeepClone(); legacy["package"] = "legacy.app";
            foreach (var button in legacy["buttons"]!.AsArray()) button!.AsObject().Remove("kind");
            root["profiles"]!.AsArray().Add(invalid);
            root["profiles"]!.AsArray().Add(legacy);
            File.WriteAllText(GamepadProfileStore.FilePath, root.ToJsonString());
            loaded = GamepadProfileStore.Load();
            Check(loaded.Profiles.Count == 2 && GamepadProfileStore.LastError is not null,
                "One malformed profile discarded other valid layouts or failed silently");
            Check(GamepadProfileStore.Find(loaded, "legacy.app")!.Buttons[0].Kind == GamepadControlKind.Fire,
                "Legacy missing-kind migration failed");
            Check(Directory.GetFiles(GamepadProfileStore.DirectoryPath, "*.broken").Length == 1,
                "Original malformed file was not backed up");
            Console.WriteLine("PASS partial recovery keeps valid profiles, reports malformed layouts and migrates only missing legacy fields");
        }
        finally { field.SetValue(null, original); }
    }
}
