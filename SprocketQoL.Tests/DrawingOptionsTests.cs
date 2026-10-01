using System.Numerics;
using SprocketQoL;

static class DrawingOptionsTests
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("drawing options: " + why); }
    static Drawing.Shape Box(Vector3 min, Vector3 max)
    {
        var p = new[] { new Vector3(min.X,min.Y,min.Z),new Vector3(max.X,min.Y,min.Z),new Vector3(max.X,max.Y,min.Z),new Vector3(min.X,max.Y,min.Z),
            new Vector3(min.X,min.Y,max.Z),new Vector3(max.X,min.Y,max.Z),new Vector3(max.X,max.Y,max.Z),new Vector3(min.X,max.Y,max.Z) };
        return Drawing.Weld(p, new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 });
    }
    public static void Run()
    {
        DrawingMountTests.Run();
        checks = 0;
        var choices = new GunAnnotationPreferences(null);
        string mainGun = GunAnnotationPreferences.Key("RGM-167", 42);
        string coax = GunAnnotationPreferences.Key("RGM-167", 43);
        string otherTank = GunAnnotationPreferences.Key("Casemate", 42);
        Check(choices.Shows(mainGun) && choices.Shows(coax), "all gun annotations enabled by default");
        choices.Set(mainGun, false);
        Check(!choices.Shows(mainGun) && choices.Shows(coax), "exclude only the selected gun");
        Check(choices.Shows(otherTank), "same gun ID in another named design stays enabled");
        choices.Set(coax, false);
        var restored = new GunAnnotationPreferences(choices.Serialize());
        Check(!restored.Shows(mainGun) && !restored.Shows(coax), "multiple per-gun choices survive restart");
        restored.Set(mainGun, true);
        Check(restored.Shows(mainGun) && !restored.Shows(coax), "reenabling one gun preserves other exclusions");
        Check(!choices.Shows(mainGun), "export snapshot unaffected by later preference edits");
        restored.Set(coax, true);
        Check(restored.Serialize() == "[]", "reenabling all guns clears stored exclusions");
        string unicode = GunAnnotationPreferences.Key("Tank | \"A\" / รถถัง", 42);
        choices.Set(unicode, false);
        Check(!new GunAnnotationPreferences(choices.Serialize()).Shows(unicode), "punctuation and Unicode names roundtrip");
        foreach (string invalid in new[] { "", "null", "oops", "{}", "[42]" })
            Check(new GunAnnotationPreferences(invalid).Shows(mainGun), "invalid config safely defaults to showing annotations: " + invalid);
        Check(GunAnnotationPreferences.Key(null, 42) == "" && GunAnnotationPreferences.Key("Tank", -1) == "", "unidentified guns cannot create global exclusions");
        choices.Set("", false);
        Check(choices.Shows(""), "unknown design identity leaves annotations enabled");
        byte[] colour = { 90, 130, 180 };
        var rgb = (byte[])colour.Clone(); DrawingOptions.Ink(rgb, 0, 30, 0);
        Check(rgb.SequenceEqual(colour), "zero strength preserves paint");
        DrawingOptions.Ink(rgb, 0, 30, 0.5f); Check(rgb.SequenceEqual(new byte[] { 60,80,105 }), "half strength blends each colour channel");
        DrawingOptions.Ink(rgb, 0, 30, 1); Check(rgb.All(c => c == 30), "full strength retains original wireframe ink");
        Check(DrawingOptions.Strength(-1) == 0 && DrawingOptions.Strength(200) == 1 && DrawingOptions.Strength(float.NaN) == 1, "invalid setting bounded");
        Check(DrawingOptions.Weight(13490) == "13.49 t" && DrawingOptions.Weight(float.NaN) == "" && DrawingOptions.Weight(0) == "", "weight in tonnes without invented missing mass");
        byte[] bw = { 255,255,255,0,0,0,128,128,128 };
        var blue = DrawingOptions.Blueprint(bw);
        Check(blue.Take(3).SequenceEqual(new byte[] { 19,55,91 }), "blue paper");
        Check(blue.Skip(3).Take(3).SequenceEqual(new byte[] { 230,245,255 }), "white blueprint ink");
        Check(blue[6] > blue[0] && blue[6] < blue[3] && bw[0] == 255, "anti-alias shades preserved; source sheet intact");
        var paper = Enumerable.Repeat((byte)255, 41*41*3).ToArray();
        var plain = DrawingOptions.Blueprint(paper);
        var grid = DrawingOptions.Blueprint(paper,41,41,10,0.2f);
        int Pixel(int x,int y)=>(y*41+x)*3;
        Check(grid[Pixel(5,5)]==plain[Pixel(5,5)], "grid leaves cell interiors unchanged");
        Check(grid[Pixel(10,5)]>plain[Pixel(10,5)] && grid[Pixel(0,5)]>grid[Pixel(10,5)], "minor squares and stronger major grid lines");
        Check(grid[Pixel(10,5)]==grid[Pixel(5,10)], "horizontal and vertical spacing and intensity match");
        Check(grid[Pixel(0,0)]==grid[Pixel(0,5)], "grid crossing does not double brightness");
        Check(DrawingOptions.Blueprint(paper,41,41,10,0).SequenceEqual(plain), "zero grid strength preserves plain blueprint");
        Check(DrawingOptions.Blueprint(paper,41,41,0,1).SequenceEqual(plain), "disabled grid preserves plain blueprint");
        paper[0]=paper[1]=paper[2]=0;
        Check(DrawingOptions.Blueprint(paper,41,41,10,1).Take(3).SequenceEqual(new byte[]{230,245,255}), "text and solid vehicle lines stay bright at grid intersections");
        bool badSize=false; try { DrawingOptions.Blueprint(paper,40,41,10); } catch(ArgumentException) { badSize=true; }
        Check(badSize,"grid rejects mismatched image dimensions");
        bool incomplete=false; try { DrawingOptions.Blueprint(new byte[4]); } catch(ArgumentException) { incomplete=true; }
        Check(incomplete,"blueprint rejects incomplete RGB pixels before indexing");

        var barrel = Box(new(-0.09f,0.81f,1), new(0.09f,0.99f,4));
        var pivot = new Vector3(0,0.9f,1); var muzzle = new Vector3(0,0.9f,4);
        var before = barrel.P.ToArray();
        List<DrawingOptions.Ghost> Motion(bool e, bool t) => DrawingOptions.Motion(new[] { barrel }, pivot, muzzle, Vector3.UnitX, Vector3.UnitY, -10, 25, -22.5f, 30, e, t);
        Check(Motion(false,false).Count == 0, "disabled options create no overlays");
        Check(Motion(true,false).Count == 2 && Motion(true,false).All(g => g.View == 2), "elevation toggle only side view");
        Check(Motion(false,true).Count == 2 && Motion(false,true).All(g => g.View == 0), "traverse toggle only top view");
        var ghosts = Motion(true,true); Check(ghosts.Count == 4, "four independently labelled limits");
        foreach (var ghost in ghosts)
        {
            Check(Math.Abs(Vector3.Distance(ghost.Tip,pivot) - 3) < 0.0001f, "rotates around trunnion without stretching barrel");
            Check(Vector3.Distance(ghost.Arc[0],muzzle) < 0.0001f && Vector3.Distance(ghost.Arc[^1],ghost.Tip) < 0.0001f, "arc spans neutral to limit");
            Check(ghost.Shapes[0].T.SequenceEqual(barrel.T), "gun shape topology preserved");
        }
        Check(ghosts.Single(g => g.Label.Contains("ELEVATION")).Tip.Y > muzzle.Y, "positive elevation goes up");
        Check(ghosts.Single(g => g.Label.Contains("DEPRESSION")).Tip.Y < muzzle.Y, "negative elevation goes down");
        Check(ghosts.Single(g => g.Label.Contains("LEFT")).Tip.X < 0 && ghosts.Single(g => g.Label.Contains("RIGHT")).Tip.X > 0, "traverse has correct left and right signs");
        Check(barrel.P.SequenceEqual(before), "drawing motion does not pose or mutate original geometry");
        Check(DrawingOptions.Motion(new[]{barrel},pivot,muzzle,new(float.NaN,0,0),Vector3.UnitY,-10,25,0,0,true,false).Count==0,
            "invalid rotation axis cannot contaminate drawing bounds with NaN");
        Check(DrawingOptions.Motion(new[]{barrel},pivot,muzzle,Vector3.UnitX,Vector3.UnitY,25,-10,30,-20,true,true).Count==0,
            "reversed gun limits are omitted instead of drawing misleading ranges");
        Check(DrawingOptions.TurretMotion(new[]{barrel},pivot,muzzle,new(0,float.NaN,0),-180,180).Count==0,
            "invalid turret axis is omitted");
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var turned = DrawingOptions.Motion(Array.Empty<Drawing.Shape>(), Vector3.Zero, Vector3.Transform(Vector3.UnitZ,turn),
            Vector3.Transform(Vector3.UnitX,turn), Vector3.UnitY,0,25,0,0,true,false);
        Check(turned.Single().Tip.Y > 0, "side-facing mount elevates around its own right axis");

        // Traverse is measured at the turret ring, not the forward-offset gun trunnion.
        var ringPivot = new Vector3(0,0.9f,0);
        var limited = DrawingOptions.TurretMotion(new[] { barrel },ringPivot,muzzle,Vector3.UnitY,-35,75);
        Check(limited.Count == 2 && limited[0].Label == "-35° TURRET MIN" && limited[1].Label == "75° TURRET MAX", "configured asymmetric ring limits");
        Check(limited.All(g=>Math.Abs(Vector3.Distance(g.Tip,ringPivot)-4)<0.0001), "turret rotates round ring, not trunnion");
        Check(Vector3.Distance(limited[0].Arc[0],limited[0].Tip)<0.0001f && Vector3.Distance(limited[0].Arc[^1],limited[1].Tip)<0.0001f, "turret arc spans only configured range");
        var restricted=DrawingOptions.TurretMotion(new[]{barrel},ringPivot,muzzle,Vector3.UnitY,30,60);
        Check(restricted[0].Arc.All(p=>p.X>1.9f), "positive-only limits do not falsely include neutral");
        var full=DrawingOptions.TurretMotion(new[]{barrel},ringPivot,muzzle,Vector3.UnitY,-180,180);
        Check(full.Count==1 && full[0].FullCircle && full[0].Shapes.Length==0 && full[0].Label=="360° TURRET ROTATION", "full rotation is one circle without duplicate rearward ghost barrels");
        Check(Vector3.Distance(full[0].Arc[0],full[0].Arc[^1])<0.0001f && full[0].Arc.Length==181, "full turret circle closes");
        Check(DrawingOptions.TurretMotion(new[]{barrel},ringPivot,muzzle,Vector3.UnitY,0,0).Count==0, "locked turret has no rotation arc");
        Check(DrawingOptions.TurretMotion(new[]{barrel},ringPivot,muzzle,Vector3.UnitY,75,-35).Count==0, "invalid reversed limits omitted");

        const int w = 1600, h = 1430;
        var views = new Drawing.View[4];
        views[0] = new(new(0,1.1f,0.7f), -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY,130,1280,560);
        views[2] = new(new(0,1.1f,0.7f), -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX,130,1280,560);
        var at = new (int X,int Y)[] { (180,750),(0,0),(180,120),(0,0) };
        var sheet = Enumerable.Repeat((byte)255,w*h*3).ToArray();
        var hull = Box(new(-1.4f,-0.6f,-2.6f),new(1.4f,0.6f,2.6f));
        var casemate = Box(new(-0.9f,0.6f,-0.8f),new(0.9f,1.35f,1.1f));
        foreach (int i in new[] { 0,2 })
        {
            var v=views[i]; var shapes=new[] { hull,casemate,barrel }; var depth=Drawing.Depths(shapes,v);
            var mask=new bool[v.Width*v.Height]; Drawing.Lines(shapes,v,depth,mask);
            for(int y=0;y<v.Height;y++) for(int x=0;x<v.Width;x++)
                if(mask[y*v.Width+x]) DrawingOptions.Ink(sheet,((at[i].Y+y)*w+at[i].X+x)*3,0,1);
            var solid=new bool[mask.Length]; Drawing.Lines(new[] {barrel},v,Drawing.Depths(new[] {barrel},v),solid);
            var dashed=new bool[mask.Length]; Drawing.Lines(new[] {barrel},v,Drawing.Depths(new[] {barrel},v),dashed,10);
            Check(dashed.Count(x=>x)>0 && dashed.Count(x=>x)<solid.Count(x=>x), "ghost barrel lines are dashed");
            foreach(var g in ghosts.Where(g=>g.View==i)) foreach(var p in g.Shapes.SelectMany(s=>s.P).Concat(g.Arc))
            { var q=v.Project(p); Check(q.X>=0&&q.Y>=0&&q.X<v.Width&&q.Y<v.Height,"ghost geometry fits preview"); }
        }
        DrawingOptions.DrawMotion(ghosts,new[]{sheet},views,w,h,at);
        var narrowView=new Drawing.View(Vector3.Zero,Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ,1,20,20);
        var narrowPage=Enumerable.Repeat((byte)255,40*40*3).ToArray();
        var narrowMotion=new[]{new DrawingOptions.Ghost(0,Array.Empty<Drawing.Shape>(),Vector3.Zero,Vector3.UnitX,Array.Empty<Vector3>(),"15° ELEVATION")};
        DrawingOptions.DrawMotion(narrowMotion,new[]{narrowPage},new[]{narrowView},40,40,new[]{(X:5,Y:5)});
        Check(narrowPage.Any(c=>c<255),"motion labels in a narrow view draw without invalid clamp bounds");
        var clippedPage=Enumerable.Repeat((byte)255,40*40*3).ToArray();
        DrawingOptions.DrawMotion(new[]{new DrawingOptions.Ghost(0,new[]{barrel},pivot,muzzle,Array.Empty<Vector3>(),"LIMIT")},new[]{clippedPage},new[]{narrowView},40,40,new[]{(X:-8,Y:-8)});
        Check(clippedPage.Any(c=>c<255),"ghosts partially outside the sheet are clipped before RGB indexing");
        var title=Drawing.Words("DRAWING OPTIONS TEST — INTACT CASEMATE",32,true,1200);
        Drawing.Stamp(sheet,w,h,180,1390,title,0);
        Drawing.Stamp(sheet,w,h,180,65,Drawing.Words("Test vehicle",32,true,700),0);
        Drawing.Stamp(sheet,w,h,1300,65,Drawing.Words(DrawingOptions.Weight(13490),32,true,200),0);
        string dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../scratch/drawing-options-preview")); Directory.CreateDirectory(dir);
        Drawing.SavePng(Path.Combine(dir,"motion-white.png"),w,h,sheet);
        Drawing.SavePng(Path.Combine(dir,"motion-blue.png"),w,h,DrawingOptions.Blueprint(sheet));
        Drawing.SavePng(Path.Combine(dir,"motion-grid-blue.png"),w,h,DrawingOptions.Blueprint(sheet,w,h,33,0.2f));
        var turretSheet=Enumerable.Repeat((byte)255,1700*950*3).ToArray();
        var tv=new Drawing.View[4]; tv[0]=new(ringPivot,-Vector3.UnitZ,Vector3.UnitX,-Vector3.UnitY,70,750,720);
        var ta=new (int X,int Y)[4];
        foreach(var (group,x,label) in new[]{(limited,50,"TURRET LIMITS: -35° TO 75°"),(full,900,"FULL ROTATION: -180° TO 180°")})
        {
            ta[0]=(x,60);
            DrawingOptions.DrawMotion(group,new[]{turretSheet},tv,1700,950,ta);
            Drawing.Stamp(turretSheet,1700,950,x,880,Drawing.Words(label,30,true,750),0);
        }
        Drawing.SavePng(Path.Combine(dir,"turret-limits.png"),1700,950,DrawingOptions.Blueprint(turretSheet));
        Console.WriteLine($"DRAWING_OPTIONS_TESTS_OK: {checks} checks; previews {dir}");
    }
}
