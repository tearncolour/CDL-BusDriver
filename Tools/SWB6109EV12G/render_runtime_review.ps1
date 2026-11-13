param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$MirrorMountsOnly
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $destination 'render-manifest.json')) {
    throw 'This directory already holds a completed review. Choose a new output directory to retain the previous evidence.'
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$snapshot = Join-Path $destination 'assembly'
New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
$compiled = Join-Path $repo 'Tests/CDLC.BusDriver.Tests/bin/Release/net10.0'
foreach ($name in @('CDLC.BusDriver.Tests.dll', 'CDLC.BusDriver.Tests.deps.json', 'CDLC.BusDriver.Tests.runtimeconfig.json', 'Unity.Mathematics.dll')) {
    Copy-Item -LiteralPath (Join-Path $compiled $name) -Destination (Join-Path $snapshot $name) -Force
}
[Reflection.Assembly]::LoadFrom((Join-Path $snapshot 'Unity.Mathematics.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $snapshot 'CDLC.BusDriver.Tests.dll'))
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$specType = $assembly.GetType('CDLC.BusDriver.Interior.Models.BusInteriorSpec', $true)
$busType = $assembly.GetType('CDLC.BusDriver.Interior.Models.BusInterior', $true)
$buildType = $assembly.GetType('CDLC.BusDriver.Kit.KitBuild', $true)
$bufferType = $assembly.GetType('CDLC.BusDriver.Kit.KitPoseBuffer', $true)
$previewType = $assembly.GetType('Preview', $true)
$viewType = $assembly.GetType('Preview+ViewDef', $true)
$vectorType = [Type]::GetType('Unity.Mathematics.float3, Unity.Mathematics', $true)
# the bus as the mod reads it: its vehicle pack (Packs/CDLC.Bus.SWB6109EV12G, Tools/VehiclePack/make_pack.py)
$env:CDLC_BUSDRIVER_REPO = $repo
$spec = $assembly.GetType('TestPacks', $true).GetMethod('Spec', $flags).Invoke($null, [object[]]@('SWB6109EV12G'))
$bus = [Activator]::CreateInstance($busType, [object[]]@($spec))
$data = $buildType.GetMethod('BuildAll', $flags).Invoke($null, [object[]]@($bus, $null))
if ($data.Error) { throw $data.Error }
$bufferArgs = [object[]]::new(1); $bufferArgs[0] = $data.Parts
$buffer = [Activator]::CreateInstance($bufferType, $bufferArgs)
$render = $previewType.GetMethod('Render', $flags)
$viewsMethod = $previewType.GetMethod('Views', $flags)
$halfPoseMethod = $assembly.GetType('Sunwin6109Tests', $true).GetMethod('Posed', $flags)
$apply = $busType.GetMethod('Apply', $flags)
$board = $busType.GetMethod('BoardPoseOf', $flags)
function V3($v) { return [Activator]::CreateInstance($vectorType, [object[]]@([single]$v[0], [single]$v[1], [single]$v[2])) }
function Field($object, [string]$name, $value) { $object.GetType().GetField($name).SetValue($object, $value) }
function Set-Pose([string]$name) {
    if ($name -eq 'half') {
        return $halfPoseMethod.Invoke($null, [object[]]@($data, $spec, [single]0.5))
    }
    $nextBuffer = [Activator]::CreateInstance($bufferType, $bufferArgs)
    $active = if ($name -eq 'closed') { 0 } else { 1 }
    $pose = $board.Invoke($null, [object[]]@($active, $false))
    $apply.Invoke($null, [object[]]@($nextBuffer, $pose, $spec)) | Out-Null
    return $nextBuffer
}
function Write-Blittable($writer, $array, [int]$byteCount) {
    if (-not $byteCount) { return }
    $pin = [Runtime.InteropServices.GCHandle]::Alloc($array, [Runtime.InteropServices.GCHandleType]::Pinned)
    try {
        $bytes = [byte[]]::new($byteCount)
        [Runtime.InteropServices.Marshal]::Copy($pin.AddrOfPinnedObject(), $bytes, 0, $byteCount)
        $writer.Write($bytes)
    } finally { $pin.Free() }
}
$binary = Join-Path $repo 'ModelSource/SWB6109EV12G/sunwin-mesh.bin.gz'
$binaryHash = (Get-FileHash -LiteralPath $binary).Hash
$packMesh = Join-Path $repo 'Packs/CDLC.Bus.SWB6109EV12G/SWB6109EV12G.swb1.gz'
if (-not (Test-Path -LiteralPath $packMesh)) { throw 'No vehicle pack: run Tools/VehiclePack/make_pack.py SWB6109EV12G' }
$embeddedHash = (Get-FileHash -LiteralPath $packMesh).Hash
if ($embeddedHash -ne $binaryHash) { throw 'The vehicle pack does not hold the current runtime binary (make_pack.py SWB6109EV12G)' }
# Dump exactly the compiled KitBuild output and actual Apply pose matrices for
# later ray attribution, including generated strap parts and Kit winding.
$stream = [IO.File]::Create((Join-Path $destination 'runtime-geometry.dat'))
$writer = [IO.BinaryWriter]::new($stream)
$poseAngles = [ordered]@{}
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('KRV1'))
    $writer.Write([int]$data.Parts.Length); $writer.Write([int]3)
    foreach ($part in $data.Parts) {
        $writer.Write([string]$part.Name)
        for ($lod = 0; $lod -lt 3; $lod++) {
            $mesh = $part.Data[$lod]
            if (-not $mesh) { $writer.Write([int]0); $writer.Write([int]0); continue }
            $writer.Write([int]$mesh.P.Length); $writer.Write([int]$mesh.Idx.Length)
            Write-Blittable $writer $mesh.P ($mesh.P.Length * 12)
            Write-Blittable $writer $mesh.Idx ($mesh.Idx.Length * 4)
        }
    }
    $writer.Write([int]3)
    foreach ($poseName in @('closed', 'half', 'open')) {
        $buffer = Set-Pose $poseName
        $doorRotation = $buffer.Delta[4].value
        $doorAngle = 2. * [Math]::Atan2($doorRotation.y, $doorRotation.w) * 180. / [Math]::PI
        $poseAngles[$poseName] = $doorAngle
        $expectedAngle = if ($poseName -eq 'open') { 86. } elseif ($poseName -eq 'half') { 43. } else { 0. }
        if ([Math]::Abs($doorAngle - $expectedAngle) -gt .001) { throw "Incorrect $poseName door pose $doorAngle" }
        $writer.Write([string]$poseName)
        for ($part = 0; $part -lt $data.Parts.Length; $part++) {
            $writer.Write([bool]$buffer.Visible[$part])
            $matrix = $buffer.PartToModel($part)
            foreach ($column in @('c0','c1','c2','c3')) {
                foreach ($axis in @('x','y','z','w')) { $writer.Write([single]$matrix.$column.$axis) }
            }
        }
    }
} finally { $writer.Dispose() }
$ordinary = @(
    @{name='cab'; eye=@(-.69,1.95,4.18); look=@(-.69,1.79,5.18); fov=62},
    @{name='cab-up'; eye=@(-.69,1.95,4.18); look=@(-.69,1.48,4.45); fov=50},
    @{name='cab-door'; eye=@(-.69,1.95,4.18); look=@(.21,1.70,4.78); fov=62},
    @{name='saloon'; eye=@(.1,1.99,2.65); look=@(.1,1.74,1.65); fov=70},
    @{name='rear'; eye=@(.1,2.09,-4.35); look=@(.1,1.89,-3.35); fov=70},
    @{name='front-aisle-low'; eye=@(0,.75,2.10); look=@(0,.68,-1.20); fov=78},
    @{name='rear-aisle-low'; eye=@(0,1.36,-4.50); look=@(0,1.25,-1.20); fov=78},
    @{name='front-wheelhouse-left'; eye=@(0,1.10,1.50); look=@(-.87,.73,2.85); fov=72},
    @{name='front-wheelhouse-right'; eye=@(0,1.10,1.50); look=@(.87,.73,2.85); fov=72},
    @{name='rear-wheelhouse-left'; eye=@(0,1.62,-1.40); look=@(-.87,1.21,-2.75); fov=72},
    @{name='rear-wheelhouse-right'; eye=@(0,1.62,-1.40); look=@(.87,1.21,-2.75); fov=72},
    @{name='ceiling-front'; eye=@(0,2.05,1.75); look=@(0,2.85,3.65); fov=78},
    @{name='ceiling-rear'; eye=@(0,2.05,-1.0); look=@(0,2.95,-3.75); fov=78}
)
$doors = @(
    @{name='front-door'; eye=@(0,1.50,3.45); look=@(1.20,1.05,4.35); fov=78},
    @{name='middle-door'; eye=@(0,1.50,-.70); look=@(1.20,1.05,-.30); fov=78}
)
if ($MirrorMountsOnly) {
    # Physical driver's eye and two external contact views per side. These
    # use the actual compiled SWB1 geometry, not editable source substitutes.
    $ordinary = @(
        @{name='driver-eye'; eye=@(-.69,1.95,4.18); look=@(-1.293,2.46,4.995); fov=58},
        @{name='door-eye'; eye=@(-.69,1.95,4.18); look=@(1.292,2.46,5.014); fov=58},
        @{name='driver-exterior'; eye=@(-2.5,2.50,4.6); look=@(-1.275,2.46,4.995); fov=32},
        @{name='driver-front-oblique'; eye=@(-2.0,2.62,5.5); look=@(-1.3,2.46,5.0); fov=36},
        @{name='door-exterior'; eye=@(2.5,2.50,4.7); look=@(1.275,2.46,5.014); fov=33},
        @{name='door-front-oblique'; eye=@(2.1,2.7,5.8); look=@(1.38,2.42,5.25); fov=43}
    )
}
$rows = [Collections.Generic.List[object]]::new()
$reviewPoses = if ($MirrorMountsOnly) { @('closed') } else { @('open','half','closed') }
foreach ($lod in 0..2) {
    foreach ($poseName in $reviewPoses) {
        $buffer = Set-Pose $poseName
        if ($MirrorMountsOnly) {
            $views = @($ordinary)
        } else {
            $views = if ($poseName -eq 'open') { @($ordinary) + @($doors) } else { @($doors) }
            if ($poseName -eq 'closed') { $views += @($ordinary[1]) }
        }
        foreach ($config in $views) {
            # Construct this value type inside C#: PowerShell field setters
            # otherwise modify an unboxed copy rather than the view passed on.
            $configuredViews = $viewsMethod.Invoke($null, [object[]]@($bus, $data,
                [string]::Join(',', $config.eye), [string]::Join(',', $config.look), [single]$config.fov))
            $view = $configuredViews[0]
            $folder = Join-Path $destination "lod$lod/$poseName"
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            $path = Join-Path $folder ($config.name + '.png')
            $render.Invoke($null, [object[]]@($data, $buffer, [int]$lod, $view, [string]$path)) | Out-Null
            $rows.Add([ordered]@{name=$config.name; lod=$lod; pose=$poseName; path=[IO.Path]::GetRelativePath($repo,$path); eye=$config.eye; target=$config.look; fov=$config.fov; sha256=(Get-FileHash -LiteralPath $path).Hash})
            Write-Output "rendered lod$lod $poseName $($config.name)"
        }
    }
}
if ((Get-FileHash -LiteralPath $binary).Hash -ne $binaryHash) { throw 'Runtime binary changed during review' }
$report = [ordered]@{
    model='SWB6109EV12G'; scope='Actual compiled KitBuild geometry and BusInterior.Apply poses, rendered by the existing software Preview.Render without source edits';
    binary_sha256=$binaryHash; embedded_sha256=$embeddedHash; compiled_test_dll_sha256=(Get-FileHash -LiteralPath (Join-Path $snapshot 'CDLC.BusDriver.Tests.dll')).Hash;
    lod_triangles=@($data.Tris); door_angles_degrees=$poseAngles; rendered_at=(Get-Date).ToString('o'); images=@($rows)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'render-manifest.json') -Encoding utf8
Write-Output "REVIEW_RENDER_OK $($rows.Count) images"
