<# Optional stock-launcher setup for ElevenLabs Agents 1.0.1.
   Release licence notices are prepended by the package generator.
   Downloads only the pinned public bridge package; never imports keys or accounts.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [string]$RuntimeArchive,
    [switch]$NoStart
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$bridgeRuntimeUrl='https://github.com/h7sd/scam-with-your-friends-mods/releases/download/catalogue-runtime-v1.0.1/ElevenLabs-Agents-Bridge-1.0.1.zip'
$bridgeRuntimeSha256='__ELEVENLABS_BRIDGE_SHA256__'
$bridgeManifestSha256='__ELEVENLABS_BRIDGE_MANIFEST_SHA256__'
$bridgeMaximumBytes=33554432
$bridgeMaximumFiles=2048

function Assert-BridgeClosed {
    foreach($name in @('Scam With Your Friends','Scam With Your Friends Playtest')) {
        if(Get-Process -Name $name -ErrorAction SilentlyContinue) {throw 'Close Scam With Your Friends before running ElevenLabs Agents setup.'}
    }
}
function Assert-BridgePath([string]$Root,[string]$Path) {
    $boundary=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $target=[IO.Path]::GetFullPath($Path)
    if($target -ine $boundary -and -not $target.StartsWith($boundary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'A bridge setup path would escape its intended folder.'}
    $current=$target
    while($current) {
        if(Test-Path -LiteralPath $current) {
            if(([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {throw 'Bridge setup does not follow symbolic links or junctions.'}
        }
        $next=[IO.Path]::GetDirectoryName($current)
        if($next -eq $current) {break};$current=$next
    }
}
function Get-BridgeChild([string]$Root,[string]$Relative) {
    $target=[IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
    Assert-BridgePath $Root $target
    return $target
}
function Assert-BridgeHash([string]$Path,[string]$Expected) {
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {throw 'A bridge package file does not match its pinned SHA-256 digest.'}
}
function Assert-BridgePublicName([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.StartsWith('/') -or $Name.EndsWith('/') -or $Name.Contains(':')) {throw 'The bridge package contains an unsafe path.'}
    foreach($segment in $Name.Split('/')) {
        if($segment -cnotmatch '\A[A-Za-z0-9_.@+-]+\z' -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment -match '\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {throw 'The bridge package contains an unsafe path.'}
        if(($segment -match '^\.env' -and $Name -cne 'bridge/.env.example') -or $segment -match '^\.git$|^caller-profiles\.json$|\.log$') {throw 'The bridge package must not contain private settings or logs.'}
    }
    if($Name -cin @('Start-ElevenLabs.cmd','scripts/Start-Backend.ps1','scripts/Start-Bridge.ps1','LICENSE')) {return}
    if($Name -cin @('bridge/.env.example','bridge/package.json','bridge/package-lock.json')) {return}
    if($Name.StartsWith('bridge/src/',[StringComparison]::Ordinal) -or $Name.StartsWith('bridge/public/',[StringComparison]::Ordinal) -or $Name.StartsWith('bridge/node_modules/',[StringComparison]::Ordinal)) {return}
    throw 'The bridge package contains an unexpected or private file.'
}
function Assert-BridgeProperties($Object,[string[]]$Names) {
    if($null -eq $Object -or @($Object.PSObject.Properties.Name).Count -ne $Names.Count -or @($Object.PSObject.Properties.Name | Where-Object {$Names -cnotcontains $_}).Count) {throw 'The bridge manifest has unexpected or missing fields.'}
}
function Download-BridgeRuntime([string]$Destination) {
    Add-Type -AssemblyName System.Net.Http
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('ElevenLabsAgents-Setup/1.0.1')
    $timeout=[Threading.CancellationTokenSource]::new();$timeout.CancelAfter([TimeSpan]::FromMinutes(3))
    $oldTls=[Net.ServicePointManager]::SecurityProtocol;[Net.ServicePointManager]::SecurityProtocol=$oldTls -bor [Net.SecurityProtocolType]::Tls12
    $response=$null
    try {
        $uri=[Uri]$bridgeRuntimeUrl
        for($redirect=0;$redirect -le 5;$redirect++) {
            if($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Port -ne 443 -or @('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') -cnotcontains $uri.DnsSafeHost) {throw 'Unexpected download host.'}
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$timeout.Token).GetAwaiter().GetResult()
            if([int]$response.StatusCode -in @(301,302,303,307,308)) {
                if($null -eq $response.Headers.Location -or $redirect -eq 5) {throw 'Unexpected download redirect.'}
                $uri=[Uri]::new($uri,$response.Headers.Location);$response.Dispose();$response=$null;continue
            }
            if(-not $response.IsSuccessStatusCode -or ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $bridgeMaximumBytes)) {throw 'Unavailable or oversized archive.'}
            $input=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try {
                $buffer=[byte[]]::new(65536);[long]$total=0
                while(($count=$input.ReadAsync($buffer,0,$buffer.Length,$timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total+=$count;if($total -gt $bridgeMaximumBytes) {throw 'Oversized archive.'};$output.Write($buffer,0,$count)
                }
                if($total -le 0) {throw 'Empty archive.'}
            } finally {$output.Dispose();$input.Dispose()}
            return
        }
        throw 'Unavailable archive.'
    } catch {throw 'The pinned ElevenLabs Agents bridge could not be downloaded safely. No game files were changed; try setup again when the release is available.'}
    finally {if($null -ne $response) {$response.Dispose()};$client.Dispose();$handler.Dispose();$timeout.Dispose();[Net.ServicePointManager]::SecurityProtocol=$oldTls}
}
function Expand-BridgeRuntime([string]$ArchivePath,[string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if($archive.Entries.Count -lt 2 -or $archive.Entries.Count -gt $bridgeMaximumFiles+1) {throw 'Unexpected bridge archive inventory.'}
        $entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total=0
        foreach($entry in $archive.Entries) {
            if($entry.FullName -cne 'bridge-files.json') {Assert-BridgePublicName $entry.FullName}
            if($entries.ContainsKey($entry.FullName) -or $entry.Length -le 0 -or $entry.Length -gt 8388608 -or (($entry.ExternalAttributes -shr 16) -band 61440) -in @(40960,16384) -or ($entry.ExternalAttributes -band 1024) -ne 0) {throw 'The bridge archive contains duplicate or unsafe entries.'}
            $entries.Add($entry.FullName,$entry);$total+=$entry.Length
            if($total -gt $bridgeMaximumBytes) {throw 'The bridge archive expands beyond its allowed size.'}
        }
        if(-not $entries.ContainsKey('bridge-files.json') -or $entries['bridge-files.json'].FullName -cne 'bridge-files.json' -or $entries['bridge-files.json'].Length -gt 1048576) {throw 'The bridge manifest is missing or too large.'}
        $manifestStream=$entries['bridge-files.json'].Open();$memory=[IO.MemoryStream]::new()
        try {
            $buffer=[byte[]]::new(4096);[long]$manifestBytes=0
            while(($count=$manifestStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                $manifestBytes+=$count
                if($manifestBytes -gt $entries['bridge-files.json'].Length -or $manifestBytes -gt 1048576) {throw 'The bridge manifest exceeds its declared size.'}
                $memory.Write($buffer,0,$count)
            }
            if($manifestBytes -ne $entries['bridge-files.json'].Length) {throw 'The bridge manifest is truncated.'}
            $bytes=$memory.ToArray()
        } finally {$memory.Dispose();$manifestStream.Dispose()}
        $sha=[Security.Cryptography.SHA256]::Create()
        try {$digest=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
        if($digest -cne $bridgeManifestSha256) {throw 'The bridge manifest does not match its pinned SHA-256 digest.'}
        try {$manifest=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)|ConvertFrom-Json} catch {throw 'Invalid bridge manifest.'}
        Assert-BridgeProperties $manifest @('schemaVersion','version','files')
        if(($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or $manifest.schemaVersion -ne 1 -or $manifest.version -cne '1.0.1' -or $manifest.files -isnot [array] -or $manifest.files.Count -ne $archive.Entries.Count-1) {throw 'Unexpected bridge manifest version or inventory.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($file in $manifest.files) {
            Assert-BridgeProperties $file @('path','size','sha256')
            if($file.path -isnot [string]) {throw 'Invalid bridge manifest path.'};Assert-BridgePublicName $file.path
            if(-not $seen.Add($file.path) -or -not $entries.ContainsKey($file.path) -or $entries[$file.path].FullName -cne $file.path -or ($file.size -isnot [int] -and $file.size -isnot [long]) -or $file.size -ne $entries[$file.path].Length -or $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '\A[0-9a-f]{64}\z') {throw 'The bridge manifest does not describe the exact archive files.'}
        }
        foreach($required in @('Start-ElevenLabs.cmd','scripts/Start-Backend.ps1','scripts/Start-Bridge.ps1','LICENSE','bridge/src/server.js','bridge/public/index.html','bridge/.env.example','bridge/package.json','bridge/package-lock.json','bridge/node_modules/ws/package.json','bridge/node_modules/ajv/package.json')) {
            if(-not $seen.Contains($required)) {throw 'The bridge package is incomplete.'}
        }
        foreach($file in $manifest.files) {
            $target=Get-BridgeChild $Destination $file.path;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
            $input=$entries[$file.path].Open();$output=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try {
                $buffer=[byte[]]::new(65536);[long]$written=0
                while(($count=$input.Read($buffer,0,$buffer.Length)) -gt 0) {$written+=$count;if($written -gt $file.size) {throw 'A bridge archive entry exceeds its declared size.'};$output.Write($buffer,0,$count)}
                if($written -ne $file.size) {throw 'A bridge archive entry is truncated.'}
            } finally {$output.Dispose();$input.Dispose()}
            Assert-BridgeHash $target $file.sha256
        }
        $manifestPath=Get-BridgeChild $Destination 'bridge-files.json';[IO.File]::WriteAllBytes($manifestPath,$bytes)
        return @($manifest.files.path)+@('bridge-files.json')
    } finally {$archive.Dispose()}
}
function Get-BridgePort([string]$GameRoot) {
    $config=Get-BridgeChild $GameRoot 'BepInEx/config/com.community.scamwyf.elevenlabsagents.cfg'
    if(-not (Test-Path -LiteralPath $config -PathType Leaf)) {return 8765}
    if((Get-Item -LiteralPath $config).Length -gt 1048576) {throw 'The ElevenLabs Agents config is too large.'}
    $section=''
    foreach($line in [IO.File]::ReadAllLines($config)) {
        if($line -match '^\s*\[([^\]]+)\]\s*$') {$section=$Matches[1];continue}
        if($section -ceq 'Bridge' -and $line -match '^\s*BaseUrl\s*=\s*(.*?)\s*$') {
            $uri=$null
            if(-not [Uri]::TryCreate($Matches[1],[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'http' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or @('127.0.0.1','localhost','::1','[::1]') -inotcontains $uri.Host) {throw 'Set the ElevenLabs Agents Bridge BaseUrl to a local HTTP address before running local setup. Existing configuration was not changed.'}
            return $uri.Port
        }
    }
    return 8765
}
function Copy-BridgeAtomic([string]$Source,[string]$Destination,[string]$Root) {
    Assert-BridgePath $Root $Destination
    $sibling=Get-BridgeChild ([IO.Path]::GetDirectoryName($Destination)) ('.bridge-setup-'+[guid]::NewGuid().ToString('N')+'.tmp')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))|Out-Null
    try {
        [IO.File]::Copy($Source,$sibling,$false)
        if(Test-Path -LiteralPath $Destination -PathType Leaf) {[IO.File]::Replace($sibling,$Destination,[NullString]::Value)}
        else {[IO.File]::Move($sibling,$Destination)}
    } finally {if(Test-Path -LiteralPath $sibling -PathType Leaf) {Assert-BridgePath $Root $sibling;Remove-Item -LiteralPath $sibling -Force}}
}
function Remove-BridgeTemporary([string]$Root,[string]$Path) {
    Assert-BridgePath $Root $Path
    if([IO.Path]::GetDirectoryName($Path) -ine $Root -or [IO.Path]::GetFileName($Path) -cnotmatch '\A[0-9a-f]{32}\z') {throw 'Invalid bridge temporary cleanup path.'}
    $pending=[Collections.Generic.Queue[string]]::new();$pending.Enqueue($Path)
    while($pending.Count) {foreach($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {Assert-BridgePath $Path $entry.FullName;if($entry.PSIsContainer) {$pending.Enqueue($entry.FullName)}}}
    Remove-Item -LiteralPath $Path -Recurse -Force
}

Assert-BridgeClosed
foreach($pin in @($bridgeRuntimeSha256,$bridgeManifestSha256)) {if($pin -cnotmatch '\A[0-9a-f]{64}\z') {throw 'This ElevenLabs Agents setup build is missing its pinned release digests.'}}
$bridgeGame=[IO.Path]::GetFullPath($GameDir).TrimEnd('\','/');Assert-BridgePath $bridgeGame $bridgeGame
foreach($required in @('Scam With Your Friends.exe','winhttp.dll','BepInEx/core/BepInEx.dll','unstripped_corlib/mscorlib.dll')) {if(-not (Test-Path -LiteralPath (Get-BridgeChild $bridgeGame $required) -PathType Leaf)) {throw 'Select the game folder and complete BepInEx Setup in the launcher first.'}}
foreach($mod in @(@('plugins/ScamWYF.ElevenLabsAgents.dll','ScamWYF.ElevenLabsAgents','1.0.1.0'),@('plugins/ScamWYF.AiBackend.dll','ScamWYF.AiBackend','1.1.0.0'),@('core/ScamWYF.Modding.Core.dll','ScamWYF.Modding.Core','1.0.4.0'))) {
    $path=Get-BridgeChild $bridgeGame ('BepInEx/'+$mod[0])
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)) {throw 'Install ElevenLabs Agents, AI Backend and the shared library through the launcher before running setup.'}
    $identity=[Reflection.AssemblyName]::GetAssemblyName($path)
    $badVersion=if($mod[1] -ceq 'ScamWYF.Modding.Core') {$identity.Version -lt [version]$mod[2]} else {$identity.Version -ne [version]$mod[2]}
    if($identity.Name -cne $mod[1] -or $badVersion) {throw 'This setup requires ElevenLabs Agents 1.0.1, AI Backend 1.1.0 and shared Core 1.0.4 or newer.'}
}
$node=Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if(-not $node) {throw 'Node.js 22.13 or newer is required. Install Node.js from nodejs.org before running setup.'}
$nodeVersion=& $node.Source -p 'process.versions.node';$parsedVersion=$null
if($LASTEXITCODE -ne 0 -or -not [version]::TryParse([string]$nodeVersion,[ref]$parsedVersion) -or $parsedVersion -lt [version]'22.13.0') {throw 'Node.js 22.13 or newer is required. Setup does not install it automatically.'}
$bridgePort=Get-BridgePort $bridgeGame
$bridgeDestination=Get-BridgeChild $bridgeGame 'ElevenLabsAgents'
foreach($private in @('bridge/.env','bridge/caller-profiles.json','bridge/runtime')) {Assert-BridgePath $bridgeDestination (Get-BridgeChild $bridgeDestination $private)}
if(-not $PSCmdlet.ShouldProcess($bridgeDestination,'Verify and install the public ElevenLabs Agents bridge; preserve local settings, then start the hidden backend and open its local setup page')) {return}
$bridgeTempRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'ScamWYF-ElevenLabs-Setup')).TrimEnd('\','/')
$bridgeTemp=Get-BridgeChild $bridgeTempRoot ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($bridgeTemp)|Out-Null
try {
    if($RuntimeArchive) {
        $archive=[IO.Path]::GetFullPath($RuntimeArchive);Assert-BridgePath ([IO.Path]::GetDirectoryName($archive)) $archive
        if([IO.Path]::GetExtension($archive) -ine '.zip') {throw 'The offline runtime must be the pinned ZIP archive.'}
    } else {$archive=Get-BridgeChild $bridgeTemp 'runtime.zip';Write-Output 'Downloading the pinned ElevenLabs Agents bridge...';Download-BridgeRuntime $archive}
    if(-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -le 0 -or (Get-Item -LiteralPath $archive).Length -gt $bridgeMaximumBytes) {throw 'The bridge archive is missing or too large.'}
    Assert-BridgeHash $archive $bridgeRuntimeSha256
    $package=Get-BridgeChild $bridgeTemp 'package';$files=Expand-BridgeRuntime $archive $package
    Assert-BridgeClosed
    foreach($relative in $files) {$target=Get-BridgeChild $bridgeDestination $relative;if(Test-Path -LiteralPath $target -PathType Container) {throw 'A bridge file destination is occupied by a folder.'}}
    $backup=Get-BridgeChild $bridgeGame ('BepInEx/elevenlabs-bridge-backups/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N'))
    $existing=@{};$written=[Collections.Generic.List[string]]::new()
    foreach($relative in $files) {
        $target=Get-BridgeChild $bridgeDestination $relative
        if(Test-Path -LiteralPath $target -PathType Leaf) {$save=Get-BridgeChild $backup $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($save))|Out-Null;[IO.File]::Copy($target,$save,$false);$existing[$relative]=$save}
    }
    try {
        Assert-BridgeClosed
        foreach($relative in $files) {Copy-BridgeAtomic (Get-BridgeChild $package $relative) (Get-BridgeChild $bridgeDestination $relative) $bridgeDestination;$written.Add($relative)}
    } catch {
        $restoreFailed=$false
        foreach($relative in $written) {
            try {$target=Get-BridgeChild $bridgeDestination $relative;if($existing.ContainsKey($relative)) {Copy-BridgeAtomic $existing[$relative] $target $bridgeDestination} elseif(Test-Path -LiteralPath $target -PathType Leaf) {Remove-Item -LiteralPath $target -Force}}
            catch {$restoreFailed=$true}
        }
        if($restoreFailed) {throw 'Bridge installation failed and some public files could not be restored. Public-file backups were retained in BepInEx/elevenlabs-bridge-backups. Local keys, caller profiles and logs were not changed.'}
        throw 'Bridge installation failed; changed public files were restored. Local keys, caller profiles and logs were not changed.'
    }
    Write-Output 'ElevenLabs Agents bridge installed. Existing local keys, caller profiles and logs were preserved.'
    if($existing.Count) {Write-Output ('Public-file backup: '+$backup)}
    if(-not $NoStart) {Assert-BridgeClosed;& (Get-BridgeChild $bridgeDestination 'scripts/Start-Backend.ps1') -ProjectDirectory $bridgeDestination -Port $bridgePort}
} finally {if(Test-Path -LiteralPath $bridgeTemp -PathType Container) {Remove-BridgeTemporary $bridgeTempRoot $bridgeTemp}}
