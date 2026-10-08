using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.PlateMesh.Rivets;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Structure panel (hand-made shapes): "Merge selected faces" joins selected faces that share edges into as few faces
/// as their outline allows (FaceMerge does the maths). It runs as one of the game's own mesh edits, so Ctrl+Z undoes it.
[HarmonyPatch]
public static class MergeFaces
{
    static int sides; // index into SideNames / SideModes
    static bool mirror; // the editor's Mirror was on when Merge was pressed
    static readonly string[] SideNames = { "Shared points: remove extra lines", "Shared points: allow gaps", "Shared points: keep corners" };
    static readonly FaceMerge.SidePoints[] SideModes = { FaceMerge.SidePoints.TakeOutLine, FaceMerge.SidePoints.RunPast, FaceMerge.SidePoints.Keep };

    internal static void Draw(PlateStructureEditor __instance, Panel layout) => Ui.Inspector("Merge faces", layout, () =>
    {
        var ui = Ui.Drawer(layout);
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return; // face editing is freeform only
        Ui.Section(layout, "Merge faces");
        ui.InfoField("Faces mode: select adjacent faces to simplify their shared surface. Ctrl+Z undoes it.", 2);
        var sideTip = new Tip("Shared boundary points", "Click to cycle how straight-side points shared with unselected faces are handled. Remove extra lines also rebuilds adjoining faces, keeping them connected. Allow gaps skips shared points and can leave seams when moved. Keep corners preserves every shared boundary point.");
        ui.Button(SideNames[sides], Ui.Callback(() => { sides = (sides + 1) % SideNames.Length; __instance.RequestRedraw(); }), sideTip);
        var tip = new Tip("Merge selected faces", "Select at least two faces sharing an edge. Combines coplanar faces into fewer faces; bends over 20 degrees stay separate. Mirror follows the editor's Mirror setting. Ctrl+Z undoes it.");
        ui.Button("Merge selected faces", Ui.Callback(() =>
        {
            mirror = __instance.meshEditor.Symmetry;
            MeshTools.Run(__instance, "Merge faces", mesh => Apply(mesh) is var result && result.StartsWith("merged") ? (true, result) : (false, result));
        }), tip);

        Ui.Section(layout, "Separate");
        ui.InfoField("Move selected faces into new add-ons without changing their position. Ctrl+Z undoes it.", 2);
        var sepTip = new Tip("Separate selection", "The selected faces leave this part and become a new add-on in the same place, " +
            "with their thickness, armour and rivets. In Points or Edges mode, faces whose corners are all selected go. " +
            "With Mirror on, the mirrored faces go too; a mirrored part's twin (or image) gives up the same faces to a twin of the new add-on.");
        ui.Button("Selection to new add-on", Ui.Callback(() => Separate(__instance, pieces: false)), sepTip);
        var pieceTip = new Tip("Separate picked pieces", "For a shape already in pieces that don't touch: click one face on each piece " +
            "(Shift for more) and each whole piece becomes its own add-on in the same place. Pick every piece and the biggest stays here.");
        ui.Button("Disconnected pieces to add-ons", Ui.Callback(() => Separate(__instance, pieces: true)), pieceTip);
    });

    /// The selected faces into a new add-on, or (`pieces`) each loose piece with a selected face into its own.
    static void Separate(PlateStructureEditor e, bool pieces)
    {
        var editor = DesignEditor.Instance;
        var mesh = e.meshEditor?.Mesh?.EditMesh;
        if (editor == null || mesh == null) return;
        var v = new MeshTools.View(mesh);
        var faces = new HashSet<int>(v.SelectedFaces);
        if (e.meshEditor!.SelectType != MeshEditType.Face) // points or edges: every face they fully enclose, as Blender
            for (int f = 0; f < v.Corners.Count; f++) if (v.Corners[f].All(v.SelectedPoints.Contains)) faces.Add(f);
        faces = MeshTools.WithTwins(v, faces, e.meshEditor.Symmetry);
        if (faces.Count == 0) { e.operations.NotifyError(pieces ? "Separate pieces: click a face on each piece first" : "Separate: select faces (or the points around them) first"); return; }
        var picked = faces.Select(f => v.Corners[f].Select(p => v.Pos[p]).ToArray()).ToList();
        int part = (int)e.Component.VehicleObject.VUID;
        if (pieces)
            editor.RequestSeparate("Separating pieces", "Separated the picked pieces into their own add-ons.", json => AddonEdits.SeparatePieces(json, part, picked));
        else
            editor.RequestSeparate("Separating faces", "Separated into a new add-on.", json =>
            {
                var (result, parts, log) = AddonEdits.Separate(json, part, picked);
                return (result, new List<List<(int Source, int Added)>> { parts }, log);
            });
    }

    static string Apply(EditMesh mesh)
    {
        var index = new Dictionary<IntPtr, int>();
        var verts = new List<Vertex>();
        var pos = new List<Num>();
        int Id(Vertex v)
        {
            if (!index.TryGetValue(v.Pointer, out int i)) { index[v.Pointer] = i = verts.Count; verts.Add(v); pos.Add(HoleQuality.ToNum(v.position)); }
            return i;
        }
        // Every face (the plan may take lines on through unselected ones), which are selected, and loose edges.
        var faces = mesh.faces;
        var all = new List<Face>();
        var corners = new List<int[]>();
        var selected = new HashSet<int>();
        for (int i = 0; i < faces.Count; i++)
        {
            all.Add(faces[i]);
            corners.Add(HoleQuality.Corners(faces[i], Id));
            if (faces[i].HasFlag(ElementFlags.Selected)) selected.Add(i);
        }
        var loose = new HashSet<(int, int)>();
        var edges = mesh.edges;
        for (int i = 0; i < edges.Count; i++)
            if (edges[i].loop == null) loose.Add(FaceMerge.Key(Id(edges[i].v0), Id(edges[i].v1)));
        if (selected.Count < 2) return "select two or more faces that share edges first";
        // Mirror on: the faces mirroring the selection merge too, matched the way the game's Mirror matches points.
        int twins = 0;
        if (mirror)
        {
            float tolerance = MeshTransformation.MirrorMaxDistance > 0 ? MeshTransformation.MirrorMaxDistance : 0.0003f;
            foreach (int f in FaceMerge.Mirrored(pos, corners, selected.ToList(), tolerance)) if (selected.Add(f)) twins++;
        }

        var plan = FaceMerge.Plan(pos, corners, selected, loose, SideModes[sides]);
        var todo = plan.Where(g => g.Why == null).ToList();
        var why = string.Join("; ", plan.Where(g => g.Why != null).Select(g => g.Why).Distinct());
        if (todo.Count == 0) return "nothing merged: " + why;
        // Checked before anything changes (running past points leaves them on the merged face's side on purpose).
        var asPlan = new MeshPlans.Rebuild(todo.SelectMany(g => g.Faces).Distinct().ToList(),
            todo.SelectMany(g => g.NewFaces.Select(nf => new MeshPlans.NewFace(nf, g.Faces[0]))).ToList(), new(), null);
        if (MeshPlans.Check(pos, corners, asPlan, gaps: SideModes[sides] != FaceMerge.SidePoints.RunPast) is string broken) return "nothing merged: " + broken;

        // Edges that stay: those of every face not being replaced, loose ones, and (added below) the new faces' sides.
        var replaced = todo.SelectMany(g => g.Faces).ToHashSet();
        var kept = new HashSet<(int, int)>(loose);
        for (int f = 0; f < corners.Count; f++)
            if (!replaced.Contains(f))
                for (int k = 0; k < corners[f].Length; k++) kept.Add(FaceMerge.Key(corners[f][k], corners[f][(k + 1) % corners[f].Length]));
        var rivets = new MeshTools.RivetKeeper(mesh, replaced.Select(f => all[f]));
        var made = new List<(Face Face, Dictionary<int, Loop> Old)>();
        foreach (var g in todo)
        {
            // Each corner keeps the thickness and thickening of a face that had it.
            var old = new Dictionary<int, Loop>();
            foreach (int f in g.Faces)
            {
                var l = all[f].firstLoop;
                for (int k = 0; k < all[f].vertexCount; k++, l = l.next) old.TryAdd(Id(l.vertex), l);
            }
            var prototype = all[g.Faces[0]];
            foreach (var nf in g.NewFaces)
            {
                var vs = nf.Select(i => verts[i]).ToArray();
                var es = new Edge[vs.Length];
                for (int k = 0; k < vs.Length; k++)
                {
                    int a = nf[k], b = nf[(k + 1) % nf.Length];
                    kept.Add(FaceMerge.Key(a, b));
                    es[k] = Edge.GetConnectingEdge(vs[k], verts[b]) ?? NewEdge(a, b);
                }
                made.Add((mesh.CreateFace(new Il2CppReferenceArray<Vertex>(vs), new Il2CppReferenceArray<Edge>(es), prototype,
                    new Il2CppStructArray<ushort>(nf.Select(i => old[i].thickness).ToArray())), old));
            }

            Edge NewEdge(int a, int b)
            {
                // A side along the outline copies the old side it replaces (sharp or not); one across the patch is smooth.
                bool outline = g.Joined.TryGetValue(FaceMerge.Key(a, b), out var was);
                var like = (outline ? Edge.GetConnectingEdge(verts[was.From], verts[was.OldNext]) : null) ?? prototype.firstLoop.edge;
                var e = mesh.CreateEdge(verts[a], verts[b], outline ? like : null);
                const ElementFlags authored = ElementFlags.Selected | ElementFlags.Sharp | ElementFlags.AlternatePlateConnection;
                e.flags = outline ? like.flags & authored : ElementFlags.None;
                return e;
            }
        }

        // Out go the merged faces, the edges no face uses any more, and the points inside and along straight sides.
        foreach (var g in todo)
        {
            foreach (int f in g.Faces)
            {
                var l = all[f].firstLoop;
                for (int k = 0; k < all[f].vertexCount; k++, l = l.next)
                    if (!kept.Contains(FaceMerge.Key(Id(l.vertex), Id(l.next.vertex)))) l.edge.EnableFlag(ElementFlags.Delete);
                all[f].EnableFlag(ElementFlags.Delete);
            }
            foreach (int v in g.Removed) verts[v].EnableFlag(ElementFlags.Delete);
        }
        foreach (var (face, old) in made)
        {
            var l = face.firstLoop;
            for (int k = 0; k < face.vertexCount; k++, l = l.next)
            {
                MeshTools.CopyThickening(l, old[Id(l.vertex)]);
            }
        }
        var (rivetsKept, rivetsLost) = rivets.Place(made.Select(m => m.Face), reach: 0.05f);
        int repointed = MeshTools.FinishDelete(mesh);

        var problems = made.Select(m => HoleQuality.Problem(m.Face)).Where(p => p != null).Distinct().ToList();
        int leftOn = todo.SelectMany(g => g.LeftOn).Distinct().Count();
        int neighbours = replaced.Count(f => !selected.Contains(f));
        return $"merged {replaced.Count} faces into {made.Count}" + (mirror ? $" (Mirror: {twins} mirrored faces added)" : "") + (neighbours == 0 ? "" : $" ({neighbours} of them unselected, rebuilt to take their lines out)") +
               (leftOn == 0 ? "" : $", running past {leftOn} point{(leftOn == 1 ? "" : "s")} other faces keep") +
               (repointed == 0 ? "" : $", {repointed} corner{(repointed == 1 ? "" : "s")} now thicken the game's way") + (why == "" ? "" : $" (left alone: {why})") +
               (rivets.Count == 0 ? "" : $", rivets {rivetsKept} kept" + (rivetsLost > 0 ? $" {rivetsLost} lost" : "")) +
               (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems));
    }
}
