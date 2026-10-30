using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkpdbDesktop;

public sealed class GamepadProfileCollection
{
    public int Schema { get; set; } = 1;
    public List<GamepadProfile> Profiles { get; set; } = new();
    public string LastPackage { get; set; } = GamepadProfileStore.DefaultPackage;
    // Global switch, not per-profile: turning the cursor off must stick when
    // the foreground package and its profile change.
    public bool CursorEnabled { get; set; } = true;
}

public static class GamepadProfileStore
{
    public const string DefaultPackage = "__default__";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DirectoryPath => Path.Combine(AppPaths.DataDirectory, "gamepad");

    public static string FilePath => Path.Combine(DirectoryPath, "profiles.json");

    public static string? LastError { get; private set; }

    public static string SerializeProfile(GamepadProfile profile) =>
        JsonSerializer.Serialize(profile, JsonOptions);

    public static GamepadProfile? DeserializeProfile(string json) =>
        JsonSerializer.Deserialize<GamepadProfile>(json, JsonOptions);

    public static GamepadProfile CreateDefaultProfile(string package = DefaultPackage,
        string displayName = "全局默认")
    {
        var profile = new GamepadProfile
        {
            Package = package,
            DisplayName = displayName,
            Buttons =
            {
                new GamepadButton
                {
                    Id = "fire",
                    Label = "开火",
                    X = 0.84,
                    Y = 0.90,
                    Radius = 0.06,
                    Slot = 2,
                    Binding = "MouseLeft",
                    Mode = GamepadButtonMode.Hold,
                    Kind = GamepadControlKind.Fire
                },
                new GamepadButton
                {
                    Id = "jump",
                    Label = "跳跃",
                    X = 0.93,
                    Y = 0.80,
                    Radius = 0.055,
                    Slot = 3,
                    Binding = "Space",
                    Mode = GamepadButtonMode.Hold,
                    Kind = GamepadControlKind.Tap
                },
                new GamepadButton
                {
                    // Mode switch: while the engine runs this toggles between
                    // moving the on-screen cursor and turning the camera.
                    Id = "viewmode",
                    Label = "视角",
                    X = 0.62,
                    Y = 0.90,
                    Radius = 0.055,
                    Slot = 4,
                    Binding = "Tab",
                    Mode = GamepadButtonMode.Toggle,
                    Kind = GamepadControlKind.Look
                }
            }
        };
        profile.Validate();
        return profile;
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static void UpgradeLegacy(GamepadProfile profile, JsonElement source)
    {
        var buttons = Property(source, "buttons");
        if (buttons.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        var allLegacy = profile.Buttons.Count > 0;
        foreach (var raw in buttons.EnumerateArray())
        {
            var button = profile.Buttons[index++];
            var legacy = Property(raw, "kind").ValueKind == JsonValueKind.Undefined;
            allLegacy &= legacy;
            if (legacy && (button.Id == "fire" || button.Binding.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase)))
                button.Kind = GamepadControlKind.Fire;
        }
        if (allLegacy) UpgradeLegacyLayout(profile);
    }

    private static void UpgradeLegacyLayout(GamepadProfile profile)
    {
        // The first landscape build put jump inside the look rectangle. Only
        // migrate untouched legacy defaults so hand-edited layouts survive.
        foreach (var button in profile.Buttons)
        {
            if (button.Id == "fire" && Math.Abs(button.X - 0.84) < 0.001 &&
                Math.Abs(button.Y - 0.82) < 0.001)
            {
                button.Y = 0.90;
            }
            else if (button.Id == "jump" && Math.Abs(button.X - 0.90) < 0.001 &&
                     Math.Abs(button.Y - 0.72) < 0.001)
            {
                button.X = 0.93;
                button.Y = 0.80;
            }
        }
    }

    public static GamepadProfileCollection Load()
    {
        LastError = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return WithDefault(new GamepadProfileCollection());
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            var schema = Property(root, "schema");
            if (root.ValueKind != JsonValueKind.Object || (schema.ValueKind != JsonValueKind.Undefined && schema.GetInt32() != 1))
                throw new InvalidOperationException("不支持的布局集合格式或版本");
            var collection = new GamepadProfileCollection();
            var lastPackage = Property(root, "lastPackage");
            if (lastPackage.ValueKind == JsonValueKind.String) collection.LastPackage = lastPackage.GetString() ?? DefaultPackage;
            var cursorEnabled = Property(root, "cursorEnabled");
            if (cursorEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False) collection.CursorEnabled = cursorEnabled.GetBoolean();
            var profiles = Property(root, "profiles");
            var errors = new List<string>();
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profiles.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("布局集合缺少 profiles 数组");
            foreach (var raw in profiles.EnumerateArray())
            {
                try
                {
                    var profile = raw.Deserialize<GamepadProfile>(JsonOptions) ?? throw new InvalidOperationException("空布局");
                    UpgradeLegacy(profile, raw);
                    profile.Validate();
                    if (!packages.Add(profile.Package)) throw new InvalidOperationException("重复包名：" + profile.Package);
                    collection.Profiles.Add(profile);
                }
                catch (Exception error) { errors.Add(error.Message); }
            }
            if (errors.Count != 0)
            {
                LastError = string.Join("；", errors);
                TryBackupBrokenFile();
            }
            return WithDefault(collection);
        }
        catch (Exception error)
        {
            LastError = error.Message;
            TryBackupBrokenFile();
            return WithDefault(new GamepadProfileCollection());
        }
    }

    public static bool Save(GamepadProfileCollection collection)
    {
        LastError = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var profile in collection.Profiles)
            {
                profile.Validate();
            }
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(collection, JsonOptions));
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
    }

    public static GamepadProfile? Find(GamepadProfileCollection collection, string? package)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return collection.Profiles.FirstOrDefault(p => p.Package == DefaultPackage);
        }
        return collection.Profiles.FirstOrDefault(p =>
                   string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase))
               ?? collection.Profiles.FirstOrDefault(p => p.Package == DefaultPackage);
    }

    public static GamepadProfile GetOrCreate(GamepadProfileCollection collection, string package)
    {
        var existing = collection.Profiles.FirstOrDefault(p =>
            string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var profile = CreateDefaultProfile(package, package);
        collection.Profiles.Add(profile);
        return profile;
    }

    public static void Upsert(GamepadProfileCollection collection, GamepadProfile profile)
    {
        profile.Validate();
        var index = collection.Profiles.FindIndex(p =>
            string.Equals(p.Package, profile.Package, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            collection.Profiles[index] = profile;
        }
        else
        {
            collection.Profiles.Add(profile);
        }
    }

    public static bool Delete(GamepadProfileCollection collection, string package)
    {
        if (string.Equals(package, DefaultPackage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var removed = collection.Profiles.RemoveAll(p =>
            string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    public static void EnsureDefault(GamepadProfileCollection collection)
    {
        if (collection.Profiles.All(p => !p.Package.Equals(DefaultPackage, StringComparison.OrdinalIgnoreCase)))
        {
            collection.Profiles.Insert(0, CreateDefaultProfile());
        }
    }

    private static GamepadProfileCollection WithDefault(GamepadProfileCollection collection)
    {
        EnsureDefault(collection);
        collection.Profiles = collection.Profiles
            .OrderBy(p => p.Package == DefaultPackage ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return collection;
    }

    private static void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "." + Guid.NewGuid().ToString("N")[..8] + ".broken");
            }
        }
        catch
        {
            // 备份失败不改变回退逻辑。
        }
    }
}
