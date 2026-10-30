namespace SkpdbDesktop;

public enum GamepadControlKind
{
    Tap,
    Rapid,
    Swipe,
    DPad,
    Crosshair,
    Look,
    Fire,
    RightWalk,
    Skill,
    Scroll,
    Zoom,
    Observe
}

public sealed record GamepadControlKindInfo(
    GamepadControlKind Kind,
    string Name,
    string Glyph,
    string Binding,
    GamepadButtonMode Mode,
    double Radius,
    string Hint)
{
    /// <summary>按住 / 切换 / 连发 对这类按键是否有意义。</summary>
    public bool UsesButtonMode => Kind
        is GamepadControlKind.Fire
        or GamepadControlKind.RightWalk
        or GamepadControlKind.Crosshair;

    public bool UsesInterval => Kind == GamepadControlKind.Rapid;

    public bool UsesDirection => Kind
        is GamepadControlKind.Swipe or GamepadControlKind.DPad;

    public bool UsesDistance => Kind
        is GamepadControlKind.Swipe or GamepadControlKind.DPad;

    public bool UsesDuration => Kind is GamepadControlKind.Swipe or GamepadControlKind.Scroll or GamepadControlKind.Zoom;

    public bool UsesAmount => Kind
        is GamepadControlKind.Scroll or GamepadControlKind.Zoom;

    public bool UsesWheel => Kind
        is GamepadControlKind.Scroll or GamepadControlKind.Zoom;

    /// <summary>只有视角键会在鼠标模式与视角模式之间切换。</summary>
    public bool IsModeSwitch => Kind == GamepadControlKind.Look;
}

public static class GamepadControlCatalog
{
    public static IReadOnlyList<GamepadControlKindInfo> All { get; } = new[]
    {
        new GamepadControlKindInfo(GamepadControlKind.Tap, "点击按键", "\uE8B0", "F",
            GamepadButtonMode.Hold, 0.055, "按下立即点一次屏幕"),
        new GamepadControlKindInfo(GamepadControlKind.Rapid, "连击按键", "\uE8EE", "R",
            GamepadButtonMode.Rapid, 0.055, "按住后按连发间隔连续点击"),
        new GamepadControlKindInfo(GamepadControlKind.Swipe, "滑动键位", "\uE8AB", "G",
            GamepadButtonMode.Hold, 0.055, "按下后沿方向滑动固定距离"),
        new GamepadControlKindInfo(GamepadControlKind.DPad, "方向按键", "\uE7FC", "V",
            GamepadButtonMode.Hold, 0.09, "按住并朝拖动方向持续移动"),
        new GamepadControlKindInfo(GamepadControlKind.Crosshair, "准星键", "\uE9A6", "X",
            GamepadButtonMode.Hold, 0.055, "固定位置按住，用于瞄准或蓄力"),
        new GamepadControlKindInfo(GamepadControlKind.Look, "视角移动", "\uE7C2", "Tab",
            GamepadButtonMode.Toggle, 0.055, "切换鼠标模式 / 视角模式"),
        new GamepadControlKindInfo(GamepadControlKind.Fire, "开火/攻击键", "\uE9A2", "MouseLeft",
            GamepadButtonMode.Hold, 0.055, "按住开火；也可用鼠标左键直接点击画布"),
        new GamepadControlKindInfo(GamepadControlKind.RightWalk, "右键行走", "\uE962", "MouseRight",
            GamepadButtonMode.Hold, 0.055, "按住鼠标右键对应的行走 / 瞄准"),
        new GamepadControlKindInfo(GamepadControlKind.Skill, "技能施法", "\uE945", "Q",
            GamepadButtonMode.Hold, 0.055, "按下施放一次技能"),
        new GamepadControlKindInfo(GamepadControlKind.Scroll, "滚动键", "\uE70E", "Z",
            GamepadButtonMode.Hold, 0.05, "滚轮或点击上下滚动列表"),
        new GamepadControlKindInfo(GamepadControlKind.Zoom, "缩放键", "\uE8A3", "C",
            GamepadButtonMode.Hold, 0.05, "滚轮或点击缩放地图 / 视野"),
        new GamepadControlKindInfo(GamepadControlKind.Observe, "观察键", "\uE890", "LeftAlt",
            GamepadButtonMode.Hold, 0.05, "按住观察四周，松开回到鼠标模式")
    };

    public static GamepadControlKindInfo Info(GamepadControlKind kind) =>
        All.FirstOrDefault(item => item.Kind == kind) ?? All[0];

    public static bool IsKnownName(string? name) =>
        name is not null && All.Any(item => item.Name == name);

    public static string NameOf(GamepadControlKind kind) => Info(kind).Name;
}
