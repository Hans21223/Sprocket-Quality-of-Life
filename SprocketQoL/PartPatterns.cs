using System.Globalization;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.VehicleDesigner;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Num = System.Numerics.Vector3;
using Matrix = System.Numerics.Matrix4x4;

namespace SprocketQoL;

/// Repeat an attached assembly with one backed-up design edit. Cancelling only closes the preview.
[HarmonyPatch]
internal static class PartPatterns
{
    static GameObject? canvas, panel;
    static TMP_FontAsset? font;
    static TextMeshProUGUI? preview;
    static readonly List<UnityAction> callbacks=new();
    static readonly List<UnityAction<string>> textCallbacks=new();
    static string source="",message="";
    static int[] selection=Array.Empty<int>();
    static IntPtr target;
    static int releaseAt=-10,count=8,axis=1,partCount;
    static float x=.25f,y,z,sweep=360,radius=1,centreX,centreY,centreZ;
    static bool radial,rotate=true;
    static Num pivot;
    static Matrix[] transforms=Array.Empty<Matrix>();
    internal static bool IsOpen=>canvas!=null;
    internal static bool BlocksInput=>IsOpen||Time.frameCount<=releaseAt;
    internal static IReadOnlyList<Matrix> PreviewTransforms=>transforms;
    internal static string SourceJson=>source;
    internal static IReadOnlyList<int> SelectedPartIds=>selection;

    static PartPatterns()=>ToolSettings.RegisterFields(typeof(PartPatterns),"Part patterns",nameof(count),nameof(axis),nameof(x),nameof(y),nameof(z),nameof(sweep),nameof(radius),nameof(radial),nameof(rotate));
    [HarmonyPrefix,HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.UpdatePointerOperators))]
    static bool Pointer()=>!BlocksInput;

    internal static void Update()
    {
        if(!IsOpen&&Time.frameCount>releaseAt)RestoreInputs();
        if(!IsOpen)return;
        if(DesignEditor.Instance?.IsReady!=true||DesignEditor.Instance.Core?.Target?.Pointer!=target){Close();return;}
        if(Keyboard.current?.escapeKey.wasPressedThisFrame==true)Close();
    }
    internal static void Open()
    {
        var editor=DesignEditor.Instance;
        if(EditorModal.BlocksInput||IsOpen||editor?.IsReady!=true||editor.IsBusy||ObjTransfer.IsOpen||WorkshopLibrary.IsOpen||CommandPalette.IsOpen||QolMenu.IsOpen||editor.Core?.Editor?.OperationInProgress==true||editor.CaptureBlocked())return;
        selection=editor.SelectedParts().ToArray();
        if(selection.Length==0){editor.Say("Select attached parts to repeat first.");return;}
        source=editor.Snapshot();pivot=PartPatternMath.SelectionPivot(source,selection);
        var copy=BlueprintClipboard.Copy(source,selection);partCount=copy.Count;
        var objects=Conversion.Objects(Conversion.Parse(source));
        if(selection.Any(id=>Conversion.Id(objects[id],"pvuid")<0)){editor.Say("Select attached parts rather than the main hull.");return;}
        centreX=pivot.X;centreY=pivot.Y;centreZ=pivot.Z;count=Math.Clamp(count,1,100);axis=Math.Clamp(axis,0,2);message="";
        target=editor.Core!.Target.Pointer;font=NativeUi.Font();if(font==null)return;
        try
        {
            InputLease.Take("Part patterns");
            RightClickMenu.Close();canvas=NativeUi.Canvas("Part pattern preview");Build();
        }
        catch{Close();RestoreInputs();throw;}
    }
    static void Close()
    {
        if(canvas!=null){canvas.SetActive(false);UnityEngine.Object.Destroy(canvas);}canvas=panel=null;font=null;preview=null;callbacks.Clear();textCallbacks.Clear();transforms=Array.Empty<Matrix>();releaseAt=Time.frameCount+2;ToolSettings.Flush();
    }
    internal static void LeftEditor(){Close();RestoreInputs();target=IntPtr.Zero;source="";selection=Array.Empty<int>();}
    static void RestoreInputs()=>InputLease.Release("Part patterns");
    static PartPatternMath.Options Options()=>new(radial?PartPatternMath.Pattern.Radial:PartPatternMath.Pattern.Linear,count,new Num(x,y,z),axis==0?Num.UnitX:axis==1?Num.UnitY:Num.UnitZ,sweep,new Num(centreX,centreY,centreZ),radius,rotate);

    static void Build()
    {
        if(panel!=null){panel.SetActive(false);UnityEngine.Object.Destroy(panel);}callbacks.Clear();textCallbacks.Clear();
        var rect=NativeUi.Node("Part patterns",canvas!.transform,0,0,1000,670);rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);panel=rect.gameObject;
        panel.AddComponent<Image>().color=new Color(.08f,.09f,.105f,1);
        Label("Repeat selected parts",22,14,740,35,24);Button("Cancel / Esc",820,14,155,34,Close);
        Button(radial?"Pattern: radial ›":"Pattern: line ›",22,67,350,36,()=>{radial=!radial;Build();});
        Label("Added groups (1–100)",400,73,280,30);Input(count.ToString(CultureInfo.InvariantCulture),700,67,275,v=>{if(int.TryParse(v,out int n))count=n;Changed();});
        if(!radial)
        {
            Label("Spacing per copy (metres)",22,127,940,28,19);
            Number("X",x,22,167,v=>x=v);Number("Y",y,342,167,v=>y=v);Number("Z",z,662,167,v=>z=v);
            Label("The original stays in place. Copy 1 moves by this offset; later copies add the same offset again.",22,219,950,48);
        }
        else
        {
            Button("Axis: "+new[]{"X","Y","Z"}[Math.Clamp(axis,0,2)]+" ›",22,125,285,34,()=>{axis=(axis+1)%3;Build();});
            Label("Sweep degrees",342,129,150,28);Input(F(sweep),505,125,150,v=>{sweep=Parse(v);Changed();});
            Label("Radius (m)",687,129,140,28);Input(F(radius),830,125,145,v=>{radius=Parse(v);Changed();});
            Label("Ring centre in vehicle coordinates (metres)",22,178,950,26);
            Number("X",centreX,22,215,v=>centreX=v);Number("Y",centreY,342,215,v=>centreY=v);Number("Z",centreZ,662,215,v=>centreZ=v);
            Button(rotate?"Rotate each copy: on":"Rotate each copy: off",22,270,460,34,()=>{rotate=!rotate;Build();});
            Button("Centre on original selection",500,270,475,34,()=>{centreX=pivot.X;centreY=pivot.Y;centreZ=pivot.Z;Build();});
            Label("Adds a ring or arc around this centre; the original stays in place. A 360° ring has no duplicate endpoint. Radius 0 keeps the original distance from the axis.",22,317,950,53,16);
        }
        preview=NativeUi.Label(font,panel.transform,"",22,385,950,190,16);
        Button("Create pattern",22,603,625,42,Confirm);Button("Cancel",665,603,310,42,Close);Changed();
    }
    static void Changed()
    {
        try
        {
            if((long)partCount*count>5000)throw new ArgumentException("A pattern may add at most 5,000 parts, including attached components.");
            transforms=PartPatternMath.Transforms(Options(),pivot);
            string sample=string.Join("\n",transforms.Take(5).Select((t,i)=>{var p=Num.Transform(pivot,t);return $"{i+1}: X {p.X:0.###} m, Y {p.Y:0.###} m, Z {p.Z:0.###} m";}));
            if(preview!=null)preview.text=$"Position preview — {count} groups, {partCount*count} added parts\n{sample}"+(transforms.Length>5?$"\n…and {transforms.Length-5} more groups.":"")+"\nAttached parts, settings and paint are copied. No design changes until Create pattern.";
            message="";
        }
        catch(Exception ex){transforms=Array.Empty<Matrix>();message=ex.Message;if(preview!=null)preview.text="Cannot create this pattern: "+message;}
    }
    static void Confirm()
    {
        Changed();if(transforms.Length==0)return;
        var editor=DesignEditor.Instance;
        if(editor?.IsReady!=true||editor.IsBusy||editor.Core?.Target?.Pointer!=target)return;
        var options=Options();var ids=selection.ToArray();Close();
        editor.RequestEdit("Repeating parts","Created part pattern. Restore undoes it.",json=>
        {
            var plan=PartPatternMath.Apply(json,ids,options);
            return new EditResult(plan.Json,plan.FocusVuid,$"added {plan.AddedParts} parts in {options.Copies} groups");
        });
    }
    static string F(float value)=>float.IsFinite(value)?value.ToString("G",CultureInfo.InvariantCulture):"";
    static float Parse(string value)=>float.TryParse(value.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out float n)?n:float.NaN;
    static void Label(string text,float x,float y,float w,float h,float size=17)=>NativeUi.Label(font,panel!.transform,text,x,y,w,h,size);
    static void Button(string text,float x,float y,float w,float h,Action run)=>NativeUi.Click(font,callbacks,panel!.transform,text,x,y,w,h,run);
    static void Number(string label,float number,float x,float y,Action<float> set){Label(label,x,y+5,35,27);Input(F(number),x+40,y,275,value=>{set(Parse(value));Changed();});}
    static void Input(string text,float x,float y,float w,Action<string> change)
    {
        var rect=NativeUi.Node("Pattern value",panel!.transform,x,y,w,34);var background=rect.gameObject.AddComponent<Image>();background.color=new Color(.12f,.13f,.15f);
        var field=rect.gameObject.AddComponent<TMP_InputField>();field.targetGraphic=background;
        var viewport=NativeUi.Node("Text viewport",rect,8,5,w-16,24);viewport.gameObject.AddComponent<RectMask2D>();field.textViewport=viewport;
        var value=NativeUi.Label(font,viewport,"",0,0,w-16,24,17);value.enableWordWrapping=false;value.overflowMode=TextOverflowModes.Overflow;field.textComponent=value;
        field.lineType=TMP_InputField.LineType.SingleLine;field.SetTextWithoutNotify(text);
        var callback=DelegateSupport.ConvertDelegate<UnityAction<string>>(change)!;textCallbacks.Add(callback);field.onValueChanged.AddListener(callback);
    }
}
