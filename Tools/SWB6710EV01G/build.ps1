param(
    [string]$Blender = 'D:\SteamLibrary\steamapps\common\Blender\blender.exe',
    [switch]$Quick
)
$ErrorActionPreference = 'Stop'
$modelRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modelDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $modelDotnet) { $modelDotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $Blender)) { throw "Blender was not found at $Blender" }
Push-Location $modelRoot
try {
    & $modelDotnet run --project Tools/SWB6710EV01G/KitExport/KitExport.csproj -c Release -- --output ModelSource/SWB6710EV01G/kit-cabin.json
    if ($LASTEXITCODE) { throw 'MeshKit export failed' }
    $modelArgs = @('--background','--factory-startup','--python-exit-code','1','--python','Tools/SWB6710EV01G/build_model.py','--')
    if ($Quick) { $modelArgs += '--quick' }
    & $Blender @modelArgs
    if ($LASTEXITCODE) { throw 'Blender build failed' }
    $modelPython = Join-Path (Split-Path $Blender) '5.2\python\bin\python.exe'
    & $modelPython Tools/SWB6710EV01G/verify_assets.py
    if ($LASTEXITCODE) { throw 'Asset validation failed' }
    & $modelPython Tools/SWB6710EV01G/verify_runtime.py
    if ($LASTEXITCODE) { throw 'Runtime mesh validation failed' }
    # the painted livery lines against every LOD of both exports (paint_lines.py)
    & $Blender --background ModelSource/SWB6710EV01G/SWB6710EV01G.blend --python-exit-code 1 --python Tools/SWB6710EV01G/verify_paint_lines.py
    if ($LASTEXITCODE) { throw 'Painted line validation failed' }
    # the vehicle pack (Packs/CDLC.Bus.SWB6710EV01G): the profile, its mirrors and side sign refreshed from this build
    & $modelPython Tools/VehiclePack/make_pack.py SWB6710EV01G --sync
    if ($LASTEXITCODE) { throw 'Vehicle pack failed' }
} finally { Pop-Location }
