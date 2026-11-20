using System.Globalization;
using System.Text;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Turrets.Editor;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.Turrets;
using Sprocket.Vehicles.Weapons;
using UnityEngine;
using M = SprocketQoL.TurretPreviewMath;
using N = System.Numerics.Vector3;

namespace SprocketQoL;

/// Turret rotation and gun depression preview (a Discord request), drawn over the editor for the selected turret or gun
/// (Mod Options: for every turret, all the time):
/// - blue: the turret's turning range, at the muzzle, with its limits (or a hull-mounted gun's own traverse);
/// - yellow: the gun's elevation fan, from full depression to full elevation;
/// - green / red: the muzzle at full depression all round the turret's range, green where the barrel clears the hull,
///   red where it would go into a plate that doesn't turn with the turret. The game doesn't stop a barrel at the hull.
/// Read-only: nothing in the editor is posed or changed. Worked out again only when the design, selection or a turret's
/// place changes.
internal static class TurretPreview
{
    sealed class Wire
    {
        internal readonly List<Vector3> Points = new();
        internal readonly List<int[]> Edges = new();
        internal void Add(N a, N b) { Edges.Add(new[] { Points.Count, Points.Count + 1 }); Points.Add(U(a)); Points.Add(U(b)); }
        internal void Path(IReadOnlyList<N> points) { for (int i = 0; i + 1 < points.Count; i++) Add(points[i], points[i + 1]); }
    }
    sealed record Obstacle(EditMesh Mesh, Matrix4x4 ToLocal, Bounds Bounds);

    static Wire turn = new(), lay = new(), clear = new(), blocked = new();
    static bool any;
    static string fingerprint = "";
    static float nextLook;
    static readonly Color Blue = new(.35f, .8f, 1, .9f), Yellow = new(1, .85f, .2f, .9f), Green = new(.3f, 1, .45f, .9f), Red = new(1, .3f, .25f, .95f);

    internal static void Draw()
    {
        if (Event.current?.type != EventType.Repaint) return;
        bool always = Plugin.TurretPreviewAlways?.Value == true;
        if (!always && Plugin.TurretPreview?.Value != true || DrawingSheet.Capturing || PhotoShot.Capturing || ExplodedView.Active
            || EditorModal.BlocksInput || DesignEditor.Instance is not { IsReady: true } editor || Camera.main is not { } camera) return;
        if (Time.unscaledTime >= nextLook)
        {
            nextLook = Time.unscaledTime + 0.2f;
            try { Refresh(editor, always); }
            catch { any = false; fingerprint = ""; throw; } // Ui.Guard logs it once; nothing stale stays drawn
        }
        if (!any) return;
        foreach (var (wire, tint) in new[] { (turn, Blue), (lay, Yellow), (clear, Green), (blocked, Red) })
            if (wire.Edges.Count > 0) GeometryPreview.DrawWire(camera, wire.Points, wire.Edges, Matrix4x4.identity, tint);
    }

    /// Its switches, in the turret ring's and the gun's panels (the settings screen doesn't scroll, and is full).
    internal static void RingPanel(TurretRingEditor __instance, Panel layout) => Switches(layout, __instance.RequestRedraw);
    internal static void GunPanel(CannonEditor __instance, Panel layout) => Switches(layout, __instance.RequestRedraw);

    static void Switches(Panel layout, Action redraw) => Ui.Inspector("Turret preview", layout, () =>
    {
        var ui = Ui.Drawer(layout);
        if (ui == null) return;
        Ui.Section(layout, "Turret preview");
        ui.ToggleField("Turret preview", Plugin.TurretPreview?.Value ?? true, Ui.BoolCallback(v => { if (Plugin.TurretPreview != null) Plugin.TurretPreview.Value = v; redraw(); }),
            "While a turret or gun part is selected: its turning range, the gun's elevation fan, and where the barrel at full depression clears the hull.");
        ui.ToggleField("Always on", Plugin.TurretPreviewAlways?.Value ?? false, Ui.BoolCallback(v => { if (Plugin.TurretPreviewAlways != null) Plugin.TurretPreviewAlways.Value = v; redraw(); }),
            "Show the preview for every turret all the time, not only the selected one.");
        ui.InfoField("Blue: turning range. Yellow: gun elevation.\nGreen / red: the barrel at full depression clears / goes into the hull.", 2);
    });

    /// One gun to preview: its mount's laying drive (null for a gun fixed to its turret) and the turret it turns with.
    sealed record Gun(int Owner, LayingDrive? Drive, Cannon Cannon, TurretRing? Ring, int RingId);

    static void Refresh(DesignEditor editor, bool always)
    {
        var selected = editor.SelectedParts();
        if (!always && selected.Count == 0) { any = false; fingerprint = ""; return; }
        var parts = editor.AllParts().GroupBy(p => (int)p.VUID).ToDictionary(g => g.Key, g => g.First());
        var graph = parts.ToDictionary(p => p.Key, p => new DrawingMounts.Part(p.Value.VehicleTransform?.Parent?.VehicleObject is { } up ? (int)up.VUID : -1,
            p.Value.GUID == DrawingMounts.TrunnionGuid, p.Value.GUID == Conversion.RingGuid));
        bool Under(int id, int ancestor)
        {
            var seen = new HashSet<int>();
            for (int p = id; p >= 0 && seen.Add(p) && graph.TryGetValue(p, out var part); p = part.Parent) if (p == ancestor) return true;
            return false;
        }
        int RingOf(int id)
        {
            var seen = new HashSet<int>();
            for (int p = graph.TryGetValue(id, out var first) ? first.Parent : -1; p >= 0 && seen.Add(p) && graph.TryGetValue(p, out var part); p = part.Parent)
                if (part.Turret) return p;
            return -1;
        }
        var components = editor.AllComponents().ToList();
        var cannons = components.Select(c => c.TryCast<Cannon>()).OfType<Cannon>().GroupBy(c => (int)c.VehicleObject.VUID).ToDictionary(g => g.Key, g => g.First());
        var rings = components.Select(c => c.TryCast<TurretRing>()).OfType<TurretRing>().GroupBy(r => (int)r.VehicleObject.VUID).ToDictionary(g => g.Key, g => g.First());
        var calibres = cannons.Select(c => (Id: c.Key, Caliber: (int)(c.Value.Blueprint?.Caliber ?? 0))).ToArray();
        var guns = new List<Gun>();
        foreach (var drive in components.Select(c => c.TryCast<LayingDrive>()).OfType<LayingDrive>())
        {
            if (drive.Trunnions?.VehicleObject is not { } mount || drive.BlueprintSlot?.HasBlueprint != true) continue;
            int mountId = (int)mount.VUID;
            if (DrawingMounts.SelectGun(mountId, calibres, graph, _ => true) is not int gunId) continue;
            int ringId = RingOf(mountId);
            guns.Add(new(ringId >= 0 ? ringId : mountId, drive, cannons[gunId], rings.GetValueOrDefault(ringId), ringId));
        }
        // A turret whose gun sits on the turret itself (no mount): its turning range only.
        foreach (var (ringId, ring) in rings)
            if (!guns.Any(g => g.RingId == ringId) && cannons.Where(c => RingOf(c.Key) == ringId)
                    .OrderByDescending(c => c.Value.Blueprint?.Caliber ?? 0).ThenBy(c => c.Key).Select(c => c.Value).FirstOrDefault() is { } cannon)
                guns.Add(new(ringId, null, cannon, ring, ringId));
        if (!always) guns = guns.Where(g => selected.Any(s => Under(s, g.Owner))).ToList();
        if (guns.Count == 0) { any = false; fingerprint = ""; return; }

        // Worked out again only when something it shows could have changed.
        var key = new StringBuilder().Append(editor.Session).Append('|').Append(editor.Core?.Editor?.ops?.UndoCount ?? -1).Append('|').Append(string.Join(",", selected));
        void Place(Transform t) { var p = t.position; var r = t.rotation; foreach (float f in new[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w }) key.Append('|').Append(f.ToString("R", CultureInfo.InvariantCulture)); }
        foreach (var g in guns)
        {
            key.Append("|g").Append(g.Cannon.VehicleObject.VUID);
            Place(g.Cannon.transform);
            if (g.Drive is { } d) { Place(d.Trunnions.transform); key.Append('|').Append(d.BlueprintSlot.Blueprint.Elevation.Min).Append('|').Append(d.BlueprintSlot.Blueprint.Elevation.Max); }
            if (g.Ring is { } r)
            {
                Place(r.transform);
                if (r.motor?.TraverseBlueprintSlot is { HasBlueprint: true } slot) key.Append('|').Append(slot.Blueprint.MinAngle).Append('|').Append(slot.Blueprint.MaxAngle);
            }
        }
        if (key.ToString() == fingerprint) return;
        fingerprint = key.ToString();

        turn = new(); lay = new(); clear = new(); blocked = new();
        var obstacles = new Dictionary<int, List<Obstacle>>();
        foreach (var g in guns)
        {
            if (!Muzzle(g.Cannon, out var muzzle)) continue;
            var traverse = g.Ring?.motor?.TraverseBlueprintSlot is { HasBlueprint: true } slot ? slot.Blueprint : null;
            N centre = g.Ring != null ? V(g.Ring.transform.position) : default, axis = g.Ring != null ? V(g.Ring.transform.up) : default;
            if (g.Drive is { } drive)
            {
                var trunnions = drive.Trunnions.transform;
                var bp = drive.BlueprintSlot.Blueprint;
                N pivot = V(trunnions.position), raise = -V(trunnions.right);
                float low = bp.Elevation.Min, high = bp.Elevation.Max;
                var fan = M.Angles(low, high, 2).Select(a => M.Turn(muzzle, pivot, raise, a)).ToArray();
                if (fan.Length > 1) { lay.Path(fan); lay.Add(pivot, fan[0]); lay.Add(pivot, fan[^1]); }
                if (g.Ring != null && traverse != null && low < 0)
                    Sweep(pivot, M.Turn(muzzle, pivot, raise, low), centre, axis, traverse.MinAngle, traverse.MaxAngle,
                        RingRadius(g.Ring), Obstacles(g.RingId));
                else if (g.Ring == null && bp.Azimuth.Max > bp.Azimuth.Min) // a hull-mounted gun's own traverse
                    turn.Path(M.Angles(bp.Azimuth.Min, bp.Azimuth.Max, 2).Select(a => M.Turn(muzzle, pivot, V(trunnions.up), a)).ToArray());
            }
            if (traverse != null)
            {
                turn.Path(M.Angles(traverse.MinAngle, traverse.MaxAngle, 2).Select(a => M.Turn(muzzle, centre, axis, a)).ToArray());
                if (!M.Full(traverse.MinAngle, traverse.MaxAngle))
                    foreach (float limit in new float[] { traverse.MinAngle, traverse.MaxAngle })
                        turn.Add(M.Turn(V(g.Cannon.transform.position), centre, axis, limit), M.Turn(muzzle, centre, axis, limit));
            }
        }
        any = turn.Edges.Count + lay.Edges.Count + clear.Edges.Count + blocked.Edges.Count > 0;

        List<Obstacle> Obstacles(int ringId)
        {
            if (obstacles.TryGetValue(ringId, out var list)) return list;
            obstacles[ringId] = list = new();
            foreach (var (id, part) in parts)
            {
                if (Under(id, ringId)) continue; // turns with the turret
                var structure = DesignEditor.Each(part.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s?.Mesh?.EditMesh != null);
                if (structure?.Mesh?.EditMesh is not { } mesh || mesh.faces.Count == 0) continue;
                var origin = structure.StructureToWorldSpace(Vector3.zero);
                var x = structure.StructureToWorldSpace(Vector3.right) - origin;
                var y = structure.StructureToWorldSpace(Vector3.up) - origin;
                var z = structure.StructureToWorldSpace(Vector3.forward) - origin;
                var toWorld = new Matrix4x4(new(x.x, x.y, x.z, 0), new(y.x, y.y, y.z, 0), new(z.x, z.y, z.z, 0), new(origin.x, origin.y, origin.z, 1));
                if (!float.IsFinite(toWorld.determinant) || Math.Abs(toWorld.determinant) < 1e-10f) continue;
                list.Add(new(mesh, toWorld.inverse, structure.Mesh.LocalBounds));
            }
            return list;
        }
    }

    /// The muzzle all round the turret's range at full depression, 2° apart: green where the barrel, from the edge of its
    /// turret ring out, clears every plate that doesn't turn with the turret; red (with the barrel drawn) where it doesn't.
    static void Sweep(N pivot, N depressed, N centre, N axis, float min, float max, float radius, List<Obstacle> obstacles)
    {
        N? last = null;
        bool lastHit = false;
        int i = 0;
        foreach (float a in M.Angles(min, max, 2))
        {
            N from = M.Turn(pivot, centre, axis, a), to = M.Turn(depressed, centre, axis, a);
            float start = M.Outside(from, to, centre, axis, radius);
            bool hit = start < 1 && Hits(obstacles, N.Lerp(from, to, start), to);
            if (last is { } previous) (hit || lastHit ? blocked : clear).Add(previous, to);
            if (hit && i % 3 == 0) blocked.Add(from, to);
            last = to; lastHit = hit; i++;
        }
    }

    static bool Hits(List<Obstacle> obstacles, N from, N to)
    {
        foreach (var o in obstacles)
        {
            var a = o.ToLocal.MultiplyPoint3x4(U(from));
            var dir = o.ToLocal.MultiplyPoint3x4(U(to)) - a;
            float length = dir.magnitude;
            if (!(length > 1e-6f) || !float.IsFinite(length)) continue;
            dir /= length;
            if (!o.Bounds.IntersectRay(new Ray(a, dir), out float enter) || enter > length) continue;
            if (MeshSampling.RaycastFaces(o.Mesh, a, dir, length, out MeshSampling.ElementHit _, FaceMode.Both)) return true;
        }
        return false;
    }

    /// The turret ring's radius (metres), past which the barrel is over the hull rather than its own turret.
    static float RingRadius(TurretRing ring) => ring.ringRadius is var r && float.IsFinite(r) && r > 0.05f && r < 10 ? r : 0.3f;

    /// The muzzle: the middle of the barrel's farthest-forward points (as the drawing sheet finds it), else the barrel's
    /// length forward from the gun.
    static bool Muzzle(Cannon cannon, out N tip)
    {
        tip = default;
        var forward = V(cannon.transform.forward);
        var points = new List<N>();
        if (cannon.Barrel?.BarrelTransforms is { } barrel)
            foreach (var t in barrel)
                if (t != null)
                    foreach (var filter in t.GetComponentsInChildren<MeshFilter>())
                        if (filter.sharedMesh is { isReadable: true } mesh)
                        {
                            var matrix = filter.transform.localToWorldMatrix;
                            foreach (var v in mesh.vertices) points.Add(V(matrix.MultiplyPoint3x4(v)));
                        }
        if (points.Count > 0)
        {
            float end = points.Max(p => N.Dot(p, forward));
            var front = points.Where(p => end - N.Dot(p, forward) < 0.002f).ToList();
            tip = front.Aggregate(N.Zero, (s, p) => s + p) / front.Count;
            return true;
        }
        int length = cannon.Blueprint?.BarrelLength ?? 0;
        if (length <= 0) return false;
        tip = V(cannon.transform.position) + forward * (length / 1000f);
        return true;
    }

    static N V(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 U(N v) => new(v.X, v.Y, v.Z);
}
