using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.VehicleDesigner;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketQoL;

/// Search every context action, with persistent favourites and the most recently used actions first.
[HarmonyPatch]
internal static class CommandPalette
{
    sealed record Preferences(string[] Favourites, string[] Recent);
    static readonly List<Func<IEnumerable<RightClickMenu.Command>>> providers = new();
    static readonly HashSet<string> favourites = new(StringComparer.Ordinal);
    static readonly List<string> recent = new();
    static readonly List<UnityAction> callbacks = new(), rowCallbacks = new();
    static readonly List<UnityAction<string>> textCallbacks = new();
    static IReadOnlyList<RightClickMenu.Command> commands = Array.Empty<RightClickMenu.Command>();
    static IReadOnlyList<RightClickMenu.Command> shown = Array.Empty<RightClickMenu.Command>();
    static GameObject? canvas, panel, rows;
    static TMP_FontAsset? font;
    static IntPtr target;
    static string search = "", message = "";
    static bool loaded, redrawRows, onlyFavourites;
    static int page, releaseAt = -10;
    static Action? pending;
    internal static bool IsOpen => canvas != null;
    internal static bool BlocksInput => IsOpen || Time.frameCount <= releaseAt;
    static string PreferencePath => Path.Combine(WorkshopStorage.Root, "SprocketQoL", "Commands.json");

    internal static void Register(Func<IEnumerable<RightClickMenu.Command>> provider) { if (!providers.Contains(provider)) providers.Add(provider); }
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.UpdatePointerOperators))]
    static bool Pointer() => !BlocksInput;

    internal static void Update()
    {
        if (!IsOpen && Time.frameCount > releaseAt)
        {
            RestoreInputs();
            if (pending != null) { var run = pending; pending = null; try { run(); } catch (Exception ex) { DesignEditor.Instance?.Say(ex.Message); Plugin.ModLog.LogWarning("Command: " + ex); } return; }
        }
        if (IsOpen && (DesignEditor.Instance?.IsReady != true || DesignEditor.Instance.Core?.Target?.Pointer != target)) { Close(); return; }
        if (IsOpen)
        {
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || Keybinds.Pressed("commandSearch")) { Close(); return; }
            if (Keyboard.current?.enterKey.wasPressedThisFrame == true && shown.FirstOrDefault(c => c.Enabled) is { } first) { Execute(first); return; }
            if (redrawRows) { redrawRows = false; Rows(); }
        }
        else if (Keybinds.Pressed("commandSearch") && !MeshTools.Typing()) Open();
    }

    internal static void Open()
    {
        var editor = DesignEditor.Instance;
        if (EditorModal.BlocksInput || IsOpen || editor?.IsReady != true || editor.IsBusy || ObjTransfer.IsOpen || WorkshopLibrary.IsOpen || PartPatterns.IsOpen || QolMenu.IsOpen || editor.Core?.Editor?.OperationInProgress == true || editor.CaptureBlocked()) return;
        Load(); target = editor.Core!.Target.Pointer;
        search = message = ""; page = 0; onlyFavourites = false;
        commands = RightClickMenu.Commands().Concat(BuiltIns()).Concat(providers.SelectMany(provider => provider())).GroupBy(c => c.Id).Select(g => g.First()).ToArray();
        font = NativeUi.Font(); if (font == null) return;
        try
        {
            InputLease.Take("Tool search");
            RightClickMenu.Close(); canvas = NativeUi.Canvas("QoL command search"); Build();
        }
        catch { Close(); RestoreInputs(); throw; }
    }
    static IEnumerable<RightClickMenu.Command> BuiltIns()
    {
        yield return new("Workshop/Open", "Workshop", "Open presets, saved parts and recovery", Keybinds.Shown("workshop"), true, "", WorkshopLibrary.Open);
        bool parts = DesignEditor.Instance?.SelectedParts().Count > 0;
        yield return new("Patterns/Open", "Parts", "Repeat selected parts: line or circle", "", parts, "Select attached parts to repeat.", PartPatterns.Open);
    }
    static void Execute(RightClickMenu.Command command)
    {
        if (!command.Enabled) { message = command.Reason; redrawRows = true; return; }
        recent.Remove(command.Id); recent.Insert(0, command.Id); if (recent.Count > 15) recent.RemoveRange(15, recent.Count - 15); Save();
        Close(); pending = command.Run; // restore native maps before dispatching an action or opening another modal
    }
    static void Close()
    {
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        canvas = panel = rows = null; font = null; callbacks.Clear(); rowCallbacks.Clear(); textCallbacks.Clear();
        releaseAt = Time.frameCount + 2; redrawRows = false;
    }
    internal static void LeftEditor() { pending = null; Close(); RestoreInputs(); target = IntPtr.Zero; }
    static void RestoreInputs() => InputLease.Release("Tool search");
    static void Build()
    {
        var rect = NativeUi.Node("Commands", canvas!.transform, 0, 0, 1000, 630); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f,.5f); panel = rect.gameObject;
        panel.AddComponent<Image>().color = new Color(.08f,.09f,.105f,1);
        NativeUi.Label(font,panel.transform,"Find an editor action",22,16,740,35,24);
        NativeUi.Click(font,callbacks,panel.transform,"Close / Esc",820,16,155,34,Close);
        var input = NativeUi.Node("Command search",panel.transform,22,66,745,40); var background = input.gameObject.AddComponent<Image>(); background.color = new Color(.12f,.13f,.15f);
        var field = input.gameObject.AddComponent<TMP_InputField>(); field.targetGraphic = background;
        var viewport = NativeUi.Node("Text viewport",input,8,5,729,30); viewport.gameObject.AddComponent<RectMask2D>(); field.textViewport = viewport;
        var text = NativeUi.Label(font,viewport,"",0,0,729,30,18); text.enableWordWrapping=false; text.overflowMode=TextOverflowModes.Overflow; field.textComponent=text;
        var hint = NativeUi.Label(font,viewport,"Search tools, settings, selection…",0,0,729,30,18); hint.color=new Color(.6f,.63f,.68f); field.placeholder=hint; field.lineType=TMP_InputField.LineType.SingleLine;
        var change=DelegateSupport.ConvertDelegate<UnityAction<string>>(new Action<string>(value=>{search=value;page=0;redrawRows=true;}))!; textCallbacks.Add(change);field.onValueChanged.AddListener(change);
        field.ActivateInputField();
        NativeUi.Click(font,callbacks,panel.transform,"Favourites on/off",780,66,195,40,()=>{onlyFavourites=!onlyFavourites;page=0;redrawRows=true;}); Rows();
    }
    static void Rows()
    {
        if(rows!=null){rows.SetActive(false);UnityEngine.Object.Destroy(rows);}rowCallbacks.Clear(); rows=NativeUi.Node("Command results",panel!.transform,0,120,1000,500).gameObject;
        var words=search.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        shown=commands.Where(c=>(!onlyFavourites||favourites.Contains(c.Id))&&words.All(w=>(c.Section+" "+c.Label).Contains(w,StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c=>c.Enabled).ThenByDescending(c=>favourites.Contains(c.Id)).ThenBy(c=>recent.IndexOf(c.Id)<0?int.MaxValue:recent.IndexOf(c.Id)).ThenBy(c=>c.Label,StringComparer.OrdinalIgnoreCase).ToArray();
        const int size=8;int pages=Math.Max(1,(shown.Count+size-1)/size);page=Math.Clamp(page,0,pages-1);int row=0;
        foreach(var command in shown.Skip(page*size).Take(size))
        {
            float y=row*47;var button=NativeUi.Click(font,rowCallbacks,rows.transform,command.Label+(command.Key.Length>0?"  ["+command.Key+"]":""),22,y,825,28,()=>Execute(command),true);
            if(!command.Enabled)button.GetComponent<Image>().color=new Color(.13f,.14f,.15f);
            NativeUi.Label(font,rows.transform,command.Section+(!command.Enabled?" · "+command.Reason:""),32,y+29,815,17,12);
            NativeUi.Click(font,rowCallbacks,rows.transform,favourites.Contains(command.Id)?"Starred":"Star",862,y,113,29,()=>{if(!favourites.Add(command.Id))favourites.Remove(command.Id);Save();redrawRows=true;});row++;
        }
        if(shown.Count==0)NativeUi.Label(font,rows.transform,"No matching commands. Try another word or turn off the favourites filter.",22,10,950,60,18);
        NativeUi.Click(font,rowCallbacks,rows.transform,"Previous",22,392,150,32,()=>{page--;redrawRows=true;});
        NativeUi.Label(font,rows.transform,$"{page+1} / {pages} · {shown.Count} actions"+(onlyFavourites?" · favourites":""),195,399,560,26,16);
        NativeUi.Click(font,rowCallbacks,rows.transform,"Next",820,392,155,32,()=>{page++;redrawRows=true;});
        NativeUi.Label(font,rows.transform,message.Length>0?message:"Enter runs the first available result. Star actions for quick access; recent actions rise to the top.",22,444,953,45,16);
    }
    static void Load()
    {
        if(loaded)return;loaded=true;
        try{if(File.Exists(PreferencePath)&&new FileInfo(PreferencePath).Length<1024*1024&&JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencePath))is{}p){foreach(var id in p.Favourites??Array.Empty<string>())favourites.Add(id);recent.AddRange((p.Recent??Array.Empty<string>()).Distinct().Take(15));}}
        catch(Exception ex){Plugin.ModLog.LogWarning("Command preferences: "+ex.Message);}
    }
    static void Save()
    {
        try{WorkshopStorage.AtomicWrite(PreferencePath,JsonSerializer.Serialize(new Preferences(favourites.OrderBy(id=>id,StringComparer.Ordinal).ToArray(),recent.ToArray())));}
        catch(Exception ex){message="Could not save command preferences: "+ex.Message;}
    }
}
