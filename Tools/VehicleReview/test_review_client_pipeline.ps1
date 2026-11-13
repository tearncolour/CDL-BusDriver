# Offline end-to-end client fixture. The current PowerShell process represents
# the mock server; no Cities2 process is created and no real mod path is accessed.
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('cdlc-review-pipeline-'+[Guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh fixture directory'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$null=[IO.Directory]::CreateDirectory($OutputDirectory)
$deploy=Join-Path $OutputDirectory 'mock-deploy';$data=Join-Path $OutputDirectory 'mock-userdata';$null=[IO.Directory]::CreateDirectory($deploy);$null=[IO.Directory]::CreateDirectory($data)
$model='SWB6109EV12G';$instance=[Guid]::NewGuid().ToString('N')
$process=Microsoft.PowerShell.Management\Get-Process -Id $PID
$source=Join-Path $OutputDirectory 'fixture.blend';$runtime=Join-Path $OutputDirectory 'fixture.gz';$package=Join-Path $deploy 'fixture.cok'
foreach($file in $source,$runtime,$package){[IO.File]::WriteAllText($file,'mock protocol fixture '+[IO.Path]::GetExtension($file))}
Copy-Item -LiteralPath ([psobject].Assembly.Location) -Destination (Join-Path $deploy 'CDLC.BusDriver.dll')
. (Join-Path $PSScriptRoot 'review_helpers.ps1')
$assemblyHash=Get-ReviewHash (Join-Path $deploy 'CDLC.BusDriver.dll');$mvid=Get-ReviewMvid (Join-Path $deploy 'CDLC.BusDriver.dll')
$runtimeHash=Get-ReviewHash $runtime;$packageHash=Get-ReviewHash $package
$log=Join-Path $data 'Player.log';[IO.File]::WriteAllText($log,"Offline mock server; this is not game evidence.`n")
$null=[IO.Directory]::CreateDirectory((Join-Path $data 'Logs'))
$modLog=Join-Path $data 'Logs/CDLC.BusDriver.Mod.log';[IO.File]::WriteAllText($modLog,"Offline mock mod log.`n")
$server=[PowerShell]::Create()
$serverCode={param($deploy,$log,$modLog,$instance,$processId,$start,$assemblyHash,$mvid,$runtimeHash,$packageHash,$package)
    $ErrorActionPreference='Stop'
    function Atomic($path,$value){$temp=$path+'.tmp';[IO.File]::WriteAllText($temp,($value|ConvertTo-Json -Depth 15));if([IO.File]::Exists($path)){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}}
    $status=[ordered]@{protocol=1;instance_id=$instance;pid=$processId;process_start_utc=$start;heartbeat_utc=[DateTime]::UtcNow.ToString('o');ready=$true;busy=$false;suspended=$false;needs_restart=$false;stopping=$false;active_request_id='';assembly_sha256=$assemblyHash;assembly_mvid=$mvid;packages=@([ordered]@{name='SWB6109EV12G';path=$package;sha256=$packageHash;resource_sha256=$runtimeHash;load_verification='ok mock native verification'});capabilities=@('verify','glass','vehicle','vehicle-mirrors','vehicle-review-sample','close','resume')}
    $handled=@();$watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists((Join-Path $deploy 'stop-mock')) -and $watch.Elapsed.TotalSeconds -lt 90){
        $status.heartbeat_utc=[DateTime]::UtcNow.ToString('o');Atomic (Join-Path $deploy 'review-session.json') $status
        $mailbox=Join-Path $deploy 'review-request.json'
        if([IO.File]::Exists($mailbox)){
            $request=[IO.File]::ReadAllText($mailbox)|ConvertFrom-Json
            [IO.File]::Delete($mailbox)
            $status.busy=$true;$status.active_request_id=$request.request_id;Atomic (Join-Path $deploy 'review-session.json') $status
            [IO.File]::AppendAllText($modLog,"CDLC_REVIEW_BEGIN $($request.request_id) $($request.command)`n")
            [IO.File]::AppendAllText($log,"Mock stage $($request.command) actual Player output.`n")
            Start-Sleep -Milliseconds 250
            $folder=Join-Path (Join-Path $deploy 'review-results') $request.request_id;$null=[IO.Directory]::CreateDirectory($folder)
            $result=[ordered]@{protocol=1;instance_id=$instance;request_id=$request.request_id;pid=$processId;command=$request.command;status='PASS';result='ok mock request';started_utc=[DateTime]::UtcNow.ToString('o');finished_utc=[DateTime]::UtcNow.ToString('o');assembly_sha256=$assemblyHash;assembly_mvid=$mvid;packages=$status.packages;output_directory=$folder;cleanup=[ordered]@{passed=$true;errors=@();frames_waited=3;remaining_objects=@()};process_state='RUNNING';exit_code=$null}
            Atomic (Join-Path $folder 'response.json') $result
            Start-Sleep -Milliseconds 250 # response can precede the public idle heartbeat/log flush.
            [IO.File]::AppendAllText($modLog,"CDLC_REVIEW_END $($request.request_id) PASS`n")
            $handled+=$request.command;$status.busy=$false;$status.active_request_id=''
            Atomic (Join-Path $deploy 'handled.json') @{commands=$handled}
        }
        Start-Sleep -Milliseconds 100
    }
}
$null=$server.AddScript($serverCode).AddArgument($deploy).AddArgument($log).AddArgument($modLog).AddArgument($instance).AddArgument($PID).AddArgument($process.StartTime.ToUniversalTime().ToString('o')).AddArgument($assemblyHash).AddArgument($mvid).AddArgument($runtimeHash).AddArgument($packageHash).AddArgument($package)
$async=$server.BeginInvoke()
function Get-Process {
    [CmdletBinding()]param([Parameter(Position=0)][string[]]$Name,[int[]]$Id)
    if($PSBoundParameters.ContainsKey('Name')){
        if($Name.Count -eq 1 -and $Name[0] -eq 'Cities2'){return Microsoft.PowerShell.Management\Get-Process -Id $PID}
        return Microsoft.PowerShell.Management\Get-Process -Name $Name
    }
    return Microsoft.PowerShell.Management\Get-Process -Id $Id
}
function Start-Process { throw 'Fixture forbids all process launches; StartOrConnect must reuse the mock identity' }
try{
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists((Join-Path $deploy 'review-session.json'))){if($watch.Elapsed.TotalSeconds -gt 10){throw 'Mock server failed to initialize'};Start-Sleep -Milliseconds 100}
    $first=Join-Path $OutputDirectory 'first-batch'
    & (Join-Path $PSScriptRoot 'review_session.ps1') -DeployDirectory $deploy -GamePath $process.Path -UserDataDirectory $data -Workspace $OutputDirectory -SourcePath $source -RuntimePath $runtime -PackagePath $package -OutputDirectory $first -StartupTimeoutSeconds 10 -StageTimeoutSeconds 10 | Out-Null
    $second=Join-Path $OutputDirectory 'reconnect'
    & (Join-Path $PSScriptRoot 'review_session.ps1') -DeployDirectory $deploy -GamePath $process.Path -UserDataDirectory $data -Workspace $OutputDirectory -SourcePath $source -RuntimePath $runtime -PackagePath $package -OutputDirectory $second -Stages Verify -StartupTimeoutSeconds 10 -StageTimeoutSeconds 10 | Out-Null
    $a=Read-ReviewJson (Join-Path $first 'client-summary.json');$b=Read-ReviewJson (Join-Path $second 'client-summary.json')
    if($a.status -ne 'STAGES_PASS_PROCESS_RUNNING' -or $b.status -ne 'STAGES_PASS_PROCESS_RUNNING'){throw 'Mock batch/reconnect did not pass'}
    if($a.process.pid -ne $b.process.pid -or $a.instance_id -ne $b.instance_id -or $null -ne $a.process.exit_code -or $null -ne $b.process.exit_code){throw 'Client did not preserve the same running identity'}
    if($a.process.started_by_this_client -or $b.process.started_by_this_client){throw 'Client unexpectedly launched a process'}
    $all=@($a.stages)+@($b.stages)
    if(@($all.request_id|Select-Object -Unique).Count -ne 5){throw 'Request IDs were reused'}
    if(($all.command -join ',') -ne 'verify,glass,vehicle,vehicle-mirrors,verify'){throw 'Stages ran out of order'}
    if([IO.File]::Exists((Join-Path $deploy '.cdlc-asset-tools.lock')) -or [IO.File]::Exists((Join-Path $deploy 'review-request.json'))){throw 'Completed batch left a lock or request'}
    if((Get-ReviewHash (Join-Path $deploy 'CDLC.BusDriver.dll')) -ne $assemblyHash -or (Get-ReviewHash $package) -ne $packageHash){throw 'Client changed the running model or DLL'}
    Write-ReviewJson (Join-Path $OutputDirectory 'report.json') @{status='PASS';same_process=$true;unique_requests=5;first_stages=@($a.stages.command);reconnect_stages=@($b.stages.command);process_exit_observed=$false;scope='Offline PowerShell mock: actual client entry point connected twice to one fake protocol server. No game launched, no real deployment accessed.'}
    Write-Output "Mock persistent pipeline PASS: $OutputDirectory"
}finally{
    [IO.File]::WriteAllText((Join-Path $deploy 'stop-mock'),'stop')
    $null=$server.EndInvoke($async)
    if($server.Streams.Error.Count){$server.Streams.Error|Out-String|Write-Warning}
    $server.Dispose()
}
