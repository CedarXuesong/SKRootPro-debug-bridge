using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace SkpdbDesktop;

public sealed class TouchTraceView : UserControl
{
    private sealed class Contact
    {
        public readonly List<Point> Points = new();
        public required ushort Slot;
        public Point Start;
        public bool Active;
        public double LastTime;
    }
    private readonly List<Contact> _contacts = new();
    private readonly Dictionary<ushort, Contact> _active = new();
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(255, 20, 23, 29)) };
    private readonly TextBlock _label = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _root;
    private bool _overlay;
    private Rect _screenRect;
    private int _width = 1280, _height = 2772;
    private GamepadLookSettings? _look;
    private GamepadMoveSettings? _move;
    private HashSet<ushort>? _slots;
    private bool _zoom;
    private int _downs, _ups;
    private double _lastTime;
    private static readonly Color[] Palette = [Colors.DeepSkyBlue, Colors.Orange, Colors.LightGreen, Colors.Violet, Colors.Gold, Colors.Cyan];
    public TouchTraceView()
    {
        var root = new Grid { RowSpacing = 5, MinHeight = 150 };
        _root = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_label); root.Children.Add(_canvas); Grid.SetRow(_canvas, 1);
        Content = root;
        _canvas.SizeChanged += (_, _) => Render();
        _label.Text = "采样轨迹 · 等待输入";
    }
    public void Configure(GamepadLookSettings? look, bool zoom)
    { _look = look; _move = null; _slots = null; _zoom = zoom; Clear(); }

    public void ConfigureCanvas(TouchDeviceInfo device)
    {
        _width = device.DisplayWidth; _height = device.DisplayHeight;
        _overlay = true; _move = null; _look = null; _slots = null;
        _root.MinHeight = 0; _root.RowSpacing = 0; _label.Visibility = Visibility.Collapsed;
        _canvas.Background = new SolidColorBrush(Colors.Transparent);
        Clear();
    }

    public void SetScreenRect(Rect rect) { _screenRect = rect; Render(); }

    public void ConfigureMove(TouchDeviceInfo device, GamepadMoveSettings move)
    {
        _width = device.DisplayWidth; _height = device.DisplayHeight;
        _move = move.Clone(); _look = null; _zoom = true;
        _slots = new() { (ushort)move.Slot }; Clear();
    }

    public void ConfigureLook(TouchDeviceInfo device, GamepadProfile profile)
    {
        _width = device.DisplayWidth; _height = device.DisplayHeight;
        _look = profile.Clone().Look; _move = null; _zoom = true;
        // Use the production allocator, including fully occupied layouts.
        var relay = new LookTouchRelay(); relay.Configure(device, profile);
        _slots = new() { relay.ActiveSlot, relay.SpareSlot }; Clear();
    }
    public void Clear()
    { _contacts.Clear(); _active.Clear(); _downs = _ups = 0; _lastTime = 0; Render(); }

    public void Append(IEnumerable<TouchTracePoint> points)
    {
        foreach (var item in points)
        {
            if (_slots is not null && !_slots.Contains(item.Slot)) continue;
            if (_width != item.Width || _height != item.Height)
            { _width = item.Width; _height = item.Height; _contacts.Clear(); _active.Clear(); }
            _lastTime = item.TimeMs;
            if (item.Action == 3)
            {
                if (_active.Remove(item.Slot, out var lifted)) { lifted.Active = false; lifted.LastTime = item.TimeMs; _ups++; }
                continue;
            }
            if (item.Action == 1 || !_active.TryGetValue(item.Slot, out _))
            {
                if (_active.Remove(item.Slot, out var previous)) previous.Active = false;
                var contact = new Contact { Slot = item.Slot, Active = true, LastTime = item.TimeMs,
                    Start = new Point(item.X, item.Y) };
                _contacts.Add(contact); _active[item.Slot] = contact; _downs++;
            }
            var current = _active[item.Slot];
            var p = new Point(item.X, item.Y);
            if (current.Points.Count == 0 || Math.Abs(current.Points[^1].X - p.X) + Math.Abs(current.Points[^1].Y - p.Y) >= .5)
            {
                current.Points.Add(p);
                if (current.Points.Count > 900) current.Points.RemoveAt(0);
            }
            current.LastTime = item.TimeMs;
        }
        _contacts.RemoveAll(c => !c.Active && _lastTime - c.LastTime > 4500);
        while (_contacts.Count > 80)
        {
            var old = _contacts.FirstOrDefault(c => !c.Active);
            if (old is null) break;
            _contacts.Remove(old);
        }
        Render();
    }

    private void Render()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth; var h = _canvas.ActualHeight;
        if (w < 20 || h < 20) return;
        Rect viewport = new(0, 0, _width, _height);
        if (_zoom && _look is { } look)
        {
            var size = Math.Min(Math.Max(look.MaxRadiusX * _width, look.MaxRadiusY * _height) * 2.35, Math.Max(_width, _height));
            viewport = new Rect(Math.Clamp(look.AnchorX * _width - size / 2, 0, Math.Max(0, _width - size)),
                Math.Clamp(look.AnchorY * _height - size / 2, 0, Math.Max(0, _height - size)), Math.Min(size, _width), Math.Min(size, _height));
        }
        if (_move is { } move)
        {
            var radius = MoveRadius(move);
            var size = Math.Max(10, radius * 3.1);
            viewport = new Rect(move.CenterX * (_width - 1) - size / 2,
                move.CenterY * (_height - 1) - size / 2, size, size);
        }
        var scale = _overlay ? _screenRect.Width / _width : Math.Min((w - 18) / viewport.Width, (h - 18) / viewport.Height);
        var ox = _overlay ? _screenRect.X : (w - viewport.Width * scale) / 2;
        var oy = _overlay ? _screenRect.Y : (h - viewport.Height * scale) / 2;
        if (scale <= 0) return;
        Point Map(Point p) => new(ox + (p.X - viewport.X) * scale, oy + (p.Y - viewport.Y) * scale);
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
        if (_move is { } stick)
        {
            var center = Map(new Point(stick.CenterX * (_width - 1), stick.CenterY * (_height - 1)));
            var radius = MoveRadius(stick) * scale;
            Ring(center, radius * (1 + stick.SprintExtra), Colors.SlateGray);
            Ring(center, radius, Colors.SteelBlue);
            Ring(center, radius * stick.Deadzone, Colors.SlateGray);
        }
        if (_look is { } region)
        {
            var a = Map(new Point((region.AnchorX - region.MaxRadiusX) * _width, (region.AnchorY - region.MaxRadiusY) * _height));
            var rect = new Rectangle { Width = 2 * region.MaxRadiusX * _width * scale, Height = 2 * region.MaxRadiusY * _height * scale,
                Stroke = new SolidColorBrush(Colors.SlateGray), StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(20, 70, 140, 220)) };
            Canvas.SetLeft(rect, a.X); Canvas.SetTop(rect, a.Y); _canvas.Children.Add(rect);
        }
        foreach (var contact in _contacts)
        {
            if (contact.Points.Count == 0) continue;
            var color = Palette[contact.Slot % Palette.Length];
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = contact.Active ? 2 : 1.3,
                Opacity = contact.Active ? 1 : .55 };
            foreach (var p in contact.Points) line.Points.Add(Map(p));
            _canvas.Children.Add(line);
            Dot(Map(contact.Start), Colors.LightGreen, 5);
            var end = Map(contact.Points[^1]);
            Dot(end, contact.Active ? Colors.White : Colors.Orange, contact.Active ? 10 : 5);
            if (contact.Active)
            {
                var label = new TextBlock { Text = "slot " + contact.Slot, Foreground = new SolidColorBrush(color), FontSize = 11 };
                Canvas.SetLeft(label, Math.Clamp(end.X + 7, 0, Math.Max(0, w - 45)));
                Canvas.SetTop(label, Math.Clamp(end.Y - 17, 0, h - 15)); _canvas.Children.Add(label);
            }
        }
        _label.Text = $"当前 {_active.Count} 指 · 按下 {_downs} · 抬起 {_ups}" +
            (_look is not null ? $" · 接力 {(_active.Count > 1 ? "分担中" : "—")}" : "");
    }
    private double MoveRadius(GamepadMoveSettings move)
    {
        return SampledJoystick.FittedRadius(move, _width, _height);
    }
    private void Ring(Point center, double radius, Color color)
    {
        var ring = new Ellipse { Width = radius * 2, Height = radius * 2,
            Stroke = new SolidColorBrush(color), StrokeThickness = 1 };
        Canvas.SetLeft(ring, center.X - radius); Canvas.SetTop(ring, center.Y - radius);
        _canvas.Children.Add(ring);
    }
    private void Dot(Point p, Color color, double size)
    {
        var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(dot, p.X - size / 2); Canvas.SetTop(dot, p.Y - size / 2); _canvas.Children.Add(dot);
    }
}
