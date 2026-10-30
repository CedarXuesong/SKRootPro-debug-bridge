using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using SkpdbDesktop;
using Windows.Foundation;

internal static class DynamicSlotUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }

    public static async Task RunAsync(GamepadPage page)
    {
        var original = Field<GamepadProfile>(page, "_profile");
        var collection = Field<GamepadProfileCollection>(page, "_collection");
        var editor = Field<bool>(page, "_editorMode");
        var profile = GamepadProfileStore.CreateDefaultProfile("test.dynamic.slots");
        profile.Buttons.Clear();
        Set(page, "_collection", new GamepadProfileCollection { Profiles = new() { profile } });
        try
        {
            Call(page, "SelectProfile", profile, false);
            await (Task)Call(page, "ToggleEditorAsync", true)!;
            Check(Field<NumberBox>(page, "SlotBox").Maximum == GamepadProfile.MaxLogicalSlot,
                "XAML inspector still caps IDs at nine");
            for (var i = 0; i < 30; i++)
                Call(page, "AddControlAt", GamepadControlKind.Fire, new Point(.1 + i % 10 * .08, .1 + i / 10 * .2));
            Check(profile.Buttons.Count == 30, "Actual editor refused more than ten controls");
            for (var i = 0; i < 70; i++) Call(page, "DuplicateSelected");
            Check(profile.Buttons.Count == 100 && profile.Buttons.Select(b => b.Slot).Distinct().Count() == 100 &&
                profile.Buttons.Select(b => b.Id).Distinct().Count() == 100 && profile.Buttons.Max(b => b.Slot) == 101,
                "Duplicating a large layout lost controls/unique logical IDs");
            profile.Validate();
            var saved = GamepadProfileStore.Load().Profiles.Single(p => p.Package == profile.Package);
            Check(saved.Buttons.Count == 100 && saved.Buttons.Last().Slot == 101, "Editor did not persist all controls");
            Call(page, "Undo");
            Check(Field<GamepadProfile>(page, "_profile").Buttons.Count == 99, "Undo failed for a large layout");
            Call(page, "Redo");
            Check(Field<GamepadProfile>(page, "_profile").Buttons.Count == 100, "Redo failed for a large layout");
            Call(page, "AddAlternative", true, null!);
            Call(page, "AddAlternative", false, null!);
            var current = Field<GamepadProfile>(page, "_profile");
            Check(current.ExtraMoves.Count == 1 && current.ExtraLooks.Count == 1 &&
                current.ExtraMoves[0].Slot == current.Move.Slot && current.ExtraLooks[0].Slot == current.Look.Slot,
                "Large layout alternatives did not retain their type's logical contact");
            Console.WriteLine("PASS actual WinUI editor: 30 adds + 70 duplicates, 100 unique IDs, save/reload, undo/redo and extra move/look components");
        }
        finally
        {
            Set(page, "_collection", collection);
            Call(page, "SelectProfile", original, false);
            Set(page, "_editorMode", editor);
            Call(page, "ApplyModeLayout");
            Call(page, "SaveCollection");
        }
    }
}
