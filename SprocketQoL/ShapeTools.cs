using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;

namespace SprocketQoL;

/// Add-on panel: "Merge add-ons" folds the other selected add-ons into this one, "Scatter" spreads copies of them over
/// it, "Cut with this add-on" cuts the add-on's shape out of the structure under it (a Boolean cut) or keeps only the
/// overlap (the opposite: an intersect), "Drape as cloth" turns it into cloth or a net, and "Mirror side" makes its
/// mirrored copy an add-on of its own.
/// (Round add-ons are palette parts in the "Round Add-on Parts" data mod, placed like the game's cube.)
[HarmonyPatch]
public static class ShapeTools
{
    internal static void Draw(PlateStructureEditor __instance, Panel layout) => Ui.Inspector("Merge and cut", layout, () =>
    {
        var part = __instance.Component.VehicleObject;
        var editor = DesignEditor.Instance;
        var ui = Ui.Drawer(layout);
        if (editor == null || ui == null || part.GUID is not (Conversion.AddonGuid or Conversion.CompartmentGuid)) return;
        int addon = (int)part.VUID;
        bool body = part.GUID == Conversion.CompartmentGuid; // a turret or hull: add-ons can merge into it
        var others = editor.SelectedParts(Conversion.AddonGuid).Where(v => v != addon).ToList();
        if (body && others.Count == 0) return; // only offered when add-ons are selected with it
        Ui.Section(layout, body ? "Merge add-ons into this" : "Merge add-ons");
        if (others.Count == 0) ui.InfoField($"Select at least two add-ons. {Keybinds.Shown("join")} merges them into the last selected add-on.", 2);
        else
        {
            ui.InfoField($"{others.Count} selected add-on{(others.Count == 1 ? "" : "s")} will join this {(body ? "hull or turret" : "add-on")}. Ctrl+Z undoes the merge.", 2);
            var tip = new Tip("Merge into this part", body
                ? "Keeps the add-ons' position, shape and armour, and makes them part of this hull or turret. Their interior becomes part of this compartment. Mirrored copies merge too. Ctrl+Z undoes the merge."
                : "Keeps the add-ons' position, shape and armour. Attached parts move onto this add-on. When every selected add-on has a mirror partner, those partners merge too. Ctrl+Z undoes the merge.");
            ui.Button($"Merge {others.Count} add-on{(others.Count == 1 ? "" : "s")} into this", Ui.Callback(() =>
                editor.RequestLiveEdit("Merging add-ons", $"Merged {others.Count} add-on{(others.Count == 1 ? "" : "s")} into part {addon}.", json => AddonEdits.PlanMerge(json, addon, others, editor.LiveShapes(json)))), tip);
        }

        Ui.Section(layout, "Scatter add-ons over this");
        if (others.Count == 0) ui.InfoField("Like Blender's particles: select the add-ons to scatter (a leaf, a stone; several kinds mix), then this part last.", 2);
        else
        {
            ui.InfoField($"Copies of the {others.Count} selected add-on{(others.Count == 1 ? "" : "s")} land at random on this part, each standing on the face as it stands on flat ground.", 2);
            ui.Slider("Copies", scatterCount, 1, AddonEdits.MaxScatter, Ui.FloatCallback(v => scatterCount = (int)Math.Round(v)));
            ui.ToggleField("Faces up only", scatterUp, Ui.BoolCallback(v => scatterUp = v),
                "On: copies land only where this part's faces look up (a deck, a roof), as leaves fall. Off: on every face, sides and underneath too.");
            ui.Button("Scatter the copies", Ui.Callback(() => Scatter(addon, others)), new Tip("Scatter",
                "Each copy is an add-on attached to this part, turned at random, its type picked at random from the selected add-ons. Bigger faces get more. Each click spreads them anew. Restore undoes it."));
        }
        if (body) return; // cutting with a part is for add-ons

        Ui.Section(layout, "Boolean with this add-on");
        var targets = editor.SelectedParts().Where(v => v != addon).ToList();
        string what = targets.Count == 0 ? "the shape it sits on" : $"{targets.Count} selected part{(targets.Count == 1 ? "" : "s")}";
        ui.InfoField($"Like Blender's Boolean, with {what}: cut it (difference), keep only the overlap (intersect), join it with this add-on (union) or slice it in two. The add-on must overlap it. Ctrl+Z undoes a cut.", 3);
        // Labels fit the game's label column (about 14 characters); the tooltip says the rest.
        ui.ToggleField("Keep cutter", keepCutter, Ui.BoolCallback(v => keepCutter = v),
            "Keep the cutting add-on. Off: removes this add-on after cutting. On: keeps it so you can move it and cut again.");
        ui.ToggleField("Box border", rectangleBox, Ui.BoolCallback(v =>
        {
            rectangleBox = v;
            fill = v ? Fill.Mode.Rectangle : (fill == Fill.Mode.Rectangle ? Fill.Mode.Fewest : fill);
            __instance.RequestRedraw();
        }), "Adds a rectangular border around the opening to keep the surrounding face tidy. Off: connects the opening directly to the face's corners.");
        var fillTip = new Tip("Surrounding faces", "Click to cycle the layout around the cut. Fewest points uses only existing corners and the opening. Light fill adds a few points so no faces are long and thin. Smooth fill adds a ring of quads along the cut and more points for even faces. Rectangle box adds a rectangular border.");
        ui.Button($"Faces: {Fill.ModeNames[(int)fill]}", Ui.Callback(() =>
        {
            fill = (Fill.Mode)(((int)fill + 1) % Fill.ModeNames.Length);
            rectangleBox = (fill == Fill.Mode.Rectangle);
            __instance.RequestRedraw();
        }), fillTip);
        ui.Button("Cut through-hole", Ui.Callback(() => Cut(addon, targets, pocket: false)), new Tip("Cut through-hole",
            "Makes an opening through every plate this add-on overlaps. Mirrored plates are cut on both sides. Ctrl+Z undoes the cut."));
        ui.Button("Cut recess (walls and floor)", Ui.Callback(() => Cut(addon, targets, pocket: true)), new Tip("Cut recess",
            "Makes a recess with walls and a floor, shaped by this add-on. The new plates inherit the add-on's armour. Where the plates don't enclose a space (single open plates), it's a plain hole. Ctrl+Z undoes the cut."));
        ui.Button("Keep only the overlap", Ui.Callback(() => Cut(addon, targets, pocket: false, keepInside: true)), new Tip("Keep only the overlap",
            "The opposite of a cut (an intersect): keeps only the part of the plates inside this add-on and removes the rest. The add-on's surface closes it off with the add-on's armour; plates that don't enclose a space there (single open plates) are only trimmed. Mirrored plates change on both sides. Ctrl+Z undoes it."));
        ui.Button("Join into one shape (union)", Ui.Callback(() => Union(addon, targets)), new Tip("Join (union)",
            "This add-on and the one shape it overlaps become one shape, its outside only: each loses its surface inside the other, then the add-on joins the shape, like Merge without the hidden insides. Both must be closed shapes. Ctrl+Z undoes it."));
        ui.Button("Slice in two", Ui.Callback(() => Slice(addon, targets)), new Tip("Slice in two",
            "Splits the one shape this add-on overlaps along the add-on's surface: the shape keeps what's outside, and a new add-on in the same place gets what's inside, both closed by the add-on's surface. Keep cutter keeps this add-on. Restore undoes it."));

        Ui.Section(layout, "Drape as cloth");
        ui.InfoField("Turns this add-on into a cloth sheet as wide and long as its top, dropped onto the vehicle until it settles: a tarp, a cover, a camouflage net. Place it above where it should land first.", 3);
        ui.Slider("Spacing (cm)", clothCell * 100, 5, 25, Ui.FloatCallback(v => { clothCell = MathF.Round(v) / 100; __instance.RequestRedraw(); }));
        ui.Slider("Stiffness (%)", clothStiffness * 100, 2, 100, Ui.FloatCallback(v => clothStiffness = MathF.Round(v) / 100));
        ui.ToggleField("Net", clothNet, Ui.BoolCallback(v => { clothNet = v; __instance.RequestRedraw(); }),
            "On: the sheet becomes a net, each cell between points a hole framed by the net's cords, for a camouflage net. Off: solid cloth.");
        if (clothNet)
        {
            ui.Slider("Hole size (%)", netHole * 100, 40, 85, Ui.FloatCallback(v => { netHole = MathF.Round(v) / 100; __instance.RequestRedraw(); }));
            // Smaller holes than the spacing gives: the settled sheet's cells are cut finer (AddonEdits.PlanDrape).
            ui.Slider("Holes (mm)", netHoleMm, 2, 200, Ui.FloatCallback(v => { netHoleMm = MathF.Round(v); __instance.RequestRedraw(); }));
            int split = (int)MathF.Ceiling(clothCell * netHole * 1000 / netHoleMm - 1e-4f);
            ui.InfoField($"Holes come out {clothCell * netHole * 1000 / Math.Max(1, split):0.#} mm wide{(split > 1 ? $", {split} x {split} to each {clothCell * 100:0} cm of spacing" : " (one per spacing)")}. Small holes on a big add-on can pass the game's limit per part.", 2);
        }
        ui.Button($"Lands on: {LandsOnNames[(int)landsOn]}", Ui.Callback(() =>
        {
            landsOn = (AddonEdits.LandsOn)(((int)landsOn + 1) % LandsOnNames.Length);
            __instance.RequestRedraw();
        }), new Tip("Lands on", "Click to cycle. Everything: every other plate catches it. Only the selected parts: select the parts it should lie on, then this add-on last. All but the selected parts: it falls through them (an antenna, a gun barrel). A mirror twin counts with its partner."));
        ui.Button(clothNet ? "Drape as a net" : "Drape as cloth", Ui.Callback(() => Drape(addon)), new Tip("Drape as cloth",
            "The add-on's top becomes a sheet of points that falls onto the hull, turret and add-ons below and settles; hatches, periscopes, wheels, tracks and other parts catch it as boxes around them. Closer points follow shapes better but make more faces and take longer; a stiffer sheet bends less over edges. The settled sheet replaces the add-on's shape, thin (10 mm at most). As a net, Holes sets how big each hole is, down to 2 mm: the sheet still drapes at its spacing, then each of its cells is cut into smaller ones. Very small holes only fit a small add-on (the game's limit per part). A mirrored add-on drapes on both sides. Ctrl+Z undoes it."));

        Ui.Section(layout, "Mirror side");
        ui.Button("Make the mirror side a real add-on", Ui.Callback(() => RealMirror(addon)), new Tip("Make the mirror side real",
            "The mirrored copy the game shows on the other side becomes an add-on of its own, to edit, move or delete apart from this one. A mirror twin pair stops being a pair, each with its own shape. Nothing moves. Mirror (symmetry) goes off: with it on, the game mirrors any part you drag, so either side would be mirrored again. Restore undoes it."));
    });

    /// Drapes add-on `addon` as cloth onto the vehicle (AddonEdits.PlanDrape) with the panel's settings; the other
    /// selected parts are the ones "Lands on" names. Also the right-click menu's.
    internal static void Drape(int addon)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var options = new AddonEdits.DrapeOptions(clothCell, clothNet ? netHole : 0, clothStiffness, landsOn, editor.SelectedParts().Where(v => v != addon).ToList(), netHoleMm / 1000);
        editor.RequestLiveEdit(clothNet ? "Draping the net" : "Draping the cloth", clothNet ? "Net draped." : "Cloth draped.", json => AddonEdits.PlanDrape(json, addon, options, editor.LiveShapes(json), editor.PartBoxes()));
    }

    /// Copies of add-ons `types` scattered over part `target` (AddonEdits.PlanScatter), a new spread each time. Also the
    /// right-click menu's.
    internal static void Scatter(int target, List<int> types)
    {
        int count = scatterCount, seed = Environment.TickCount;
        bool upOnly = scatterUp;
        if (DesignEditor.Instance is { } editor)
            editor.RequestLiveEdit("Scattering add-ons", $"Scattered {count} copies. Restore undoes it.", json => AddonEdits.PlanScatter(json, target, types, count, seed, upOnly, editor.LiveShapes(json)));
    }

    /// Add-on `addon`'s mirror side becomes an add-on of its own (AddonEdits.PlanRealMirror), and the game's Mirror goes
    /// off: with it on, the game mirrors any part dragged that has no mirror (PlaceOperation.ApplyDuplicateMirrorState),
    /// so dragging either side would mirror it back over the other. Also the right-click menu's.
    internal static void RealMirror(int addon)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var modes = UnityEngine.Object.FindObjectOfType<DesignerFramework.UI.TransformModeSelectUI>()?.modes;
        bool mirrorOn = modes?.Symmetry == true;
        editor.RequestLiveEdit("Making the mirror side real",
            mirrorOn ? "The mirror side is an add-on of its own, and Mirror is off now: with it on, dragging either side mirrors it again. Restore undoes it."
                     : "The mirror side is an add-on of its own. Keep Mirror off to move each side alone. Restore undoes it.",
            json => AddonEdits.PlanRealMirror(json, addon, editor.LiveShapes(json)),
            () => { if (mirrorOn && modes!.Symmetry) modes.Symmetry = false; });
    }

    /// Add-on `addon` and the one other selected shape (or the one it sits on) become one shape (AddonEdits.PlanUnion).
    /// Also the right-click menu's.
    internal static void Union(int addon, List<int> targets)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var mode = rectangleBox ? Fill.Mode.Rectangle : fill;
        editor.RequestLiveEdit("Joining (union)", "Joined into one shape.", json => AddonEdits.PlanUnion(json, addon, targets, mode, editor.LiveShapes(json)));
    }

    /// The one other selected shape (or the one add-on `addon` sits on) is sliced in two along the add-on
    /// (AddonEdits.PlanSlice); "Keep cutter" applies. Also the right-click menu's.
    internal static void Slice(int addon, List<int> targets)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var mode = rectangleBox ? Fill.Mode.Rectangle : fill;
        bool remove = !keepCutter;
        editor.RequestLiveEdit("Slicing in two", "Sliced in two: the inside is a new add-on. Restore undoes it.",
            json => AddonEdits.PlanSlice(json, addon, targets, remove, mode, editor.LiveShapes(json)));
    }

    /// Cuts add-on `addon`'s shape from `targets` (or the shape it sits on): a hole, a recess (`pocket`), or the opposite,
    /// keeping only the overlap (`keepInside`). The panel's settings (keep the add-on, face layout) apply. Also the
    /// right-click menu's.
    internal static void Cut(int addon, List<int> targets, bool pocket, bool keepInside = false)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var mode = rectangleBox ? Fill.Mode.Rectangle : fill;
        editor.RequestLiveEdit(keepInside ? "Keeping the overlap" : pocket ? "Cutting a pocket" : "Cutting a hole",
            keepInside ? "Kept only the overlap." : pocket ? "Pocket cut." : "Hole cut.",
            json => AddonEdits.PlanCut(json, addon, targets, !keepCutter, pocket, mode, editor.LiveShapes(json), keepInside));
    }

    static bool keepCutter;
    static bool rectangleBox;
    static Fill.Mode fill = Fill.Mode.Fewest;
    static float clothCell = 0.1f, clothStiffness = 0.15f, netHole = 0.6f, netHoleMm = 60;
    static bool clothNet;
    static AddonEdits.LandsOn landsOn;
    static readonly string[] LandsOnNames = { "everything", "only the selected parts", "all but the selected parts" };
    static int scatterCount = 40;
    static bool scatterUp = true;
}
