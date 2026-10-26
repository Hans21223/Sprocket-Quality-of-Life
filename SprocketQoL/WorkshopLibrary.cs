using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.Factions;
using Sprocket.VehicleDesigner;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketQoL;

/// Native modal workshop: named preferences, reusable assemblies, recovery copies and shareable diagnostics.
[HarmonyPatch]
internal static class WorkshopLibrary
{
    static GameObject? canvas, panel, rows;
    static TMP_FontAsset? font;
    static readonly List<UnityAction> callbacks = new(), rowCallbacks = new();
    static readonly List<UnityAction<string>> textCallbacks = new();
    static readonly List<(string Name, string Directory)> factions = new();
    static IntPtr target;
    static int tab, page, group, faction, releaseAt = -10;
    static string search = "", name = "", tags = "", message = "", selected = "", confirmDelete = "";
    static bool redraw, redrawRows;
    static IReadOnlyList<ToolPresetStore.Preset> presets = Array.Empty<ToolPresetStore.Preset>();
    static IReadOnlyList<WorkshopStorage.PartInfo> parts = Array.Empty<WorkshopStorage.PartInfo>();
    static IReadOnlyList<WorkshopStorage.Backup> backups = Array.Empty<WorkshopStorage.Backup>();
    static IReadOnlyList<string> selectionSets = Array.Empty<string>();
    const int PageSize = 3;
    // Each entry's thumbnail, made once per list (not again on every page or search) and let go with it.
    static readonly Dictionary<string, GeometryPreview?> thumbnails = new();
    internal static bool IsOpen => canvas != null;
    internal static bool BlocksInput => IsOpen || Time.frameCount <= releaseAt;
    internal static void Initialize(string root)
    {
        WorkshopStorage.Root = root;
        BlueprintClipboard.StorageDirectory = root;
        ToolPresetStore.DirectoryPath = Path.Combine(root, "SprocketQoL", "ToolPresets");
    }

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.UpdatePointerOperators))]
    static bool Pointer() => !BlocksInput;

    internal static void Update()
    {
        ToolSettings.Tick();
        if (!IsOpen && Time.frameCount > releaseAt) RestoreInputs();
        if (IsOpen && (DesignEditor.Instance?.IsReady != true || DesignEditor.Instance.Core?.Target?.Pointer != target)) { Close(); return; }
        if (IsOpen)
        {
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || Keybinds.Pressed("workshop")) { Close(); return; }
            if (redraw) { redraw = false; Build(); }
            else if (redrawRows) { redrawRows = false; RefreshRows(); }
        }
        else if (Keybinds.Pressed("workshop") && !MeshTools.Typing()) Open();
    }

    internal static void Open()
    {
        var editor = DesignEditor.Instance;
        if (EditorModal.BlocksInput || IsOpen || editor?.IsReady != true || editor.IsBusy || ObjTransfer.IsOpen || CommandPalette.IsOpen || PartPatterns.IsOpen || QolMenu.IsOpen || editor.Core?.Editor?.OperationInProgress == true || editor.CaptureBlocked()) return;
        target = editor.Core!.Target.Pointer;
        search = selected = confirmDelete = message = ""; page = 0;
        font = NativeUi.Font(); if (font == null) { editor.Say("Select a structure, then open the workshop again."); return; }
        try
        {
            InputLease.Take("Workshop");
            RightClickMenu.Close();
            factions.Clear();
            foreach (var f in FactionIO.GetAllFactions()) if (Directory.Exists(f.Directory)) factions.Add((f.Name, f.Directory));
            factions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            faction = Math.Max(0, factions.FindIndex(f => f.Directory == FactionManager.CurrentFaction?.Directory));
            canvas = NativeUi.Canvas("QoL workshop"); Reload(); Build();
        }
        catch { Close(); RestoreInputs(); throw; }
    }

    static void Close()
    {
        ForgetThumbnails();
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        canvas = panel = rows = null; callbacks.Clear(); rowCallbacks.Clear(); textCallbacks.Clear(); font = null;
        releaseAt = Time.frameCount + 2; redraw = redrawRows = false; ToolSettings.Flush();
    }
    internal static void LeftEditor() { Close(); RestoreInputs(); target = IntPtr.Zero; }
    static void RestoreInputs() => InputLease.Release("Workshop");
    /// Only the open tab's list: the part tabs read every saved group whole, too much to do for the other tabs.
    static void Reload()
    {
        presets = tab == 0 ? ToolPresetStore.List() : Array.Empty<ToolPresetStore.Preset>();
        parts = tab is 1 or 2 ? WorkshopStorage.ListParts(tab == 2) : Array.Empty<WorkshopStorage.PartInfo>();
        backups = tab == 3 ? WorkshopStorage.ListBackups() : Array.Empty<WorkshopStorage.Backup>();
        selectionSets = tab == 4 ? MeshSelection.SetNames() : Array.Empty<string>();
        ForgetThumbnails();
    }
    static void ForgetThumbnails() { foreach (var thumbnail in thumbnails.Values) thumbnail?.Dispose(); thumbnails.Clear(); }
    static void Run(Action action)
    {
        try { action(); Reload(); }
        catch (Exception ex) { message = ex.Message; Plugin.ModLog.LogWarning("Workshop: " + ex); }
        redraw = true;
    }

    static void Build()
    {
        if (panel != null) { panel.SetActive(false); UnityEngine.Object.Destroy(panel); }
        callbacks.Clear(); rowCallbacks.Clear(); textCallbacks.Clear();
        var rect = NativeUi.Node("Workshop panel", canvas!.transform, 0, 0, 1060, 720);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); panel = rect.gameObject;
        panel.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.105f, 1);
        Label("Workshop", 22, 14, 700, 34, 25); Click("Close / Esc", 880, 14, 155, 34, Close);
        string[] tabs = { "Tool presets", "Saved parts", "Clipboard history", "Recovery", "Selection sets", "Support" };
        for (int i = 0; i < tabs.Length; i++) { int pick = i; Click((tab == i ? "● " : "") + tabs[i], 22 + i * 170, 58, 163, 34, () => { tab = pick; page = 0; selected = confirmDelete = search = ""; Reload(); redraw = true; }); }
        if (tab == 5) { Support(); return; }
        Input(search, "Search names, tags and operations…", 22, 106, 690, 34, value => { search = value; page = 0; redrawRows = true; });
        Click("Refresh", 725, 106, 145, 34, () => { Reload(); redrawRows = true; });
        if (tab != 4) Click("Open folder", 882, 106, 153, 34, () => OpenFolder(tab == 0 ? ToolPresetStore.DirectoryPath : tab == 3 ? WorkshopStorage.BackupDirectory : WorkshopStorage.LibraryDirectory));
        Input(name, tab == 3 ? "Checkpoint name" : tab == 0 ? "Preset name" : tab == 4 ? "Selection set name" : "Part group name", 22, 157, 420, 34, value => name = value);
        if (tab == 0)
        {
            var groups = ToolSettings.Groups; group = Math.Clamp(group, 0, Math.Max(0, groups.Count - 1));
            Click(groups.Count == 0 ? "No registered tools" : groups[group] + " ›", 455, 157, 350, 34, () => { if (groups.Count > 0) group = (group + 1) % groups.Count; redraw = true; });
            Click("Save current values", 817, 157, 218, 34, () => Run(() =>
            {
                if (groups.Count == 0) throw new InvalidOperationException("No tool settings are registered.");
                ToolPresetStore.Save(groups[group], name, ToolSettings.Snapshot(groups[group])); message = "Saved tool preset. Saving the same name in this group replaces its values.";
            }));
        }
        else if (tab == 4)
        {
            Click("Save selected geometry", 455, 157, 580, 34, () => Run(() => { MeshSelection.SaveSet(name); message = "Selection sets belong to this design and structure for this editing session."; }));
        }
        else if (tab == 3)
        {
            Click("Save pinned checkpoint", 455, 157, 320, 34, () => Run(() => { WorkshopStorage.SaveCheckpoint(name, DesignEditor.Instance!.Snapshot()); message = "Saved a pinned copy of this design, including unsaved changes."; }));
        }
        else
        {
            Input(tags, "Tags (optional)", 455, 157, 330, 34, value => tags = value);
            Click("Save copied parts", 797, 157, 238, 34, () => Run(() =>
            {
                var json = BlueprintClipboard.GetClipboardJson();
                if (string.IsNullOrEmpty(json)) throw new InvalidOperationException("Select parts and press Ctrl+C first.");
                WorkshopStorage.SavePart(name, json, tags); message = "Saved the copied group to the part library.";
            }));
        }
        RefreshRows();
    }

    static void RefreshRows()
    {
        if (rows != null) { rows.SetActive(false); UnityEngine.Object.Destroy(rows); }
        rowCallbacks.Clear(); rows = NativeUi.Node("Workshop rows", panel!.transform, 0, 205, 1060, 500).gameObject;
        bool Matches(params string[] text) => search.Length == 0 || text.Any(s => s.Contains(search, StringComparison.OrdinalIgnoreCase));
        var list = tab == 0 ? presets.Where(p => Matches(p.Name, p.Group)).Select(p => (p.Id, Title: p.Name + "  ·  " + p.Group, Detail: p.CreatedUtc.ToLocalTime().ToString("g"))).ToArray()
            : tab == 3 ? backups.Where(b => Matches(b.Name, b.Operation)).Select(b => (b.Id, Title: (b.Pinned ? "Pinned · " : "") + b.Name, Detail: b.Operation + "  ·  " + b.CreatedUtc.ToLocalTime().ToString("g"))).ToArray()
            : tab == 4 ? selectionSets.Where(s => Matches(s)).Select(s => (Id: s, Title: s, Detail: "Geometry selection on this structure")).ToArray()
            : parts.Where(p => Matches(p.Name, p.Tags)).Select(p => (p.Id, Title: p.Name + $"  ·  {p.Count} parts", Detail: (p.TooLarge ? "Too large to open: delete it here.  " : "") + p.Tags + "  " + p.CreatedUtc.ToLocalTime().ToString("g"))).ToArray();
        int pages = Math.Max(1, (list.Length + PageSize - 1) / PageSize); page = Math.Clamp(page, 0, pages - 1);
        if (!list.Any(p => p.Id == selected)) selected = list.FirstOrDefault().Id ?? "";
        int row = 0;
        foreach (var item in list.Skip(page * PageSize).Take(PageSize))
        {
            var id = item.Id;
            bool geometry = tab is 1 or 2 or 3;
            float left = geometry ? 194 : 22;
            NativeUi.Click(font, rowCallbacks, rows.transform, (selected == id ? "› " : "") + item.Title, left, row * 98, 1035-left, 35, () => { selected = id; confirmDelete = ""; redrawRows = true; }, true);
            NativeUi.Label(font, rows.transform, item.Detail, left+12, row * 98 + 40, 1010-left, 42, 13);
            if (geometry)
            {
                try
                {
                    // Only the entries shown read their saved group, once (review WF-06).
                    if (!thumbnails.TryGetValue(id, out var thumbnail))
                    {
                        thumbnails[id] = null; // a group that can't be drawn isn't tried again
                        var document = WorkshopGeometry.Read(tab == 3 ? File.ReadAllText(Path.Combine(backups.First(b=>b.Id==id).Directory,"original.blueprint")) : WorkshopStorage.ReadPartJson(id, tab == 2));
                        if (document.Meshes.Length > 0) { thumbnails[id] = thumbnail = new GeometryPreview(); thumbnail.Set(document,1,ObjImportUnits.Metres,ObjUpAxis.Y); }
                    }
                    if (thumbnail != null)
                    {
                        var image = NativeUi.Node("Saved geometry thumbnail",rows.transform,22,row*98,160,90).gameObject.AddComponent<RawImage>();
                        image.texture=thumbnail.Texture; image.raycastTarget=false;
                    }
                }
                catch (Exception ex) { Plugin.ModLog.LogDebug("Thumbnail unavailable: "+ex.Message); }
            }
            row++;
        }
        if (list.Length == 0) NativeUi.Label(font, rows.transform, "No entries yet. Save a named group, copy some parts, or make a checkpoint.", 22, 12, 1010, 55, 18);
        RowClick("Previous", 22, 307, 145, () => { page--; redrawRows = true; });
        NativeUi.Label(font, rows.transform, $"Page {page + 1} / {pages}  ·  {list.Length} entries", 185, 313, 480, 25, 16);
        RowClick("Next", 880, 307, 155, () => { page++; redrawRows = true; });
        if (selected.Length > 0)
        {
            if (tab == 0)
            {
                RowClick("Apply selected preset", 22, 359, 300, () => Run(() => { var p = presets.First(p => p.Id == selected); int changed = ToolSettings.Apply(p.Values); message = $"Applied {changed} tool settings from {p.Name}."; }));
                Delete(() => ToolPresetStore.Delete(selected));
            }
            else if (tab == 4)
            {
                RowClick("Recall selection", 22, 359, 300, () => Run(() => { MeshSelection.RecallSet(selected); message = "Requested this saved geometry selection. Ctrl+Z undoes a successful recall."; }));
                RowClick("Add to current selection", 334, 359, 315, () => Run(() => { MeshSelection.RecallSet(selected, add: true); message = "Requested adding the saved geometry selection."; }));
                Delete(() => { MeshSelection.DeleteSet(selected); return true; });
            }
            else if (tab == 3)
            {
                var b = backups.First(b => b.Id == selected);
                RowClick(b.Pinned ? "Unpin" : "Pin checkpoint", 22, 359, 180, () => Run(() => { WorkshopStorage.SetPinned(selected, !b.Pinned); message = b.Pinned ? "Unpinned checkpoint." : "Pinned checkpoint: automatic cleanup keeps it."; }));
                RowClick(factions.Count == 0 ? "No faction" : "Faction: " + factions[faction].Name + " ›", 214, 359, 490, () => { if (factions.Count > 0) faction = (faction + 1) % factions.Count; redrawRows = true; });
                RowClick("Restore as a new copy", 716, 359, 319, () => Run(() =>
                {
                    if (factions.Count == 0) throw new InvalidOperationException("Create a faction before recovering a copy.");
                    string path = WorkshopStorage.RestoreCopy(selected, factions[faction].Directory); message = "Saved " + Path.GetFileName(path) + ". Load it from that faction's vehicle library. The open design is unchanged.";
                }));
            }
            else
            {
                var p = parts.First(p => p.Id == selected);
                RowClick("Copy group to clipboard", 22, 359, 300, () => Run(() => { BlueprintClipboard.SetClipboardJson(WorkshopStorage.ReadPartJson(p.Id, p.History)); message = "Copied this group. Select a destination part and press Ctrl+V."; }));
                RowClick("Paste onto selected part", 334, 359, 315, () =>
                {
                    string json;
                    try { json = WorkshopStorage.ReadPartJson(p.Id, p.History); }
                    catch (Exception ex) { message = ex.Message; redrawRows = true; return; }
                    var editor = DesignEditor.Instance!; Close(); PartClipboard.PasteJson(editor, json);
                });
                Delete(() => WorkshopStorage.DeletePart(selected, tab == 2));
            }
        }
        NativeUi.Label(font, rows.transform, message.Length > 0 ? message : tab == 3 ? "Recovery writes a new vehicle file. Pinned checkpoints are preserved during backup cleanup." : tab == 4 ? "Select a structure before opening this tab. Selection sets are kept for this design session and checked against mesh topology before recall." : tab == 2 ? "The 30 most recent distinct copied groups are kept across game sessions. Save favourites to Saved parts." : tab == 1 ? "Save copied groups for reuse on other designs. Select a destination part before opening the workshop to paste onto it." : "Preferences are saved across sessions. Named presets affect only their registered tool group.", 22, 414, 1013, 72, 16);
    }

    static void Delete(Func<bool> delete) => RowClick(confirmDelete == selected ? "Confirm delete" : "Delete selected", 724, 359, 311, () =>
    {
        if (confirmDelete != selected) { confirmDelete = selected; redrawRows = true; return; }
        Run(() => { delete(); selected = confirmDelete = ""; message = "Deleted this saved entry."; });
    });

    static void Support()
    {
        Label("Shareable support report", 22, 120, 1000, 38, 22);
        Label("Includes the game and QoL versions, graphics device, hook compatibility results, registered tool preferences and the last 100 log lines. Vehicle blueprints, clipboard groups and screenshots are excluded. User and game folder paths are replaced with short labels.", 22, 172, 1013, 105, 18);
        Click("Save report", 22, 300, 310, 42, () => Run(() => { string path = WorkshopStorage.SaveSupportReport(SupportText()); message = "Saved " + path; }));
        Click("Copy report text", 350, 300, 310, 42, () => { GUIUtility.systemCopyBuffer = SupportText(); message = "Copied the support report."; redraw = true; });
        Click("Open reports folder", 678, 300, 357, 42, () => OpenFolder(Path.Combine(WorkshopStorage.Root, "SprocketQoL", "Reports")));
        Label(message, 22, 380, 1013, 180, 17);
    }

    internal static string SupportText()
    {
        var report = new StringBuilder();
        report.AppendLine("Sprocket Quality of Life support report").AppendLine(DateTime.UtcNow.ToString("O"));
        report.AppendLine("QoL: " + typeof(Plugin).Assembly.GetName().Version);
        report.AppendLine("Game: " + Application.version + "; Unity: " + Application.unityVersion);
        report.AppendLine("Graphics: " + SystemInfo.graphicsDeviceName + "; " + SystemInfo.graphicsDeviceType);
        report.AppendLine("Hooks: " + Hooks.Attached + "/" + Hooks.Report.Count);
        foreach (var hook in Hooks.Report) report.AppendLine(hook.Feature + ": " + hook.Hook + " — " + (hook.Error ?? "attached"));
        report.AppendLine().AppendLine("Registered tool preferences:");
        foreach (var p in ToolSettings.Snapshot()) report.AppendLine(p.Key + " = " + p.Value.GetRawText());
        string log = Path.Combine(WorkshopStorage.Root, "LogOutput.log");
        if (File.Exists(log))
        {
            report.AppendLine().AppendLine("Recent log:");
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 128 * 1024) { stream.Seek(-128 * 1024, SeekOrigin.End); }
                using var reader = new StreamReader(stream);
                if (stream.Position > 0) reader.ReadLine();
                var tail = new Queue<string>(); string? line;
                while ((line = reader.ReadLine()) != null) { tail.Enqueue(line); if (tail.Count > 100) tail.Dequeue(); }
                foreach (var entry in tail) report.AppendLine(entry);
            }
            catch (IOException ex) { report.AppendLine("Could not read log: " + ex.Message); }
        }
        return WorkshopStorage.Scrub(report.ToString(), (WorkshopStorage.Root, "[BepInEx]"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "[User]"), (Path.GetDirectoryName(WorkshopStorage.Root) ?? "", "[Game]"));
    }

    static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { message = ex.Message; redraw = true; }
    }
    static void Label(string text, float x, float y, float w, float h, float size = 17) => NativeUi.Label(font, panel!.transform, text, x, y, w, h, size);
    static void Click(string text, float x, float y, float w, float h, Action run) => NativeUi.Click(font, callbacks, panel!.transform, text, x, y, w, h, run);
    static void RowClick(string text, float x, float y, float w, Action run) => NativeUi.Click(font, rowCallbacks, rows!.transform, text, x, y, w, 34, run);
    static void Input(string text, string placeholder, float x, float y, float w, float h, Action<string> change)
    {
        var rect = NativeUi.Node("Workshop input", panel!.transform, x, y, w, h);
        var background = rect.gameObject.AddComponent<Image>(); background.color = new Color(0.12f, 0.13f, 0.15f);
        var field = rect.gameObject.AddComponent<TMP_InputField>(); field.targetGraphic = background;
        var viewport = NativeUi.Node("Text viewport", rect, 8, 5, w - 16, h - 10); viewport.gameObject.AddComponent<RectMask2D>(); field.textViewport = viewport;
        var value = NativeUi.Label(font, viewport, "", 0, 0, w - 16, h - 10, 16); value.enableWordWrapping = false; value.overflowMode = TextOverflowModes.Overflow; field.textComponent = value;
        var hint = NativeUi.Label(font, viewport, placeholder, 0, 0, w - 16, h - 10, 16); hint.color = new Color(0.6f, 0.63f, 0.68f); field.placeholder = hint;
        field.lineType = TMP_InputField.LineType.SingleLine; field.SetTextWithoutNotify(text);
        var callback = DelegateSupport.ConvertDelegate<UnityAction<string>>(change)!; textCallbacks.Add(callback); field.onValueChanged.AddListener(callback);
    }
}
