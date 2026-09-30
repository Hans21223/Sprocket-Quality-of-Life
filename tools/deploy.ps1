# Build a mod from this kit and copy its DLL into <GameDir>\BepInEx\plugins (waits for Sprocket to close first).
#   .\tools\deploy.ps1 MyFirstMod
#   .\tools\deploy.ps1 TurretAddon -GameDir "D:\Games\Sprocket" -Dotnet "C:\path\to\dotnet.exe"
param(
    [Parameter(Mandatory)][string]$Project,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Sprocket",
    [string]$Dotnet = "dotnet"
)
if (-not (Test-Path (Join-Path $GameDir "BepInEx\interop")) -and (Test-Path "D:\Projects\SprocketInteropPatch\TestGame\BepInEx\interop")) {
    $GameDir = "D:\Projects\SprocketInteropPatch\TestGame"
}
$proj = Get-ChildItem (Join-Path (Split-Path $PSScriptRoot) $Project) -Filter *.csproj | Select-Object -First 1
if (-not $proj) { throw "No .csproj in folder '$Project'" }
& $Dotnet build $proj.FullName -c Release -nologo -v q "-p:GameDir=$GameDir"
if ($LASTEXITCODE) { throw "Build failed" }
$name = ([xml](Get-Content $proj.FullName)).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
if (-not $name) { $name = $proj.BaseName }
$dll = Join-Path $proj.DirectoryName "bin\Release\net6.0\$name.dll"
# A closed game can linger as a tiny husk process; only wait for one that is really running.
while (Get-CimInstance Win32_Process -Filter "Name='Sprocket.exe'" | Where-Object { $_.WorkingSetSize -gt 1MB }) {
    Write-Host "Close Sprocket to install $name.dll..."; Start-Sleep -Seconds 3
}
Copy-Item $dll (Join-Path $GameDir "BepInEx\plugins") -Force
"Installed $name.dll into $GameDir\BepInEx\plugins"

$pdb = [System.IO.Path]::ChangeExtension($dll, ".pdb")
$targets = @(
    "D:\Projects\SprocketInteropPatch\TestGame\BepInEx\plugins",
    "C:\Program Files (x86)\Steam\steamapps\common\Sprocket\ModManager\mods\Sprocket (test copy)\Quality of Life\BepInEx\plugins",
    "C:\Program Files (x86)\Steam\steamapps\common\Sprocket\ModManager\mods\Sprocket (test copy)\BepInEx\plugins",
    "C:\Program Files (x86)\Steam\steamapps\common\Sprocket\ModManager\mods\Sprocket\Quality of Life\BepInEx\plugins"
)
foreach ($t in $targets) {
    if (Test-Path (Split-Path $t)) {
        if (-not (Test-Path $t)) { New-Item -ItemType Directory -Path $t -Force | Out-Null }
        Copy-Item $dll $t -Force
        if (Test-Path $pdb) { Copy-Item $pdb $t -Force }
        "Installed $name.dll into $t"
    }
}
