using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SkpdbDesktop;

internal static class EditorUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(GamepadPage page)
    {
        var collection = Field<GamepadProfileCollection>(page, "_collection");
        var original = Field<GamepadProfile>(page, "_profile");
        var wasEditor = Field<bool>(page, "_editorMode");
        var profile = GamepadProfileStore.CreateDefaultProfile("test.editor.sliders");
        Set(page, "_collection", new GamepadProfileCollection { Profiles = new() { profile } });
        try
        {
            Call(page, "SelectProfile", profile, false);
            await (Task)Call(page, "ToggleEditorAsync", true)!;
            var list = Field<ToggleButton>(page, "ProfilesToggle");
            list.IsChecked = false;
            Check(Field<ColumnDefinition>(page, "ProfileColumn").Width.Value == 0, "Profile list still consumes canvas width");
            list.IsChecked = true;
            Check(Field<ColumnDefinition>(page, "ProfileColumn").Width.Value == 200, "Profile list cannot expand");
            list.IsChecked = false;
            Call(page, "SelectElement", "look", "0");
            var sliders = Field<Dictionary<NumberBox, Slider>>(page, "_numericSliders");
            Check(sliders.Count == 19, "Numeric inspector fields are missing sliders");
            var sensitivity = Field<NumberBox>(page, "SensitivityBox");
            var before = Field<List<string>>(page, "_undo").Count;
            var oldSensitivity = profile.Look.Sensitivity;
            for (var i = 0; i < 100; i++) sliders[sensitivity].Value = .1 + i * .01;
            Check(Math.Abs(profile.Look.Sensitivity - 1.09) < .0001 && Math.Abs(sensitivity.Value - 1.09) < .0001,
                "Slider did not update the model/number without Apply");
            Check(Field<List<string>>(page, "_undo").Count == before + 1, "Continuous slider changes flooded undo history");
            await Task.Delay(550);
            Check(Math.Abs(GamepadProfileStore.Load().Profiles.Single(p => p.Package == profile.Package).Look.Sensitivity - 1.09) < .0001,
                "Debounced editor save lost slider value");
            Call(page, "Undo");
            var restored = Field<GamepadProfile>(page, "_profile");
            Check(Math.Abs(restored.Look.Sensitivity - oldSensitivity) < .0001, "Undo did not restore pre-drag sensitivity");
            Call(page, "Redo");
            restored = Field<GamepadProfile>(page, "_profile");
            Check(Math.Abs(restored.Look.Sensitivity - 1.09) < .0001, "Redo did not restore final drag value");
            Call(page, "SelectElement", "look", "0");
            sensitivity.Value = .37;
            Check(Math.Abs(sliders[sensitivity].Value - .37) < .0001 && Math.Abs(restored.Look.Sensitivity - .37) < .0001,
                "Typed number did not update slider/model");
            var invertX = Field<ToggleSwitch>(page, "InvertXToggle");
            var invertY = Field<ToggleSwitch>(page, "InvertYToggle");
            Check(!invertX.IsOn && !invertY.IsOn, "New editor changed inversion defaults");
            invertX.IsOn = true;
            Check(restored.Look.InvertX && !restored.Look.InvertY, "X toggle failed to update independently");
            await Task.Delay(550);
            var persisted = GamepadProfileStore.Load().Profiles.Single(p => p.Package == profile.Package);
            Check(persisted.Look.InvertX && !persisted.Look.InvertY, "X toggle was not saved");
            Call(page, "Undo");
            restored = Field<GamepadProfile>(page, "_profile");
            Call(page, "SelectElement", "look", "0");
            Check(!restored.Look.InvertX && !restored.Look.InvertY && !invertX.IsOn,
                "Undo did not restore inversion switches");
            Call(page, "Redo");
            restored = Field<GamepadProfile>(page, "_profile");
            Call(page, "SelectElement", "look", "0");
            Check(restored.Look.InvertX && !restored.Look.InvertY && invertX.IsOn,
                "Redo did not restore X inversion");
            invertY.IsOn = true;
            Check(restored.Look.InvertX && restored.Look.InvertY, "Y toggle changed X preference");
            restored.ExtraLooks.Add(new GamepadLookSettings { InvertX = false, InvertY = true });
            Call(page, "SelectElement", "look", "1");
            Check(!invertX.IsOn && invertY.IsOn && restored.Look.InvertX && restored.Look.InvertY,
                "Selecting another look component changed saved inversion preferences");
            Call(page, "SelectElement", "look", "0");
            Check(invertX.IsOn && invertY.IsOn && !restored.ExtraLooks[0].InvertX && restored.ExtraLooks[0].InvertY,
                "Returning to the primary look component lost per-component switches");
            Check(Field<NumberBox>(page, "LookWidthBox").Maximum == 1024 && Field<NumberBox>(page, "LookRadiusYBox").Maximum == 512,
                "Look dimensions exceed device geometry");
            Check(!Field<NumberBox>(page, "SlotBox").IsEnabled, "Logical IDs should remain automatically managed");
            Call(page, "SelectElement", "move", "0");
            var picker = Field<ComboBox>(page, "ElementPicker");
            picker.SelectedIndex = 1;
            Check(Field<string>(page, "_selection") == "look", "Component picker cannot select overlapping elements");
            var signature = GamepadProfileStore.SerializeProfile(restored);
            var undoCount = Field<List<string>>(page, "_undo").Count;
            Call(page, "FillLookFields"); Call(page, "DrawLayout"); Call(page, "PopulateProperties", "look", "0");
            Check(signature == GamepadProfileStore.SerializeProfile(restored) && Field<List<string>>(page, "_undo").Count == undoCount,
                "Refreshing inspector changed model or undo history");
            Call(page, "PreviewPropertyChange", sensitivity);
            Check(Field<List<string>>(page, "_undo").Count == undoCount, "Unchanged focus commit created an undo item");
            Call(page, "ApplySelected_Click", page, new Microsoft.UI.Xaml.RoutedEventArgs());
            Check(Field<List<string>>(page, "_undo").Count == undoCount, "Explicit save created an empty undo item");
            await Task.Delay(100);
            await CanaryUiTests.SaveWindowEvidenceAsync("editor-sliders");
            Call(page, "SelectElement", "button", restored.Buttons[0].Id);
            var radius = Field<NumberBox>(page, "RadiusBox");
            sliders[radius].Value = .085;
            await (Task)Call(page, "ToggleEditorAsync", false)!;
            var saved = GamepadProfileStore.Load().Profiles.Single(p => p.Package == profile.Package);
            Check(Math.Abs(saved.Buttons[0].Radius - .085) < .0001, "Leaving editor dropped pending changes");
            Console.WriteLine("PASS real editor: 19 bidirectional sliders, independent X/Y inversion, debounce save, undo/redo, per-component switches, safe refresh, geometry limits, picker and collapsible profiles");
        }
        finally
        {
            Call(page, "CommitPropertyEdit");
            Set(page, "_collection", collection); Call(page, "SelectProfile", original, false);
            Set(page, "_editorMode", wasEditor); Call(page, "ApplyModeLayout"); Call(page, "SaveCollection");
        }
    }
}
