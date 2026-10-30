using System.Globalization;
using System.Text.RegularExpressions;

namespace SkpdbDesktop;

public sealed record TouchDeviceInfo(
    string Name,
    string EventPath,
    string InputDevice,
    int ScreenWidth,
    int ScreenHeight,
    int SlotMax,
    bool HasButtonTouch,
    bool HasButtonToolFinger)
{
    private static readonly Regex ViewportPattern = new(
        @"(?:Viewport\s+INTERNAL|DisplayViewport\{type=INTERNAL)[^\n]*?orientation=(?<rotation>\d+)[^\n]*?logicalFrame=Rect\(0,\s*0\s*-\s*(?<width>\d+),\s*(?<height>\d+)\)",
        RegexOptions.Compiled);

    public int Rotation { get; init; }
    public int LogicalWidth { get; init; } = ScreenWidth;
    public int LogicalHeight { get; init; } = ScreenHeight;

    public int DisplayWidth => LogicalWidth > 0 ? LogicalWidth : ScreenWidth;
    public int DisplayHeight => LogicalHeight > 0 ? LogicalHeight : ScreenHeight;
    public bool IsLandscape => Rotation == 1 || Rotation == 3;

    public TouchDeviceInfo WithDisplay(int rotation, int width, int height) => this with
    {
        Rotation = ((rotation % 4) + 4) % 4,
        LogicalWidth = width > 0 ? width : ScreenWidth,
        LogicalHeight = height > 0 ? height : ScreenHeight
    };

    /// <summary>
    /// Converts a point in the current Android logical display to the physical
    /// touchscreen pixel space expected by the module. Android's input mapper
    /// keeps the panel in its natural orientation, so a rotated display needs
    /// the inverse display transform before injection.
    /// </summary>
    public (int X, int Y) ToNative(int logicalX, int logicalY)
    {
        var x = Math.Clamp(logicalX, 0, Math.Max(0, DisplayWidth - 1));
        var y = Math.Clamp(logicalY, 0, Math.Max(0, DisplayHeight - 1));
        var u = x / (double)Math.Max(1, DisplayWidth - 1);
        var v = y / (double)Math.Max(1, DisplayHeight - 1);
        var nativeX = Rotation switch
        {
            1 => 1 - v,
            2 => 1 - u,
            3 => v,
            _ => u
        };
        var nativeY = Rotation switch
        {
            1 => u,
            2 => 1 - v,
            3 => 1 - u,
            _ => v
        };
        return ((int)Math.Round(nativeX * Math.Max(0, ScreenWidth - 1)),
            (int)Math.Round(nativeY * Math.Max(0, ScreenHeight - 1)));
    }

    public (int X, int Y) ToLogical(int nativeX, int nativeY)
    {
        var u = Math.Clamp(nativeX, 0, Math.Max(0, ScreenWidth - 1)) / (double)Math.Max(1, ScreenWidth - 1);
        var v = Math.Clamp(nativeY, 0, Math.Max(0, ScreenHeight - 1)) / (double)Math.Max(1, ScreenHeight - 1);
        var point = Rotation switch
        {
            1 => (X: v, Y: 1 - u),
            2 => (X: 1 - u, Y: 1 - v),
            3 => (X: 1 - v, Y: u),
            _ => (X: u, Y: v)
        };
        return ((int)Math.Round(point.X * Math.Max(0, DisplayWidth - 1)),
            (int)Math.Round(point.Y * Math.Max(0, DisplayHeight - 1)));
    }

    public static (int Rotation, int Width, int Height)? ParseDisplay(string text)
    {
        var match = ViewportPattern.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["rotation"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var rotation) ||
            !int.TryParse(match.Groups["width"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(match.Groups["height"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }
        return (rotation, width, height);
    }
    public static TouchDeviceInfo Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        if (!string.Equals(Get(values, "touch"), "ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("触摸屏尚未就绪: " + text.Trim());
        }

        var screen = Get(values, "screen");
        var dimensions = screen.Split('x');
        if (dimensions.Length != 2 ||
            !int.TryParse(dimensions[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(dimensions[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("无法解析触摸屏尺寸: " + screen);
        }

        var slots = Get(values, "slots");
        var range = slots.Split("..");
        if (range.Length != 2 ||
            !int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotMax) ||
            slotMax < 0)
        {
            throw new InvalidOperationException("无法解析触摸 slot 范围: " + slots);
        }

        return new TouchDeviceInfo(
            Get(values, "name"),
            Get(values, "event"),
            Get(values, "input_dev"),
            width,
            height,
            slotMax,
            Get(values, "btn_touch") == "1",
            Get(values, "btn_tool_finger") == "1");
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;
}
