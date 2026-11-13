#requires -Version 5.1
<#
Reuse one protocol-capable Cities2 process for serial vehicle checks. Nothing is
built or deployed. Run leaves the process open; Close is an explicit request.

  ./review_session.ps1                       # Start once or reuse; run all four stages, leave open.
  ./review_session.ps1 -Stages Glass,Mirrors  # Same PID and loaded resources; new unique requests.
  ./review_session.ps1 -Command Status       # Read-only identity/heartbeat.
  ./review_session.ps1 -Resume -Stages Verify # Explicitly resume after returning to the menu.
  ./review_session.ps1 -Command Close        # Explicit graceful exit; never Stop-Process.

Cold-start validation: explicitly Close, deploy offline if needed, then Run.
The old package importer remains separate. This client never moves a package
between mod/standalone directories while a game is running.
#>
[CmdletBinding()]
param(
    [ValidateSet('Run','Status','Close')][string]$Command='Run',
    [ValidateSet('Verify','Glass','Vehicle','Mirrors','ReviewSample','Assembly')][string[]]$Stages=@('Verify','Glass','Vehicle','Mirrors'),
    [ValidatePattern('^[A-Za-z0-9_]+$')][string]$Model='SWB6109EV12G',
    [switch]$Resume,
    [switch]$DeployLatest,
    [string]$Workspace,
    [string]$DeployDirectory,
    [string]$GamePath,
    [string]$UserDataDirectory,
    [string]$ModLogPath,
    [string]$OutputDirectory,
    [string]$SourcePath,
    [string]$RuntimePath,
    [string]$PackagePath,
    [ValidateRange(5,3600)][int]$StartupTimeoutSeconds=600,
    [ValidateRange(5,7200)][int]$StageTimeoutSeconds=1200,
    [ValidateRange(5,600)][int]$ExitTimeoutSeconds=120
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'review_helpers.ps1')
if($DeployLatest){throw '-DeployLatest is not implemented: build/deploy separately only with the game stopped. Run never replaces a running DLL/package.'}
if(-not $Workspace){$Workspace=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path}
function Get-ReviewEnvironment([string]$Name){
    $value=[Environment]::GetEnvironmentVariable($Name,'Process')
    if(-not $value){$value=[Environment]::GetEnvironmentVariable($Name,'User')}
    if(-not $value){throw "Missing environment variable: $Name"};return $value
}
if(-not $DeployDirectory){$DeployDirectory=Join-Path (Get-ReviewEnvironment 'CSII_LOCALMODSPATH') 'CDLC.BusDriver'}
if(-not $GamePath){$GamePath=Join-Path (Get-ReviewEnvironment 'CSII_INSTALLATIONPATH') 'Cities2.exe'}
if(-not $UserDataDirectory){$UserDataDirectory=Get-ReviewEnvironment 'CSII_USERDATAPATH'}
$statusPath=Join-Path $DeployDirectory 'review-session.json'
$requestPath=Join-Path $DeployDirectory 'review-request.json'
$responseRoot=Join-Path $DeployDirectory 'review-results'
$assemblyPath=Join-Path $DeployDirectory 'CDLC.BusDriver.dll'
$logPath=Join-Path $UserDataDirectory 'Player.log'
if(-not $ModLogPath){$ModLogPath=Join-Path $UserDataDirectory 'Logs/CDLC.BusDriver.Mod.log'}
if($Command -eq 'Status'){
    $status=Read-ReviewJson $statusPath
    if($null -eq $status){[pscustomobject]@{status='NO_PROTOCOL_STATUS';game_processes=@(Get-Process Cities2 -ErrorAction SilentlyContinue|ForEach-Object{Get-ReviewProcess $_.Id});deployment_performed=$false};return}
    Assert-ReviewStatus $status $GamePath
    $status;return
}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $Workspace ('out/vehicle-review-session/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
if(Test-Path -LiteralPath $OutputDirectory){throw 'OutputDirectory already exists; a new invocation must preserve prior evidence'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
foreach($file in @($GamePath)+$(if($Command -eq 'Run'){@($assemblyPath)}else{@()})){if(-not [IO.File]::Exists($file)){throw "Required file missing: $file"}}
if(-not $SourcePath){$SourcePath=Join-Path $Workspace ('ModelSource/'+$Model+'/'+$Model+'.blend')}
if(-not $RuntimePath){$RuntimePath=Join-Path $Workspace ('ModelSource/'+$Model+'/sunwin-mesh.bin.gz')}
if(-not $PackagePath){$PackagePath=Join-Path $Workspace ('Asset/'+$Model+'.cok')}
$sourceHash=if([IO.File]::Exists($SourcePath)){Get-ReviewHash $SourcePath}else{''}
$runtimeHash=if([IO.File]::Exists($RuntimePath)){Get-ReviewHash $RuntimePath}else{''}
$packageHash=if([IO.File]::Exists($PackagePath)){Get-ReviewHash $PackagePath}else{''}
if($Command -eq 'Run' -and (-not $sourceHash -or -not $runtimeHash -or -not $packageHash)){throw 'Run requires the reviewed source, runtime and package files (override their paths explicitly if needed)'}
$summary=[ordered]@{protocol=1;client_started_utc=[DateTime]::UtcNow.ToString('o');command=$Command;model=$Model;status='IN_PROGRESS';source_sha256=$sourceHash;runtime_sha256=$runtimeHash;package_sha256=$packageHash;deployment_performed=$false;stages=@();process=[ordered]@{status='UNKNOWN';exit_code=$null};scope='Persistent main-menu inspection. Stage results do not assert an unobserved eventual process exit, cold-load after each stage, or road driving.'}
$lock=$null;$processHandle=$null;$instance='';$activeRequest=$null;$completed=$false
try{
    $lock=Enter-ReviewLock (Join-Path $DeployDirectory '.cdlc-asset-tools.lock') $statusPath $responseRoot $OutputDirectory
    # A request left behind by a game that has since exited can never be claimed: archive it as evidence instead of refusing.
    if([IO.File]::Exists($requestPath)){
        $orphan=$null;try{$orphan=Read-ReviewJson $requestPath}catch{}
        $owner=if($null -ne $orphan){Get-ReviewProcess ([int](Get-ReviewValue $orphan 'pid' 0))}else{$null}
        $alive=$null -ne $owner -and (Test-ReviewSameStart $owner.process_start_utc (Get-ReviewValue $orphan 'process_start_utc' ''))
        if($null -ne $orphan -and -not $alive){
            Move-Item -LiteralPath $requestPath -Destination (Join-Path $OutputDirectory 'orphaned-review-request.json')
            $summary.orphaned_request_archived=$true
        }
    }
    foreach($pending in (Join-Path $DeployDirectory 'pack-request.txt'),$requestPath){if([IO.File]::Exists($pending)){throw "Another request is pending; leaving it untouched: $pending"}}
    # The mod only runs its review protocol (heartbeat, pack hashing at the menu) while this marker exists; removed after Close.
    $markerPath=Join-Path $DeployDirectory 'review-enabled'
    if(-not [IO.File]::Exists($markerPath)){[IO.File]::WriteAllText($markerPath,'Written by Tools/VehicleReview/review_session.ps1; removed by its Close. Enables the BusDriver review protocol.');$summary.review_marker_created=$true}
    $games=@(Get-Process Cities2 -ErrorAction SilentlyContinue)
    if($games.Count -gt 1){throw 'Multiple Cities2 processes exist; refusing to guess an owner'}
    $startedHere=$false
    if($games.Count -eq 0){
        if($Command -eq 'Close'){
            if([IO.File]::Exists($markerPath)){[IO.File]::Delete($markerPath);$summary.review_marker_removed=$true}
            $summary.status='NOT_RUNNING';$summary.process.status='NOT_RUNNING';$completed=$true;return
        }
        $oldApp=$env:SteamAppId;$oldGame=$env:SteamGameId
        try{
            $env:SteamAppId='949230';$env:SteamGameId='949230'
            $processHandle=Start-Process -FilePath $GamePath -WorkingDirectory ([IO.Path]::GetDirectoryName($GamePath)) -WindowStyle Hidden -PassThru
        }finally{$env:SteamAppId=$oldApp;$env:SteamGameId=$oldGame}
        $startedHere=$true
    }else{$processHandle=$games[0]}
    $null=$processHandle.Handle # Retain a real process handle so Close can report its exit code.
    $processIdentity=Get-ReviewProcess $processHandle.Id
    if([IO.Path]::GetFullPath($processIdentity.path) -ine [IO.Path]::GetFullPath($GamePath)){throw 'Existing Cities2 is not the selected executable'}
    $summary.process=[ordered]@{status='RUNNING';exit_code=$null;pid=$processIdentity.pid;process_start_utc=$processIdentity.process_start_utc;started_by_this_client=$startedHere}
    $lock.Data.game_pid=$processIdentity.pid;$lock.Data.game_start_utc=$processIdentity.process_start_utc;Update-ReviewLock $lock
    Write-Host "Waiting for review protocol on PID $($processIdentity.pid); no deployment was performed."
    $watch=[Diagnostics.Stopwatch]::StartNew();$lastNotice=-30
    while($true){
        if($processHandle.HasExited){throw 'Game exited before the review session became available'}
        $status=Read-ReviewJson $statusPath
        $matching=$null -ne $status -and [int]$status.pid -eq $processIdentity.pid -and (Test-ReviewSameStart $status.process_start_utc $processIdentity.process_start_utc)
        if($matching){
            Assert-ReviewStatus $status $GamePath
            if([bool]$status.needs_restart -and $Command -ne 'Close'){throw 'Running review service requires restart; no deployment was performed'}
            if([bool]$status.suspended -and -not $Resume){throw 'User session is suspended; return to the main menu and explicitly request -Resume. The game remains open.'}
            if([bool]$status.ready -and -not [bool]$status.busy -and -not [bool]$status.stopping){break}
        }
        if($watch.Elapsed.TotalSeconds -gt $StartupTimeoutSeconds){throw 'No ready matching review protocol before timeout. Existing game is left running; install/restart once explicitly if its DLL predates the protocol.'}
        if($watch.Elapsed.TotalSeconds-$lastNotice -ge 30){Write-Host 'Game initialization / review handshake still pending.';$lastNotice=$watch.Elapsed.TotalSeconds}
        Start-Sleep -Milliseconds 500
    }
    $instance=$status.instance_id;$lock.Data.instance_id=$instance;Update-ReviewLock $lock
    if($Command -eq 'Close'){
        if([bool]$status.suspended -and -not $Resume){throw 'Refusing Close while user ownership is suspended; explicit -Resume is required'}
        $selected=@($status.packages|Where-Object{$_.name -eq $Model})
        if($selected.Count -ne 1){throw 'Close requires the selected running model snapshot'}
        $packageHash=$selected[0].sha256;$runtimeHash=$selected[0].resource_sha256
    }
    if($Command -ne 'Close'){$package=Assert-ReviewBindings $status $Model $assemblyPath $packageHash $runtimeHash}
    $summary.assembly_sha256=$status.assembly_sha256;$summary.assembly_mvid=$status.assembly_mvid;$summary.instance_id=$instance
    $summary.package_sha256=$packageHash;$summary.runtime_sha256=$runtimeHash
    Write-ReviewJson (Join-Path $OutputDirectory 'session-before.json') $status
    if($Command -ne 'Close'){
        Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $OutputDirectory 'tested-assembly.dll.bin')
        if((Get-ReviewHash (Join-Path $OutputDirectory 'tested-assembly.dll.bin')) -ne $status.assembly_sha256){throw 'Assembly changed while freezing client evidence'}
    }
    $commands=if($Command -eq 'Close'){@('close')}else{@($Stages|ForEach-Object{switch($_){'Verify'{'verify'}'Glass'{'glass'}'Vehicle'{'vehicle'}'Mirrors'{'vehicle-mirrors'}'ReviewSample'{'vehicle-review-sample'}'Assembly'{'assembly'}}})}
    $index=0
    foreach($stage in $commands){
        $status=Read-ReviewJson $statusPath;Assert-ReviewStatus $status $GamePath 15 $instance
        if($Command -ne 'Close'){$null=Assert-ReviewBindings $status $Model $assemblyPath $packageHash $runtimeHash}
        if([bool]$status.busy -or [bool]$status.stopping){throw 'Review service is busy/stopping; no request will overwrite it'}
        if([bool]$status.suspended -and -not($Resume -and $index -eq 0)){throw 'User took over the session; no subsequent stage will run'}
        if($stage -notin @($status.capabilities)){throw "Service lacks requested capability: $stage"}
        if($Command -eq 'Run' -and (Get-ReviewHash $SourcePath) -ne $sourceHash){throw 'Source changed during review batch'}
        $requestId=[Guid]::NewGuid().ToString('N');$index++
        $stageDirectory=Join-Path $OutputDirectory ('{0:D2}-{1}-{2}' -f $index,$stage,$requestId.Substring(0,8))
        $null=[IO.Directory]::CreateDirectory($stageDirectory)
        $request=[ordered]@{protocol=1;instance_id=$instance;request_id=$requestId;pid=$status.pid;process_start_utc=$status.process_start_utc;command=$stage;names=@($Model);source_sha256=$sourceHash;expected_assembly_sha256=$status.assembly_sha256;expected_assembly_mvid=$status.assembly_mvid;expected_package_sha256=$packageHash;expected_resource_sha256=$runtimeHash;resume=[bool]($Resume -and $index -eq 1)}
        $responsePath=Join-Path (Join-Path $responseRoot $requestId) 'response.json'
        if([IO.File]::Exists($responsePath)){throw 'Response ID collision; no existing evidence is overwritten'}
        Write-ReviewJson (Join-Path $stageDirectory 'request.json') $request
        Copy-ReviewLog $logPath (Join-Path $stageDirectory 'Player-before.log')
        $lock.Data.active_request_id=$requestId;Update-ReviewLock $lock
        $activeRequest=$request
        Publish-ReviewRequest $requestPath $request
        Write-Host "Running $stage ($requestId) in existing PID $($status.pid)."
        $watch.Restart();$lastNotice=-30;$timedOut=$false;$response=$null
        while($null -eq $response){
            $response=Read-ReviewJson $responsePath
            if($null -ne $response){break}
            if($processHandle.HasExited){throw 'Game exited before this request produced a terminal response'}
            $state=Read-ReviewJson $statusPath
            Assert-ReviewStatus $state $GamePath 15 $instance
            if($watch.Elapsed.TotalSeconds -gt $StageTimeoutSeconds){
                if(-not $timedOut){$timedOut=$true;Write-Warning 'Stage timeout; no new request or forced exit. Retaining the lock while the active request drains.'}
                if(-not [bool]$state.busy -and $state.active_request_id -ne $requestId -and -not [IO.File]::Exists($requestPath)){throw 'Timed-out request is idle without a terminal response; evidence retained'}
            }
            if($watch.Elapsed.TotalSeconds-$lastNotice -ge 30){Write-Host "$stage pending; elapsed $([int]$watch.Elapsed.TotalSeconds)s, busy=$($state.busy).";$lastNotice=$watch.Elapsed.TotalSeconds}
            Start-Sleep -Milliseconds 500
        }
        Write-ReviewJson (Join-Path $stageDirectory 'response.json') $response
        Assert-ReviewResponse $response $request
        # response is atomic, but the public heartbeat can lag completion by 1 s.
        # Do not race the next stage against the just-finished busy flag or log queue.
        $settle=[Diagnostics.Stopwatch]::StartNew()
        while($true){
            $state=Read-ReviewJson $statusPath
            $logText=if([IO.File]::Exists($ModLogPath)){Get-ReviewLogText $ModLogPath}else{''}
            $hasEnd=$logText.Contains('CDLC_REVIEW_END '+$requestId+' ')
            $idle=$null -ne $state -and $state.instance_id -eq $instance -and -not [bool]$state.busy
            if($hasEnd -and ($idle -or $stage -eq 'close' -or $processHandle.HasExited)){break}
            if($settle.Elapsed.TotalSeconds -gt 15){throw 'Terminal response was not followed by an idle state and complete END log marker'}
            Start-Sleep -Milliseconds 250
        }
        $output=[string](Get-ReviewValue $response 'output_directory' '')
        if($output){
            $fullOutput=[IO.Path]::GetFullPath($output);$root=[IO.Path]::GetFullPath($DeployDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
            if(-not $fullOutput.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Directory]::Exists($fullOutput)){throw 'Response output directory lies outside this deployed review tool'}
            Copy-Item -LiteralPath $fullOutput -Destination (Join-Path $stageDirectory 'capture') -Recurse
            $manifest=@(Get-ChildItem -LiteralPath (Join-Path $stageDirectory 'capture') -File -Recurse|ForEach-Object{[ordered]@{path=$_.FullName;sha256=(Get-ReviewHash $_.FullName);bytes=$_.Length}})
            Write-ReviewJson (Join-Path $stageDirectory 'capture-manifest.json') $manifest
        }
        # The mod logger owns BEGIN/END; never invent these markers in Player.log.
        # Preserve both complete logs and prove the Player baseline is byte-identical.
        Copy-ReviewLog $ModLogPath (Join-Path $stageDirectory 'CDLC.BusDriver.Mod.log')
        $modGate=Get-ReviewLogGate (Join-Path $stageDirectory 'CDLC.BusDriver.Mod.log') $requestId
        Write-ReviewJson (Join-Path $stageDirectory 'mod-log-boundaries.json') $modGate
        Copy-ReviewLog $logPath (Join-Path $stageDirectory 'Player.log')
        $boundary=Assert-ReviewLogPrefix (Join-Path $stageDirectory 'Player-before.log') (Join-Path $stageDirectory 'Player.log')
        Write-ReviewJson (Join-Path $stageDirectory 'player-log-boundaries.json') $boundary
        $gate=Get-ReviewLogGate (Join-Path $stageDirectory 'Player.log') '' $boundary.start_line
        Write-ReviewJson (Join-Path $stageDirectory 'player-log-errors.json') $gate
        $summary.full_session_log_gate=$(if($gate.full_snapshot_error_count -eq 0){'PASS_SO_FAR'}else{'ERRORS_RETAINED'})
        $summary.full_session_log_error_count=$gate.full_snapshot_error_count
        $summary.stages+= [ordered]@{command=$stage;request_id=$requestId;status=$response.status;response_path=(Join-Path $stageDirectory 'response.json');response_sha256=(Get-ReviewHash (Join-Path $stageDirectory 'response.json'));log_gate=$gate.status;mod_log_gate=$modGate.status;process_state=$response.process_state;exit_code=$null}
        $activeRequest=$null;$lock.Data.active_request_id='';Update-ReviewLock $lock
        if($response.status -ne 'PASS'){throw "Request returned $($response.status): $($response.result)"}
        if($gate.status -ne 'PASS'){throw 'Request produced Unity/Colossal/managed/native-crash errors; inspect the preserved full log'}
        if($modGate.status -ne 'PASS'){throw 'Request produced errors in the actual mod log; inspect its preserved markers and full snapshot'}
        if($timedOut){throw 'Request eventually completed, but exceeded the requested client timeout; no subsequent stages were sent'}
        if($stage -eq 'close'){
            if($response.process_state -ne 'EXIT_REQUESTED'){throw 'Close response did not request process exit'}
            $watch.Restart()
            while(-not $processHandle.HasExited){if($watch.Elapsed.TotalSeconds -gt $ExitTimeoutSeconds){throw 'Explicit Close did not exit in time; user/game process was not forcibly terminated'};Start-Sleep -Milliseconds 500}
            $processHandle.WaitForExit();$exitCode=$processHandle.ExitCode
            Copy-ReviewLog $logPath (Join-Path $OutputDirectory 'Player-final.log')
            $finalGate=Get-ReviewLogGate (Join-Path $OutputDirectory 'Player-final.log')
            Write-ReviewJson (Join-Path $OutputDirectory 'player-final-errors.json') $finalGate
            $summary.process.status='EXITED';$summary.process.exit_code=$exitCode;$summary.process.final_log_gate=$finalGate.status
            # The player's own next launch stays free of the protocol.
            if([IO.File]::Exists($markerPath)){[IO.File]::Delete($markerPath);$summary.review_marker_removed=$true}
            if($exitCode -ne 0 -or $finalGate.status -ne 'PASS'){throw 'Stage result does not override abnormal exit or full-session errors'}
        }elseif($response.process_state -ne 'RUNNING'){throw 'Ordinary review request unexpectedly requested exit'}
    }
    if($Command -eq 'Run'){
        if($processHandle.HasExited){throw 'Game unexpectedly exited at the end of a keep-running batch'}
        $status=Read-ReviewJson $statusPath;Assert-ReviewStatus $status $GamePath 15 $instance
        $null=Assert-ReviewBindings $status $Model $assemblyPath $packageHash $runtimeHash
        Write-ReviewJson (Join-Path $OutputDirectory 'session-after.json') $status
        $summary.process.status='RUNNING';$summary.process.exit_code=$null
        $summary.status='STAGES_PASS_PROCESS_RUNNING'
        Write-Host 'Selected stages completed; game remains running for reuse. Manual image review remains separate.'
    }else{$summary.status='CLOSE_PASS_PROCESS_EXITED'}
    $completed=$true
}catch{
    $summary.status='FAIL';$summary.error=$_.Exception.Message
    if($null -ne $processHandle -and $processHandle.HasExited){$processHandle.WaitForExit();$summary.process.status='EXITED';$summary.process.exit_code=$processHandle.ExitCode}
    Write-Error $_ -ErrorAction Continue
}finally{
    if($null -ne $lock){
        if($null -ne $activeRequest){
            # Never free a busy/unknown game's lock on timeout, interruption or malformed response.
            $lock.Retain=$true
            try{
                $state=Read-ReviewJson $statusPath
                $terminal=Read-ReviewJson (Join-Path (Join-Path $responseRoot $activeRequest.request_id) 'response.json')
                $knownTerminal=Test-ReviewTerminal $terminal $activeRequest.instance_id $activeRequest.request_id
                $game=Get-ReviewProcess ([int]$activeRequest.pid)
                $sameGame=$null -ne $game -and (Test-ReviewSameStart $game.process_start_utc $activeRequest.process_start_utc)
                $lock.Retain=$sameGame -and (-not $knownTerminal -or $null -eq $state -or [bool]$state.busy)
            }catch{$summary.lock_cleanup_note='Unknown/malformed terminal state; own lock retained for safe recovery: '+$_.Exception.Message}
        }
        $summary.lock_retained=$lock.Retain
        Exit-ReviewLock $lock
    }
    $summary.client_finished_utc=[DateTime]::UtcNow.ToString('o')
    Write-ReviewJson (Join-Path $OutputDirectory 'client-summary.json') $summary
    if($null -ne $processHandle){$processHandle.Dispose()}
}
if(-not $completed){exit 1}
$summary|ConvertTo-Json -Depth 12
