using System.Reflection;
using HarmonyLib;

namespace SprocketQoL;

/// Attaching QoL to the game one hook at a time: a hook whose game method is gone (renamed or removed by a game update)
/// turns off only that hook, not every hook of its feature, and the report says which (Mod Options > Compatibility, and
/// the log).
internal static class Hooks
{
    internal sealed record Entry(string Feature, string Hook, string? Error);
    internal static readonly List<Entry> Report = new();

    internal static int Attached => Report.Count(e => e.Error == null);
    internal static IEnumerable<Entry> Failures => Report.Where(e => e.Error != null);

    internal static void Failed(string feature, string hook, Exception ex) =>
        Report.Add(new(feature, hook, (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message));

    /// Every Harmony patch method of `feature` (its [HarmonyPatch] target, merged with the class's), each on its own.
    internal static void Attach(Harmony harmony, Type feature)
    {
        MethodInfo[] methods;
        try { methods = feature.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
        catch (Exception ex) { Failed(feature.Name, "(the whole feature)", ex); return; }
        HarmonyMethod classInfo;
        try { classInfo = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(feature)); }
        catch (Exception ex) { Failed(feature.Name, "(the whole feature)", ex); return; }
        foreach (var method in methods)
        {
            string? kind;
            List<HarmonyMethod> attributes;
            try
            {
                var names = method.GetCustomAttributes(true).Select(a => a.GetType().Name).ToList();
                kind = names.Contains(nameof(HarmonyPrefix)) ? "prefix" : names.Contains(nameof(HarmonyPostfix)) ? "postfix"
                     : names.Contains(nameof(HarmonyFinalizer)) ? "finalizer" : names.Contains(nameof(HarmonyTranspiler)) ? "transpiler"
                     : method.Name is "Prefix" or "Postfix" or "Finalizer" or "Transpiler" ? method.Name.ToLowerInvariant() : null; // Harmony's naming convention
                if (kind == null) continue;
                attributes = HarmonyMethodExtensions.GetFromMethod(method);
            }
            catch (Exception ex) { Failed(feature.Name, method.Name, ex); continue; }
            var info = classInfo.Clone();
            info = info.Merge(HarmonyMethod.Merge(attributes));
            string hook = $"{info.declaringType?.Name ?? "?"}.{info.methodName ?? "?"} ({kind})";
            try
            {
                var original = Original(info) ?? throw new MissingMethodException($"{info.declaringType?.FullName}.{info.methodName} isn't in the game");
                var patch = new HarmonyMethod(method);
                var processor = harmony.CreateProcessor(original);
                switch (kind)
                {
                    case "prefix": processor.AddPrefix(patch); break;
                    case "postfix": processor.AddPostfix(patch); break;
                    case "finalizer": processor.AddFinalizer(patch); break;
                    default: processor.AddTranspiler(patch); break;
                }
                processor.Patch();
                Report.Add(new(feature.Name, hook, null));
            }
            catch (Exception ex) { Failed(feature.Name, hook, ex); }
        }
    }

    static MethodBase? Original(HarmonyMethod info)
    {
        if (info.declaringType == null) return null;
        return info.methodType switch
        {
            MethodType.Getter => AccessTools.DeclaredPropertyGetter(info.declaringType, info.methodName) ?? AccessTools.PropertyGetter(info.declaringType, info.methodName),
            MethodType.Setter => AccessTools.DeclaredPropertySetter(info.declaringType, info.methodName) ?? AccessTools.PropertySetter(info.declaringType, info.methodName),
            MethodType.Constructor => AccessTools.DeclaredConstructor(info.declaringType, info.argumentTypes),
            _ => AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes) ?? AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes),
        };
    }

    /// For the log once everything is attached.
    internal static string Summary()
    {
        var failed = Failures.ToList();
        return failed.Count == 0 ? $"all {Attached} game hooks attached"
            : $"{Attached} of {Report.Count} game hooks attached; not attached: " + string.Join("; ", failed.Select(f => $"{f.Feature}: {f.Hook} ({f.Error})"));
    }
}
