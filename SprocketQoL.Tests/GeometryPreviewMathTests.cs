using System.Numerics;
using System.Text.Json.Nodes;
using SprocketQoL;

static class GeometryPreviewMathTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Geometry preview: " + why); }
        var positions = new[] { new Vector3(1000, 2000, 3000), new Vector3(2000, 2000, 3000), new Vector3(1000, 4000, 5000) };
        var document = new ObjDocument(new[] { new ObjMeshData("Offset model", positions, new[] { new[] { 0, 1, 2 } }) });
        var original = positions.ToArray();
        foreach (var units in Enum.GetValues<ObjImportUnits>())
        foreach (var up in Enum.GetValues<ObjUpAxis>())
        foreach (float scale in new[] { .25f, 1f, 2f })
        {
            float metres = units switch { ObjImportUnits.Metres => 1, ObjImportUnits.Centimetres => .01f, _ => .001f };
            Vector3 Expected(Vector3 p)
            {
                var oriented = up switch { ObjUpAxis.Y => p, ObjUpAxis.Z => new(p.X, p.Z, -p.Y), _ => new Vector3(-p.Y, p.X, p.Z) };
                return new Vector3(-oriented.X, oriented.Y, oriented.Z) * (metres * scale);
            }
            var converted = positions.Select(Expected).ToArray();
            var bounds = GeometryPreviewMath.Measure(document, scale, units, up);
            Check(bounds.Min == converted.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min), "bounds minimum uses actual imported coordinates");
            Check(bounds.Max == converted.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max), "bounds maximum uses actual imported coordinates");
            var result = ObjBlueprintImport.Build(document, "Preview matches import", scale, 20, sourceUnits: units, sourceUp: up);
            var saved = JsonNode.Parse(result.Json)!["mesh"]!["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
            for (int i = 0; i < positions.Length; i++)
            {
                var live = new Vector3(saved[i * 3], saved[i * 3 + 1], saved[i * 3 + 2]);
                Check(live == converted[i], "unit/up/scale selections alter saved geometry exactly as previewed");
                foreach (float yaw in new[] { -120f, 0f, 60f })
                foreach (float pitch in new[] { -80f, 0f, 70f })
                {
                    var p = GeometryPreviewMath.Project(live, bounds, yaw, pitch, new(640, 360));
                    Check(float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X >= 0 && p.X <= 640 && p.Y >= 0 && p.Y <= 360,
                        "rotating a correctly framed preview keeps its real geometry within the viewport");
                }
            }
            Check(result.VertexCount == 3 && result.FaceCount == 1, "orientation and units preserve topology");
            Check(positions.SequenceEqual(original), "preview and import leave original source coordinates untouched");
        }
        foreach (var up in Enum.GetValues<ObjUpAxis>())
        {
            var sourceUp = up switch { ObjUpAxis.Y => Vector3.UnitY, ObjUpAxis.Z => Vector3.UnitZ, _ => Vector3.UnitX };
            Check(GeometryPreviewMath.Orient(sourceUp, up) == Vector3.UnitY, "chosen source up becomes game up");
            var x = GeometryPreviewMath.Orient(Vector3.UnitX, up);
            var y = GeometryPreviewMath.Orient(Vector3.UnitY, up);
            var z = GeometryPreviewMath.Orient(Vector3.UnitZ, up);
            Check(Vector3.Cross(x, y) == z, "up-axis conversion is a proper rotation and does not unexpectedly reverse winding");
        }
        foreach (float scale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 1001f })
        {
            bool rejected = false;
            try { GeometryPreviewMath.Measure(document, scale); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "unsafe scales cannot produce a misleading preview");
        }
        bool coordinateRejected = false;
        try { GeometryPreviewMath.ImportPosition(new(float.PositiveInfinity, 0, 0)); } catch (FormatException) { coordinateRejected = true; }
        Check(coordinateRejected, "non-finite positions are rejected");
        var collapsed = new GeometryPreviewMath.Bounds(Vector3.One, Vector3.One);
        Check(GeometryPreviewMath.Project(Vector3.One, collapsed, 0, 0, new(640, 360)) == new Vector2(320, 180),
            "zero-sized bounds remain a finite centred preview");
        Console.WriteLine($"GEOMETRY_PREVIEW_MATH_TESTS_OK: {checks} checks");
    }
}
