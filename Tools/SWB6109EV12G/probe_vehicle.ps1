# Reuse the persistent main-menu review session by default. -ColdStart explicitly selects the historical build/start/quit path.
# Invoke only after the final source/export/native checks and .cok packing have passed.
[CmdletBinding()]
param([switch] $PreflightOnly, [switch] $MaterialDiagnostic, [switch] $RefractionDiagnostic, [switch] $ShaderDiagnostic, [switch] $InspectionDiagnostic, [switch] $ShadowDiagnostic, [switch] $RendererDiagnostic, [switch] $SurfaceDiagnostic, [switch] $ContextDiagnostic, [switch] $ReflectionDiagnostic, [switch] $PipelineDiagnostic, [switch] $TransmissionDiagnostic, [switch] $ReviewSample, [switch] $ReviewExterior, [switch] $ReviewMips, [switch] $MirrorProbe, [switch] $StandaloneAssetPackage, [ValidateRange(0,120)][int] $QuitDelaySeconds = 30, [switch] $ColdStart, [switch] $Resume)

$ErrorActionPreference = 'Stop'

if (-not $ColdStart) {
    $sessionClient = Join-Path $PSScriptRoot '../VehicleReview/review_session.ps1'
    if ($PreflightOnly) { & $sessionClient -Command Status; return }
    if ($StandaloneAssetPackage -or $MaterialDiagnostic -or $RefractionDiagnostic -or $ShaderDiagnostic -or $InspectionDiagnostic -or $ShadowDiagnostic -or $RendererDiagnostic -or $SurfaceDiagnostic -or $ContextDiagnostic -or $ReflectionDiagnostic -or $PipelineDiagnostic -or $TransmissionDiagnostic -or $ReviewExterior -or $ReviewMips) {
        throw 'This specialized isolation/cold-load mode requires explicit -ColdStart. Ordinary vehicle/mirror reviews reuse one process by default.'
    }
    $sessionStage = if ($MirrorProbe) { 'Mirrors' } elseif ($ReviewSample) { 'ReviewSample' } else { 'Vehicle' }
    & $sessionClient -Model SWB6109EV12G -Stages $sessionStage -Resume:$Resume
    return
}

function Get-ProbePlayerLogErrors {
    param([Parameter(Mandatory=$true)][string] $Path)
    # Inspect the complete archived log, including errors emitted during cleanup.
    # Do not suppress Unity dialogs or change logger severity to satisfy this gate.
    $patterns = [ordered]@{
        invalid_light_unit = 'Set Light Unit\s+'
        unity_error_or_exception = 'UnityEngine\.Debug[:.](?:LogError(?:Format)?|LogException|LogAssertion(?:Format)?|Assert)\s*\('
        unity_logger_exception = 'UnityEngine\.Logger[:.]LogException\s*\('
        colossal_error_record = '\[(?:ERROR|FATAL|EXCEPTION)\]|Colossal\.Logging\.UnityLogger[:.](?:Error|Critical)\s*\('
        unhandled_exception = '^\s*(?:Unhandled Exception:|[A-Za-z_][\w.]*Exception\s*:)'
    }
    $lines = [IO.File]::ReadAllLines($Path)
    $findings = @()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $kinds = @($patterns.Keys | Where-Object { $lines[$i] -match $patterns[$_] })
        if ($kinds.Count -eq 0) { continue }
        $context = @()
        for ($j = [Math]::Max(0, $i - 2); $j -le [Math]::Min($lines.Length - 1, $i + 3); $j++) {
            $context += [ordered]@{ line = $j + 1; text = $lines[$j] }
        }
        $findings += [ordered]@{ line = $i + 1; kinds = $kinds; text = $lines[$i]; context = $context }
    }
    return [ordered]@{
        schema = 'cdlc-player-log-error-check-v1'
        status = $(if ($findings.Count -eq 0) { 'PASS' } else { 'FAIL' })
        player_log_sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        total_lines = $lines.Length
        error_record_count = $findings.Count
        patterns = $patterns
        errors = $findings
    }
}

# Since Bus Driver 0.7.0 the bus is a vehicle pack of its own (Mods/CDLC.Bus.SWB6109EV12G), standalone by itself.
if ($StandaloneAssetPackage) { throw 'StandaloneAssetPackage is obsolete: the vehicle pack Mods/CDLC.Bus.SWB6109EV12G is the standalone installation.' }
$standaloneOwned = $false
$isDiagnostic = $MaterialDiagnostic -or $RefractionDiagnostic -or $ShaderDiagnostic -or $InspectionDiagnostic -or $ShadowDiagnostic -or $RendererDiagnostic -or $SurfaceDiagnostic -or $ContextDiagnostic -or $ReflectionDiagnostic -or $PipelineDiagnostic -or $TransmissionDiagnostic -or $ReviewSample -or $ReviewExterior -or $ReviewMips -or $MirrorProbe
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
foreach ($key in 'CSII_LOCALMODSPATH', 'CSII_INSTALLATIONPATH', 'CSII_MANAGEDPATH') {
    $value = [Environment]::GetEnvironmentVariable($key, 'Process')
    if ([string]::IsNullOrWhiteSpace($value)) { $value = [Environment]::GetEnvironmentVariable($key, 'User') }
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Missing environment variable: $key" }
    [Environment]::SetEnvironmentVariable($key, $value, 'Process')
}
$deploy = Join-Path $env:CSII_LOCALMODSPATH 'CDLC.BusDriver'
$game = Join-Path $env:CSII_INSTALLATIONPATH 'Cities2.exe'
$package = Join-Path $workspace 'Asset/SWB6109EV12G.cok'
$resource = Join-Path $workspace 'ModelSource/SWB6109EV12G/sunwin-mesh.bin.gz'
foreach ($required in $game, $package, $resource, (Join-Path $env:CSII_MANAGEDPATH 'Game.dll'), (Join-Path $env:CSII_LOCALMODSPATH 'CDLC/CDLC.dll')) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file missing: $required" }
}
$null = Get-Command dotnet -ErrorAction Stop
if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { throw 'Cities2 is already running. Its loaded files will not be replaced.' }
$expectedPackage = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedResource = (Get-FileHash -LiteralPath $resource -Algorithm SHA256).Hash.ToLowerInvariant()
$sourcePath = Join-Path $workspace 'ModelSource/SWB6109EV12G/SWB6109EV12G.blend'
$expectedSource = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($PreflightOnly) {
    [pscustomobject]@{ stage = 'preflight only: no build, deployment or game launch'; model = 'SWB6109EV12G'; deploy = $deploy; game = $game; package_sha256 = $expectedPackage; resource_sha256 = $expectedResource }
    return
}

# Cooperates with pack_asset.sh and probe_glass.sh across the entire game run.
# CreateNew is atomic: an existing lock is never replaced or treated as stale.
$lockPath = Join-Path $deploy '.cdlc-asset-tools.lock'
$lockToken = [Guid]::NewGuid().ToString('N')
$lockPayload = "vehicle $PID $lockToken"
$null = [IO.Directory]::CreateDirectory($deploy)
try {
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
} catch [IO.IOException] {
    throw "Asset tools are busy or their lock cannot be created: $lockPath. No deployment or request was written."
}
try {
    $lockBytes = [Text.UTF8Encoding]::new($false).GetBytes($lockPayload)
    $lockStream.Write($lockBytes, 0, $lockBytes.Length)
    $lockStream.Dispose()
    $lockStream = $null
    if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { throw 'Cities2 started before the asset lock was acquired. No deployment was performed.' }

$requestPath = Join-Path $deploy 'pack-request.txt'
$resultPath = Join-Path $deploy 'pack-result.txt'
if (Test-Path -LiteralPath $requestPath) { throw "A pending request already exists: $requestPath" }
& dotnet build (Join-Path $workspace 'CDLC.BusDriver.csproj') -c Release -nologo -v q -p:DeployToGame=true
if ($LASTEXITCODE -ne 0) { throw "Build/deployment failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $workspace 'mod.json') -Destination (Join-Path $deploy 'mod.json')
# the vehicle pack (Tools/VehiclePack/make_pack.py) into the local Mods, holding exactly the selected package and runtime mesh
$packSource = Join-Path $workspace 'Packs/CDLC.Bus.SWB6109EV12G'
$packDeploy = Join-Path $env:CSII_LOCALMODSPATH 'CDLC.Bus.SWB6109EV12G'
if (-not (Test-Path -LiteralPath (Join-Path $packSource 'SWB6109EV12G.cdlb.json'))) { throw 'No vehicle pack: run Tools/VehiclePack/make_pack.py SWB6109EV12G' }
if (Test-Path -LiteralPath $packDeploy) { Remove-Item -LiteralPath $packDeploy -Recurse -Force }
Copy-Item -LiteralPath $packSource -Destination $packDeploy -Recurse
if ((Get-FileHash -LiteralPath (Join-Path $packDeploy 'SWB6109EV12G.cok') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedPackage) { throw 'Deployed vehicle pack does not hold the selected package.' }
if ((Get-FileHash -LiteralPath (Join-Path $packDeploy 'SWB6109EV12G.swb1.gz') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedResource) { throw 'Deployed vehicle pack does not hold the selected runtime mesh.' }
$expectedAssembly = (Get-FileHash -LiteralPath (Join-Path $deploy 'CDLC.BusDriver.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $assemblyBytes = [IO.File]::ReadAllBytes((Join-Path $deploy 'CDLC.BusDriver.dll'))
    $expectedModuleId = [Reflection.Assembly]::ReflectionOnlyLoad($assemblyBytes).ManifestModule.ModuleVersionId.ToString('D')
} else {
    # ReflectionOnlyLoad is unsupported in PowerShell Core. Read PE metadata
    # without loading/executing the game assembly or resolving its dependencies.
    $assemblyStream = [IO.File]::OpenRead((Join-Path $deploy 'CDLC.BusDriver.dll'))
    $peReader = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        $expectedModuleId = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('D')
    } finally { $peReader.Dispose(); $assemblyStream.Dispose() }
}
$audit = Join-Path $workspace ('out/vehicle-probe-launch-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $audit
if ($StandaloneAssetPackage) {
    if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { throw 'Cities2 started before independent package placement.' }
    $standalonePackage = Join-Path $env:CSII_LOCALMODSPATH 'CDLC.Buses/SWB6109EV12G.cok'
    if ((Test-Path -LiteralPath $standalonePackage) -or (Test-Path -LiteralPath ($standalonePackage + '.cid'))) {
        throw 'An independent package or CID already exists; leaving it untouched.'
    }
    $deployedPackage = Join-Path $deploy 'Asset/SWB6109EV12G.cok'
    $savedPackage = Join-Path $audit 'deployed-original.cok.bin'
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $standalonePackage)
    Move-Item -LiteralPath $deployedPackage -Destination $savedPackage
    $standaloneOwned = $true
    if (Test-Path -LiteralPath ($deployedPackage + '.cid')) { Move-Item -LiteralPath ($deployedPackage + '.cid') -Destination ($savedPackage + '.cid') }
    Copy-Item -LiteralPath $savedPackage -Destination $standalonePackage
    if ((Get-FileHash -LiteralPath $standalonePackage).Hash.ToLowerInvariant() -ne $expectedPackage) { throw 'Independent package copy failed its hash guard.' }
}
if (Test-Path -LiteralPath $resultPath) { Move-Item -LiteralPath $resultPath -Destination (Join-Path $audit 'previous-pack-result.txt') }
$requestId = [Guid]::NewGuid().ToString('N')
$probeMode = if ($MirrorProbe) { 'vehicle-mirrors' } elseif ($ReviewMips) { 'vehicle-review-mips' } elseif ($ReviewExterior) { 'vehicle-review-exterior' } elseif ($ReviewSample) { 'vehicle-review-sample' } elseif ($TransmissionDiagnostic) { 'vehicle-transmission' } elseif ($PipelineDiagnostic) { 'vehicle-pipeline' } elseif ($ReflectionDiagnostic) { 'vehicle-reflection' } elseif ($ContextDiagnostic) { 'vehicle-context' } elseif ($SurfaceDiagnostic) { 'vehicle-surface' } elseif ($RendererDiagnostic) { 'vehicle-renderer' } elseif ($ShadowDiagnostic) { 'vehicle-shadows' } elseif ($InspectionDiagnostic) { 'vehicle-inspection' } elseif ($ShaderDiagnostic) { 'vehicle-shader' } elseif ($RefractionDiagnostic) { 'vehicle-isolation' } elseif ($MaterialDiagnostic) { 'vehicle-material' } else { 'vehicle' }
$payload = "probe=$probeMode`nnames=SWB6109EV12G`nquit=1`nquit_delay_seconds=$QuitDelaySeconds`nsource_sha256=$expectedSource`nrequest_id=$requestId`n"
[IO.File]::WriteAllText($requestPath, $payload, [Text.UTF8Encoding]::new($false))
$oldSteamApp = $env:SteamAppId
$oldSteamGame = $env:SteamGameId
try {
    $env:SteamAppId = '949230'; $env:SteamGameId = '949230'
    $gameStartedUtc = [DateTime]::UtcNow
    $gameProcess = Start-Process -FilePath $game -WorkingDirectory $env:CSII_INSTALLATIONPATH -WindowStyle Hidden -PassThru
} catch {
    if ((Test-Path -LiteralPath $requestPath) -and ([IO.File]::ReadAllText($requestPath) -eq $payload)) { Remove-Item -LiteralPath $requestPath }
    throw
} finally {
    $env:SteamAppId = $oldSteamApp; $env:SteamGameId = $oldSteamGame
}

$watch = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $resultPath)) {
    Start-Sleep -Seconds 5
    if ($watch.Elapsed.TotalSeconds -gt 1200) { throw 'No vehicle probe result after 1200 seconds. The game was not forcibly terminated.' }
    if ($watch.Elapsed.TotalSeconds -gt 60 -and -not (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath $resultPath)) {
        if ((Test-Path -LiteralPath $requestPath) -and ([IO.File]::ReadAllText($requestPath) -eq $payload)) { Remove-Item -LiteralPath $requestPath }
        throw 'The game exited without producing the vehicle probe result.'
    }
}
$result = [IO.File]::ReadAllText($resultPath).Trim()
[IO.File]::WriteAllText((Join-Path $audit 'pack-result.txt'), $result)
Write-Output $result
for ($attempt = 0; $attempt -lt 60 -and (Get-Process -Name Cities2 -ErrorAction SilentlyContinue); $attempt++) { Start-Sleep -Seconds 2 }
if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { throw 'The game did not exit; loaded files were left untouched.' }
$gameProcess.WaitForExit()
$exitCode = $gameProcess.ExitCode
$playerLog = Join-Path ([Environment]::GetEnvironmentVariable('CSII_USERDATAPATH','User')) 'Player.log'
$crashMarker = $false
$currentPlayerLog = $false
$logCheck = $null
if ((Test-Path -LiteralPath $playerLog) -and (Get-Item -LiteralPath $playerLog).LastWriteTimeUtc -ge $gameStartedUtc) {
    $archivedPlayerLog = Join-Path $audit 'Player.log'
    Copy-Item -LiteralPath $playerLog -Destination $archivedPlayerLog
    $currentPlayerLog = $true
    $crashMarker = [bool](Select-String -LiteralPath $archivedPlayerLog -Pattern '^Crash!!!\s*$' -Quiet)
    $logCheck = Get-ProbePlayerLogErrors -Path $archivedPlayerLog
    $logCheck | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $audit 'player-log-errors.json') -Encoding utf8
}
[ordered]@{ pid=$gameProcess.Id; exit_code=$exitCode; native_crash_marker=$crashMarker; quit_delay_seconds=$QuitDelaySeconds;
    current_player_log_archived=$currentPlayerLog; player_log_error_records=$(if ($null -ne $logCheck) { $logCheck.error_record_count } else { $null });
    player_log_sha256=$(if ($null -ne $logCheck) { $logCheck.player_log_sha256 } else { $null }) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $audit 'process-exit.json') -Encoding utf8
if ($exitCode -ne 0) { throw "Game exited abnormally with code $exitCode; a successful result does not override process failure." }
if (-not $currentPlayerLog) { throw 'A current complete Player.log was not available after game exit; this run cannot pass.' }
if ($crashMarker) { throw 'The current Player.log contains a native crash marker; a successful result does not override it.' }
if ($logCheck.error_record_count -ne 0) { throw "Player.log contains $($logCheck.error_record_count) Unity/error records. Inspect player-log-errors.json and the complete archived Player.log; exit 0 or an ok result does not override them." }
if (-not $result.StartsWith('ok vehicle supplemental probe: ')) { throw 'The vehicle probe reported failure. Inspect the saved report and images.' }
$reportDirectory = $result.Substring($result.LastIndexOf('; ') + 2)
$reportPath = Join-Path $reportDirectory 'report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.status -ne 'automatic checks passed; manual image review required') { throw 'Unexpected report status.' }
if (@($report.asset_load_checks).Count -ne 1 -or -not $report.asset_load_checks[0].result.StartsWith('ok ')) { throw 'Production cold-load Verify did not pass before rendering.' }
$loadedSource = $report.asset_load_checks[0].result.Replace('\','/')
$expectedLocation = '/Mods/CDLC.Bus.SWB6109EV12G/'
if (-not $loadedSource.Contains($expectedLocation)) { throw "Loaded package was not from the expected directory: $expectedLocation" }
if ($report.assembly_sha256 -ne $expectedAssembly -or $report.assembly_mvid -ne $expectedModuleId -or $report.models.Count -ne 1 -or $report.models[0].resource_sha256 -ne $expectedResource -or $report.models[0].package_sha256 -ne $expectedPackage) { throw 'The running assembly, module ID, embedded resource or package does not match the selected files.' }
$expectedShots = if ($MirrorProbe) { 0 } elseif ($ReviewMips) { 3 } elseif ($ReviewExterior) { 4 } elseif ($ReviewSample) { 7 } elseif ($TransmissionDiagnostic) { 7 } elseif ($PipelineDiagnostic) { 7 } elseif ($ReflectionDiagnostic) { 5 } elseif ($ContextDiagnostic) { 4 } elseif ($SurfaceDiagnostic -or $RendererDiagnostic) { 3 } elseif ($ShadowDiagnostic) { 2 } elseif ($InspectionDiagnostic -or $ShaderDiagnostic) { 4 } elseif ($RefractionDiagnostic) { 12 } elseif ($MaterialDiagnostic) { 8 } else { 90 }
$expectedPoses = if ($InspectionDiagnostic) { 39 } elseif ($isDiagnostic) { 1 } else { 39 }
$expectedNative = if ($isDiagnostic) { 0 } else { 3 }
if ($report.screenshots.Count -ne $expectedShots -or $report.pose_checks.Count -ne $expectedPoses -or $report.material_checks.Count -lt 12 -or $report.native_checks.Count -ne $expectedNative) { throw 'The report is missing one or more planned native/runtime captures, pose checks or material checks.' }
if (-not $isDiagnostic -and @($report.glass_buffer_checks).Count -ne 15) { throw 'Final vehicle probe is missing one or more packed glass buffer checks.' }
if (-not $isDiagnostic) {
    if (@($report.sign_checks).Count -ne 3) { throw 'Final vehicle probe is missing production LED checks for all three LODs.' }
    $expectedFont = (Get-FileHash -LiteralPath (Join-Path $deploy 'Fonts/led-font.bin.gz') -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($check in $report.sign_checks) {
        if ($check.font_sha256 -ne $expectedFont -or @($check.screens).Count -ne 3) { throw 'LED font provenance or three-screen check is incomplete.' }
    }
}
$expectedGroups = if ($ReviewMips) { 4 } elseif ($ReviewExterior) { 6 } elseif ($ReviewSample) { 7 } elseif ($TransmissionDiagnostic) { 7 } elseif ($PipelineDiagnostic) { 7 } elseif ($ReflectionDiagnostic) { 5 } elseif ($SurfaceDiagnostic -or $RendererDiagnostic) { 3 } elseif ($ShadowDiagnostic) { 2 } else { 4 }
if ($isDiagnostic -and -not $MirrorProbe -and (@($report.diagnostic_groups).Count -ne $expectedGroups -or -not $report.material_diagnostic)) { throw 'The material comparison is incomplete.' }
if ($RefractionDiagnostic -and -not $report.refraction_diagnostic) { throw 'Refraction isolation mode did not run.' }
if ($ShaderDiagnostic -and -not $report.shader_diagnostic) { throw 'Private shader diagnostic mode did not run.' }
if ($InspectionDiagnostic -and (-not $report.inspection_diagnostic -or @($report.glass_buffer_checks).Count -ne 15)) { throw 'Inspection mode is missing packed glass checks for all three cab LODs.' }
if ($ShadowDiagnostic -and -not $report.shadow_diagnostic) { throw 'Shadow diagnostic mode did not run.' }
if ($RendererDiagnostic -and -not $report.renderer_diagnostic) { throw 'Renderer contract diagnostic mode did not run.' }
if ($SurfaceDiagnostic -and -not $report.surface_diagnostic) { throw 'Surface input diagnostic mode did not run.' }
if ($ContextDiagnostic -and -not $report.context_diagnostic) { throw 'Context diagnostic mode did not run.' }
if ($ReflectionDiagnostic -and -not $report.reflection_diagnostic) { throw 'Reflection diagnostic mode did not run.' }
if ($PipelineDiagnostic -and -not $report.pipeline_diagnostic) { throw 'Pipeline diagnostic mode did not run.' }
if ($ReviewMips -and -not $report.review_mips) { throw 'Mipmap review mode did not run.' }
if ($ReviewExterior -and -not $report.review_exterior) { throw 'Exterior review mode did not run.' }
if ($ReviewSample -and -not $report.review_sample) { throw 'Review sample mode did not run.' }
if ($TransmissionDiagnostic -and -not $report.transmission_diagnostic) { throw 'Transmission diagnostic mode did not run.' }
if ($report.source_sha256 -ne $expectedSource -or (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSource) { throw 'Source provenance changed during the probe.' }
if ($MirrorProbe) {
    if (-not $report.mirror_probe -or -not $report.mirror_checks.automatic_passed -or @($report.mirror_checks.files).Count -ne 19) { throw 'Production mirror probe failed or has missing captures.' }
    foreach ($shot in $report.mirror_checks.files) {
        $path = Join-Path $reportDirectory $shot.filename
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $shot.sha256) { throw "Mirror capture hash mismatch: $path" }
    }
    Write-Output "Production mirror automatic checks passed; 19 mirror images still require manual review."
}
foreach ($shot in $report.screenshots) {
    $image = Get-Item -LiteralPath (Join-Path $reportDirectory $shot.filename)
    if ($image.Length -le 0 -or -not $shot.contrast_check_passed) { throw "Invalid screenshot: $($shot.filename)" }
}
Copy-Item -LiteralPath $reportPath -Destination (Join-Path $audit 'report.json')
$reviewImageCount = if ($MirrorProbe) { 19 } else { $expectedShots }
Write-Output "Automatic checks passed; manual review of all $reviewImageCount images remains required: $reportDirectory"
Write-Output 'This supplements, and does not replace, entering/driving the vehicle in a loaded game.'
} finally {
    try {
    if ($standaloneOwned) {
        if (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) {
            Write-Warning "The game remains open; independent package placement was retained. Restore source: $savedPackage"
        } else {
            if (Test-Path -LiteralPath $standalonePackage) {
                if ((Get-FileHash -LiteralPath $standalonePackage).Hash.ToLowerInvariant() -ne $expectedPackage) { throw 'Independent package changed; refusing to remove another file.' }
                Remove-Item -LiteralPath $standalonePackage
            }
            if (Test-Path -LiteralPath ($standalonePackage + '.cid')) { Remove-Item -LiteralPath ($standalonePackage + '.cid') }
            if (Test-Path -LiteralPath $deployedPackage) { throw 'Deployed package unexpectedly reappeared; retaining original snapshot.' }
            Move-Item -LiteralPath $savedPackage -Destination $deployedPackage
            if (Test-Path -LiteralPath ($savedPackage + '.cid')) { Move-Item -LiteralPath ($savedPackage + '.cid') -Destination ($deployedPackage + '.cid') }
            [ordered]@{ restored_package_sha256=(Get-FileHash -LiteralPath $deployedPackage).Hash.ToLowerInvariant(); independent_copy_removed=$true } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $audit 'standalone-restored.json') -Encoding utf8
        }
    }
    } finally {
    if ($null -ne $lockStream) { $lockStream.Dispose() }
    if ((Test-Path -LiteralPath $lockPath) -and ([IO.File]::ReadAllText($lockPath) -eq $lockPayload)) {
        Remove-Item -LiteralPath $lockPath
    }
    }
}
