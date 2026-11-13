# Offline fixtures: never launches a process, deploys files or touches the real review mailbox.
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'review_helpers.ps1')
if(-not $OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('cdlc-review-client-fixtures-'+[Guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh fixture directory'}
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$cases=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body){try{& $Body;$cases.Add([ordered]@{name=$Name;status='PASS'})}catch{$cases.Add([ordered]@{name=$Name;status='FAIL';error=$_.Exception.ToString()})}}
function Require($Condition,[string]$Message='assertion failed'){if(-not $Condition){throw $Message}}
function Reject([scriptblock]$Body){$rejected=$false;try{& $Body}catch{$rejected=$true};Require $rejected 'Expected rejection did not occur'}
$identity=Get-ReviewProcess $PID
$request=[ordered]@{protocol=1;instance_id=('a'*32);request_id=('b'*32);pid=$PID;process_start_utc=$identity.process_start_utc;command='verify';names=@('SWB6109EV12G');source_sha256=('c'*64);expected_assembly_sha256=('d'*64);expected_assembly_mvid=[Guid]::NewGuid().ToString('D');expected_package_sha256=('e'*64);expected_resource_sha256=('f'*64);resume=$false}
function Response {
    return [pscustomobject]@{protocol=1;instance_id=$request.instance_id;request_id=$request.request_id;pid=$PID;command='verify';status='PASS';assembly_sha256=$request.expected_assembly_sha256;assembly_mvid=$request.expected_assembly_mvid;packages=@([pscustomobject]@{name='SWB6109EV12G';sha256=$request.expected_package_sha256;resource_sha256=$request.expected_resource_sha256});cleanup=[pscustomobject]@{passed=$true;errors=@();frames_waited=3;remaining_objects=0};process_state='RUNNING';exit_code=$null}
}
Check 'atomic-request-complete-json' {
    $path=Join-Path $OutputDirectory 'request.json';Publish-ReviewRequest $path $request
    $actual=Read-ReviewJson $path;Require ($actual.request_id -eq $request.request_id)
    Require (@(Get-ChildItem $OutputDirectory -Filter '*.tmp').Count -eq 0)
}
Check 'pending-mailbox-never-overwritten' {
    $path=Join-Path $OutputDirectory 'request.json';$before=Get-ReviewHash $path
    Reject {Publish-ReviewRequest $path @{request_id='other'}}
    Require ((Get-ReviewHash $path) -eq $before)
}
Check 'heartbeat-sharing-violation-recovers' {
    if(-not ('ReviewReadLockFixture' -as [type])) {
        Add-Type -TypeDefinition @'
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class ReviewReadLockFixture {
    public static Task Hold(string path, int milliseconds) {
        var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return Task.Run(() => { try { Thread.Sleep(milliseconds); } finally { file.Dispose(); } });
    }
}
'@
    }
    $path=Join-Path $OutputDirectory 'locked-heartbeat.json'
    Write-ReviewJson $path @{ready=$true}
    $task=[ReviewReadLockFixture]::Hold($path,250)
    try {Require ((Read-ReviewJson $path).ready -eq $true)} finally {$task.GetAwaiter().GetResult()}
}
Check 'persistent-sharing-violation-still-fails' {
    $path=Join-Path $OutputDirectory 'locked-heartbeat.json'
    $held=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {Reject {Read-ReviewJson $path}} finally {$held.Dispose()}
}
Check 'malformed-heartbeat-still-fails' {
    $path=Join-Path $OutputDirectory 'malformed-heartbeat.json'
    [IO.File]::WriteAllText($path,'{invalid')
    Reject {Read-ReviewJson $path}
}
Check 'absent-heartbeat-is-pending' {Require ($null -eq (Read-ReviewJson (Join-Path $OutputDirectory 'not-published.json')))}
Check 'matching-terminal-response' {Assert-ReviewResponse (Response) $request}
Check 'matching-response-empty-remaining-array' {$r=Response;$r.cleanup.remaining_objects=@();Assert-ReviewResponse $r $request}
foreach($field in 'protocol','instance_id','request_id','pid','command','assembly_sha256','assembly_mvid'){
    $fieldName=$field
    Check ('reject-response-'+$fieldName) { $r=Response;$r.$fieldName=$(if($fieldName -in @('pid','protocol')){-1}else{'different'});Reject {Assert-ReviewResponse $r $request} }
}
Check 'reject-response-resource-drift' {$r=Response;$r.packages[0].resource_sha256='x';Reject {Assert-ReviewResponse $r $request}}
Check 'reject-cleanup-error' {$r=Response;$r.cleanup.errors=@('Dispose failed');Reject {Assert-ReviewResponse $r $request}}
Check 'reject-unfinished-destroy-barrier' {$r=Response;$r.cleanup.frames_waited=0;Reject {Assert-ReviewResponse $r $request}}
Check 'reject-leftover-probe-objects' {$r=Response;$r.cleanup.remaining_objects=1;Reject {Assert-ReviewResponse $r $request}}
Check 'reject-stage-pretending-exit-code' {$r=Response;$r.exit_code=0;Reject {Assert-ReviewResponse $r $request}}
Check 'failed-terminal-can-be-archived' {$r=Response;$r.status='FAIL';$r.cleanup=$null;Assert-ReviewResponse $r $request}
Check 'old-response-is-not-this-request-terminal' {Require (-not(Test-ReviewTerminal (Response) $request.instance_id ('c'*32)))}
Check 'process-start-distinguishes-pid-reuse' {Require (-not(Test-ReviewSameStart '2026-01-01T00:00:00Z' '2026-01-02T00:00:00Z'))}
Check 'equivalent-timezone-process-start' {Require (Test-ReviewSameStart '2026-01-01T08:00:00+08:00' '2026-01-01T00:00:00Z')}
Check 'json-roundtrip-retains-process-start-ticks' {$value=@{start=$identity.process_start_utc}|ConvertTo-Json|ConvertFrom-Json;Require (Test-ReviewSameStart $value.start $identity.process_start_utc)}
$status=[pscustomobject]@{protocol=1;instance_id=$request.instance_id;pid=$PID;process_start_utc=$identity.process_start_utc;heartbeat_utc=[DateTime]::UtcNow.ToString('o');assembly_sha256=$request.expected_assembly_sha256;assembly_mvid=$request.expected_assembly_mvid;busy=$false;stopping=$false}
Check 'live-status-process-identity' {Assert-ReviewStatus $status $identity.path}
Check 'stale-heartbeat-rejected' {$old=$status.heartbeat_utc;try{$status.heartbeat_utc=[DateTime]::UtcNow.AddSeconds(-60).ToString('o');Reject {Assert-ReviewStatus $status $identity.path}}finally{$status.heartbeat_utc=$old}}
Check 'different-instance-rejected' {Reject {Assert-ReviewStatus $status $identity.path 15 ('9'*32)}}
Check 'same-pid-wrong-start-rejected' {$old=$status.process_start_utc;try{$status.process_start_utc=[DateTime]::UtcNow.AddDays(-1).ToString('o');Reject {Assert-ReviewStatus $status $identity.path}}finally{$status.process_start_utc=$old}}
$managed=[psobject].Assembly.Location
Check 'portable-mvid-matches-loaded-module' {Require ((Get-ReviewPortableMvid $managed) -eq [psobject].Assembly.ManifestModule.ModuleVersionId.ToString('D'))}
$fixturePackage=Join-Path $OutputDirectory 'fixture.cok';[IO.File]::WriteAllText($fixturePackage,'immutable package fixture')
$bindings=[pscustomobject]@{needs_restart=$false;assembly_sha256=(Get-ReviewHash $managed);assembly_mvid=(Get-ReviewMvid $managed);packages=@([pscustomobject]@{name='SWB6109EV12G';path=$fixturePackage;sha256=(Get-ReviewHash $fixturePackage);resource_sha256=('f'*64);load_verification='ok fixture'})}
Check 'binding-real-managed-pe-and-package' {$null=Assert-ReviewBindings $bindings 'SWB6109EV12G' $managed $bindings.packages[0].sha256 ('f'*64)}
Check 'binding-reject-running-vs-disk-mvid' {$old=$bindings.assembly_mvid;try{$bindings.assembly_mvid=[Guid]::NewGuid().ToString('D');Reject {Assert-ReviewBindings $bindings 'SWB6109EV12G' $managed $bindings.packages[0].sha256 ('f'*64)}}finally{$bindings.assembly_mvid=$old}}
Check 'binding-reject-package-hot-change' {try{[IO.File]::AppendAllText($fixturePackage,'changed');Reject {Assert-ReviewBindings $bindings 'SWB6109EV12G' $managed $bindings.packages[0].sha256 ('f'*64)}}finally{[IO.File]::WriteAllText($fixturePackage,'immutable package fixture')}}
Check 'binding-reject-failed-boot-native-verify' {try{$bindings.packages[0].load_verification='failed fixture';Reject {Assert-ReviewBindings $bindings 'SWB6109EV12G' $managed $bindings.packages[0].sha256 ('f'*64)}}finally{$bindings.packages[0].load_verification='ok fixture'}}
Check 'binding-reject-needs-restart' {try{$bindings.needs_restart=$true;Reject {Assert-ReviewBindings $bindings 'SWB6109EV12G' $managed $bindings.packages[0].sha256 ('f'*64)}}finally{$bindings.needs_restart=$false}}
Check 'unknown-lock-not-recoverable' {Require (-not(Test-ReviewLockRecovery @{schema='other-tool'} $status $OutputDirectory))}
$dead=[ordered]@{schema='cdlc-review-client-lock-v1';token='fixture';owner_pid=2147483646;owner_start_utc='2000-01-01T00:00:00Z';instance_id=$request.instance_id;game_pid=$PID;game_start_utc=$identity.process_start_utc;active_request_id=$request.request_id}
Check 'live-client-lock-not-recoverable' {$l=$dead|ConvertTo-Json|ConvertFrom-Json;$l.owner_pid=$PID;$l.owner_start_utc=$identity.process_start_utc;Require (-not(Test-ReviewLockRecovery $l $status $OutputDirectory))}
Check 'dead-client-busy-game-lock-retained' {$status.busy=$true;try{Require (-not(Test-ReviewLockRecovery $dead $status $OutputDirectory))}finally{$status.busy=$false}}
Check 'dead-client-idle-without-terminal-lock-retained' {Require (-not(Test-ReviewLockRecovery $dead $status $OutputDirectory))}
Check 'dead-client-idle-matching-terminal-recovery' {Write-ReviewJson (Join-Path (Join-Path $OutputDirectory $request.request_id) 'response.json') (Response);Require (Test-ReviewLockRecovery $dead $status $OutputDirectory)}
Check 'dead-client-game-exited-recovery' {$l=$dead|ConvertTo-Json|ConvertFrom-Json;$l.game_pid=2147483645;Require (Test-ReviewLockRecovery $l $null $OutputDirectory)}
Check 'new-client-cannot-delete-live-shared-lock' {
    $path=Join-Path $OutputDirectory 'shared.lock';$lock=Enter-ReviewLock $path (Join-Path $OutputDirectory 'absent-status.json') $OutputDirectory $OutputDirectory
    try{Reject {Enter-ReviewLock $path (Join-Path $OutputDirectory 'absent-status.json') $OutputDirectory $OutputDirectory};Require ([IO.File]::Exists($path))}finally{Exit-ReviewLock $lock}
    Require (-not [IO.File]::Exists($path))
}
Check 'retained-lock-is-not-removed-by-finally' {
    $path=Join-Path $OutputDirectory 'retained.lock';$lock=Enter-ReviewLock $path (Join-Path $OutputDirectory 'none.json') $OutputDirectory $OutputDirectory
    $lock.Retain=$true;Exit-ReviewLock $lock;Require ([IO.File]::Exists($path))
}
Check 'per-request-log-range-retains-old-errors' {
    $path=Join-Path $OutputDirectory 'range.log'
    [IO.File]::WriteAllText($path,"[ERROR] previous request`nCDLC_REVIEW_BEGIN $($request.request_id)`nwork`nCDLC_REVIEW_END $($request.request_id) PASS`n")
    $gate=Get-ReviewLogGate $path $request.request_id;Require ($gate.status -eq 'PASS' -and $gate.full_snapshot_error_count -eq 1)
}
Check 'cleanup-error-after-END-still-fails' {
    $path=Join-Path $OutputDirectory 'late.log'
    [IO.File]::WriteAllText($path,"CDLC_REVIEW_BEGIN $($request.request_id)`nCDLC_REVIEW_END $($request.request_id) PASS`nUnityEngine.Debug:LogError (object)`n")
    Require ((Get-ReviewLogGate $path $request.request_id).status -eq 'FAIL')
}
Check 'missing-BEGIN-END-cannot-pass' {$path=Join-Path $OutputDirectory 'missing.log';[IO.File]::WriteAllText($path,'old result');Reject {Get-ReviewLogGate $path $request.request_id}}
Check 'native-crash-is-error-even-with-no-unity-error' {$path=Join-Path $OutputDirectory 'crash.log';[IO.File]::WriteAllText($path,"Crash!!!`n");Require ((Get-ReviewLogGate $path).status -eq 'FAIL')}
Check 'open-log-snapshot-complete' {$source=Join-Path $OutputDirectory 'range.log';$target=Join-Path $OutputDirectory 'snapshot.log';Copy-ReviewLog $source $target;Require ((Get-ReviewHash $source) -eq (Get-ReviewHash $target))}
Check 'player-log-prefix-and-boundary' {$before=Join-Path $OutputDirectory 'before.log';$after=Join-Path $OutputDirectory 'after.log';[IO.File]::WriteAllText($before,"old line`nlast baseline line`n");[IO.File]::WriteAllText($after,"old line`nlast baseline line`nnew stage line`n");$boundary=Assert-ReviewLogPrefix $before $after;Require ($boundary.prefix_unchanged -and $boundary.start_line -eq 2)}
Check 'player-log-truncate-rejected' {$before=Join-Path $OutputDirectory 'before.log';$after=Join-Path $OutputDirectory 'short.log';[IO.File]::WriteAllText($after,'old');Reject {Assert-ReviewLogPrefix $before $after}}
Check 'player-log-same-length-rewrite-rejected' {$before=Join-Path $OutputDirectory 'before.log';$after=Join-Path $OutputDirectory 'rewrite.log';[IO.File]::WriteAllText($after,"new line`nlast baseline line`n");Reject {Assert-ReviewLogPrefix $before $after}}
Check 'source-scripts-parse' {foreach($name in 'review_session.ps1','review_helpers.ps1','test_review_client.ps1'){$tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name),[ref]$tokens,[ref]$errors);Require ($errors.Count -eq 0) (($errors|Out-String).Trim())}}
$failures=@($cases|Where-Object{$_.status -eq 'FAIL'})
$report=[ordered]@{status=$(if($failures.Count){'FAIL'}else{'PASS'});checks=$cases.Count;failed=$failures.Count;cases=@($cases.ToArray());scope='Offline pure protocol/file/process-identity fixtures. No Cities2 launch, deployment or real mailbox modification.';powershell=$PSVersionTable.PSVersion.ToString();source_hashes=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1'|ForEach-Object{[ordered]@{file=$_.Name;sha256=(Get-ReviewHash $_.FullName)}})}
Write-ReviewJson (Join-Path $OutputDirectory 'report.json') $report
[pscustomobject]@{status=$report.status;checks=$report.checks;failed=$report.failed;report=(Join-Path $OutputDirectory 'report.json')}
if($failures.Count){$failures|ConvertTo-Json -Depth 5;exit 1}
