using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace SkpdbDesktop;

public sealed partial class GamepadPage
{
    private readonly Dictionary<NumberBox, Slider> _numericSliders = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _propertySaveTimer;
    private bool _propertySavePending, _syncNumeric, _syncElementPicker;
    private object? _lastPropertySource, _dragPropertySource;
    private DateTime _lastPropertyAt;
    private string _elementPickerSignature = "";
    private sealed record EditorElementChoice(string Label, string Kind, string? Id);
    private readonly struct EditorUiUpdate : IDisposable
    {
        private readonly GamepadPage _page;
        private readonly bool _previous;
        public EditorUiUpdate(GamepadPage page) { _page = page; _previous = page._updatingUi; page._updatingUi = true; }
        public void Dispose() => _page._updatingUi = _previous;
    }

    private void InitializeEditor()
    {
        _propertySaveTimer = DispatcherQueue.CreateTimer();
        _propertySaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _propertySaveTimer.IsRepeating = false;
        _propertySaveTimer.Tick += (_, _) => SavePendingProperties();
        foreach (var box in new[] { XBox, YBox, RadiusBox, DeadzoneBox, MoveResponseBox, LookWidthBox,
            LookRadiusYBox, SensitivityBox, SmoothingBox, RecenterDelayBox, RelayOverlapBox,
            RelayStartJitterBox, RelayEarlyEndBox, TapIntervalBox, DirectionXBox, DirectionYBox,
            DistanceBox, DurationBox, AmountBox }) AttachNumericSlider(box);
        foreach (var slider in new[] { MoveStartBox, MoveTargetBox, MoveAngularBox, MoveCurveBox,
            MoveDistortionBox, MoveSwayBox, SprintExtraBox })
        {
            TrackSliderGesture(slider, slider);
            slider.ValueChanged += (_, _) => PreviewPropertyChange(slider);
        }
        Snap8Toggle.Toggled += (_, _) => PreviewPropertyChange(Snap8Toggle);
        InvertXToggle.Toggled += (_, _) => PreviewPropertyChange(InvertXToggle);
        InvertYToggle.Toggled += (_, _) => PreviewPropertyChange(InvertYToggle);
        RecenterBox.SelectionChanged += (_, _) => PreviewPropertyChange(RecenterBox);
        ButtonModeBox.SelectionChanged += (_, _) => PreviewPropertyChange(ButtonModeBox);
        LabelBox.LostFocus += (_, _) => PreviewPropertyChange(LabelBox);
        BindingBox.LostFocus += (_, _) => PreviewPropertyChange(BindingBox);
    }

    private void AttachNumericSlider(NumberBox box)
    {
        // Collapsed/expander content may not have a visual Parent until layout.
        var parent = box.Parent as Panel ?? FindEditorParent(PageRoot, box);
        if (parent is null) throw new InvalidOperationException("无法定位数值编辑器：" + box.Name);
        var index = parent.Children.IndexOf(box);
        var wrapper = new StackPanel { Spacing = 2, Visibility = box.Visibility };
        Grid.SetRow(wrapper, Grid.GetRow(box)); Grid.SetColumn(wrapper, Grid.GetColumn(box));
        Grid.SetRowSpan(wrapper, Grid.GetRowSpan(box)); Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(box));
        parent.Children.RemoveAt(index); parent.Children.Insert(index, wrapper);
        wrapper.Children.Add(box);
        var step = box == XBox || box == YBox || box == RadiusBox ? .001 :
            box == SensitivityBox || box == SmoothingBox || box == RelayStartJitterBox || box == RelayEarlyEndBox ? .01 : box.SmallChange;
        var slider = new Slider { Minimum = box.Minimum, Maximum = box.Maximum,
            StepFrequency = step, SmallChange = step, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, box.Header + " 滑块");
        wrapper.Children.Add(slider); _numericSliders.Add(box, slider);
        box.RegisterPropertyChangedCallback(NumberBox.MinimumProperty, (_, _) => slider.Minimum = box.Minimum);
        box.RegisterPropertyChangedCallback(NumberBox.MaximumProperty, (_, _) => slider.Maximum = box.Maximum);
        box.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => wrapper.Visibility = box.Visibility);
        box.ValueChanged += (_, args) =>
        {
            if (_syncNumeric || !double.IsFinite(args.NewValue)) return;
            _syncNumeric = true;
            try { slider.Value = Math.Clamp(args.NewValue, slider.Minimum, slider.Maximum); }
            finally { _syncNumeric = false; }
            PreviewPropertyChange(box);
        };
        slider.ValueChanged += (_, args) =>
        {
            if (_syncNumeric) return;
            // The NumberBox event performs the model update once, for both entry paths.
            box.Value = Math.Round(args.NewValue, 5);
        };
        TrackSliderGesture(slider, box);
        box.LostFocus += (_, _) => { if (_dragPropertySource is null) _lastPropertySource = null; };
    }

    private static Panel? FindEditorParent(DependencyObject root, UIElement target)
    {
        if (root is Panel panel)
        {
            if (panel.Children.Contains(target)) return panel;
            foreach (var child in panel.Children)
                if (FindEditorParent(child, target) is { } found) return found;
        }
        else if (root is Border border && border.Child is { } child)
            return FindEditorParent(child, target);
        else if (root is ContentControl content && content.Content is DependencyObject value)
            return FindEditorParent(value, target);
        return null;
    }

    private void TrackSliderGesture(Slider slider, object source)
    {
        slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragPropertySource = source), true);
        void End()
        {
            _dragPropertySource = null;
            CommitPropertyEdit();
        }
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => End()), true);
        slider.PointerCaptureLost += (_, _) => End();
    }

    private void PreviewPropertyChange(object source)
    {
        if (_updatingUi || !_editorMode || _profile is null || _selection is null) return;
        var before = GamepadProfileStore.SerializeProfile(_profile);
        ApplySelectedProperties(save: false, refresh: false, recordUndo: false);
        if (before == GamepadProfileStore.SerializeProfile(_profile)) return;
        if (!ReferenceEquals(_lastPropertySource, source) ||
            (!ReferenceEquals(_dragPropertySource, source) && (DateTime.UtcNow - _lastPropertyAt).TotalMilliseconds > 800))
        {
            var dragging = _dragPropertySource;
            CommitPropertyEdit(); AddUndoSnapshot(before);
            _dragPropertySource = dragging;
        }
        _lastPropertySource = source; _lastPropertyAt = DateTime.UtcNow;
        _propertySavePending = true;
        _propertySaveTimer!.Stop(); _propertySaveTimer.Start();
    }

    private void SavePendingProperties()
    {
        if (!_propertySavePending) return;
        _propertySavePending = false;
        SaveCollection();
    }

    private void CommitPropertyEdit()
    {
        _propertySaveTimer?.Stop(); SavePendingProperties();
        _lastPropertySource = _dragPropertySource = null;
    }

    private void ProfilesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ProfileColumn is not null) ApplyModeLayout();
    }

    private void RefreshElementPicker()
    {
        if (!_editorMode || _profile is null) return;
        var choices = _profile.Moves.Select((m, i) => new EditorElementChoice($"轮盘 {i + 1}", "move", i.ToString()))
            .Concat(_profile.Looks.Select((l, i) => new EditorElementChoice($"视角 {i + 1}", "look", i.ToString())))
            .Concat(_profile.Buttons.Select(b => new EditorElementChoice(b.Label + " · " + b.Binding, "button", b.Id))).ToList();
        var signature = string.Join("|", choices.Select(c => $"{c.Kind}:{c.Id}:{c.Label}"));
        _syncElementPicker = true;
        try
        {
            if (signature != _elementPickerSignature)
            { ElementPicker.ItemsSource = choices; _elementPickerSignature = signature; }
            var id = _selection == "move" ? _moveIndex.ToString() : _selection == "look" ? _lookIndex.ToString() : _selectedButtonId;
            ElementPicker.SelectedItem = ElementPicker.Items.OfType<EditorElementChoice>().FirstOrDefault(c => c.Kind == _selection && c.Id == id);
        }
        finally { _syncElementPicker = false; }
    }

    private void ElementPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncElementPicker || ElementPicker.SelectedItem is not EditorElementChoice choice) return;
        SelectElement(choice.Kind, choice.Id);
    }
}
