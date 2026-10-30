using System.Text.Json.Serialization;

namespace SkpdbDesktop;

public enum GamepadButtonMode
{
    Hold,
    Toggle,
    Rapid
}

public sealed class GamepadMoveSettings
{
    public bool Enabled { get; set; } = true;
    public double CenterX { get; set; } = 0.18;
    public double CenterY { get; set; } = 0.76;
    public double Radius { get; set; } = 0.11;
    public double Deadzone { get; set; } = 0.12;
    public int Slot { get; set; }
    public bool Snap8 { get; set; }
    public string SprintKey { get; set; } = "LeftShift";
    public int ResponseMs { get; set; } = 110;
    public int ReleaseMs { get; set; } = 65;
    public double StartSpread { get; set; } = .55;
    public double TargetSpread { get; set; } = .08;
    public double Curvature { get; set; } = .16;
    public double Distortion { get; set; } = .045;
    public double AngularSpread { get; set; } = .10;
    public double SprintExtra { get; set; } = .35;
    public double HoldSway { get; set; } = .018;

    public GamepadMoveSettings Clone() => (GamepadMoveSettings)MemberwiseClone();
}

public sealed class GamepadLookSettings
{
    public GamepadLookSettings Clone() => (GamepadLookSettings)MemberwiseClone();
    public bool Enabled { get; set; } = true;
    public double AnchorX { get; set; } = 0.82;
    public double AnchorY { get; set; } = 0.52;
    public double MaxRadiusX { get; set; } = 0.16;
    public double MaxRadiusY { get; set; } = 0.22;
    public double Sensitivity { get; set; } = 1.6;
    public bool InvertY { get; set; }
    public double Smoothing { get; set; } = 0.25;
    public int Slot { get; set; } = 1;
    public string RecenterMode { get; set; } = "onIdle";
    public int RecenterDelayMs { get; set; } = 80;
    public int RelayOverlapMs { get; set; } = 160;
    public double RelayStartJitter { get; set; } = 0.12;
    public double RelayEarlyEnd { get; set; } = 0.14;
}

public sealed class GamepadButton
{
    public string Id { get; set; } = "button";
    public string Label { get; set; } = "按钮";
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; } = 0.06;
    public int Slot { get; set; } = 2;
    public string Binding { get; set; } = "MouseLeft";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GamepadButtonMode Mode { get; set; } = GamepadButtonMode.Hold;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GamepadControlKind Kind { get; set; } = GamepadControlKind.Tap;

    [JsonIgnore]
    public bool UsesTouchSlot => Kind is not (GamepadControlKind.Look or GamepadControlKind.Observe);

    public int TapIntervalMs { get; set; } = 80;
    public double DirectionX { get; set; }
    public double DirectionY { get; set; } = -1;
    public double Distance { get; set; } = 0.16;
    public int DurationMs { get; set; } = 120;
    public double Amount { get; set; } = 0.14;
}

public sealed class GamepadCursorSettings
{
    public bool Enabled { get; set; } = true;
    public int Size { get; set; } = 48;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool HideDuringLook { get; set; } = true;
}

public sealed class GamepadProfile
{
    public bool GyroEnabled { get; set; }
    // Two further logical IDs are reserved for look handoff and direct mouse touch.
    public const int MaxLogicalSlot = ushort.MaxValue - 2;
    public int Schema { get; set; } = 1;
    public string Package { get; set; } = GamepadProfileStore.DefaultPackage;
    public string DisplayName { get; set; } = "全局默认";
    public int ScreenWidth { get; set; } = 1280;
    public int ScreenHeight { get; set; } = 2772;
    public GamepadMoveSettings Move { get; set; } = new();
    public GamepadLookSettings Look { get; set; } = new();
    // Alternatives share their type's touch slot; only one is active per gesture.
    public List<GamepadMoveSettings> ExtraMoves { get; set; } = new();
    public List<GamepadLookSettings> ExtraLooks { get; set; } = new();
    [JsonIgnore] public IEnumerable<GamepadMoveSettings> Moves => new[] { Move }.Concat(ExtraMoves);
    [JsonIgnore] public IEnumerable<GamepadLookSettings> Looks => new[] { Look }.Concat(ExtraLooks);

    public GamepadMoveSettings ChooseMove(Random random)
    {
        var choices = Moves.Where(m => m.Enabled).ToArray();
        return choices.Length == 0 ? DisabledMove() : choices[choices.Length == 1 ? 0 : random.Next(choices.Length)];
    }
    public GamepadLookSettings ChooseLook(Random random)
    {
        var choices = Looks.Where(l => l.Enabled).ToArray();
        if (choices.Length != 0) return choices[choices.Length == 1 ? 0 : random.Next(choices.Length)];
        var disabled = Look.Clone(); disabled.Enabled = false; return disabled;
    }
    public List<GamepadButton> Buttons { get; set; } = new();
    public GamepadCursorSettings Cursor { get; set; } = new();
    public Dictionary<string, string> Bindings { get; set; } = DefaultBindings();
    public bool AutoSwitch { get; set; } = true;

    private GamepadMoveSettings DisabledMove()
    { var disabled = Move.Clone(); disabled.Enabled = false; return disabled; }

    public static Dictionary<string, string> DefaultBindings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["MoveUp"] = "W",
        ["MoveDown"] = "S",
        ["MoveLeft"] = "A",
        ["MoveRight"] = "D",
        ["Sprint"] = "LeftShift"
    };

    public GamepadProfile Clone() => new()
    {
        GyroEnabled = GyroEnabled,
        Schema = Schema,
        Package = Package,
        DisplayName = DisplayName,
        ScreenWidth = ScreenWidth,
        ScreenHeight = ScreenHeight,
        Move = Move.Clone(),
        Look = Look.Clone(),
        ExtraMoves = ExtraMoves.Select(m => m.Clone()).ToList(),
        ExtraLooks = ExtraLooks.Select(l => l.Clone()).ToList(),
        Buttons = Buttons.Select(button => new GamepadButton
        {
            Id = button.Id,
            Label = button.Label,
            X = button.X,
            Y = button.Y,
            Radius = button.Radius,
            Slot = button.Slot,
            Binding = button.Binding,
            Mode = button.Mode,
            Kind = button.Kind,
            TapIntervalMs = button.TapIntervalMs,
            DirectionX = button.DirectionX,
            DirectionY = button.DirectionY,
            Distance = button.Distance,
            DurationMs = button.DurationMs,
            Amount = button.Amount
        }).ToList(),
        Cursor = new GamepadCursorSettings
        {
            Enabled = Cursor.Enabled,
            Size = Cursor.Size,
            OffsetX = Cursor.OffsetX,
            OffsetY = Cursor.OffsetY,
            HideDuringLook = Cursor.HideDuringLook
        },
        Bindings = new Dictionary<string, string>(Bindings, StringComparer.OrdinalIgnoreCase),
        AutoSwitch = AutoSwitch
    };

    public void Validate()
    {
        if (Schema != 1)
        {
            throw new InvalidOperationException($"不支持的配置版本: {Schema}");
        }
        if (string.IsNullOrWhiteSpace(Package))
        {
            throw new InvalidOperationException("配置缺少包名");
        }
        if (ScreenWidth <= 0 || ScreenHeight <= 0)
        {
            throw new InvalidOperationException("屏幕尺寸无效");
        }

        if (Move is null || Look is null || Cursor is null || Buttons is null ||
            ExtraMoves is null || ExtraLooks is null || Bindings is null ||
            ExtraMoves.Any(m => m is null) || ExtraLooks.Any(l => l is null) || Buttons.Any(b => b is null))
            throw new InvalidOperationException("布局包含空组件或缺失字段");
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, binding) in Bindings)
            if (!bindings.TryAdd(name, binding) || string.IsNullOrWhiteSpace(binding))
                throw new InvalidOperationException("方向绑定为空或名称重复：" + name);
        Bindings = bindings;

        if (Move.Slot < 0 || Move.Slot > MaxLogicalSlot || Look.Slot < 0 || Look.Slot > MaxLogicalSlot)
            throw new InvalidOperationException("轮盘或视角逻辑编号无效");
        var slots = new HashSet<int>();
        if (Moves.Any(m => m.Enabled)) AddSlot(slots, Move.Slot, "移动轮盘");
        if (Looks.Any(l => l.Enabled)) AddSlot(slots, Look.Slot, "视角区域");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in Buttons)
        {
            if (string.IsNullOrWhiteSpace(button.Id) || string.IsNullOrWhiteSpace(button.Binding))
            {
                throw new InvalidOperationException("按钮配置缺少 id 或绑定按键");
            }
            if (!ids.Add(button.Id)) throw new InvalidOperationException("按钮 id 重复：" + button.Id);
            if (!Enum.IsDefined(button.Kind) || !Enum.IsDefined(button.Mode))
                throw new InvalidOperationException("按钮类型或模式无效：" + button.Id);
            if (!double.IsFinite(button.DirectionX) || !double.IsFinite(button.DirectionY))
                throw new InvalidOperationException("按钮方向包含非有限数值：" + button.Id);
            AddSlot(slots, button.Slot, "按钮 " + button.Label);
            button.X = ClampFinite(button.X, 0, 1);
            button.Y = ClampFinite(button.Y, 0, 1);
            button.Radius = ClampFinite(button.Radius, 0.01, 0.3);
            button.TapIntervalMs = Math.Clamp(button.TapIntervalMs, 16, 2000);
            button.Distance = ClampFinite(button.Distance, 0.02, 0.5);
            button.DurationMs = Math.Clamp(button.DurationMs, 30, 1000);
            button.Amount = ClampFinite(button.Amount, 0.02, 0.5);
            var length = Math.Sqrt(button.DirectionX * button.DirectionX +
                                   button.DirectionY * button.DirectionY);
            if (length > 0.001)
            {
                button.DirectionX /= length;
                button.DirectionY /= length;
            }
        }

        var modeKeys = Buttons.Where(b => b.Kind is GamepadControlKind.Look or GamepadControlKind.Observe).ToArray();
        foreach (var mode in modeKeys)
        {
            if (SampledInputState.IsMouse(mode.Binding))
                throw new InvalidOperationException("视角切换和观察请绑定键盘按键；鼠标模式下鼠标键只用于直接触摸。");
            var key = SampledInputState.Canonical(mode.Binding);
            if (Buttons.Any(b => b != mode && b.Kind != mode.Kind &&
                    string.Equals(key, SampledInputState.Canonical(b.Binding), StringComparison.OrdinalIgnoreCase)) ||
                (Moves.Any(m => m.Enabled) && Bindings.Values.Any(b =>
                    string.Equals(key, SampledInputState.Canonical(b), StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException($"视角/观察键 {mode.Binding} 与其它操控冲突，请使用独立按键。");
        }
        if (Buttons.Any(b => SampledInputState.Canonical(b.Binding).Equals("Escape", StringComparison.OrdinalIgnoreCase)) ||
            Bindings.Values.Any(b => SampledInputState.Canonical(b).Equals("Escape", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Esc 用于退出视角和停止操控，请勿用作触摸绑定。");

        foreach (var alternative in ExtraMoves) alternative.Slot = Move.Slot;
        foreach (var alternative in ExtraLooks) alternative.Slot = Look.Slot;
        foreach (var move in Moves)
        {
            move.CenterX = ClampFinite(move.CenterX, 0, 1);
            move.CenterY = ClampFinite(move.CenterY, 0, 1);
            move.Radius = ClampFinite(move.Radius, 0.02, 0.35);
            move.Deadzone = ClampFinite(move.Deadzone, 0, 0.9);
            move.ResponseMs = Math.Clamp(move.ResponseMs, 40, 300);
            move.ReleaseMs = Math.Clamp(move.ReleaseMs, 16, 180);
            move.StartSpread = ClampFinite(move.StartSpread, 0, .95);
            move.TargetSpread = ClampFinite(move.TargetSpread, 0, .20);
            move.Curvature = ClampFinite(move.Curvature, 0, .40);
            move.Distortion = ClampFinite(move.Distortion, 0, .15);
            move.AngularSpread = ClampFinite(move.AngularSpread, 0, .35);
            move.SprintExtra = ClampFinite(move.SprintExtra, 0, 1);
            move.HoldSway = ClampFinite(move.HoldSway, 0, .08);
        }
        foreach (var look in Looks)
        {
            look.AnchorX = ClampFinite(look.AnchorX, 0, 1);
            look.AnchorY = ClampFinite(look.AnchorY, 0, 1);
            look.MaxRadiusX = ClampFinite(look.MaxRadiusX, 0.01, 0.5);
            look.MaxRadiusY = ClampFinite(look.MaxRadiusY, 0.01, 0.5);
            look.Sensitivity = ClampFinite(look.Sensitivity, 0.1, 12);
            look.Smoothing = ClampFinite(look.Smoothing, 0, 0.95);
            look.RecenterDelayMs = Math.Clamp(look.RecenterDelayMs, 20, 1000);
            look.RelayOverlapMs = Math.Clamp(look.RelayOverlapMs, 40, 600);
            look.RelayStartJitter = ClampFinite(look.RelayStartJitter, 0, 0.25);
            look.RelayEarlyEnd = ClampFinite(look.RelayEarlyEnd, 0, 0.30);
        }
        Cursor.Size = Math.Clamp(Cursor.Size, 16, 128);
    }

    private static void AddSlot(HashSet<int> slots, int slot, string label)
    {
        if (slot < 0 || slot > MaxLogicalSlot)
        {
            throw new InvalidOperationException($"{label} 的逻辑编号超出 0..{MaxLogicalSlot}");
        }
        if (!slots.Add(slot))
        {
            throw new InvalidOperationException($"{label} 与其它元素使用了重复逻辑编号 {slot}");
        }
    }

    private static double ClampFinite(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("布局包含无效数值（NaN 或无穷大）");
        return Math.Clamp(value, minimum, maximum);
    }
}
