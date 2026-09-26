using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AssetManagement;
using Sprocket.Vehicles.PlateStructures.Design;
using VehicleDesigner.PaintJobs;

namespace SprocketQoL;

/// Paint per part: a part can have its own paint job instead of the vehicle's. The game has spare paint slots (User 1
/// to 9) that nothing uses; the part's outside moves to one, with its own paint job ("Own paint 1", ...), edited in the
/// part's panel. Which parts use it is written in that paint job's description, so it's saved with the design and comes
/// back wherever the vehicle is built (editor, battles).
[HarmonyPatch]
public static class PartPaint
{
    const int First = (int)VehicleMaterialSlot.User1, Last = (int)VehicleMaterialSlot.User9;
    const string Tag = "Quality of Life parts:";
    static bool themesLogged;

    static IEditableVehicleMaterialPainter Jobs(VehicleMaterialPainter p) => p.Cast<IEditableVehicleMaterialPainter>();

    // Every vehicle's painter seen lately, newest first. A design loaded or rebuilt gets a new one, and previews have
    // their own, so a part's painter is looked up each time: the one whose materials include the part's.
    static readonly List<VehicleMaterialPainter> painters = new();

    static void Seen(VehicleMaterialPainter p)
    {
        if (painters.Count > 0 && painters[0].Pointer == p.Pointer) return;
        painters.RemoveAll(x => x.Pointer == p.Pointer);
        painters.Insert(0, p);
        if (painters.Count > 12) painters.RemoveAt(painters.Count - 1);
    }

    static int Count<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list) => list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;

    /// The painter painting this part now, or null.
    static VehicleMaterialPainter? PainterOf(VehicleObject part)
    {
        var transform = part.GetComponent<VehicleTransform>();
        if (transform == null) return null;
        foreach (var p in painters.ToList())
            try
            {
                if (p.materialRegister != null && p.materialRegister.TryGetMappedMaterials(transform, out var materials) && materials != null && Count(materials) > 0) return p;
            }
            catch (Exception) { painters.Remove(p); } // released with its vehicle
        return null;
    }

    static VehicleMaterialPainter? PainterOf(int vuid) =>
        DesignEditor.Instance?.AllParts().FirstOrDefault(o => (int)o.VUID == vuid) is { } part ? PainterOf(part) : null;

    /// Part number -> own paint slot, read from the own paint jobs' descriptions.
    static Dictionary<int, int> Owners(VehicleMaterialPainter p)
    {
        var owners = new Dictionary<int, int>();
        var jobs = Jobs(p);
        for (int slot = First; slot < Math.Min(jobs.PaintJobCount, Last + 1); slot++)
            if (jobs.GetPaintJob(slot)?.Description is string d && d.StartsWith(Tag))
                foreach (var word in d[Tag.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(word, out int vuid)) owners[vuid] = slot;
        return owners;
    }

    /// As each part's material is painted: a part with its own paint goes to its slot (only the outside). The
    /// vehicle's own painter holds the list, so this works for any vehicle, in the editor or in battle.
    /// (Whichever way the game paints it: a material just registered, or any repaint.)
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.PaintRegisteredMaterial))]
    static void Registered(VehicleMaterialPainter __instance, VehicleMaterial item) => Place(__instance, item);

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.Process), new[] { typeof(VehicleMaterial) })]
    static void Painted(VehicleMaterialPainter __instance, VehicleMaterial material) => Place(__instance, material);

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.Process), new[] { typeof(VehicleMaterial), typeof(VehicleTransform) })]
    static void PaintedOn(VehicleMaterialPainter __instance, VehicleMaterial material) => Place(__instance, material);

    static void Place(VehicleMaterialPainter painter, VehicleMaterial? item) => Ui.Guard("Own paint", () =>
    {
        Seen(painter);
        if (item?.associatedTransform?.VehicleObject is not { } part) return;
        if (item.PaintSlot == VehicleMaterialSlot.Exterior && Owners(painter).TryGetValue((int)part.VUID, out int slot)) item.PaintSlot = (VehicleMaterialSlot)slot;
    });

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Paint");

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Own paint", () =>
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || PainterOf(__instance.Component.VehicleObject) is not { } p) return;
        int vuid = (int)__instance.Component.VehicleObject.VUID;
        var owners = Owners(p);
        int slot = owners.GetValueOrDefault(vuid);
        int used = owners.Values.DefaultIfEmpty(First - 1).Max() - First + 1; // own paints in use
        var parts = editor.SelectedParts().Append(vuid).Distinct().ToList();
        Ui.Section(layout, "Own paint");
        // Cycles: vehicle paint, own paint 1, 2, ... up to one more than in use, then back.
        int next = slot == 0 ? First : slot - First + 1 < Math.Min(used + 1, Last - First + 1) ? slot + 1 : 0;
        var tip = new UITooltip("Own paint", "Gives this part (and the other selected parts) its own paint job. A new one starts as a copy of " +
                                "the Primary paint. Only the outside changes; the inside keeps the vehicle's interior paint.");
        ui.Button($"Paint: {(slot == 0 ? "vehicle" : Name(slot))}  (click: {(next == 0 ? "vehicle" : Name(next))})", Ui.Callback(() => Ui.Guard("Own paint", () =>
        {
            Commit();
            Assign(p, parts, next);
            __instance.RequestRedraw();
        })), ref tip);
        if (slot == 0 || Jobs(p).GetPaintJob(slot) is not { } job) return;

        // The own paint's settings, as the Paint tab has them for the vehicle's paints. Each drag is one Ctrl+Z step.
        void Slider(string label, string what, Func<PaintJob, float> get, Action<PaintJob, float> set, float min = 0, float max = 100) =>
            ui.Slider(label, get(job) * 100, min, max, Ui.FloatCallback(v => Ui.Guard("Own paint", () => Slide(vuid, slot, what, get, set, v / 100))));
        Slider("Red (%)", "red", j => j.TintR, (j, v) => j.TintR = v);
        Slider("Green (%)", "green", j => j.TintG, (j, v) => j.TintG = v);
        Slider("Blue (%)", "blue", j => j.TintB, (j, v) => j.TintB = v);
        Slider("Saturation (%)", "saturation", j => j.Saturation, (j, v) => j.Saturation = v);
        Slider("Roughness (%)", "roughness", j => j.Roughness, (j, v) => j.Roughness = v);
        Slider("Metallic (%)", "metallic", j => j.Metallic, (j, v) => j.Metallic = v);
        Slider("Condition (%)", "condition", j => j.Condition, (j, v) => j.Condition = v);
        Slider("Grime (%)", "grime", j => j.Grime, (j, v) => j.Grime = v);
        Slider("Camo scale (%)", "camo scale", j => j.Scale, (j, v) => j.Scale = v, 10, 400);
        // Camo: none, or one of the images in My Games\Sprocket\Paint (stored the way the Paint tab stores them).
        var camos = new List<string> { "" };
        if (Directory.Exists(Folder)) camos.AddRange(Directory.GetFiles(Folder, "*.png").Select(f => "Sprocket/Paint/" + Path.GetFileName(f)).OrderBy(f => f));
        string camo = job.ColourMapUri ?? "", nextCamo = camos[(camos.IndexOf(camo) + 1) % camos.Count];
        var camoTip = new UITooltip("Camo", "Cycles through no camo and the camo images in Documents\\My Games\\Sprocket\\Paint.");
        ui.Button($"Camo: {Short(camo)}  (click: {Short(nextCamo)})", Ui.Callback(() => Ui.Guard("Own paint", () =>
        {
            Commit();
            void Put(string uri)
            {
                if (PainterOf(vuid) is not { } q || Jobs(q).GetPaintJob(slot) is not { } j) return;
                j.ColourMapUri = uri;
                Repaint(q, slot);
                Hotkeys.Current?.RequestRedraw();
            }
            if (!editor.Undoable($"{Name(slot)} camo", () => Put(nextCamo), () => Put(camo))) Put(nextCamo);
        })), ref camoTip);
    });

    /// A slider being dragged: the change shows at once; when it's left alone (or another setting is touched) the
    /// whole drag becomes one undoable step.
    sealed class Drag
    {
        public readonly int Part; // whose painter: looked up again each time
        public readonly int Slot;
        public readonly string What;
        public readonly Action<PaintJob, float> Set;
        public readonly float Old;
        public float New, At;
        public Drag(int part, int slot, string what, Action<PaintJob, float> set, float old) =>
            (Part, Slot, What, Set, Old) = (part, slot, what, set, old);
    }
    static Drag? drag;

    static void Slide(int part, int slot, string what, Func<PaintJob, float> get, Action<PaintJob, float> set, float value)
    {
        if (PainterOf(part) is not { } p || Jobs(p).GetPaintJob(slot) is not { } job) return;
        if (drag != null && (drag.Slot != slot || drag.What != what || drag.Part != part)) Commit();
        drag ??= new Drag(part, slot, what, set, get(job));
        set(job, value);
        drag.New = value;
        drag.At = UnityEngine.Time.unscaledTime;
        Repaint(p, slot);
    }

    /// From DesignEditor.Update: a slider left alone for half a second is done.
    internal static void Tick()
    {
        if (drag != null && UnityEngine.Time.unscaledTime - drag.At > 0.5f) Commit();
    }

    static void Commit()
    {
        var d = drag;
        drag = null;
        if (d == null || Math.Abs(d.New - d.Old) < 1e-6f) return;
        void Put(float v)
        {
            if (PainterOf(d.Part) is not { } q || Jobs(q).GetPaintJob(d.Slot) is not { } j) return;
            d.Set(j, v);
            Repaint(q, d.Slot);
            Hotkeys.Current?.RequestRedraw(); // the panel's slider follows
        }
        DesignEditor.Instance?.Undoable($"{Name(d.Slot)} {d.What}", () => Put(d.New), () => Put(d.Old));
    }

    static string Short(string? uri) => string.IsNullOrEmpty(uri) ? "none" : uri.Contains('/') ? Path.GetFileNameWithoutExtension(uri) : "game camo";

    static string Name(int slot) => $"Own paint {slot - First + 1}";

    /// The paint job's settings to its materials: reloaded, then every material on that slot repainted.
    static void Repaint(VehicleMaterialPainter p, int slot)
    {
        p.GetPaintJob(slot)?.PaintJobReference.MarkModified(); // saved with the design
        p.GetPaintJob(slot)?.Refresh();
        var materials = p.materialRegister.Materials;
        int count = materials.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleMaterial>>().Count;
        for (int i = 0; i < count; i++)
            if (materials[i] is { } m && (int)m.PaintSlot == slot && p.materialRegister.TryGetMappedTransform(m, out var t)) p.Process(m, t);
    }

    /// Gives `parts` paint `slot` (0: the vehicle's) as one undoable step: Ctrl+Z puts back each part's paint from before.
    static void Assign(VehicleMaterialPainter p, List<int> parts, int slot)
    {
        var owners = Owners(p);
        var before = parts.ToDictionary(v => v, v => owners.GetValueOrDefault(v));
        var after = parts.ToDictionary(v => v, _ => slot);
        int moved = 0;
        int any = parts[0]; // its painter when the step runs (a rebuilt design has a new one)
        if (DesignEditor.Instance?.Undoable(slot == 0 ? "Vehicle paint" : Name(slot), () => moved = SetSlots(PainterOf(any) ?? p, after),
                                            () => SetSlots(PainterOf(any) ?? p, before)) != true)
            moved = SetSlots(p, after);
        DesignEditor.Instance?.Say(moved > 0 ? $"{parts.Count} part{(parts.Count == 1 ? "" : "s")}: {(slot == 0 ? "vehicle paint" : Name(slot) + " (its colours are in this panel)")}. Ctrl+Z undoes it."
                                             : "Own paint: no paintable material found on these parts", 5);
    }

    /// Each part to its paint slot (0: the vehicle's), making own paint jobs that aren't there yet, and repaints them.
    /// Returns how many materials moved.
    static int SetSlots(VehicleMaterialPainter p, Dictionary<int, int> slots)
    {
        foreach (int slot in slots.Values.Where(s => s != 0).Distinct()) EnsureJob(p, slot);
        var jobs = Jobs(p);
        // The lists of parts, one per own paint.
        var owners = Owners(p);
        foreach (var (v, slot) in slots) { if (slot == 0) owners.Remove(v); else owners[v] = slot; }
        for (int s = First; s < Math.Min(jobs.PaintJobCount, Last + 1); s++)
            if (jobs.GetPaintJob(s) is { } j && (j.Description ?? "").StartsWith(Tag))
                j.Description = Tag + " " + string.Join(" ", owners.Where(o => o.Value == s).Select(o => o.Key).OrderBy(v => v));
        // The parts' outside materials to their slots.
        int moved = 0;
        foreach (var part in DesignEditor.Instance!.AllParts().Where(o => slots.ContainsKey((int)o.VUID)))
        {
            int slot = slots[(int)part.VUID];
            var transform = part.GetComponent<VehicleTransform>();
            if (transform == null || !p.materialRegister.TryGetMappedMaterials(transform, out var materials) || materials == null) continue;
            int count = materials.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleMaterial>>().Count;
            for (int i = 0; i < count; i++)
            {
                var m = materials[i];
                if (m == null || !(m.PaintSlot == VehicleMaterialSlot.Exterior || (int)m.PaintSlot >= First)) continue; // the outside only
                m.PaintSlot = slot == 0 ? VehicleMaterialSlot.Exterior : (VehicleMaterialSlot)slot;
                p.Process(m, transform);
                moved++;
            }
        }
        foreach (int slot in slots.Values.Where(s => s != 0).Distinct()) Repaint(p, slot);
        Hotkeys.Current?.RequestRedraw();
        var check = Owners(p);
        Plugin.ModLog.LogInfo($"Own paint: {string.Join(", ", slots.Select(s => $"{s.Key} -> {(s.Value == 0 ? "vehicle" : Name(s.Value))}"))}, {moved} materials; " +
                              $"read back: {string.Join(",", slots.Keys.Select(v => $"{v}={check.GetValueOrDefault(v)}"))}");
        return moved;
    }

    /// Own paint `slot`'s paint job: made if it isn't there yet (registered with the design, so it's saved, and listed
    /// in the design's paint jobs), starting as a copy of the Primary paint.
    static void EnsureJob(VehicleMaterialPainter p, int slot)
    {
        var jobs = Jobs(p);
        if (slot != 0 && jobs.PaintJobCount <= slot)
        {
            int had = jobs.PaintJobCount;
            p.SetPaintJobCount(slot + 1);
            Plugin.ModLog.LogInfo($"Own paint: paint jobs {had} -> {jobs.PaintJobCount}: " +
                                  string.Join("; ", Enumerable.Range(0, jobs.PaintJobCount).Select(i => jobs.GetPaintJob(i) is { } j ? $"{i} '{j.Name}' slot {j.Slot} '{j.ColourMapUri}'" : $"{i} none")));
        }
        if (!themesLogged)
        {
            themesLogged = true;
            var names = PaintJobDesigner.PaintThemeNames;
            var tags = PaintJobDesigner.PaintThemeTags;
            Plugin.ModLog.LogInfo($"Own paint: Paint tab themes [{(names == null ? "" : string.Join(", ", names))}], tags [{(tags == null ? "" : string.Join(", ", tags))}]");
        }
        // A new slot has no paint job in it yet: make one, registered with the design (so it's saved), and note its
        // number in the design's list of paint jobs.
        if (slot != 0 && jobs.GetPaintJob(slot) == null && p.GetPaintJob(slot) is { } loader)
        {
            var made = loader.PaintJobReference.EnsureReference();
            var register = p.blueprint.Blueprint;
            var ids = Enumerable.Range(0, jobs.PaintJobCount).Select(i => p.GetPaintJob(i) is { } l && l.PaintJobReference.HasBlueprint ? l.PaintJobReference.BlueprintID : -1).ToArray();
            string was = string.Join(",", register.PaintJobIDs ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0));
            register.PaintJobIDs = ids;
            p.blueprint.NotifyModified();
            Plugin.ModLog.LogInfo($"Own paint: made a paint job for slot {slot} ({(made == null ? "none" : "id " + loader.PaintJobReference.BlueprintID)}); paint job list [{was}] -> [{string.Join(",", ids)}]");
        }
        if (slot != 0 && jobs.GetPaintJob(slot) is { } job && !(job.Description ?? "").StartsWith(Tag))
        {
            // A new own paint: named, and looking like the Primary paint until it's changed.
            var primary = jobs.GetPaintJob(0);
            job.Name = Name(slot);
            job.Description = Tag;
            job.ColourMapUri = primary.ColourMapUri;
            job.Scale = primary.Scale; job.Roughness = primary.Roughness; job.Metallic = primary.Metallic;
            job.TintR = primary.TintR; job.TintG = primary.TintG; job.TintB = primary.TintB;
            job.Saturation = primary.Saturation; job.Condition = primary.Condition; job.Grime = primary.Grime;
        }
    }
}

/// Images the game loads by address (camo in the Paint tab, decals): each address is logged once, and a local image the
/// address doesn't find, but which is in My Games\Sprocket, is loaded from there (the address written properly, spaces
/// and accents escaped).
[HarmonyPatch]
public static class ImageAddresses
{
    static readonly HashSet<string> seen = new();

    // Where every image download gets its address, whichever game code asks (hooks on the game's own image loader
    // never ran: its small methods are compiled into their callers).
    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequestTexture), nameof(UnityEngine.Networking.UnityWebRequestTexture.GetTexture), new[] { typeof(string) })]
    static void Texture(ref string uri) { var u = uri; Ui.Guard("Image address", () => u = Fix(u)); uri = u; }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequestTexture), nameof(UnityEngine.Networking.UnityWebRequestTexture.GetTexture), new[] { typeof(string), typeof(bool) })]
    static void TextureReadable(ref string uri) { var u = uri; Ui.Guard("Image address", () => u = Fix(u)); uri = u; }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequest), nameof(UnityEngine.Networking.UnityWebRequest.url), MethodType.Setter)]
    static void Url(ref string value) { var u = value; Ui.Guard("Image address", () => u = Fix(u)); value = u; }

    /// The same address, or one for the same image in My Games\Sprocket when the address doesn't find it.
    static string Fix(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return uri;
        bool fileUri = uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!fileUri && (uri.Contains("://") || !uri.Contains('/') && !uri.Contains('\\'))) return uri; // web or game asset: not ours to touch
        string path;
        try { path = fileUri ? new Uri(uri).LocalPath : uri; } catch (Exception) { path = uri; }
        string? found = File.Exists(path) ? path : null;
        if (found == null)
        {
            // Relative ("Sprocket/Paint/x.png") or moved: look under My Games for the same place, then the same name.
            var games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
            var tail = uri.Replace('\\', '/');
            int at = tail.IndexOf("Sprocket/", StringComparison.OrdinalIgnoreCase);
            var candidate = at >= 0 ? Path.Combine(games, tail[at..].Replace('/', Path.DirectorySeparatorChar)) : "";
            if (File.Exists(candidate)) found = candidate;
            else
                foreach (var folder in new[] { "Paint", "Decals" })
                    if (Path.Combine(games, "Sprocket", folder, Path.GetFileName(path)) is var named && File.Exists(named)) { found = named; break; }
        }
        if (found == null) { Log(uri, "file not found in My Games\\Sprocket either"); return uri; }
        // Written properly (spaces and accents escaped); an address already just that stays as it is.
        var proper = new Uri(found).AbsoluteUri;
        if (proper == uri) { Log(uri, null); return uri; }
        Log(uri, "loaded as " + proper);
        return proper;
    }

    static void Log(string uri, string? note)
    {
        if (seen.Add(uri)) Plugin.ModLog.LogInfo($"Image: '{uri}'{(note == null ? "" : ": " + note)}");
    }
}
