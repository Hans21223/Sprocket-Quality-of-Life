using System.Numerics;
using SprocketQoL;

static class ObjExportGeometryTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("OBJ export: " + why); }
        bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
        foreach (var pair in new[]
        {
            ("combustionEngine", ObjExportCategory.Engine), ("powertrain", ObjExportCategory.Powertrain),
            ("powertrainSteering", ObjExportCategory.Powertrain), ("transmission", ObjExportCategory.Transmission),
            ("ammoRack", ObjExportCategory.Ammunition), ("ammoRackModel", ObjExportCategory.Ammunition),
            ("crewSeat", ObjExportCategory.Crew), ("postureUnityArmature", ObjExportCategory.Crew),
            ("sight", ObjExportCategory.Sights), ("traverseMotor", ObjExportCategory.TurretMotors),
            ("traverseMotorModel", ObjExportCategory.TurretMotors), ("layingDrive", ObjExportCategory.LayingDrives)
        })
        {
            var category = ObjExportPolicy.Classify("unknown custom part", new[] { pair.Item1, "model" }, false);
            Check(category == pair.Item2 && !ObjExportPolicy.DefaultIncluded(category), "explicit exclusion " + pair.Item1);
        }
        Check(ObjExportPolicy.Classify(null, new[] { "fuelTank" }, true) == ObjExportCategory.InternalFuel, "internal tank excluded by registry tag");
        Check(ObjExportPolicy.Classify(null, new[] { "fuelTank" }, false) == ObjExportCategory.Exterior, "external tank retained despite same component type");
        Check(ObjExportPolicy.Classify(null, new[] { "trackAssembly", "powertrainNode", "trackBelt" }, false) == ObjExportCategory.Exterior, "tracks retained despite powertrainNode");
        Check(ObjExportPolicy.Classify(null, new[] { "cannon", "trunnions", "model" }, false) == ObjExportCategory.Exterior, "gun retained despite drive exclusion");
        Check(ObjExportPolicy.Classify(null, new[] { "vent", "exhaust", "plateStructure", "turretRing", "antenna", "projectedDecal" }, false) == ObjExportCategory.Exterior, "external fittings and decals retained");
        Check(ObjExportPolicy.Classify("5E8AB5C7-E9F1-4C64-A04A-29EFC78B1918", Array.Empty<string>(), false) == ObjExportCategory.InternalFuel, "internal GUID fallback is case insensitive");
        Check(ObjExportPolicy.Classify("ecd3341c-f605-4816-946c-591eaa7e4f7d", Array.Empty<string>(), false) == ObjExportCategory.Exterior, "barrel GUID fallback retained");
        foreach (var item in ObjExportPolicy.KnownParts)
            Check(ObjExportPolicy.Classify(item.Key, Array.Empty<string>(), false) == item.Value.Category, "all registry fallback classifications " + item.Value.Name);
        foreach (var category in Enum.GetValues<ObjExportCategory>())
            Check(ObjExportPolicy.DefaultIncluded(category) == (category == ObjExportCategory.Exterior), "selection default " + category);

        var origin = new Vector3(14, -2, 9);
        var basis = ObjExportGeometry.FromBasis(origin, Vector3.UnitZ * 2, Vector3.UnitY * 3, -Vector3.UnitX * 4);
        Check(Near(Vector3.Transform(Vector3.Zero, basis), origin), "common frame has its own origin");
        Check(Near(Vector3.Transform(new(1, 1, 1), basis), origin + Vector3.UnitZ * 2 + Vector3.UnitY * 3 - Vector3.UnitX * 4), "basis preserves translation rotation and nonuniform scale");
        Vector3 point = new(2, 3, 4), other = new(-7, 8, -5);
        Check(Near(ObjExportGeometry.Reflect(ObjExportGeometry.Reflect(point)), point), "OBJ handedness conversion is reversible");
        Check(Math.Abs(Vector3.Distance(point, other) - Vector3.Distance(ObjExportGeometry.Reflect(point), ObjExportGeometry.Reflect(other))) < 1e-5f, "parts preserve distances without individual centering");
        var transform = Matrix4x4.CreateScale(2, 4, 0.5f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(30, 0, -10);
        var tangent = Vector3.Normalize(new Vector3(1, 1, 0));
        var normal = Vector3.Normalize(new Vector3(1, -1, 0));
        Check(Math.Abs(Vector3.Dot(ObjExportGeometry.TransformNormal(normal, transform), Vector3.TransformNormal(tangent, transform))) < 1e-5f, "inverse transpose keeps nonuniformly scaled normal perpendicular");
        Check(!ObjExportGeometry.ReverseWinding(transform), "ordinary transform keeps winding");
        Check(ObjExportGeometry.ReverseWinding(transform * Matrix4x4.CreateScale(-1, 1, 1)), "OBJ reflection reverses winding once");
        Check(!ObjExportGeometry.ReverseWinding(Matrix4x4.CreateScale(-1, 1, 1) * transform * Matrix4x4.CreateScale(-1, 1, 1)), "native mirrored part plus OBJ reflection cancel winding flips");
        bool rejected = false;
        try { ObjExportGeometry.NormalMatrix(Matrix4x4.CreateScale(0)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "zero scale rejected before file creation");
        var padding = new[] { Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX * 2 };
        Check(!ObjExportGeometry.HasSurface(padding, new[] { 0, 1, 2 }), "separate UV seam vertices at the same point are invisible padding");
        Check(!ObjExportGeometry.HasSurface(padding, new[] { 0, 2, 4 }), "collinear triangle padding has no visible surface");
        Check(ObjExportGeometry.HasSurface(padding, new[] { 0, 2, 3 }), "visible triangles retained");

        ObjExportGeometry.ProjectedVertex P(float x, float y, float z) => new(new(x, y, z), new(10 + x, 20 + y, 30 + z), Vector3.UnitZ);
        var clipped = ObjExportGeometry.ClipToProjector(new[] { P(-1, -1, 0.1f), P(1, -1, 0.1f), P(0, 1, 0.1f) });
        Check(clipped.Length >= 3, "large receiver triangle clipped to projection volume");
        foreach (var p in clipped)
        {
            Check(Math.Abs(p.Local.X) <= 0.500001f && Math.Abs(p.Local.Y) <= 0.500001f && Math.Abs(p.Local.Z) <= 0.500001f, "clipped coordinates stay within volume");
            Check(Near(p.World - p.Local, new(10, 20, 30)), "decal stays on receiver surface instead of floating billboard");
            Check(Near(p.Normal, Vector3.UnitZ), "clipping preserves interpolated surface normals");
        }
        Check(ObjExportGeometry.ClipToProjector(new[] { P(1, 1, 1), P(2, 1, 1), P(1, 2, 1) }).Length == 0, "outside triangles omitted");
        Check(ObjExportGeometry.ClipToProjector(new[] { P(-0.5f, -0.5f, 0), P(0.5f, -0.5f, 0), P(0.5f, 0.5f, 0) }).Length == 3, "boundary triangle has no duplicate corner");

        foreach (var value in new[] { -12.5f, 0f, 1f, 65536f })
            Check(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes(value), 0, 0) == value, "GPU Float32 decoded");
        Check(Math.Abs(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)1.25f)), 0, 1) - 1.25f) < 1e-6f, "GPU Float16 decoded");
        Check(ObjExportGeometry.ReadAttribute(new byte[] { 255 }, 0, 2) == 1, "GPU UNorm8 normalized");
        Check(ObjExportGeometry.ReadAttribute(new byte[] { 128 }, 0, 3) == -1, "GPU SNorm8 minimum normalized");
        Check(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes(ushort.MaxValue), 0, 4) == 1, "GPU UNorm16 normalized");
        Check(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes(short.MinValue), 0, 5) == -1, "GPU SNorm16 minimum normalized");
        Check(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes((uint)65539), 0, 10) == 65539, "GPU UInt32 decoded");
        Check(ObjExportGeometry.ReadAttribute(BitConverter.GetBytes(-731), 0, 11) == -731, "GPU SInt32 decoded");
        Console.WriteLine("OBJ_EXPORT_TESTS_OK " + checks + " checks");
    }
}
