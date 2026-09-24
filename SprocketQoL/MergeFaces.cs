using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Structure panel (hand-made shapes): "Merge selected faces" joins selected faces that share edges into as few faces
/// as their outline allows (FaceMerge does the maths). It runs as one of the game's own mesh edits, so Ctrl+Z undoes it.
[HarmonyPatch]
public static class MergeFaces
{
    // The game's Delete operation, given nothing to delete, carries the merge so it gets the game's undo and mesh
    // rebuild. Only these instances merge; every other Delete runs as normal.
    static readonly List<DeleteOp> ours = new();
    static PlateStructureEditOperations? notify;
    static int sides; // index into SideNames / SideModes
    static readonly string[] SideNames = { "Points other faces use: take out their lines", "Points other faces use: run past them", "Points other faces use: keep as corners" };
    static readonly FaceMerge.SidePoints[] SideModes = { FaceMerge.SidePoints.TakeOutLine, FaceMerge.SidePoints.RunPast, FaceMerge.SidePoints.Keep };

    // Declared before Draw: if this can't attach, Harmony stops here and the button never shows.
    [HarmonyPrefix, HarmonyPatch(typeof(DeleteOp), "ExecuteInternal")]
    static bool Intercept(DeleteOp __instance, EditMesh mesh)
    {
        if (!ours.Any(o => o.Pointer == __instance.Pointer)) return true;
        Ui.Guard("Merge faces", () =>
        {
            string result = Apply(mesh);
            Plugin.ModLog.LogInfo("Merge faces: " + result);
            if (!result.StartsWith("merged")) notify?.NotifyError("Merge faces: " + result);
        });
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Merge faces", () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return; // face editing is freeform only
        Ui.Section(layout, "Merge faces");
        // Explicit line breaks: the panel shows exactly the lines it's told, and cuts off the rest.
        ui.InfoField("Select faces in Faces edit mode, then merge.\nFlat faces become as few faces as they can.\nCtrl+Z undoes it.", 3);
        var sideTip = new UITooltip("Points other faces use", "A point on a straight side that an unselected face also uses (a line running on into it). " +
            "Take out their lines: the faces along each line are rebuilt without it too, up to a real corner or the plate's edge, so everything stays joined. " +
            "Run past them: the merged face runs straight past, the other face keeps the point (like Delete + Fill); not joined there, so moving it opens a gap. " +
            "Keep as corners: they stay corners of the merged faces.");
        ui.Button(SideNames[sides], Ui.Callback(() => { sides = (sides + 1) % SideNames.Length; __instance.RequestRedraw(); }), ref sideTip);
        var tip = new UITooltip("Merge selected faces", "Two triangles become a quad, a strip of quads one quad, a fan a few quads. " +
                                "The selection splits at bends over 20°; a straight line of points shared across a bend goes from both sides.");
        ui.Button("Merge selected faces", Ui.Callback(() => Ui.Guard("Merge faces", () =>
        {
            var op = new DeleteOp(DeleteType.None) { Name = "Merge faces" };
            ours.Add(op);
            var ops = notify = __instance.operations;
            ops.Execute(__instance.meshEditor.CreateTopoOp(op), StructureEditOperationOptions.None, ops.GetNewGroupID());
            __instance.meshEditor.SelectFlush();
        })), ref tip);
    });

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

        var plan = FaceMerge.Plan(pos, corners, selected, loose, SideModes[sides]);
        var todo = plan.Where(g => g.Why == null).ToList();
        var why = string.Join("; ", plan.Where(g => g.Why != null).Select(g => g.Why).Distinct());
        if (todo.Count == 0) return "nothing merged: " + why;

        // Edges that stay: those of every face not being replaced, loose ones, and (added below) the new faces' sides.
        var replaced = todo.SelectMany(g => g.Faces).ToHashSet();
        var kept = new HashSet<(int, int)>(loose);
        for (int f = 0; f < corners.Count; f++)
            if (!replaced.Contains(f))
                for (int k = 0; k < corners[f].Length; k++) kept.Add(FaceMerge.Key(corners[f][k], corners[f][(k + 1) % corners[f].Length]));
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
                var e = mesh.CreateEdge(verts[a], verts[b], like);
                if (outline && like.HasFlag(ElementFlags.Sharp)) e.EnableFlag(ElementFlags.Sharp);
                else e.DisableFlag(ElementFlags.Sharp);
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
                l.thickenMode = old[Id(l.vertex)].thickenMode;
                l.thickenEdge = old[Id(l.vertex)].thickenEdge;
            }
        }
        // A corner (new, or of a face next to the merge) that thickens along an edge being deleted lets the game pick.
        int repointed = 0;
        for (int i = 0; i < faces.Count; i++)
        {
            if (faces[i].HasFlag(ElementFlags.Delete)) continue;
            var l = faces[i].firstLoop;
            for (int k = 0; k < faces[i].vertexCount; k++, l = l.next)
                if (l.thickenEdge != null && l.thickenEdge.HasFlag(ElementFlags.Delete))
                {
                    l.thickenEdge = null;
                    if (l.thickenMode == ThickenMode.AlongEdgeManual) l.thickenMode = ThickenMode.AlongEdgeAuto;
                    repointed++;
                }
        }
        mesh.DeleteMarked();
        mesh.MarkDirty(MeshDirtyFlags.All);

        var problems = made.Select(m => HoleQuality.Problem(m.Face)).Where(p => p != null).Distinct().ToList();
        int leftOn = todo.SelectMany(g => g.LeftOn).Distinct().Count();
        int neighbours = replaced.Count(f => !selected.Contains(f));
        return $"merged {replaced.Count} faces into {made.Count}" + (neighbours == 0 ? "" : $" ({neighbours} of them unselected, rebuilt to take their lines out)") +
               (leftOn == 0 ? "" : $", running past {leftOn} point{(leftOn == 1 ? "" : "s")} other faces keep") +
               (repointed == 0 ? "" : $", {repointed} corner{(repointed == 1 ? "" : "s")} now thicken the game's way") + (why == "" ? "" : $" (left alone: {why})") +
               (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems));
    }
}
