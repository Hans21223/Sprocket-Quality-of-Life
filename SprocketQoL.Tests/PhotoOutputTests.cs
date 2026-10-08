using SprocketQoL;

internal static class PhotoOutputTests
{
    internal static void Run()
    {
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        Check(PhotoOutput.SizeFor(1920, 1080, "Screen") == new PhotoOutput.Size(1920, 1080), "screen size remains unchanged");
        foreach (var (name, width, height) in new[] { ("2K", 2560, 1440), ("4K", 3840, 2160), ("6K", 5760, 3240), ("8K", 7680, 4320) })
            Check(PhotoOutput.SizeFor(1920, 1080, name) == new PhotoOutput.Size(width, height), name + " uses the requested rendered dimensions");
        Check(PhotoOutput.SizeFor(3440, 1440, "8K") == new PhotoOutput.Size(7680, 3215), "ultrawide aspect is kept");
        Check(PhotoOutput.SizeFor(1080, 1920, "4K") == new PhotoOutput.Size(2160, 3840), "portrait photos keep their orientation");
        Check(PhotoOutput.SizeFor(1920, 1080, "invalid") == new PhotoOutput.Size(1920, 1080), "unknown saved option falls back to screen");
        Check(!PhotoOutput.FitsRenderTarget(new(7680, 4320), 4096) && PhotoOutput.FitsRenderTarget(new(7680, 4320), 8192), "GPU limit can reject oversized targets before changing settings");
        byte[] rgb = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };
        Check(ReferenceEquals(rgb, PhotoOutput.ResizeRgb(rgb, 2, 2, new(2, 2))), "screen-size RGB is byte-for-byte unchanged");
        var resized = PhotoOutput.ResizeRgb(rgb, 2, 2, new(4, 4));
        Check(resized.Length == 48 && resized.Take(3).SequenceEqual(rgb.Take(3))
            && resized.Skip(9).Take(3).SequenceEqual(rgb.Skip(3).Take(3))
            && resized.Skip(36).Take(3).SequenceEqual(rgb.Skip(6).Take(3))
            && resized.Skip(45).Take(3).SequenceEqual(rgb.Skip(9).Take(3)), "RGB resize preserves colours and bottom-up orientation");
        var flat = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 24, 67, 189 }).ToArray();
        Check(PhotoOutput.ResizeRgb(flat, 3, 2, new(13, 7)).Chunk(3).All(p => p.SequenceEqual(new byte[] { 24, 67, 189 })), "resizing smoke-coloured RGB cannot darken it by applying alpha");
        Check(PhotoOutput.ResizeRgb(rgb, 2, 2, new(1, 1)).SequenceEqual(new byte[] { 128, 128, 128 }), "resampling averages colour without changing channel order");
        Console.WriteLine("PHOTO_OUTPUT_TESTS_OK: 2K-8K, aspect ratios, GPU limits, opaque RGB and resize orientation");
    }
}
