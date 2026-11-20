using Il2CppInterop.Runtime;
using Sprocket.Gameplay;
using Sprocket.PlayerControl;
using Sprocket.SettingConfiguration;
using Sprocket.UI;
using Sprocket.VehicleControl;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SprocketQoL;

/// Edits existing digital binding slots in disabled drafts. The native settings Apply/Cancel/Accept lifecycle owns
/// persistence; browsing and capturing never change the live controls or the default binding topology.
internal static class GameControlBindings
{
    sealed record Slot(string Action, int Index, string BindingId);
    sealed class Draft
    {
        internal readonly string Id;
        internal readonly string Label;
        internal readonly List<InputActionAsset> Assets = new();
        internal readonly Dictionary<Slot, string?> Pending = new();
        internal readonly InputActionAsset Asset;
        internal string SourceJson = "";
        internal bool Touched;
        internal Draft(string id, string label, InputActionAsset source)
        {
            Id = id; Label = label; Assets.Add(source);
            Asset = InputActionAsset.FromJson(source.ToJson());
            Asset.name = "QoL binding draft: " + label;
        }
    }
    sealed record Row(Draft Draft, InputAction Action, int Index, string Label, string Group)
    {
        internal Slot Slot => new(Action.actionMap.name + "/" + Action.name, Index, Action.bindings[Index].id.ToString());
    }
    sealed class Capture
    {
        internal readonly Row Row;
        internal readonly int StartedFrame = Time.frameCount;
        internal bool Armed;
        internal bool Cancelled;
        internal int ReleasedFrame = -1;
        internal string? Candidate;
        internal Capture(Row row) => Row = row;
    }
    sealed record Prepared(Draft Draft, InputActionAsset[] Assets, Dictionary<IntPtr, string> Before, string Merged, bool HadProfile, string? ProfileBefore);

    static SettingsMenu? owner;
    static IntPtr profile;
    static readonly List<Draft> drafts = new();
    static readonly List<UnityAction> keep = new();
    static Capture? capture;
    static GameObject? overlay;
    static TMPro.TMP_Text? overlayText;
    static bool previousOverlayActive;
    static string previousOverlayText = "", ownOverlayText = "";
    static Action? redraw;
    static int groupIndex, page;
    static string message = "";
    internal static bool Capturing => capture != null;

    /// Changing pages cancels only the capture. Drafts survive until native Apply or Cancel.
    internal static void Stop()
    {
        capture = null;
        // A native capture that replaced ours owns the overlay now. Never dismiss it or overwrite its message.
        if (overlay != null && overlay && overlayText != null && overlayText && overlayText.text == ownOverlayText)
        { overlayText.text = previousOverlayText; overlay.SetActive(previousOverlayActive); }
        overlay = null; overlayText = null; previousOverlayText = ownOverlayText = "";
    }

    internal static void Draw(IGUIElementDrawer ui, SettingsMenu menu, Action repaint)
    {
        Ensure(menu); redraw = repaint; keep.Clear();
        Discover(menu);
        foreach (var draft in drafts) Rebase(draft);
        var rows = Rows();
        var groups = rows.Select(r => r.Group).Distinct().ToArray();
        ui.Header("Game controls");
        if (groups.Length == 0)
        {
            ui.InfoField("No native keyboard or mouse-button controls are available here. Open Settings while in the builder or driving to include those controls.", 3);
            return;
        }
        ui.InfoField(capture is { } waiting
            ? waiting.Candidate != null ? "Release the chosen key or mouse button to finish."
              : waiting.Armed ? "Press one physical key or mouse button. Esc cancels. Modifier keys can be bound on their own."
              : "Release all keys and mouse buttons, then press the new control."
            : "Changes use the game's Apply, Cancel and Accept buttons. Edit each part of a chord or movement composite separately; pointer movement and analog axes stay unchanged.", 3);
        groupIndex = Math.Clamp(groupIndex, 0, groups.Length - 1);
        string group = groups[groupIndex];
        var groupRows = rows.Where(r => r.Group == group).ToArray();
        int pages = Math.Max(1, (groupRows.Length + 7) / 8);
        page = Math.Clamp(page, 0, pages - 1);
        ui.ColumnCount = 3;
        Button(ui, "Previous group", "Previous controls", "Browse the previous action map.", () => { Stop(); groupIndex = (groupIndex + groups.Length - 1) % groups.Length; page = 0; Repaint(); });
        Button(ui, $"{groupIndex + 1}/{groups.Length}: {group}", "Controls group", "Browse the next action map.", () => { Stop(); groupIndex = (groupIndex + 1) % groups.Length; page = 0; Repaint(); });
        Button(ui, $"Page {page + 1}/{pages} — next", "More controls", "Eight binding slots per page.", () => { Stop(); page = (page + 1) % pages; Repaint(); });
        ui.ColumnCount = 4;
        foreach (var row in groupRows.Skip(page * 8).Take(8))
        {
            var binding = row.Action.bindings[row.Index];
            string shown = Display(row.Action, row.Index);
            string clashes = Conflicts(row, rows);
            ui.InfoField(row.Label, 1);
            Button(ui, capture?.Row == row ? "Waiting..." : shown, row.Label,
                $"Default: {binding.path}. {clashes}Click to capture one physical key or mouse button. Composite parts are edited individually.", () =>
                {
                    BeginCapture(menu, row);
                });
            Button(ui, "Clear", "Clear " + row.Label, "Leaves only this binding slot unbound. Other bindings for this action remain unchanged.", () => Change(row, ""));
            Button(ui, "Reset", "Reset " + row.Label, "Removes only this slot's override and restores its native default.", () => Change(row, null));
        }
        ui.ColumnCount = 1;
        if (message.Length != 0) ui.InfoField(message, 2);
    }

    internal static void Tick(SettingsMenu menu)
    {
        if (capture is not { } waiting) return;
        if (!Same(menu)) { Stop(); return; }
        if (overlay == null || !overlay || !overlay.activeSelf || overlayText == null || !overlayText || overlayText.text != ownOverlayText)
        { Stop(); message = "Binding capture was interrupted."; Repaint(); return; }
        if (Keyboard.current is { } escapeKeys && escapeKeys.escapeKey.wasPressedThisFrame)
        { waiting.Cancelled = true; OverlayMessage("Release Escape to cancel."); }
        if (waiting.Cancelled)
        {
            if (!Released(waiting)) return;
            Stop(); message = "Binding change cancelled."; Repaint(); return;
        }
        if (!waiting.Armed)
        {
            if (Time.frameCount <= waiting.StartedFrame || AnyPressed()) return;
            waiting.Armed = true; OverlayMessage("Press one physical key or mouse button. Esc cancels."); Repaint(); return;
        }
        if (waiting.Candidate != null)
        {
            if (!Released(waiting)) return;
            Change(waiting.Row, waiting.Candidate); return;
        }
        if (Keyboard.current is { } keys)
            foreach (var key in keys.allKeys)
                if (key.wasPressedThisFrame)
                {
                    string path = "<Keyboard>/" + key.name;
                    if (GameBindingRules.ButtonPath(path)) { waiting.Candidate = path; OverlayMessage("Release " + path + " to finish."); Repaint(); return; }
                }
        foreach (var (button, name) in MouseButtons())
            if (button.wasPressedThisFrame) { waiting.Candidate = "<Mouse>/" + name; OverlayMessage("Release the mouse button to finish."); Repaint(); return; }
    }

    /// Called after the native keymapping submenu has staged its edits. Merge only the explicitly changed slots,
    /// then write the same merged overrides to the native profile and every live asset sharing its exact save ID.
    internal static void Apply(SettingsMenu menu)
    {
        Stop();
        if (!Same(menu)) return;
        Discover(menu);
        var prepared = new List<Prepared>();
        // Prepare all targets, including their exact current alias/profile snapshots, before writing any of them.
        foreach (var draft in drafts.Where(d => d.Pending.Count != 0))
        {
            var source = draft.Assets.FirstOrDefault(Alive);
            if (source == null) throw new Exception("The game controls changed while Settings was open; reopen Settings before applying these bindings.");
            // Validate every pending GUID in every alias before changing any live asset of this target. An asset with
            // the same save ID but a different action/binding schema must never receive an ordinal-only edit.
            var assets = draft.Assets.Where(Alive).ToArray();
            foreach (var asset in assets) foreach (var slot in draft.Pending.Keys) _ = Binding(asset, slot);
            var before = assets.ToDictionary(asset => asset.Pointer, Save);
            string merged;
            var preview = InputActionAsset.FromJson(source.ToJson());
            try
            {
                Load(preview, before[source.Pointer]);
                foreach (var change in draft.Pending) Set(preview, change.Key, change.Value);
                merged = Save(preview);
            }
            finally { if (Alive(preview)) UnityEngine.Object.Destroy(preview); }
            bool hadProfile = menu.editProfile.Rebinds.TryGetRebind(draft.Id, out string profileBefore);
            prepared.Add(new Prepared(draft, assets, before, merged, hadProfile, profileBefore));
        }
        try
        {
            foreach (var change in prepared)
            {
                foreach (var asset in change.Assets) foreach (var binding in change.Draft.Pending) Set(asset, binding.Key, binding.Value);
                menu.editProfile.Rebinds.SetRebind(change.Draft.Id, change.Merged);
            }
        }
        catch
        {
            foreach (var change in prepared)
            {
                foreach (var asset in change.Assets)
                    try { if (Alive(asset)) Load(asset, change.Before[asset.Pointer]); }
                    catch (Exception ex) { Plugin.ModLog.LogError("Could not restore a game-control preview: " + ex.Message); }
                try
                {
                    if (change.HadProfile) menu.editProfile.Rebinds.SetRebind(change.Draft.Id, change.ProfileBefore);
                    else menu.editProfile.Rebinds.rebindJSONs.Remove(change.Draft.Id);
                }
                catch (Exception ex) { Plugin.ModLog.LogError("Could not restore staged game controls: " + ex.Message); }
            }
            throw;
        }
        foreach (var change in prepared)
        {
            change.Draft.Touched = true; change.Draft.Pending.Clear(); change.Draft.SourceJson = "";
            try { Rebase(change.Draft); }
            catch (Exception ex) { Plugin.ModLog.LogWarning("Game-control preview will refresh when reopened: " + ex.Message); }
        }
        message = "Bindings applied. Accept saves them; Cancel restores the settings from when this menu opened.";
    }

    internal static void Cancel(SettingsMenu menu)
    {
        Stop();
        if (!Same(menu)) return;
        try
        {
            foreach (var draft in drafts.Where(d => d.Touched))
            {
                bool found = menu.initialProfile.Rebinds.TryGetRebind(draft.Id, out string json);
                foreach (var asset in draft.Assets.Where(Alive))
                    try { Load(asset, found ? json : ""); }
                    catch (Exception ex) { Plugin.ModLog.LogWarning("Native settings will restore a control set: " + ex.Message); }
            }
        }
        finally { Dispose(); }
    }

    static void Ensure(SettingsMenu menu)
    {
        if (Same(menu)) return;
        Dispose(); owner = menu; profile = menu.editProfile.Pointer;
    }
    static bool Same(SettingsMenu menu) => owner != null && owner.Pointer == menu.Pointer && menu.editProfile != null && profile == menu.editProfile.Pointer;
    static bool Alive(InputActionAsset asset) => asset != null && asset;
    static string Save(InputActionAsset asset) => InputActionRebindingExtensions.SaveBindingOverridesAsJson(asset.Cast<IInputActionCollection2>());
    static void Load(InputActionAsset asset, string? json)
    {
        if (string.IsNullOrEmpty(json)) InputActionRebindingExtensions.RemoveAllBindingOverrides(asset.Cast<IInputActionCollection2>());
        else InputActionRebindingExtensions.LoadBindingOverridesFromJson(asset.Cast<IInputActionCollection2>(), json, true);
    }
    static void Dispose()
    {
        Stop();
        foreach (var draft in drafts) if (Alive(draft.Asset)) UnityEngine.Object.Destroy(draft.Asset);
        drafts.Clear(); keep.Clear(); owner = null; profile = IntPtr.Zero; redraw = null; groupIndex = page = 0; message = "";
    }
    static void Repaint() => redraw?.Invoke();
    static void BeginCapture(SettingsMenu menu, Row row)
    {
        if (capture != null) return;
        if (menu.rebindOverlay == null || !menu.rebindOverlay || menu.rebindMessageDisplay == null || !menu.rebindMessageDisplay)
        { message = "The game's control-capture overlay is unavailable. Reopen Settings and try again."; Repaint(); return; }
        if (menu.rebindOverlay.activeSelf)
        { message = "Finish the other control rebinding first."; Repaint(); return; }
        overlay = menu.rebindOverlay; overlayText = menu.rebindMessageDisplay;
        previousOverlayActive = overlay.activeSelf; previousOverlayText = overlayText.text;
        capture = new Capture(row); message = "";
        OverlayMessage("Release all keys and mouse buttons, then press the new control.");
        UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
        overlay.SetActive(true); Repaint();
    }
    static void OverlayMessage(string text)
    {
        ownOverlayText = (capture?.Row.Label ?? "Game controls") + "\n" + text;
        if (overlayText != null && overlayText) overlayText.text = ownOverlayText;
    }
    static bool Released(Capture waiting)
    {
        if (AnyPressed()) { waiting.ReleasedFrame = -1; return false; }
        if (waiting.ReleasedFrame < 0) { waiting.ReleasedFrame = Time.frameCount; return false; }
        return Time.frameCount > waiting.ReleasedFrame;
    }
    static void Button(IGUIElementDrawer ui, string label, string title, string description, Action action)
    {
        var tip = new UITooltip(title, description);
        var callback = Ui.Callback(() => Ui.Guard("Game control binding", action)); keep.Add(callback);
        ui.Button(label, callback, ref tip);
    }

    static void Discover(SettingsMenu menu)
    {
        // The menu's assets are authoritative when the same ID also has a separately instantiated live control set.
        if (menu.rebindTargets != null) foreach (var target in menu.rebindTargets) Add(target.SaveID, target.Asset, target.SaveID);
        if (menu.keymappingMenu?.rebindTargets != null) foreach (var target in menu.keymappingMenu.rebindTargets) Add(target.SaveID, target.Asset, target.SaveID);
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Player>()))
        {
            if (obj.TryCast<Player>() is not { } player) continue;
            Add(player.generalControlsOverrideID, player.generalControls?.asset, "General");
            if (player.states == null) continue;
            foreach (var state in player.states)
            {
                if (state?.TryCast<VehicleDesignerPlayerState>() is { } designer)
                {
                    Add(designer.designerControlOverrideID, designer.controls?.asset, "Builder");
                    Add(designer.cameraControlOverrideID, designer.tpcControls?.asset, "Builder camera");
                }
                if (state?.TryCast<VehicleControlPlayerState>() is { } driving)
                {
                    Add(driving.vehicleControlOverrideID, driving.vehicleControls?.asset, "Driving");
                    Add(driving.cameraControlOverrideID, driving.tpcControls?.asset, "Driving camera");
                }
            }
        }
    }
    static void Add(string? id, InputActionAsset? asset, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || asset == null || !asset) return;
        var existing = drafts.FirstOrDefault(d => d.Id == id);
        if (existing == null) drafts.Add(new Draft(id, label, asset));
        else if (!existing.Assets.Any(a => a.Pointer == asset.Pointer)) existing.Assets.Add(asset);
    }
    static void Rebase(Draft draft)
    {
        var source = draft.Assets.FirstOrDefault(Alive);
        if (source == null) return;
        string current = Save(source);
        if (current == draft.SourceJson) return;
        Load(draft.Asset, current);
        foreach (var change in draft.Pending) Set(draft.Asset, change.Key, change.Value);
        draft.SourceJson = current;
    }
    static List<Row> Rows()
    {
        var rows = new List<Row>();
        foreach (var draft in drafts)
            foreach (var map in draft.Asset.actionMaps)
                foreach (var action in map.actions)
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        var binding = action.bindings[i];
                        if (!GameBindingRules.Editable(binding.path, binding.isComposite)) continue;
                        string leaf = binding.isPartOfComposite ? " / " + binding.name : "";
                        int copies = 0;
                        for (int k = 0; k < i; k++) if (!action.bindings[k].isComposite && action.bindings[k].name == binding.name) copies++;
                        string duplicate = copies == 0 ? "" : $" ({copies + 1})";
                        rows.Add(new Row(draft, action, i, action.name + leaf + duplicate, draft.Label + " / " + map.name));
                    }
        return rows;
    }
    static (InputAction Action, int Index) Binding(InputActionAsset asset, Slot slot)
    {
        var action = asset.FindAction(slot.Action, false);
        if (action != null)
            for (int i = 0; i < action.bindings.Count; i++)
                if (action.bindings[i].id.ToString() == slot.BindingId && GameBindingRules.Editable(action.bindings[i].path, action.bindings[i].isComposite))
                    return (action, i);
        throw new Exception("This binding slot changed while Settings was open; reopen Settings and try again.");
    }
    static void Set(InputActionAsset asset, Slot slot, string? path)
    {
        var (action, index) = Binding(asset, slot);
        if (path == null) InputActionRebindingExtensions.RemoveBindingOverride(action, index);
        else
        {
            if (path.Length != 0 && !GameBindingRules.ButtonPath(path)) throw new Exception("Choose a keyboard key or mouse button for this digital binding.");
            var previous = action.bindings[index];
            var change = new InputBinding { overridePath = path, overrideInteractions = previous.overrideInteractions, overrideProcessors = previous.overrideProcessors };
            InputActionRebindingExtensions.ApplyBindingOverride(action, index, change);
        }
    }
    static void Change(Row row, string? path)
    {
        Stop(); Set(row.Draft.Asset, row.Slot, path); row.Draft.Pending[row.Slot] = path;
        message = row.Label + ": " + (path == null ? "native default" : path.Length == 0 ? "unbound" : path) + ". Use Apply or Accept to apply this change.";
        Repaint();
    }
    static string Display(InputAction action, int index)
    {
        if (string.IsNullOrEmpty(action.bindings[index].effectivePath)) return "Unbound";
        string text = InputActionRebindingExtensions.GetBindingDisplayString(action, index, default(InputBinding.DisplayStringOptions));
        return string.IsNullOrEmpty(text) ? action.bindings[index].effectivePath : text.Replace("Control", "Ctrl");
    }
    static string Conflicts(Row row, List<Row> rows)
    {
        string path = row.Action.bindings[row.Index].effectivePath;
        var shared = rows.Where(other => other != row && other.Draft == row.Draft && other.Action.actionMap.name == row.Action.actionMap.name
                && GameBindingRules.Overlaps(path, other.Action.bindings[other.Index].effectivePath))
            .Select(other => other.Label).Distinct().Take(3).ToArray();
        return shared.Length == 0 ? "" : "Also used in this map by: " + string.Join(", ", shared) + ". ";
    }
    static IEnumerable<(ButtonControl Button, string Name)> MouseButtons()
    {
        if (Mouse.current is not { } mouse) yield break;
        yield return (mouse.leftButton, "leftButton"); yield return (mouse.rightButton, "rightButton");
        yield return (mouse.middleButton, "middleButton"); yield return (mouse.backButton, "backButton"); yield return (mouse.forwardButton, "forwardButton");
    }
    static bool AnyPressed()
    {
        if (Keyboard.current is { } keys) foreach (var key in keys.allKeys) if (key.isPressed) return true;
        foreach (var (button, _) in MouseButtons()) if (button.isPressed) return true;
        return false;
    }
}
