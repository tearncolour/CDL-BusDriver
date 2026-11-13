# Shared by review_session.ps1 and its offline protocol fixtures. No deployment code.
Set-StrictMode -Version 2.0

function Get-ReviewValue($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return ,$Object[$Name] }; return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return ,$property.Value }; return $Default
}

function Get-ReviewHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Read-ReviewJson([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return $null }
    # The server replaces its heartbeat atomically. Share Delete so a concurrent
    # reader never causes File.Replace to fail with a sharing violation.
    # A transient Windows sharing/lock violation must not abandon a long
    # capture and strand its client lock. Retry only those two OS errors;
    # malformed JSON and other I/O faults still fail normally.
    $stream=$null
    for($attempt=0;$attempt -lt 20;$attempt++) {
        try {
            $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            break
        } catch {
            $cause=$_.Exception
            while($null -ne $cause.InnerException){$cause=$cause.InnerException}
            if($cause -is [IO.FileNotFoundException]){return $null}
            $code=$cause.HResult -band 0xffff
            if(-not($cause -is [IO.IOException]) -or $code -notin @(32,33) -or $attempt -eq 19){throw}
            Start-Sleep -Milliseconds 50
        }
    }
    $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
    try { return $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
    finally { $reader.Dispose();$stream.Dispose() }
}

function Write-ReviewJson([string]$Path, $Value) {
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    $null = [IO.Directory]::CreateDirectory($parent)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 60), [Text.UTF8Encoding]::new($false))
}

function Publish-ReviewRequest([string]$Path, $Value) {
    # Same-directory rename publishes a complete UTF-8 request. Move never replaces a mailbox.
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $stream = $null
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 15))
        $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true); $stream.Dispose(); $stream = $null
        [IO.File]::Move($temporary, $Path)
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Get-ReviewProcess([int]$ProcessId) {
    if ($ProcessId -le 0) { return $null }
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        return [pscustomobject]@{ pid=$process.Id; process_start_utc=$process.StartTime.ToUniversalTime().ToString('o'); path=$process.Path }
    } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { return $null }
}

function Test-ReviewSameStart($A, $B) {
    try { return ((ConvertTo-ReviewInstant $A).UtcTicks -eq (ConvertTo-ReviewInstant $B).UtcTicks) }
    catch { return $false }
}

function ConvertTo-ReviewInstant($Value) {
    # PowerShell 7.5 ConvertFrom-Json may materialize ISO dates as DateTime;
    # casting that object to string would silently discard sub-second precision.
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime()) }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-ReviewStatus($Status, [string]$GamePath, [int]$MaximumAgeSeconds = 15, [string]$ExpectedInstance = '') {
    if ($null -eq $Status -or (Get-ReviewValue $Status 'protocol') -ne 1) { throw 'Missing/incompatible review-session protocol. Restart once with a deployed protocol-capable build; this client never hot-deploys.' }
    if ([string](Get-ReviewValue $Status 'instance_id') -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid game instance identity' }
    if ($ExpectedInstance -and $Status.instance_id -ne $ExpectedInstance) { throw 'Game instance changed; request must not cross a restart' }
    $actual = Get-ReviewProcess ([int]$Status.pid)
    if ($null -eq $actual -or -not (Test-ReviewSameStart $actual.process_start_utc $Status.process_start_utc)) { throw 'Status PID/start time does not identify the live game' }
    if ($GamePath -and [IO.Path]::GetFullPath($actual.path) -ine [IO.Path]::GetFullPath($GamePath)) { throw 'Status process is not the selected game executable' }
    $age = ([DateTimeOffset]::UtcNow - (ConvertTo-ReviewInstant $Status.heartbeat_utc)).TotalSeconds
    if ($age -gt $MaximumAgeSeconds -or $age -lt -5) { throw "Review status heartbeat is stale or in the future ($age seconds)" }
    if ([string](Get-ReviewValue $Status 'assembly_sha256') -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing boot-frozen assembly hash' }
    $guid = [Guid]::Empty
    if (-not [Guid]::TryParse([string](Get-ReviewValue $Status 'assembly_mvid'), [ref]$guid)) { throw 'Missing running assembly MVID' }
}

function Get-ReviewMvid([string]$Path) {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        return Get-ReviewPortableMvid $Path
    }
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return $metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('D')
    } finally { $pe.Dispose(); $stream.Dispose() }
}

function Get-ReviewPortableMvid([string]$Path) {
    # Read only the ECMA-335 Module row and GUID heap. ReflectionOnlyLoad can
    # cache a previous assembly identity, which is unsuitable for repeated checks.
    $stream=[IO.File]::OpenRead($Path);$reader=[IO.BinaryReader]::new($stream)
    try{
        if($stream.Length -lt 128){throw 'Truncated PE'}
        $stream.Position=0x3c;$pe=[long]$reader.ReadUInt32()
        if($pe -lt 64 -or $pe+24 -gt $stream.Length){throw 'Invalid PE header offset'}
        $stream.Position=$pe;if($reader.ReadUInt32() -ne 0x4550){throw 'Invalid PE signature'}
        $null=$reader.ReadUInt16();$sectionCount=$reader.ReadUInt16()
        $stream.Position=$pe+20;$optionalSize=$reader.ReadUInt16();$optional=$pe+24
        $stream.Position=$optional;$magic=$reader.ReadUInt16()
        $directories=if($magic -eq 0x10b){$optional+96}elseif($magic -eq 0x20b){$optional+112}else{throw 'Unsupported PE optional header'}
        if($sectionCount -lt 1 -or $sectionCount -gt 128 -or $directories+120 -gt $optional+$optionalSize){throw 'Invalid PE section/data directory bounds'}
        $sections=@()
        for($i=0;$i -lt $sectionCount;$i++){
            $stream.Position=$optional+$optionalSize+$i*40+8
            $virtualSize=$reader.ReadUInt32();$virtualAddress=$reader.ReadUInt32();$rawSize=$reader.ReadUInt32();$rawOffset=$reader.ReadUInt32()
            $sections+=@{rva=[long]$virtualAddress;size=[long][Math]::Max($virtualSize,$rawSize);raw=[long]$rawOffset;raw_size=[long]$rawSize}
        }
        $map={param([long]$Rva)
            foreach($section in $sections){if($Rva -ge $section.rva -and $Rva -lt $section.rva+$section.size){
                $offset=$section.raw+$Rva-$section.rva
                if($offset -ge $stream.Length -or $Rva-$section.rva -ge $section.raw_size){throw 'RVA is not backed by file bytes'}
                return $offset
            }}
            throw 'RVA lies outside PE sections'
        }
        $stream.Position=$directories+14*8;$cliRva=$reader.ReadUInt32();$null=$reader.ReadUInt32()
        $cli=& $map $cliRva;$stream.Position=$cli+8;$metadataRva=$reader.ReadUInt32()
        $metadata=& $map $metadataRva;$stream.Position=$metadata
        if($reader.ReadUInt32() -ne 0x424a5342){throw 'Invalid CLR metadata signature'}
        $stream.Position=$metadata+12;$versionLength=$reader.ReadUInt32()
        if($versionLength -gt 1048576){throw 'Invalid CLR metadata version length'}
        $stream.Position=$metadata+16+(($versionLength+3) -band -4)
        $null=$reader.ReadUInt16();$count=$reader.ReadUInt16();if($count -gt 64){throw 'Invalid metadata stream count'}
        $streams=@{}
        for($i=0;$i -lt $count;$i++){
            $offset=$reader.ReadUInt32();$size=$reader.ReadUInt32();$nameBytes=[Collections.Generic.List[byte]]::new();$length=0
            do{$byte=$reader.ReadByte();$length++;if($byte -ne 0){$nameBytes.Add($byte)};if($length -gt 40){throw 'Invalid metadata stream name'}}while($byte -ne 0)
            $stream.Position+=((4-($length%4))%4)
            $name=[Text.Encoding]::ASCII.GetString($nameBytes.ToArray())
            if($metadata+$offset+$size -gt $stream.Length){throw 'Metadata stream outside file'}
            $streams[$name]=@{offset=$metadata+$offset;size=[long]$size}
        }
        $tables=if($streams.ContainsKey('#~')){$streams['#~']}elseif($streams.ContainsKey('#-')){$streams['#-']}else{throw 'Missing metadata tables'}
        if(-not $streams.ContainsKey('#GUID')){throw 'Missing GUID heap'}
        $stream.Position=$tables.offset+6;$heapSizes=$reader.ReadByte();$null=$reader.ReadByte();$valid=$reader.ReadUInt64();$null=$reader.ReadUInt64()
        if(($valid -band 1) -eq 0){throw 'Missing Module metadata table'}
        $modules=0
        for($i=0;$i -lt 64;$i++){if((($valid -shr $i) -band 1) -ne 0){$rows=$reader.ReadUInt32();if($i -eq 0){$modules=$rows}}}
        if($modules -ne 1){throw 'Expected one Module metadata row'}
        $stringWidth=if(($heapSizes -band 1) -ne 0){4}else{2};$guidWidth=if(($heapSizes -band 2) -ne 0){4}else{2}
        $stream.Position+=2+$stringWidth
        $guidIndex=if($guidWidth -eq 4){$reader.ReadUInt32()}else{$reader.ReadUInt16()}
        if($guidIndex -lt 1 -or [long]$guidIndex*16 -gt $streams['#GUID'].size){throw 'Module MVID index outside GUID heap'}
        $stream.Position=$streams['#GUID'].offset+([long]$guidIndex-1)*16
        return ([Guid]::new([byte[]]$reader.ReadBytes(16))).ToString('D')
    }finally{$reader.Dispose();$stream.Dispose()}
}

function Assert-ReviewBindings($Status, [string]$Model, [string]$AssemblyPath, [string]$ExpectedPackageHash, [string]$ExpectedResourceHash) {
    if ([bool](Get-ReviewValue $Status 'needs_restart' $false)) { throw 'The running session requires a restart; no hot replacement is permitted' }
    if ((Get-ReviewHash $AssemblyPath) -ne $Status.assembly_sha256 -or (Get-ReviewMvid $AssemblyPath) -ne $Status.assembly_mvid) { throw 'Deployed DLL differs from the running boot snapshot. Close/rebuild/restart explicitly; do not relabel the running assembly.' }
    $packages = @($Status.packages | Where-Object { $_.name -eq $Model })
    if ($packages.Count -ne 1) { throw "Expected exactly one boot package snapshot for $Model" }
    $package = $packages[0]
    if (-not ([string](Get-ReviewValue $package 'load_verification' '')).StartsWith('ok ', [StringComparison]::Ordinal)) { throw 'Selected model failed the boot-time native load verification' }
    if ($package.sha256 -ne $ExpectedPackageHash -or $package.resource_sha256 -ne $ExpectedResourceHash) { throw 'Running package or embedded resource differs from the selected reviewed files' }
    if ((Get-ReviewHash $package.path) -ne $package.sha256) { throw 'Package on disk changed after the boot snapshot; restart required' }
    return $package
}

function Test-ReviewLockRecovery($Lock, $Status, [string]$ResponseRoot) {
    if ((Get-ReviewValue $Lock 'schema') -ne 'cdlc-review-client-lock-v1') { return $false }
    foreach ($key in 'token','owner_pid','owner_start_utc','instance_id','game_pid','game_start_utc','active_request_id') {
        if ($null -eq (Get-ReviewValue $Lock $key)) { return $false }
    }
    $owner = Get-ReviewProcess ([int]$Lock.owner_pid)
    if ($null -ne $owner -and (Test-ReviewSameStart $owner.process_start_utc $Lock.owner_start_utc)) { return $false }
    $game = Get-ReviewProcess ([int]$Lock.game_pid)
    if ($null -eq $game -or -not (Test-ReviewSameStart $game.process_start_utc $Lock.game_start_utc)) { return $true }
    if ($null -eq $Status -or $Status.instance_id -ne $Lock.instance_id -or $Status.pid -ne $Lock.game_pid -or -not (Test-ReviewSameStart $Status.process_start_utc $Lock.game_start_utc)) { return $false }
    try { Assert-ReviewStatus $Status '' } catch { return $false }
    if ([bool]$Status.busy -or [bool]$Status.stopping) { return $false }
    if (-not $Lock.active_request_id) { return $true }
    $terminal = Read-ReviewJson (Join-Path (Join-Path $ResponseRoot $Lock.active_request_id) 'response.json')
    return ($null -ne $terminal -and $terminal.instance_id -eq $Lock.instance_id -and $terminal.request_id -eq $Lock.active_request_id -and $terminal.status -in @('PASS','FAIL','CANCELLED','REJECTED'))
}

function Enter-ReviewLock([string]$Path, [string]$StatusPath, [string]$ResponseRoot, [string]$EvidenceDirectory) {
    if ([IO.File]::Exists($Path)) {
        $original = [IO.File]::ReadAllText($Path)
        try { $stale = $original | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Shared asset lock belongs to another/unknown tool; leaving it untouched' }
        if (-not (Test-ReviewLockRecovery $stale (Read-ReviewJson $StatusPath) $ResponseRoot)) { throw 'Shared asset tool lock is active or cannot be safely recovered' }
        # Recheck bytes immediately before removing this narrowly identified dead-client lock.
        if ([IO.File]::ReadAllText($Path) -cne $original) { throw 'Lock owner changed during recovery' }
        Write-ReviewJson (Join-Path $EvidenceDirectory 'recovered-client-lock.json') $stale
        [IO.File]::Delete($Path)
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    $self = Get-ReviewProcess $PID
    $data = [ordered]@{schema='cdlc-review-client-lock-v1';token=[Guid]::NewGuid().ToString('N');owner_pid=$PID;owner_start_utc=$self.process_start_utc;created_utc=[DateTime]::UtcNow.ToString('o');instance_id='';game_pid=0;game_start_utc='';active_request_id=''}
    $lock = [pscustomobject]@{Path=$Path;Stream=$stream;Data=$data;Retain=$false}
    Update-ReviewLock $lock
    return $lock
}

function Update-ReviewLock($Lock) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Lock.Data | ConvertTo-Json -Depth 5))
    $Lock.Stream.Position=0; $Lock.Stream.SetLength(0); $Lock.Stream.Write($bytes,0,$bytes.Length); $Lock.Stream.Flush($true)
}

function Exit-ReviewLock($Lock) {
    if ($null -eq $Lock) { return }
    $Lock.Stream.Dispose()
    if ($Lock.Retain) { return }
    if ([IO.File]::Exists($Lock.Path)) {
        $current = Read-ReviewJson $Lock.Path
        if ($null -eq $current -or $current.token -ne $Lock.Data.token) { throw 'Refusing to remove another owner lock' }
        [IO.File]::Delete($Lock.Path)
    }
}

function Copy-ReviewLog([string]$Path, [string]$Destination) {
    # Player.log remains open while the game runs. Snapshot only bytes present at open.
    $input = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $output = [IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $remaining=$input.Length; $buffer=New-Object byte[] 65536
        while($remaining -gt 0){$count=$input.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining));if($count -eq 0){throw 'Player.log was truncated during snapshot'};$output.Write($buffer,0,$count);$remaining-=$count}
        $output.Flush($true)
    } finally {$input.Dispose();$output.Dispose()}
}

function Get-ReviewLogText([string]$Path) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
    try{return $reader.ReadToEnd()}finally{$reader.Dispose();$stream.Dispose()}
}

function Assert-ReviewLogPrefix([string]$Baseline, [string]$Complete) {
    $before=[IO.File]::OpenRead($Baseline);$after=[IO.File]::OpenRead($Complete)
    try{
        if($after.Length -lt $before.Length){throw 'Player.log was truncated/rotated during this request'}
        $a=New-Object byte[] 65536;$b=New-Object byte[] 65536;$left=$before.Length
        while($left -gt 0){
            $count=[int][Math]::Min($left,$a.Length);$na=$before.Read($a,0,$count);$nb=$after.Read($b,0,$count)
            if($na -ne $count -or $nb -ne $count){throw 'Log prefix could not be read completely'}
            for($i=0;$i -lt $count;$i++){if($a[$i] -ne $b[$i]){throw 'Player.log prefix changed during request; evidence cannot be attributed to this session'}}
            $left-=$count
        }
        return [ordered]@{prefix_unchanged=$true;baseline_bytes=$before.Length;complete_bytes=$after.Length;baseline_sha256=(Get-ReviewHash $Baseline);complete_sha256=(Get-ReviewHash $Complete);start_line=[Math]::Max(1,[IO.File]::ReadAllLines($Baseline).Length);scope='Actual Player.log bytes captured before request publication are an exact prefix of the complete after-cleanup snapshot; error range begins at the baseline final line.'}
    }finally{$before.Dispose();$after.Dispose()}
}

function Get-ReviewLogGate([string]$Path, [string]$RequestId = '', [int]$StartLine = 0) {
    $patterns=[ordered]@{
        invalid_light_unit='Set Light Unit\s+'
        unity_error_or_exception='UnityEngine\.Debug[:.](?:LogError(?:Format)?|LogException|LogAssertion(?:Format)?|Assert)\s*\('
        unity_logger_exception='UnityEngine\.Logger[:.]LogException\s*\('
        colossal_error_record='\[(?:ERROR|FATAL|EXCEPTION)\]|Colossal\.Logging\.UnityLogger[:.](?:Error|Critical)\s*\('
        unhandled_exception='^\s*(?:Unhandled Exception:|[A-Za-z_][\w.]*Exception\s*:)'
        native_crash='^Crash!!!\s*$'
    }
    $lines=[IO.File]::ReadAllLines($Path);$begin=-1;$end=-1
    if($RequestId){
        for($i=0;$i -lt $lines.Length;$i++){
            if($lines[$i].Contains('CDLC_REVIEW_BEGIN '+$RequestId)){if($begin -ge 0){throw 'Duplicate BEGIN for one request ID'};$begin=$i}
            if($lines[$i].Contains('CDLC_REVIEW_END '+$RequestId+' ')){$end=$i}
        }
        if($begin -lt 0 -or $end -lt $begin){throw 'Missing request BEGIN/END log markers; stage cannot pass'}
    }
    elseif($StartLine -gt 0){$begin=$StartLine-1;$end=$lines.Length-1;if($begin -ge $lines.Length){throw 'Player log range begins beyond its final line'}}
    $errors=@();$all=@()
    for($i=0;$i -lt $lines.Length;$i++){
        $kinds=@($patterns.Keys|Where-Object{$lines[$i] -match $patterns[$_]})
        if($kinds.Count){$item=[ordered]@{line=$i+1;kinds=$kinds;text=$lines[$i]};$all+=$item;if($begin -lt 0 -or $i -ge $begin){$errors+=$item}}
    }
    return [ordered]@{status=$(if($errors.Count){'FAIL'}else{'PASS'});request_id=$RequestId;log_sha256=(Get-ReviewHash $Path);request_begin_line=$begin+1;request_end_line=$end+1;total_lines=$lines.Length;stage_error_count=$errors.Count;full_snapshot_error_count=$all.Count;stage_errors=$errors;full_snapshot_errors=$all;scope='Unity/Colossal/managed/native-crash records; not a claim that the log contains no other errors. Range is the real request marker or separately verified pre-request byte-prefix boundary through snapshot end after cleanup.'}
}

function Assert-ReviewResponse($Response, $Request) {
    foreach($field in 'protocol','instance_id','request_id','command'){
        if((Get-ReviewValue $Response $field) -cne (Get-ReviewValue $Request $field)){throw "Response identity mismatch: $field"}
    }
    if([int]$Response.pid -ne [int]$Request.pid){throw 'Response belongs to another process'}
    if($Response.assembly_sha256 -ne $Request.expected_assembly_sha256 -or $Response.assembly_mvid -ne $Request.expected_assembly_mvid){throw 'Response assembly differs from request'}
    $package=@($Response.packages|Where-Object{$_.name -eq $Request.names[0]})
    if($package.Count -ne 1 -or $package[0].sha256 -ne $Request.expected_package_sha256 -or $package[0].resource_sha256 -ne $Request.expected_resource_sha256){throw 'Response package/resource binding mismatch'}
    if($Response.status -notin @('PASS','FAIL','CANCELLED','REJECTED')){throw 'Response is not terminal'}
    if($null -ne $Response.exit_code){throw 'A stage response cannot attest the eventual process exit code'}
    if($Response.status -eq 'PASS'){
        $cleanup=Get-ReviewValue $Response 'cleanup'
        if($null -eq $cleanup -or -not $cleanup.passed -or @($cleanup.errors).Count -ne 0 -or [int]$cleanup.frames_waited -lt 3){throw 'Successful stage lacks a completed three-frame cleanup barrier'}
        $remaining=Get-ReviewValue $cleanup 'remaining_objects'
        if($null -eq $remaining -or (($remaining -is [ValueType]) -and [int]$remaining -ne 0) -or (($remaining -isnot [ValueType]) -and @($remaining).Count -ne 0)){throw 'Successful stage left probe objects or omitted the count'}
    }
}

function Test-ReviewTerminal($Response, [string]$InstanceId, [string]$RequestId) {
    return ($null -ne $Response -and (Get-ReviewValue $Response 'protocol') -eq 1 -and
        (Get-ReviewValue $Response 'instance_id') -eq $InstanceId -and
        (Get-ReviewValue $Response 'request_id') -eq $RequestId -and
        (Get-ReviewValue $Response 'status') -in @('PASS','FAIL','CANCELLED','REJECTED'))
}
