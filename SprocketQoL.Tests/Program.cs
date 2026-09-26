using System.Text.Json.Nodes;
using SprocketQoL;
using System.Numerics;
int checks=0, conversions=0;
void Check(bool ok,string message) { checks++; if(!ok)throw new Exception(message); }
void Run(string json,int id)
{
    var before=Conversion.Parse(json);
    var choice=Conversion.List(json).Single(t=>t.Id==id);
    var result=Conversion.Convert(json,id);
    var after=Conversion.Parse(result.Json);
    var a=Conversion.Objects(before);var b=Conversion.Objects(after);
    Check(b[choice.BodyId]["guid"]!.GetValue<string>()==Conversion.AddonGuid,"body GUID");
    Check(!b.ContainsKey(id),"ring removed");
    Check(a.Values.Count(x=>x.ContainsKey("cannon"))==b.Values.Count(x=>x.ContainsKey("cannon")),"guns preserved");
    Check(a.Values.Count(x=>x.ContainsKey("crewSeat"))==b.Values.Count(x=>x.ContainsKey("crewSeat")),"crew preserved");
    Check(before["meshes"]?.ToJsonString()==after["meshes"]?.ToJsonString(),"meshes preserved byte-for-byte JSON");
    var oldStructure=before["blueprints"]!.AsArray().Single(x=>x!["id"]!.GetValue<int>()==a[choice.BodyId]["structureBlueprintVuid"]!.GetValue<int>());
    var newStructure=after["blueprints"]!.AsArray().Single(x=>x!["id"]!.GetValue<int>()==b[choice.BodyId]["structureBlueprintVuid"]!.GetValue<int>());
    Check(oldStructure!.ToJsonString()==newStructure!.ToJsonString(),"geometry/armour blueprint retained exactly");
    Check(Conversion.List(result.Json).Count==Conversion.List(json).Count-1,"only chosen turret converted");
    // Compare transformed basis points independently of the converter's matrix comparison.
    var aw=Conversion.WorldMatrices(a);var bw=Conversion.WorldMatrices(b);
    foreach(var v in b.Keys)foreach(var point in new[]{Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
        Check(Vector3.Distance(Vector3.Transform(point,aw[v]),Vector3.Transform(point,bw[v]))<0.002f,"world basis preserved");
    conversions++;
}
string root=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)+@"\My Games\Sprocket\Factions\PMC\Blueprints\Vehicles";
var files=Directory.GetFiles(root,"*.blueprint").OrderBy(f=>new FileInfo(f).Length).Take(12).ToList();
foreach(var f in files)
{
    string json=File.ReadAllText(f);
    foreach(var t in Conversion.List(json))Run(json,t.Id);
}
string sample=File.ReadAllText(Path.Combine(root,"Autosave.blueprint"));
var first=Conversion.List(sample)[0];
foreach(var rot in new[]{new[]{25f,180f,15f,0f},new[]{90f,30f,0f,0f},new[]{-90f,30f,10f,0f}})
{
    var b=Conversion.Parse(sample);var objects=Conversion.Objects(b);
    objects[first.Id]["transform"]!["rot"]=new JsonArray(rot.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());
    objects[first.BodyId]["transform"]!["rot"]=new JsonArray(17,26,31,0);
    Run(b.ToJsonString(),first.Id);
}
// An unrelated integer list must not be scrubbed when its numbers overlap removed components.
{
    var b=Conversion.Parse(sample);b["blueprints"]![0]!["blueprint"]!["unrelatedIndices"]=new JsonArray(299,300,301,304);
    var converted=Conversion.Parse(Conversion.Convert(b.ToJsonString(),first.Id).Json);
    Check(converted["blueprints"]![0]!["blueprint"]!["unrelatedIndices"]!.ToJsonString()=="[299,300,301,304]","no broad integer filtering");
}
// Converting again (or an already doubled name) keeps exactly one " (Addon)".
{
    var b=Conversion.Parse(sample);b["header"]!["name"]="Tank (Addon) (Addon)";
    var named=Conversion.Parse(Conversion.Convert(b.ToJsonString(),first.Id).Json);
    Check(named["header"]!["name"]!.GetValue<string>()=="Tank (Addon)","single (Addon) suffix");
}
foreach(string kind in new[]{"cycle","missing","bad-selection"})
{
    var b=Conversion.Parse(sample);var objects=Conversion.Objects(b);
    if(kind=="cycle")objects[first.Id]["pvuid"]=first.BodyId;
    if(kind=="missing")objects[first.Id]["pvuid"]=987654321;
    string untouched=b.ToJsonString();bool failed=false;
    try{Conversion.Convert(untouched,kind=="bad-selection"?-123:first.Id);}catch{failed=true;}
    Check(failed,$"reject {kind}");Check(untouched==b.ToJsonString(),"failure leaves original intact");
}
Console.WriteLine($"CONVERSION_TESTS_OK: {checks} checks, {conversions} conversions, {files.Count} real blueprint files plus rotated fixtures");

// ---------------- Add-on shape edits ----------------
const string Compartment = "7f8a9d20-eb45-482e-b149-014c964c4e2c";
void CheckRefs(JsonObject bp, string what)
{
    var blockIds = bp["blueprints"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).ToList();
    Check(blockIds.Count == blockIds.Distinct().Count(), $"{what}: block ids unique");
    var meshIds = bp["meshes"]!.AsArray().Select(x => x!["vuid"]!.GetValue<int>()).ToHashSet();
    foreach (var o in bp["objects"]!.AsArray())
        foreach (var kv in o!.AsObject().Where(kv => kv.Key.EndsWith("BlueprintVuid")))
            Check(blockIds.Contains(kv.Value!.GetValue<int>()), $"{what}: block reference resolves");
    foreach (var s in bp["blueprints"]!.AsArray().Where(x => x!["type"]!.GetValue<string>() == "structure"))
        if (s!["blueprint"]!["bodyMeshVuid"] is JsonValue m) Check(meshIds.Contains(m.GetValue<int>()), $"{what}: mesh reference resolves");
    var vuids = bp["objects"]!.AsArray().Select(o => o!["vuid"]!.GetValue<int>()).ToList();
    Check(vuids.Count == vuids.Distinct().Count(), $"{what}: part ids unique");
    Check(bp["objects"]!.AsArray().All(o => o!["pvuid"]!.GetValue<int>() < 0 || vuids.Contains(o["pvuid"]!.GetValue<int>())), $"{what}: every parent exists");
}
Vector3[] Verts(JsonObject bp, JsonObject o)
{
    int block = o["structureBlueprintVuid"]!.GetValue<int>();
    int mesh = bp["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == block)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
    var v = bp["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == mesh)!["meshData"]!["mesh"]!["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    return Enumerable.Range(0, v.Length / 3).Select(i => new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2])).ToArray();
}

// 1) The generated cylinder is a closed shell with outward faces and the right volume and armour.
{
    const float r = 0.5f, h = 0.3f; const int n = 16, t = 20;
    var (meshData, armour) = AddonEdits.Cylinder(r, h, n, t);
    var mesh = meshData["mesh"]!;
    var vs = mesh["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    Vector3 P(int i) => new(vs[3 * i], vs[3 * i + 1], vs[3 * i + 2]);
    var faces = mesh["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();
    var uses = faces.SelectMany(f => f.Select((v, k) => (Math.Min(v, f[(k + 1) % f.Length]), Math.Max(v, f[(k + 1) % f.Length])))).GroupBy(e => e).ToList();
    Check(uses.All(g => g.Count() == 2), "cylinder is closed");
    Check(mesh["edges"]!.AsArray().Count / 2 == uses.Count, "cylinder edge list matches its faces");
    double volume = faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(P(f[0]), Vector3.Cross(P(f[k]), P(f[k + 1]))) / 6.0));
    double capArea = n / 2.0 * r * r * Math.Sin(2 * Math.PI / n), sideArea = n * 2 * r * Math.Sin(Math.PI / n) * h;
    Check(volume > 0 && Math.Abs(volume - capArea * h) < 1e-4, $"cylinder faces point outward with the right volume ({volume:0.0000})");
    Check(Math.Abs(armour - (2 * capArea + sideArea) * t / 1000.0) < 1e-5, "cylinder armour volume = area x thickness");
}

// 2) The round palette part has the cube part's layout and is a closed, outward-facing, cube-sized shell.
{
    var cube = Conversion.Parse(File.ReadAllText(@"C:\Program Files (x86)\Steam\steamapps\common\Sprocket\Sprocket_Data\StreamingAssets\Parts\cubePlateStructurePart.json"));
    var part = Conversion.Parse(AddonEdits.RoundAddonPart("00000000-0000-0000-0000-000000000016", "roundAddonTest", 16));
    string Shape(JsonNode n) => string.Join(",", n.AsObject().Select(kv => kv.Key)); // key names at one level
    var cubeInfo = cube["components"]![0]!["info"]!; var partInfo = part["components"]![0]!["info"]!;
    Check(Shape(part) == Shape(cube) && Shape(partInfo["mesh"]!) == Shape(cubeInfo["mesh"]!) && Shape(partInfo["mesh"]!["mesh"]!) == Shape(cubeInfo["mesh"]!["mesh"]!)
          && part["tags"]![0]!.GetValue<string>() == "plateStructurePrefab" && part["components"]![0]!["type"]!.GetValue<string>() == "plateStructurePrefab",
          "round part has the same layout as the game's cube part");
    var mesh = partInfo["mesh"]!["mesh"]!;
    var vs = mesh["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
    Vector3 P(int i) => new(vs[3 * i], vs[3 * i + 1], vs[3 * i + 2]);
    var faces = mesh["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();
    var uses = faces.SelectMany(f => f.Select((v, k) => (Math.Min(v, f[(k + 1) % f.Length]), Math.Max(v, f[(k + 1) % f.Length])))).GroupBy(e => e);
    double volume = faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(P(f[0]), Vector3.Cross(P(f[k]), P(f[k + 1]))) / 6.0));
    Check(uses.All(g => g.Count() == 2) && volume > 0, "round part is closed and faces outward");
    Check(Enumerable.Range(0, vs.Length / 3).All(i => Math.Abs(P(i).X) <= 0.1251 && Math.Abs(P(i).Z) <= 0.1251 && P(i).Y >= 0 && P(i).Y <= 0.25),
          "round part fits the cube's 0.25 m footprint, standing on y = 0");
}

// 2b) Converting several turrets in one go (multi-select) removes every ring and moves nothing.
int multi = 0;
foreach (var file in Directory.GetFiles(Path.GetDirectoryName(root)!.Replace(@"\PMC\Blueprints", ""), "*.blueprint", SearchOption.AllDirectories)
             .OrderBy(f => new FileInfo(f).Length))
{
    if (multi >= 5) break;
    string json = File.ReadAllText(file);
    if (!json.Contains("\"objects\"")) continue;
    var turretsHere = Conversion.List(json);
    if (turretsHere.Count < 2) continue;
    var before = Conversion.WorldMatrices(Conversion.Objects(Conversion.Parse(json)));
    string edited = json;
    foreach (var t in turretsHere) edited = Conversion.Convert(edited, t.Id).Json;
    var after = Conversion.Objects(Conversion.Parse(edited));
    Check(Conversion.List(edited).Count == 0, "multi-convert leaves no turrets");
    var wa = Conversion.WorldMatrices(after);
    Check(wa.Keys.All(k => Conversion.Near(before[k], wa[k])), "multi-convert moves nothing");
    Check(Conversion.Parse(edited)["header"]!["name"]!.GetValue<string>().Split(" (Addon)").Length == 2, "multi-convert names it once");
    CheckRefs(Conversion.Parse(edited), "multi-convert");
    multi++;
}
Check(multi > 0, "found tanks with several turrets");

// 3) Merging real add-ons keeps every vertex where it was and removes only the merged parts.
int merges = 0, refusedMixed = 0;
foreach (var file in Directory.GetFiles(Path.GetDirectoryName(root)!.Replace(@"\PMC\Blueprints", ""), "*.blueprint", SearchOption.AllDirectories)
             .OrderBy(f => new FileInfo(f).Length))
{
    if (merges >= 15) break;
    string json = File.ReadAllText(file);
    if (!json.Contains("\"objects\"")) continue;
    var bp = Conversion.Parse(json);
    var objs = Conversion.Objects(bp);
    bool Freeform(JsonObject o) { try { Verts(bp, o); return true; } catch { return false; } }
    bool Above(int a, int b) { for (int p = objs[b]["pvuid"]!.GetValue<int>(); objs.ContainsKey(p); p = objs[p]["pvuid"]!.GetValue<int>()) if (p == a) return true; return false; }
    var addons = objs.Values.Where(o => Conversion.GuidOf(o) == Conversion.AddonGuid && Freeform(o)).Select(o => o["vuid"]!.GetValue<int>()).ToList();
    if (addons.Count < 2) continue;
    int target = addons[0];
    // (Not the target's own mirror twin: that one isn't merged into it.)
    var others = addons.Skip(1).Where(v => !Above(v, target) && objs[target]["transform"]!["mirrorVuid"]?.GetValue<int>() != v).Take(2).ToList();
    if (others.Count == 0) continue;
    var before = Conversion.WorldMatrices(objs);
    // Where a part's points show: a flipped part mirrors its shape along its own x, and a part saved once that the game
    // shows twice (mirrored mark, no twin part) shows its image across the centre too.
    IEnumerable<Vector3> Shown(JsonObject b, JsonObject o, Matrix4x4 world)
    {
        var shape = (o["flags"]!.GetValue<int>() & 1) != 0 ? Matrix4x4.CreateScale(-1, 1, 1) * world : world;
        var all = Conversion.Objects(b);
        bool imaged = (o["flags"]!.GetValue<int>() & 4) != 0 && !(o["transform"]!["mirrorVuid"]?.GetValue<int>() is int t && all.ContainsKey(t));
        return (imaged ? new[] { shape, shape * Matrix4x4.CreateScale(-1, 1, 1) } : new[] { shape }).SelectMany(m => Verts(b, o).Select(p => Vector3.Transform(p, m)));
    }
    AddonEdits.EditPlan mergePlan;
    try { mergePlan = AddonEdits.PlanMerge(json, target, others); }
    catch (Exception ex) when (ex.Message.Contains("a flipped part would stop being mirrored")) { refusedMixed++; continue; } // mirrored and unmirrored mixed
    var merged = Conversion.Parse(mergePlan.DesignJson);
    var objsAfter = Conversion.Objects(merged);
    Check(others.All(mergePlan.Remove.Contains) && mergePlan.MeshIds.ContainsKey(target), "merge plan: removes the merged add-ons, reshapes the target");
    Check(mergePlan.Reparent.All(r => objsAfter[r.Child]["pvuid"]!.GetValue<int>() == r.Parent && mergePlan.MeshIds.ContainsKey(r.Parent)), "merge plan: parts it moves end up on a merged part");
    Check(mergePlan.Remove.All(v => !objsAfter.ContainsKey(v)) && objsAfter.Count == objs.Count - mergePlan.Remove.Count, "merged parts removed");
    CheckRefs(merged, "merge");
    var after = Conversion.WorldMatrices(objsAfter);
    Check(objsAfter.Keys.All(k => Conversion.Near(before[k], after[k])), "merge: no surviving part moved");
    // Every point of the merged parts and the target (and its twin) shows where it did, and nothing else is added.
    var expected = mergePlan.MeshIds.Keys.Concat(mergePlan.Remove).SelectMany(v => Shown(bp, objs[v], before[v])).ToList();
    var got = mergePlan.MeshIds.Keys.SelectMany(v => Shown(merged, objsAfter[v], after[v])).ToList();
    Check(got.Count == expected.Count && CutTests.SamePoints(expected, got), "merged vertices stay put in the world");
    merges++;
}
Check(merges > 0, "found real add-ons to merge");
Console.WriteLine($"ADDON_TESTS_OK: {checks} checks total, cylinder + round part + {multi} multi-turret tanks + {merges} real merges ({refusedMixed} mixed mirrored/unmirrored refused)");

// ---------------- Create Hole ring ----------------
void CheckHole(Matrix4x4 place, float gameRadius, bool gameReversed, Vector3 gameCentre, float expectRadius, float size = 1f)
{
    var square = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) }.Select(p => Vector3.Transform(p, place)).ToArray();
    var inner = Enumerable.Range(0, 24).Select(k => (gameReversed ? -1 : 1) * 2 * MathF.PI * k / 24)
        .Select(a => Vector3.Transform(new Vector3(0.5f + gameRadius * MathF.Cos(a), 0.5f + gameRadius * MathF.Sin(a), 0.03f), place)).ToArray();
    var ring = HoleRing.Fit(square, inner, Vector3.Transform(gameCentre, place), out var note, size);
    Matrix4x4.Invert(place, out var back);
    var local = ring.Select(p => Vector3.Transform(p, back)).ToArray();
    Check(local.All(p => MathF.Abs(p.Z) < 1e-4f), "hole ring lies in the face: " + note);
    Check(local.All(p => MathF.Abs(Vector2.Distance(new(p.X, p.Y), new(0.5f, 0.5f)) - expectRadius) < 1e-4f), "hole ring is a circle of the right size: " + note);
    Check(local.All(p => p.X > 0 && p.X < 1 && p.Y > 0 && p.Y < 1), "hole ring inside the face");
    float turn = 0;
    for (int k = 0; k < local.Length; k++) turn += Vector3.Cross(local[k] - new Vector3(0.5f, 0.5f, 0), local[(k + 1) % local.Length] - new Vector3(0.5f, 0.5f, 0)).Z;
    Check(turn > 0, "hole ring turns the same way as the face");
}
var tilted = Matrix4x4.CreateFromYawPitchRoll(0.7f, -1.1f, 2.4f) * Matrix4x4.CreateTranslation(3, -2, 5);
CheckHole(Matrix4x4.Identity, 0.2f, false, new Vector3(0.5f, 0.5f, 0.03f), 0.2f);         // plain hole kept as asked
CheckHole(tilted, 0.2f, true, new Vector3(0.5f, 0.5f, 0.03f), 0.2f);                      // odd angle + ring the wrong way round
CheckHole(tilted, 0.8f, false, new Vector3(0.5f, 0.5f, 0.03f), 0.475f);                   // too big: shrunk to fit the face
CheckHole(Matrix4x4.Identity, 0.2f, false, new Vector3(4f, 4f, 0f), 0.2f);                // centre off the face: face middle
CheckHole(Matrix4x4.Identity, 0.2f, false, new Vector3(0.5f, 0.5f, 0.03f), 0.3f, 1.5f);   // Hole size 150%: applied here
CheckHole(tilted, 0.2f, false, new Vector3(0.5f, 0.5f, 0.03f), 0.475f, 3f);               // Hole size 300%: still fits the face
CheckHole(Matrix4x4.Identity, 0.2f, false, new Vector3(0.5f, 0.5f, 0.03f), 0.1f, 0.5f);   // Hole size 50%
{
    // An L-shaped (concave) face: the hole goes in the arm the game picked, clear of every edge; its middle is
    // outside the L, so a ring with no safe spot is left as the game made it.
    var ell = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 2, 0), new Vector3(0, 2, 0) };
    var game = Enumerable.Range(0, 16).Select(k => new Vector3(0.5f + 0.2f * MathF.Cos(k * MathF.Tau / 16), 0.5f + 0.2f * MathF.Sin(k * MathF.Tau / 16), 0)).ToArray();
    var ring = HoleRing.Fit(ell, game, new Vector3(0.5f, 0.5f, 0), out var note, 3f);
    Check(ring.All(p => Vector2.Distance(new(p.X, p.Y), new(0.5f, 0.5f)) < 0.5f - 1e-4f), "concave face: big hole stays clear of its edges: " + note);
    var square = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
    var hollow = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 3, 0), new Vector3(2, 3, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 3, 0), new Vector3(0, 3, 0) };
    var kept = HoleRing.Fit(hollow, game, new Vector3(1.5f, 2f, 0), out note, 3f); // centre and middle both in the gap
    Check(kept.SequenceEqual(game), "no safe spot: game's ring left alone: " + note);
    Check(HoleRing.Fit(square, game, new Vector3(0.5f, 0.5f, 0), out _, float.NaN).All(p => MathF.Abs(Vector2.Distance(new(p.X, p.Y), new(0.5f, 0.5f)) - 0.2f) < 1e-4f), "bad size value: game's size");
}
Console.WriteLine($"HOLE_TESTS_OK: {checks} checks total");

// ---------------- Boolean cut (CutTests.cs) ----------------
CutTests.Run(Path.GetDirectoryName(root)!.Replace(@"\PMC\Blueprints", ""), CheckRefs);
