using System.Text;

namespace SprocketQoL;

/// Publishes a new plate structure in a selected, existing faction library. Never replaces a saved structure.
public static class ObjPlateFiles
{
    public static string BlueprintName(string name)
    {
        var invalid = "<>:\"/\\|?*";
        name = new string(name.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) throw new FormatException("Enter a name for the plate structure.");
        if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
        string device = name.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) &&
            (device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')) name = "_" + name;
        return name;
    }

    public static string Destination(string factionDirectory)
    {
        string faction = Path.GetFullPath(factionDirectory);
        if (!Directory.Exists(faction))
            throw new DirectoryNotFoundException("The selected faction no longer exists. Reopen the OBJ menu and choose a faction.");
        return Path.Combine(faction, "Blueprints", "Plate Structures");
    }

    public static string Save(string factionDirectory, string name, string json)
    {
        string directory = Destination(factionDirectory);
        name = BlueprintName(name);
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("No plate structure was generated.", nameof(json));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".qol-obj-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Flush a complete blueprint before publishing it. CreateNew/Move also protect against a competing save.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            for (int attempt = 1; attempt <= 10000; attempt++)
            {
                string fileName = name + (attempt == 1 ? "" : $" ({attempt})") + ".blueprint";
                string destination = Path.Combine(directory, fileName);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try { File.Move(temporary, destination); return destination; }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
            throw new IOException("There are too many plate structures with this name. Choose another name.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
