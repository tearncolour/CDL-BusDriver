param(
    [string]$Blender = 'D:\SteamLibrary\steamapps\common\Blender\blender.exe',
    [switch]$Quick,
    [switch]$FromSaved,
    [string]$NativeProbe = '',
    [string]$NativePluginDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Plugins\x86_64',
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$modelRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modelDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $modelDotnet) { $modelDotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $Blender)) { throw "Blender was not found at $Blender" }
Push-Location $modelRoot
try {
    if (-not (Test-Path -LiteralPath 'ModelSource/SWB6109EV12G/model-spec.json')) {
        throw 'The independently researched SWB6109EV12G model-spec.json is required before building.'
    }
    if (-not (Test-Path -LiteralPath 'Tools/SWB6109EV12G/build_model.py')) {
        throw 'The independently authored SWB6109EV12G build_model.py is not ready.'
    }
    & $modelDotnet run --project Tools/SWB6109EV12G/KitExport/KitExport.csproj -c Release -- --spec ModelSource/SWB6109EV12G/model-spec.json --output ModelSource/SWB6109EV12G/kit-cabin.json
    if ($LASTEXITCODE) { throw 'MeshKit export failed' }
    & $modelDotnet run --project Tools/SWB6109EV12G/KitExport/KitExport.csproj -c Release -- --reader-only --output ModelSource/SWB6109EV12G/common-card-reader.json
    if ($LASTEXITCODE) { throw 'Shared card reader export failed' }
    $modelScript = if ($FromSaved) { 'Tools/SWB6109EV12G/reexport_saved.py' } else { 'Tools/SWB6109EV12G/build_model.py' }
    $modelArgs = @('--background','--factory-startup','--python-exit-code','1','--python',$modelScript,'--')
    if ($Quick) { $modelArgs += '--quick' }
    & $Blender @modelArgs
    if ($LASTEXITCODE) { throw 'Blender build failed' }
    if ($FromSaved) {
        $modelRenderArgs = @('--background','ModelSource/SWB6109EV12G/SWB6109EV12G.blend','--python-exit-code','1','--python','Tools/SWB6109EV12G/render_model.py','--')
        if ($Quick) { $modelRenderArgs += '--quick' }
        & $Blender @modelRenderArgs
        if ($LASTEXITCODE) { throw 'Saved-source preview rendering failed' }
    }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_handedness.py
    if ($LASTEXITCODE) { throw 'Saved source handedness validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_source.py -- --blend ModelSource/SWB6109EV12G/SWB6109EV12G.blend
    if ($LASTEXITCODE) { throw 'Saved source geometry validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_mirror_mounts.py -- --blend ModelSource/SWB6109EV12G/SWB6109EV12G.blend --report ModelSource/SWB6109EV12G/mirror-mount-validation.json
    if ($LASTEXITCODE) { throw 'Saved source mirror support contact validation failed' }
    & $Blender --background ModelSource/SWB6109EV12G/SWB6109EV12G.blend --python-exit-code 1 --python Tools/SWB6109EV12G/verify_mirror_joints.py -- --report ModelSource/SWB6109EV12G/mirror-joint-validation.json
    if ($LASTEXITCODE) { throw 'Saved source mirror support joint validation failed' }
    & $Blender --background ModelSource/SWB6109EV12G/SWB6109EV12G.blend --python-exit-code 1 --python Tools/SWB6109EV12G/verify_wheelhouse_seams.py -- --report ModelSource/SWB6109EV12G/wheelhouse-seam-validation.json
    if ($LASTEXITCODE) { throw 'Saved source wheelhouse/side-panel joint validation failed' }
    & $Blender --background ModelSource/SWB6109EV12G/SWB6109EV12G.blend --python-exit-code 1 --python Tools/SWB6109EV12G/verify_sign_enclosures.py -- --report ModelSource/SWB6109EV12G/sign-enclosure-validation.json
    if ($LASTEXITCODE) { throw 'Saved source destination enclosure boundary validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_lod_source.py
    if ($LASTEXITCODE) { throw 'Parametric LOD source geometry validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_interior_clearance.py
    if ($LASTEXITCODE) { throw 'Saved source door/interior clearance validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_handrails.py
    if ($LASTEXITCODE) { throw 'Saved source handrail geometry/material validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_seat_access.py
    if ($LASTEXITCODE) { throw 'Saved source passenger seat usability validation failed' }
    & $Blender --background ModelSource/SWB6109EV12G/SWB6109EV12G.blend --python-exit-code 1 --python Tools/SWB6109EV12G/verify_material_contract.py -- --check-saved --palette ModelSource/SWB6109EV12G/kit-cabin.json --report ModelSource/SWB6109EV12G/material-validation.json
    if ($LASTEXITCODE) { throw 'Saved source/game material contract validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_window_configuration.py -- --blend ModelSource/SWB6109EV12G/SWB6109EV12G.blend --report ModelSource/SWB6109EV12G/window-configuration-validation.json
    if ($LASTEXITCODE) { throw 'Side-window tint and curtain-free cabin validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_exterior_details.py
    if ($LASTEXITCODE) { throw 'Saved source plate/curved-line details validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_saved_wheels.py
    if ($LASTEXITCODE) { throw 'Saved source full-tread wheel validation failed' }
    $modelPython = Join-Path (Split-Path $Blender) '5.2\python\bin\python.exe'
    & $modelPython Tools/SWB6109EV12G/verify_assets.py
    if ($LASTEXITCODE) { throw 'Asset validation failed' }
    & $modelPython Tools/SWB6109EV12G/verify_runtime.py
    if ($LASTEXITCODE) { throw 'Runtime mesh validation failed' }
    & $Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_straps.py -- --binary ModelSource/SWB6109EV12G/sunwin-mesh.bin.gz
    if ($LASTEXITCODE) { throw 'Source/runtime strap validation failed' }
    if (-not $NativeProbe) {
        $modelDefaultProbe = Join-Path $env:USERPROFILE 'Desktop\cs2_research\bus_asset\fbx_probe\bin\Release\net8.0\fbx_probe.exe'
        if (Test-Path -LiteralPath $modelDefaultProbe) { $NativeProbe = $modelDefaultProbe }
    }
    $modelCanRunNative = -not $SkipNative -and $NativeProbe -and
        (Test-Path -LiteralPath $NativeProbe) -and
        (Test-Path -LiteralPath (Join-Path $NativePluginDirectory 'PipelinePlugin.dll'))
    if ($modelCanRunNative) {
        & $modelPython Tools/SWB6109EV12G/verify_native.py --probe $NativeProbe --plugin-dir $NativePluginDirectory
        if ($LASTEXITCODE) { throw 'Native FBX reader validation failed' }
        & $modelPython Tools/SWB6109EV12G/validation_manifest.py --extra-report ModelSource/SWB6109EV12G/exterior-details-validation.json --extra-report ModelSource/SWB6109EV12G/handedness-validation.json --extra-report ModelSource/SWB6109EV12G/interior-clearance-validation.json --extra-report ModelSource/SWB6109EV12G/lod-source-validation.json --extra-report ModelSource/SWB6109EV12G/strap-validation.json --extra-report ModelSource/SWB6109EV12G/handrail-validation.json --extra-report ModelSource/SWB6109EV12G/seat-access-validation.json --extra-report ModelSource/SWB6109EV12G/material-validation.json --extra-report ModelSource/SWB6109EV12G/wheelhouse-seam-validation.json
        if ($LASTEXITCODE) { throw 'Validation report hashes do not match the built assets' }
        Write-Host 'SWB6109_MODEL_VALIDATION_COMPLETE; game packaging and visual review are separate required gates.'
    } else {
        $modelNativeReason = if ($SkipNative) { 'Explicit -SkipNative was supplied.' } else { 'Configure -NativeProbe and -NativePluginDirectory to run the native game reader.' }
        $modelSourceHash = (Get-FileHash -LiteralPath 'ModelSource/SWB6109EV12G/SWB6109EV12G.blend' -Algorithm SHA256).Hash.ToLowerInvariant()
        $modelRuntimeHash = (Get-FileHash -LiteralPath 'ModelSource/SWB6109EV12G/sunwin-mesh.bin.gz' -Algorithm SHA256).Hash.ToLowerInvariant()
        $modelNativeJson = @{ status='NOT_RUN'; reason=$modelNativeReason; source_sha256=$modelSourceHash; runtime_sha256=$modelRuntimeHash } | ConvertTo-Json
        $modelIncompleteJson = @{ status='INCOMPLETE'; reason='Native FBX validation was not run.'; source_sha256=$modelSourceHash; runtime_sha256=$modelRuntimeHash } | ConvertTo-Json
        $modelUtf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText((Join-Path $modelRoot 'ModelSource/SWB6109EV12G/native-validation.json'),$modelNativeJson,$modelUtf8)
        [System.IO.File]::WriteAllText((Join-Path $modelRoot 'ModelSource/SWB6109EV12G/validation-manifest.json'),$modelIncompleteJson,$modelUtf8)
        Write-Host ('SWB6109_NATIVE_NOT_RUN: '+$modelNativeReason)
        Write-Host 'SWB6109_VALIDATION_INCOMPLETE'
    }
    & $modelPython Tools/SWB6109EV12G/spec_compliance.py
    if ($LASTEXITCODE) { throw 'Could not record specification compliance status' }
    # the vehicle pack (Packs/CDLC.Bus.SWB6109EV12G): Tools/SWB6109EV12G/vehicle.json, the runtime mesh, the packed .cok
    & $modelPython Tools/VehiclePack/make_pack.py SWB6109EV12G
    if ($LASTEXITCODE) { throw 'Vehicle pack failed' }
} finally { Pop-Location }
