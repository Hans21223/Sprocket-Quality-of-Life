using SprocketQoL;

static class ObjStreamingImportTests
{
    const string Triangle = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
    sealed class ChunkedReader : TextReader
    {
        readonly string prefix, suffix;
        readonly long prefixLength;
        readonly int chunk;
        long cursor;
        public int LargestRead { get; private set; }
        public bool Disposed { get; private set; }
        internal ChunkedReader(string text, int chunk = 7) : this("", 0, text, chunk) { }
        internal ChunkedReader(string prefix, int repeats, string suffix, int chunk = 8192)
        { this.prefix = prefix; this.suffix = suffix; prefixLength = (long)prefix.Length * repeats; this.chunk = chunk; }
        public override string? ReadLine() => throw new Exception("streaming parser must not call unbounded ReadLine");
        public override int Read(char[] buffer, int index, int count)
        {
            LargestRead = Math.Max(LargestRead, count);
            int available = (int)Math.Min(Math.Min(count, chunk), prefixLength + suffix.Length - cursor);
            for (int i = 0; i < available; i++, cursor++)
                buffer[index + i] = cursor < prefixLength ? prefix[(int)(cursor % prefix.Length)] : suffix[(int)(cursor - prefixLength)];
            return available;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("OBJ stream import: " + why); }
        var rich = "\uFEFFo Original\r\nv 0 0 0\rv 1 0 0\nv 0 1 0\r\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nusemtl green\nf -3/-3/1 -2/-2/1 -1/-1/1";
        string expected = ObjMeshFormat.Write(ObjMeshFormat.Read(rich));
        foreach (int chunk in new[] { 1, 2, 3, 7, 8192 })
        {
            using var reader = new ChunkedReader(rich, chunk);
            var result = ObjMeshFormat.Read(reader);
            Check(ObjMeshFormat.Write(result) == expected, "BOM, mixed newline boundaries, negative indices and attributes survive chunk size " + chunk);
            Check(reader.LargestRead <= 8192 && !reader.Disposed, "bounded reads keep reader lifetime owned by caller");
            string export = ObjMeshFormat.Write(result);
            using var roundTrip = new ChunkedReader(export, chunk);
            Check(ObjMeshFormat.Write(ObjMeshFormat.Read(roundTrip)) == export, "streamed import/export round trip matches at chunk size " + chunk);
        }
        using (var huge = new ChunkedReader("#" + new string('x', 126) + "\n", ObjMeshFormat.MaxTextLength / 128 + 8, Triangle))
        {
            long total = 0;
            var result = ObjMeshFormat.Read(huge, progress: n => total = n);
            Check(total > ObjMeshFormat.MaxTextLength, "valid input beyond the former 64 MiB ceiling is accepted without a giant text allocation");
            Check(result.Meshes.Single().Vertices.Length == 3 && result.Meshes.Single().Faces.Length == 1, "large comments do not become retained geometry");
            Check(huge.LargestRead <= 8192, "large files use the same bounded read size");
        }
        using (var oversized = new ChunkedReader(new string(' ', ObjMeshFormat.MaxLineLength + 1) + "\n" + Triangle, 13))
        {
            bool rejected = false;
            try { ObjMeshFormat.Read(oversized); } catch (FormatException ex) { rejected = ex.Message.Contains("line 1") && ex.Message.Contains("too long"); }
            Check(rejected, "oversized lines are rejected before unbounded line/string token allocation");
        }
        foreach (string invalid in new[] { "f 0 2 3", "f 1 2 4", "f -4 -2 -1", "f 1 2 2", "f 1// 2 3", "f 1/1 2 3" })
        {
            using var reader = new ChunkedReader(Triangle[..Triangle.IndexOf('f')] + invalid, 2);
            bool rejected = false;
            try { ObjMeshFormat.Read(reader); } catch (FormatException) { rejected = true; }
            Check(rejected, "streaming preserves index/topology safeguards: " + invalid);
        }
        using (var vertices = new ChunkedReader("v 0 0 0\n", ObjMeshFormat.MaxVertices + 1, Triangle))
        {
            bool rejected = false;
            try { ObjMeshFormat.Read(vertices); } catch (FormatException ex) { rejected = ex.Message.Contains("too many vertices"); }
            Check(rejected, "streamed files still enforce the geometry vertex budget independently of text size");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var reader = new ChunkedReader("# cancel\n", 3000, Triangle))
        {
            bool cancelled = false;
            try { ObjMeshFormat.Read(reader, cancellation.Token, _ => cancellation.Cancel()); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancellation aborts streamed parsing without returning a partial document");
            Check(!reader.Disposed, "cancelled reader remains caller-owned");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var reader = new ChunkedReader(Triangle))
        {
            cancellation.Cancel(); bool cancelled = false;
            try { ObjMeshFormat.Read(reader, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && reader.LargestRead == 0, "pre-cancelled import performs no IO");
        }
        Console.WriteLine($"OBJ_STREAMING_IMPORT_TESTS_OK: {checks} checks");
    }
}
