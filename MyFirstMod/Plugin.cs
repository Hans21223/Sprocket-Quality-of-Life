using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace MyFirstMod;

// Starting point for a new mod: copy this folder, rename it, change the id/name below,
// then build and install with:  .\tools\deploy.ps1 MyFirstMod
[BepInPlugin("yourname.sprocket.myfirstmod", "My First Mod", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    public override void Load() => Log.LogInfo("My First Mod loaded. You should see this line in BepInEx\\LogOutput.log.");
}
