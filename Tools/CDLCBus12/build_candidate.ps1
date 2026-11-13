[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Bakes,
    [string]$ManagedPath='D:/SteamLibrary/steamapps/common/Cities Skylines II/Cities2_Data/Managed',
    [string]$Blender='D:/SteamLibrary/steamapps/common/Blender/blender.exe'
)
$ErrorActionPreference='Stop'
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'Keep previous evidence; select a new output directory.'}
$null=New-Item -ItemType Directory -Path $Output
if(-not $Bakes){
    $Bakes=Join-Path $Output 'surface-bakes'
    & $Blender --background --threads 4 --python-exit-code 1 --python Tools/CDLCBus12/bake_surfaces.py -- --out $Bakes *> (Join-Path $Output 'bake-surfaces.log')
    if($LASTEXITCODE -ne 0){throw 'Surface normal bake failed'}
}
& dotnet run -c Release --project Tests/CDLC.BusDriver.Tests "-p:ManagedPath=$ManagedPath" -- generic-export $Output *> (Join-Path $Output 'export-runtime.log')
if($LASTEXITCODE -ne 0){throw 'Runtime export failed'}
& dotnet run -c Release --project Tools/SWB6109EV12G/KitExport "-p:ManagedPath=$ManagedPath" -- --length 12 --width 2.54 --roof 3.05 --front-axle 3.4 --rear-axle -2.5 --doors 3 --all-parts --output (Join-Path $Output 'kit.json') *> (Join-Path $Output 'export-source.log')
if($LASTEXITCODE -ne 0){throw 'Source geometry export failed'}
& $Blender --background --python-exit-code 1 --python Tools/CDLCBus12/build_source.py -- --kit (Join-Path $Output 'kit.json') --data $Output --bakes $Bakes --out (Join-Path $Output 'source') *> (Join-Path $Output 'build-source.log')
if($LASTEXITCODE -ne 0){throw 'Blender source build failed'}
Write-Host "Candidate created: $Output"
